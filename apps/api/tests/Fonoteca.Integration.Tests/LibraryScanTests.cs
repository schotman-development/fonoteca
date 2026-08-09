using Fonoteca.Api.Library;
using Fonoteca.Data;
using Fonoteca.Domain.Abstractions;
using Fonoteca.Domain.Catalogue;
using Fonoteca.Ingest;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Fonoteca.Integration.Tests;

/// <summary>
/// The scan against a real filesystem and a real PostgreSQL.
/// </summary>
/// <remarks>
/// Both halves have to be real for these to mean anything. A fake filesystem
/// would not reproduce timestamp precision, which is the subtlest way this can
/// break, and an in-memory provider would not reproduce what a
/// <c>timestamptz</c> column actually stores — which is the same bug seen from
/// the other side.
///
/// Each test gets its own database and its own temporary library root, because
/// reconciliation deletes rows whose files are missing: sharing a database with
/// another test would mean deleting its fixture data.
/// </remarks>
[Collection(nameof(PostgresCollection))]
public sealed class LibraryScanTests(PostgresFixture postgres) : IAsyncLifetime
{
    private readonly List<ServiceProvider> _providers = [];

    private string _connectionString = string.Empty;
    private string _root = string.Empty;
    private ServiceProvider? _services;

    public async ValueTask InitializeAsync()
    {
        _connectionString = await postgres.CreateDatabaseAsync(TestContext.Current.CancellationToken);

        await using var db = PostgresFixture.CreateContext(_connectionString);
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);

