using Fonoteca.Api.Configuration;
using Fonoteca.Api.Library;
using Fonoteca.Data;
using Fonoteca.Domain.Abstractions;
using Fonoteca.Domain.Events;
using Fonoteca.Domain.Catalogue;
using Fonoteca.Fixtures;
using Fonoteca.Ingest;
using Fonoteca.Tagging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Fonoteca.Integration.Tests;

/// <summary>
/// The tag-write pass, against real PostgreSQL and real audio files.
/// </summary>
/// <remarks>
/// <c>CatalogueTagWriteTests</c> pins what goes into a file and
/// <c>CatalogueTagsTests</c> pins what the rule decides; what is left — and it
/// is the part that fails at runtime rather than at compile time — is everything
/// between a row and that write.
///
/// Two of these are worth more than the rest.
///
/// <b>The projection has to translate.</b> Ids in this schema are
/// value-converted <c>readonly record struct</c>s and EF turns no member access
/// on one into SQL, so the query that gathers a file's catalogue answer is the
/// kind of code that compiles, passes review and throws on the first row. The
/// only way to know is to run it against PostgreSQL.
///
/// <b>And the catalogue has to survive its own write.</b> A tag write changes
/// the bytes; a row still holding the old size reads as modified on the next
/// scan, and the scan then discards every derived column on it. Over a
/// library-wide run that is the entire catalogue, deleted by the feature that
/// was supposed to preserve it.
/// </remarks>
[Collection(nameof(PostgresCollection))]
public sealed class TagWritePassTests(PostgresFixture postgres) : IAsyncLifetime
{
    private static readonly Mbid RecordingMbid = Mb("11111111-1111-4111-8111-111111111111");
    private static readonly Mbid ReleaseMbid = Mb("22222222-2222-4222-8222-222222222222");
    private static readonly Mbid GroupMbid = Mb("33333333-3333-4333-8333-333333333333");
    private static readonly Mbid ArtistMbid = Mb("44444444-4444-4444-8444-444444444444");

    private readonly List<ServiceProvider> _providers = [];

    private string _connectionString = string.Empty;
    private string _root = string.Empty;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _connectionString = await postgres.CreateDatabaseAsync(Token);

        await using var db = PostgresFixture.CreateContext(_connectionString);
        await db.Database.MigrateAsync(Token);