        _root = Directory.CreateTempSubdirectory("fonoteca-scan-").FullName;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var provider in _providers)
        {
            await provider.DisposeAsync();
        }

        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public async Task FirstScanCataloguesEveryAudioFileAndNothingElse()
    {
        WriteFile("Miles Davis/Kind of Blue/01 So What.flac");
        WriteFile("Miles Davis/Kind of Blue/02 Blue in Green.mp3");
        WriteFile("Miles Davis/Kind of Blue/cover.jpg");
        WriteFile("Miles Davis/Kind of Blue/rip.log");

        var summary = await ScanAsync();

        Assert.Equal(2, summary.FilesSeen);
        Assert.Equal(2, summary.Added);
        Assert.Equal(0, summary.Updated);
        Assert.Equal(0, summary.Removed);

        var paths = await CataloguedPathsAsync();

        // Library-relative, so remounting the library elsewhere does not
        // invalidate every row.
        Assert.Equal(
            [
                "Miles Davis/Kind of Blue/01 So What.flac",
                "Miles Davis/Kind of Blue/02 Blue in Green.mp3",
            ],
            paths);
    }

    /// <summary>
    /// The one that catches the timestamp-precision trap.
    /// </summary>
    /// <remarks>
    /// PostgreSQL stores microseconds and .NET counts 100ns ticks. Storing an
    /// untruncated filesystem timestamp makes every file compare as modified on
    /// the next pass — a rescan that reports 100,000 updates, throws away every
    /// hash in the catalogue and never converges. This asserts the number that
    /// bug would change.
    /// </remarks>
    [Fact]
    public async Task RescanningAnUntouchedLibraryChangesNothing()
    {
        // Timestamps with a deliberate sub-microsecond remainder. Left to
        // whatever the clock happened to produce, roughly one file in ten lands
        // on a whole microsecond by chance and survives the round trip
        // unchanged — so this test would pass against an implementation with the
        // truncation deleted often enough to be useless as a guard.
        SetModified(WriteFile("a.flac"), ticksPastMicrosecond: 7);
        SetModified(WriteFile("b.mp3"), ticksPastMicrosecond: 3);

        await ScanAsync();
        var second = await ScanAsync();

        Assert.Equal(2, second.FilesSeen);
        Assert.Equal(2, second.Unchanged);
        Assert.Equal(0, second.Added);
        Assert.Equal(0, second.Updated);
        Assert.Equal(0, second.Removed);
    }

    /// <summary>
    /// A changed file invalidates everything derived from its bytes. A stale
    /// audio hash is worse than none: it is what makes dedupe delete the copy
    /// that was actually different.
    /// </summary>
    [Fact]
    public async Task ChangedFileIsUpdatedAndItsDerivedDataDiscarded()
    {
        var absolute = WriteFile("a.flac", "original");
        await ScanAsync();

        await using (var db = PostgresFixture.CreateContext(_connectionString))
        {
            var recording = new Recording { Id = RecordingId.New(), Title = "Something" };
            db.Recordings.Add(recording);

            var row = await db.MediaFiles.SingleAsync(TestContext.Current.CancellationToken);
            row.ContentHash = "content";
            row.AudioHash = "audio";
            row.Fingerprint = "AQAAxx";
            row.Integrity = IntegrityState.Intact;
            row.LastVerifiedUtc = DateTimeOffset.UtcNow;
            row.AcoustId = new AcoustId(Guid.CreateVersion7());
            row.AcoustIdCheckedUtc = DateTimeOffset.UtcNow;
            row.RecordingId = recording.Id;
            row.RecordingLookupUtc = DateTimeOffset.UtcNow;
            row.EnrichmentOutcome = EnrichmentOutcome.Linked;
            row.Quality = new AudioQuality
            {
                Codec = "flac",
                SampleRateHz = 44_100,
                Channels = 2,
                BitDepth = 16,
                BitrateBps = 900_000,
                IsLossless = true,
            };
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await File.WriteAllTextAsync(
            absolute,
            "a longer body, so the size differs too",
            TestContext.Current.CancellationToken);
        File.SetLastWriteTimeUtc(absolute, DateTime.UtcNow.AddMinutes(1));

        var summary = await ScanAsync();

        Assert.Equal(1, summary.Updated);
        Assert.Equal(0, summary.Added);
        Assert.Equal(0, summary.Unchanged);

        await using var check = PostgresFixture.CreateContext(_connectionString);
        var updated = await check.MediaFiles.SingleAsync(TestContext.Current.CancellationToken);

        Assert.Null(updated.ContentHash);
        Assert.Null(updated.AudioHash);
        Assert.Null(updated.Fingerprint);
        Assert.Null(updated.Quality);
        Assert.Null(updated.LastVerifiedUtc);
        Assert.Equal(IntegrityState.Unchecked, updated.Integrity);
        Assert.Equal(new FileInfo(absolute).Length, updated.SizeBytes);

        // And the link to a recording, which rests entirely on the AcoustID
        // cleared beside it. Leaving it would keep the file filed under an artist
        // on the strength of evidence that has just been withdrawn.
        Assert.Null(updated.AcoustId);
        Assert.Null(updated.RecordingId);
        Assert.Null(updated.RecordingLookupUtc);
        Assert.Equal(EnrichmentOutcome.NotAttempted, updated.EnrichmentOutcome);

        // The Recording row itself survives. It is a fact about MusicBrainz,
        // shared with every other file that resolved to it.
        Assert.Equal(1, await check.Recordings.CountAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task VanishedFileLeavesTheCatalogue()
    {
        WriteFile("a.flac");
        var doomed = WriteFile("b.mp3");

        await ScanAsync();
        File.Delete(doomed);

        var summary = await ScanAsync();

        Assert.Equal(1, summary.FilesSeen);
        Assert.Equal(1, summary.Removed);
        Assert.Equal(1, summary.Unchanged);
        Assert.Equal(["a.flac"], await CataloguedPathsAsync());
    }

    /// <summary>
    /// An empty root with a populated catalogue is an unmounted volume far more
    /// often than a deleted library, and the cost of guessing wrong is asymmetric:
    /// refusing costs a warning, deleting costs a full rescan of 100,000 files.
    /// </summary>
    [Fact]
    public async Task EmptyLibraryRemovesNothing()
    {
        WriteFile("a.flac");
        WriteFile("b.mp3");
        await ScanAsync();

        foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
        {
            File.Delete(file);
        }

        var summary = await ScanAsync();

        Assert.Equal(0, summary.FilesSeen);
        Assert.Equal(0, summary.Removed);
        Assert.Equal(2, await CountAsync());
    }

    /// <summary>
    /// A link to an ancestor is what "ln -s .. all" in a collection folder
    /// leaves behind, and following it walks <c>loop/loop/loop/…</c> forever:
    /// the request never returns and the catalogue grows a fresh row per level
    /// until the disk fills. This test finishes, which is the assertion.
    /// </summary>
    [Fact]
    public async Task DirectorySymlinkLoopDoesNotWalkForever()
    {
        WriteFile("Albums/01.flac");
        Directory.CreateSymbolicLink(Path.Combine(_root, "Albums", "up"), _root);

        var summary = await ScanAsync();

        Assert.Equal(1, summary.FilesSeen);
        Assert.Equal(["Albums/01.flac"], await CataloguedPathsAsync());
    }

    /// <summary>
    /// A symlinked file is an ordinary file. Only recursion through a link is
    /// refused — the files behind a directory link are either already reachable
    /// by their real path or outside the library entirely.
    /// </summary>
    [Fact]
    public async Task SymlinkedFilesAreStillCatalogued()
    {
        var target = WriteFile("Albums/01.flac");
        File.CreateSymbolicLink(Path.Combine(_root, "shortcut.flac"), target);

        var summary = await ScanAsync();

        Assert.Equal(2, summary.Added);
        Assert.Equal(["Albums/01.flac", "shortcut.flac"], await CataloguedPathsAsync());
    }

    /// <summary>
    /// The costly one: a directory the process cannot open looks exactly like a
    /// directory whose files were deleted, and acting on that resemblance means
    /// one <c>chmod</c> destroys a branch of the catalogue together with every
    /// hash and fingerprint in it.
    /// </summary>
    [Fact]
    public async Task UnreadableDirectoryStopsRemovalsInsteadOfLosingTheCatalogue()
    {
        WriteFile("Readable/01.flac");
        WriteFile("Private/02.flac");

        var first = await ScanAsync();
        Assert.Equal(2, first.Added);
        Assert.Equal(0, first.UnreadableDirectories);

        var locked = Path.Combine(_root, "Private");
        File.SetUnixFileMode(locked, UnixFileMode.None);

        try
        {
            var second = await ScanAsync();

            Assert.Equal(1, second.UnreadableDirectories);
            Assert.Equal(1, second.FilesSeen);

            // The file behind the locked directory is unseen, not gone.
            Assert.Equal(0, second.Removed);
            Assert.Equal(2, await CountAsync());
        }
        finally
        {
            File.SetUnixFileMode(
                locked,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [Fact]
    public async Task MissingLibraryRootIsRefusedWithoutTouchingTheCatalogue()
    {
        WriteFile("a.flac");
        await ScanAsync();

        var absent = Path.Combine(_root, "not-mounted");
        var service = BuildServices(absent).GetRequiredService<LibraryScanService>();

        var outcome = await service.ScanAsync(TestContext.Current.CancellationToken);

        Assert.Equal(LibraryScanStatus.LibraryRootMissing, outcome.Status);
        Assert.Null(outcome.Summary);
        Assert.Equal(1, await CountAsync());
    }

    [Fact]
    public async Task NestedDirectoriesAreWalkedAndPathsStayRelative()
    {
        WriteFile("A/B/C/D/deep.flac");

        var summary = await ScanAsync();

        Assert.Equal(1, summary.Added);
        Assert.Equal(["A/B/C/D/deep.flac"], await CataloguedPathsAsync());
    }

    private async Task<LibraryScanSummary> ScanAsync()
    {
        _services ??= BuildServices(_root);

        var outcome = await _services.GetRequiredService<LibraryScanService>()
            .ScanAsync(TestContext.Current.CancellationToken);

        Assert.Equal(LibraryScanStatus.Completed, outcome.Status);
        Assert.NotNull(outcome.Summary);

        return outcome.Summary;
    }

    /// <summary>
    /// The same registrations Program makes, minus the web host. If this drifts
    /// from Program the endpoint tests still cover the real wiring.
    /// </summary>
    private ServiceProvider BuildServices(string libraryRoot)
    {
        var services = new ServiceCollection();

        services.AddLogging();
        services.AddDbContext<FonotecaDbContext>(options => options.UseNpgsql(_connectionString));
        services.AddSingleton<IClock, SystemClock>();
        services.AddSingleton(new FileSystemAudioFileStore(libraryRoot));
        services.AddSingleton<IAudioFileStore>(
            sp => sp.GetRequiredService<FileSystemAudioFileStore>());
        services.AddSingleton<LibraryScanner>();
        services.AddSingleton<LibraryScanService>();

        var provider = services.BuildServiceProvider();
        _providers.Add(provider);
        return provider;
    }

    /// <summary>
    /// Stamps a file with a modification time that is <paramref name="ticksPastMicrosecond"/>
    /// 100ns ticks past a whole microsecond — precision ext4 keeps and
    /// PostgreSQL does not.
    /// </summary>
    private static void SetModified(string absolutePath, int ticksPastMicrosecond)
    {
        var whole = new DateTime(2026, 3, 4, 5, 6, 7, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(absolutePath, whole.AddTicks(ticksPastMicrosecond));

        // If the filesystem ever stops keeping this precision the test silently
        // stops testing anything, so it says so instead.
        Assert.NotEqual(0, File.GetLastWriteTimeUtc(absolutePath).Ticks % TimeSpan.TicksPerMicrosecond);
    }

    private string WriteFile(string relativePath, string content = "not really audio")
    {
        var absolute = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(absolute)!);
        File.WriteAllText(absolute, content);
        return absolute;
    }

    private async Task<List<string>> CataloguedPathsAsync()
    {
        await using var db = PostgresFixture.CreateContext(_connectionString);

        return await db.MediaFiles
            .AsNoTracking()
            .Select(f => f.Path)
            .OrderBy(p => p)
            .ToListAsync(TestContext.Current.CancellationToken);
    }

    private async Task<int> CountAsync()
    {
        await using var db = PostgresFixture.CreateContext(_connectionString);
        return await db.MediaFiles.CountAsync(TestContext.Current.CancellationToken);
    }
}