        _root = Directory.CreateTempSubdirectory("fonoteca-tagwrite-pass-").FullName;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var provider in _providers) await provider.DisposeAsync();

        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    /// <summary>
    /// A file with a complete catalogue answer comes out of the pass carrying it.
    /// </summary>
    /// <remarks>
    /// The end-to-end one, and the only test that exercises the gather query at
    /// all: everything it reads crosses a value converter, so a projection EF
    /// cannot translate fails here and nowhere else.
    /// </remarks>
    [Fact]
    public async Task AFileTheCatalogueHasAnsweredIsWrittenWithThatAnswer()
    {
        SkipWithoutTools();
        await SeedAsync(("Miles Davis/Kind of Blue/01 track.flac", 1));

        var summary = await RunAsync(TagWriteScope.Library);

        Assert.Equal(1, summary.Examined);
        Assert.Equal(1, summary.Written);
        Assert.Equal(0, summary.Failed);

        var store = new FileSystemAudioFileStore(_root);
        var reading = await new TagReader(store)
            .ReadAsync(new LibraryPath("Miles Davis/Kind of Blue/01 track.flac"), cancellationToken: Token);

        Assert.Equal("So What", reading.Fields["TITLE"]);
        Assert.Equal("Miles Davis", reading.Fields["ARTIST"]);
        Assert.Equal("Kind of Blue", reading.Fields["ALBUM"]);
        Assert.Equal("1", reading.Fields["TRACKNUMBER"]);
        Assert.Equal("1959", reading.Fields["YEAR"]);
        Assert.Equal(RecordingMbid.ToString(), reading.Find(CatalogueTags.RecordingId));
        Assert.Equal(ReleaseMbid.ToString(), reading.Find(CatalogueTags.ReleaseId));
        Assert.Equal(GroupMbid.ToString(), reading.Find(CatalogueTags.ReleaseGroupId));
        Assert.Equal(ArtistMbid.ToString(), reading.Find(CatalogueTags.ArtistId));
    }

    /// <summary>
    /// The sharp edge: writing tags must not make the next scan discard the catalogue.
    /// </summary>
    /// <remarks>
    /// Identification's own guard, reached by a second route and at a far worse
    /// scale. The pass writes the file's new size and mtime onto the row in the
    /// same transaction; without that line the next scan sees every file it just
    /// tagged as modified and clears the AcoustID, the recording, the album and
    /// the track off all of them.
    /// </remarks>
    [Fact]
    public async Task WritingTagsDoesNotMakeTheNextScanThinkTheFileChanged()
    {
        SkipWithoutTools();
        await SeedAsync(("Miles Davis/Kind of Blue/01 track.flac", 1));

        var services = Build();
        Assert.Equal(1, (await RunAsync(TagWriteScope.Library, services)).Written);

        var rescan = await services.GetRequiredService<LibraryScanService>()
            .ScanAsync(Token);

        Assert.Equal(LibraryScanStatus.Completed, rescan.Status);
        Assert.Equal(0, rescan.Summary!.Updated);
        Assert.Equal(1, rescan.Summary.Unchanged);

        await using var db = PostgresFixture.CreateContext(_connectionString);
        var row = await db.MediaFiles.SingleAsync(Token);

        Assert.NotNull(row.RecordingId);
        Assert.NotNull(row.ReleaseId);
        Assert.NotNull(row.TrackId);
    }

    /// <summary>
    /// An album's button writes that album and leaves everything else alone.
    /// </summary>
    [Fact]
    public async Task AReleaseScopedRunTouchesNoOtherAlbum()
    {
        SkipWithoutTools();

        await SeedAsync(("Miles Davis/Kind of Blue/01 track.flac", 1));
        var other = await SeedOtherAlbumAsync("Someone Else/Another Record/01 track.flac");

        ReleaseId chosen;

        await using (var db = PostgresFixture.CreateContext(_connectionString))
        {
            chosen = await db.Releases
                .Where(release => release.Mbid == ReleaseMbid)
                .Select(release => release.Id)
                .SingleAsync(Token);
        }

        var summary = await RunAsync(TagWriteScope.ForRelease(chosen, "Kind of Blue"));

        Assert.Equal(1, summary.Examined);
        Assert.Equal(1, summary.Written);

        var reader = new TagReader(new FileSystemAudioFileStore(_root));
        var untouched = await reader.ReadAsync(new LibraryPath(other), cancellationToken: Token);

        Assert.False(untouched.Fields.ContainsKey("ALBUM"));
    }

    /// <summary>
    /// An artist's button writes the tracks their page lists, and nothing else.
    /// </summary>
    /// <remarks>
    /// The scope is <c>RecordingsOfAsync</c>, which is the artist page's own
    /// browse rule — billed, linked as conductor or ensemble, or a writer of the
    /// work. A second definition of "this artist's recordings" would put a
    /// composer's symphonies in one and not the other, and the visible symptom
    /// would be a button that silently skips most of the page it sits on.
    /// </remarks>
    [Fact]
    public async Task AnArtistScopedRunWritesTheirTracksAndNoOthers()
    {
        SkipWithoutTools();

        await SeedAsync(("Miles Davis/Kind of Blue/01 track.flac", 1));
        var other = await SeedOtherAlbumAsync("Someone Else/Another Record/01 track.flac");

        ArtistId miles;

        await using (var db = PostgresFixture.CreateContext(_connectionString))
        {
            miles = await db.Artists
                .Where(artist => artist.Mbid == ArtistMbid)
                .Select(artist => artist.Id)
                .SingleAsync(Token);
        }

        var summary = await RunAsync(TagWriteScope.ForArtist(miles, "Miles Davis"));

        Assert.Equal(1, summary.Examined);
        Assert.Equal(1, summary.Written);

        var reader = new TagReader(new FileSystemAudioFileStore(_root));

        var written = await reader
            .ReadAsync(new LibraryPath("Miles Davis/Kind of Blue/01 track.flac"), cancellationToken: Token);

        Assert.Equal("Kind of Blue", written.Fields["ALBUM"]);

        var untouched = await reader.ReadAsync(new LibraryPath(other), cancellationToken: Token);

        Assert.False(untouched.Fields.ContainsKey("ALBUM"));
    }

    /// <summary>
    /// One file nothing can read does not stop the pass on every file behind it.
    /// </summary>
    /// <remarks>
    /// This pass has no "done" column — the diff is the worklist — so nothing
    /// steps over a row that threw. An escaping exception would end the run at
    /// the same file on every attempt, which turns one unreadable file into a
    /// feature that appears to do nothing.
    /// </remarks>
    [Fact]
    public async Task AFileNothingCanParseIsCountedAndTheRestAreStillWritten()
    {
        SkipWithoutTools();

        await SeedAsync(
            ("Miles Davis/Kind of Blue/01 track.flac", 1),
            ("Miles Davis/Kind of Blue/02 broken.flac", 2));

        // Text with an audio extension, so both tag libraries refuse it. Written
        // after seeding, because seeding records the real file's size.
        await File.WriteAllTextAsync(
            Path.Combine(_root, "Miles Davis/Kind of Blue/02 broken.flac"),
            "this is not a FLAC",
            Token);

        var summary = await RunAsync(TagWriteScope.Library);

        Assert.Equal(2, summary.Examined);
        Assert.Equal(1, summary.Written);
        Assert.Equal(1, summary.Failed);
    }

    /// <summary>
    /// Running it again reads every file and rewrites none of them.
    /// </summary>
    /// <remarks>
    /// There is no <c>TagsWrittenUtc</c> column: the diff is the worklist. This
    /// is what makes that affordable rather than merely correct — a second run
    /// over a tagged library is a tag read per file, not eight thousand
    /// container rewrites.
    /// </remarks>
    [Fact]
    public async Task ASecondRunOverTheSameLibraryWritesNothing()
    {
        SkipWithoutTools();
        await SeedAsync(("Miles Davis/Kind of Blue/01 track.flac", 1));

        var services = Build();

        Assert.Equal(1, (await RunAsync(TagWriteScope.Library, services)).Written);

        var again = await RunAsync(TagWriteScope.Library, services);

        Assert.Equal(1, again.Examined);
        Assert.Equal(0, again.Written);
        Assert.Equal(1, again.Unchanged);
    }

    /// <summary>
    /// The default posture: everything except the write.
    /// </summary>
    /// <remarks>
    /// Not a failure and not a no-op. Every file is read and diffed and the plan
    /// is journalled, so the run reports exactly what flipping the flag would do.
    /// </remarks>
    [Fact]
    public async Task WithMutationOffTheRunIsCompleteExceptForTheWrite()
    {
        SkipWithoutTools();
        await SeedAsync(("Miles Davis/Kind of Blue/01 track.flac", 1));

        var summary = await RunAsync(TagWriteScope.Library, Build(allowMutation: false));

        Assert.Equal(1, summary.Examined);
        Assert.Equal(0, summary.Written);
        Assert.Equal(1, summary.Refused);

        await using var db = PostgresFixture.CreateContext(_connectionString);

        var journalled = await db.DomainEvents
            .Where(entry => entry.Type == "tagging.catalogue.refused")
            .CountAsync(Token);

        Assert.Equal(1, journalled);
    }

    /// <summary>
    /// A file the catalogue has not fully answered is not on the worklist.
    /// </summary>
    /// <remarks>
    /// All three links or none. Writing an album name with no track number, or a
    /// title with no album, produces a file that reads as a half-tagged rip in
    /// every player — and the narrower question is the honest one.
    /// </remarks>
    [Fact]
    public async Task AFileWithNoAlbumIsLeftAlone()
    {
        SkipWithoutTools();

        var lonely = await SeedUnattributedAsync("Unknown/unplaced.flac");

        var summary = await RunAsync(TagWriteScope.Library);

        Assert.Equal(0, summary.Examined);

        var reading = await new TagReader(new FileSystemAudioFileStore(_root))
            .ReadAsync(new LibraryPath(lonely), cancellationToken: Token);

        Assert.False(reading.Fields.ContainsKey("ALBUM"));
    }

    /// <summary>Only one piece of library-wide work at a time.</summary>
    [Fact]
    public async Task TheWriteIsRefusedWhileAnotherPassHoldsTheGate()
    {
        SkipWithoutTools();
        await SeedAsync(("Miles Davis/Kind of Blue/01 track.flac", 1));

        var services = Build();
        var gate = services.GetRequiredService<LibraryWorkGate>();

        Assert.True(gate.TryEnter("library.scan", out var lease));

        using (lease)
        {
            var tags = services.GetRequiredService<TagWriteService>();

            Assert.Equal(
                TagWriteStartStatus.AlreadyRunning,
                tags.Start(TagWriteScope.Library).Status);
        }
    }

    private async Task<TagWriteSummary> RunAsync(TagWriteScope scope, ServiceProvider? services = null)
    {
        var provider = services ?? Build();
        var tags = provider.GetRequiredService<TagWriteService>();

        Assert.Equal(TagWriteStartStatus.Started, tags.Start(scope).Status);

        var deadline = DateTimeOffset.UtcNow.AddMinutes(2);

        while (tags.IsRunning && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(25, Token);
        }

        Assert.False(tags.IsRunning, "The tag write pass did not finish.");
        Assert.Null(tags.LastError);

        return tags.LastCompleted!;
    }

    /// <summary>The same graph Program builds, minus the web host.</summary>
    private ServiceProvider Build(bool allowMutation = true)
    {
        var services = new ServiceCollection();

        services.AddLogging();
        services.AddSignalR();
        services.AddDbContext<FonotecaDbContext>(options => options.UseNpgsql(_connectionString));

        services.AddSingleton<IClock, SystemClock>();
        services.AddSingleton(new FileSystemAudioFileStore(_root));
        services.AddSingleton<IAudioFileStore>(sp => sp.GetRequiredService<FileSystemAudioFileStore>());
        services.AddSingleton<LibraryScanner>();
        services.AddSingleton<LibraryScanService>();

        services.AddSingleton<TagReader>();
        services.AddSingleton(new TagWriterOptions { AllowFileMutation = allowMutation });
        services.AddScoped<IEventLog, EventLog>();
        services.AddScoped<TagWriter>();

        services.AddSingleton<LibraryWorkGate>();
        services.AddSingleton<TagWriteService>();

        services.AddSingleton<IOptions<FonotecaOptions>>(
            new OptionsWrapper<FonotecaOptions>(new FonotecaOptions
            {
                LibraryPath = _root,
                AllowFileMutation = allowMutation,
            }));

        services.AddSingleton<IHostApplicationLifetime, NeverStops>();

        var provider = services.BuildServiceProvider();
        _providers.Add(provider);
        return provider;
    }

    /// <summary>
    /// Files as the three passes leave them: identified, enriched and attributed.
    /// </summary>
    private async Task SeedAsync(params (string Path, int Position)[] files)
    {
        await using var db = PostgresFixture.CreateContext(_connectionString);

        var artist = new Artist { Id = ArtistId.New(), Name = "Miles Davis", Mbid = ArtistMbid };

        var group = new ReleaseGroup
        {
            Id = ReleaseGroupId.New(),
            Title = "Kind of Blue",
            Mbid = GroupMbid,
            PrimaryType = "Album",
        };

        var release = new Release
        {
            Id = ReleaseId.New(),
            Title = "Kind of Blue",
            Mbid = ReleaseMbid,
            ReleaseGroupId = group.Id,
            ReleasedYear = 1959,
            TrackCount = 5,
            DiscCount = 1,
        };

        var recording = new Recording
        {
            Id = RecordingId.New(),
            Title = "So What",
            Mbid = RecordingMbid,
        };

        db.Artists.Add(artist);
        db.ReleaseGroups.Add(group);
        db.Releases.Add(release);
        db.Recordings.Add(recording);

        // The recording's billing line and the release's, which become ARTIST
        // and ALBUMARTIST.
        db.ArtistCredits.Add(new ArtistCredit
        {
            Id = Guid.CreateVersion7(),
            ArtistId = artist.Id,
            RecordingId = recording.Id,
            Position = 0,
        });

        db.ArtistCredits.Add(new ArtistCredit
        {
            Id = Guid.CreateVersion7(),
            ArtistId = artist.Id,
            ReleaseId = release.Id,
            Position = 0,
        });

        foreach (var (path, position) in files)
        {
            var track = new Track
            {
                Id = TrackId.New(),
                ReleaseId = release.Id,
                RecordingId = recording.Id,
                Position = position,
                DiscNumber = 1,
                Title = "So What",
            };

            db.Tracks.Add(track);
            db.MediaFiles.Add(Copy(path, recording.Id, release.Id, group.Id, track.Id));
        }

        await db.SaveChangesAsync(Token);
    }

    /// <summary>A second album, so a scoped run has something to leave alone.</summary>
    private async Task<string> SeedOtherAlbumAsync(string path)
    {
        await using var db = PostgresFixture.CreateContext(_connectionString);

        var release = new Release
        {
            Id = ReleaseId.New(),
            Title = "Another Record",
            Mbid = Mb("55555555-5555-4555-8555-555555555555"),
            TrackCount = 1,
        };

        var recording = new Recording
        {
            Id = RecordingId.New(),
            Title = "Another Song",
            Mbid = Mb("66666666-6666-4666-8666-666666666666"),
        };

        var track = new Track
        {
            Id = TrackId.New(),
            ReleaseId = release.Id,
            RecordingId = recording.Id,
            Position = 1,
            DiscNumber = 1,
        };

        db.Releases.Add(release);
        db.Recordings.Add(recording);
        db.Tracks.Add(track);
        db.MediaFiles.Add(Copy(path, recording.Id, release.Id, null, track.Id));

        await db.SaveChangesAsync(Token);

        return path;
    }

    /// <summary>A file identification reached and attribution did not.</summary>
    private async Task<string> SeedUnattributedAsync(string path)
    {
        await using var db = PostgresFixture.CreateContext(_connectionString);

        var recording = new Recording
        {
            Id = RecordingId.New(),
            Title = "Unplaced",
            Mbid = Mb("77777777-7777-4777-8777-777777777777"),
        };

        db.Recordings.Add(recording);
        db.MediaFiles.Add(Copy(path, recording.Id, null, null, null));

        await db.SaveChangesAsync(Token);

        return path;
    }

    private MediaFile Copy(
        string path,
        RecordingId recording,
        ReleaseId? release,
        ReleaseGroupId? group,
        TrackId? track)
    {
        var full = Path.Combine(_root, path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.Copy(Corpus.Flac, full, overwrite: true);

        var facts = new FileInfo(full);

        return new MediaFile
        {
            Id = MediaFileId.New(),
            Path = path,
            SizeBytes = facts.Length,
            LastModifiedUtc = StoreTime.ToStorePrecision(new DateTimeOffset(facts.LastWriteTimeUtc)),
            RecordingId = recording,
            ReleaseId = release,
            ReleaseGroupId = group,
            TrackId = track,
            RecordingLookupUtc = DateTimeOffset.UtcNow,
            EnrichmentOutcome = EnrichmentOutcome.Linked,
        };
    }

    private static Mbid Mb(string id) => new(new Guid(id));

    private static void SkipWithoutTools()
    {
        Assert.SkipUnless(Corpus.IsAvailable, "ffmpeg is not on PATH; source ~/.local/opt/env.sh.");
    }

    private sealed class NeverStops : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;

        public CancellationToken ApplicationStopping => CancellationToken.None;

        public CancellationToken ApplicationStopped => CancellationToken.None;

        public void StopApplication()
        {
        }
    }
}
