using Fonoteca.Api.Configuration;
using Fonoteca.Api.Library;
using Fonoteca.Data;
using Fonoteca.Domain.Abstractions;
using Fonoteca.Domain.Catalogue;
using Fonoteca.Domain.Events;
using Fonoteca.Ingest;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Fonoteca.Integration.Tests;

/// <summary>
/// The file manager against a real filesystem and a real PostgreSQL.
/// </summary>
/// <remarks>
/// Both halves have to be real for the same reasons the scan's tests give, plus
/// one of this feature's own: the failure worth catching is an interaction
/// <i>between</i> a rename on disk and the reconciler's opinion of it, and
/// neither a fake filesystem nor an in-memory provider has an opinion.
/// </remarks>
[Collection(nameof(PostgresCollection))]
public sealed class FileManagerTests(PostgresFixture postgres) : IAsyncLifetime
{
    /// <summary>Stands in for whatever AcoustID would have answered.</summary>
    private static readonly AcoustId Cluster = new(Guid.Parse("019fffe5-4be4-7d4a-8a4a-d6459a551424"));

    private readonly List<ServiceProvider> _providers = [];

    private string _connectionString = string.Empty;
    private string _root = string.Empty;
    private string _trash = string.Empty;
    private ServiceProvider? _services;

    public async ValueTask InitializeAsync()
    {
        _connectionString = await postgres.CreateDatabaseAsync(TestContext.Current.CancellationToken);

        await using var db = PostgresFixture.CreateContext(_connectionString);
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);

        _root = Directory.CreateTempSubdirectory("fonoteca-files-").FullName;
        _trash = Directory.CreateTempSubdirectory("fonoteca-trash-").FullName;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var provider in _providers) await provider.DisposeAsync();

        foreach (var directory in new[] { _root, _trash })
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>
    /// <b>The reason this feature is an endpoint rather than a shell command.</b>
    /// </summary>
    /// <remarks>
    /// A rename changes no bytes, but to the reconciler it is a path that has
    /// gone and a path that has arrived — so the old row is deleted and a new,
    /// empty one takes its place, and every AcoustID, recording link, album
    /// decision and human answer under the folder goes with it. On a classical
    /// box set that is hours of rate-limited lookups lost to a typo correction,
    /// and nothing reports it: the album simply reappears on the worklist.
    ///
    /// The counterpart to <c>TaggingAFileDoesNotMakeTheNextScanThinkItChanged</c>,
    /// one level up — that one keeps a file's identity across a byte change,
    /// this one keeps it across a name change.
    /// </remarks>
    [Fact]
    public async Task RenamingAFolderKeepsWhatTheIdentificationPassEarned()
    {
        WriteFile("Brahms/Symphony 1 (mess)/01 Allegro.flac");
        WriteFile("Brahms/Symphony 1 (mess)/CD2/02 Andante.flac");

        await ScanAsync();
        await IdentifyByHandAsync();

        var move = await Files().MoveAsync(
            "Brahms/Symphony 1 (mess)",
            "Brahms/Symphony No. 1",
            TestContext.Current.CancellationToken);

        Assert.True(move.Applied);
        Assert.Equal(2, move.CatalogueRows);

        // Both segments below the folder came with it: a rewrite that replaced
        // the whole path rather than its prefix would have flattened CD2.
        Assert.Equal(
            [
                "Brahms/Symphony No. 1/01 Allegro.flac",
                "Brahms/Symphony No. 1/CD2/02 Andante.flac",
            ],
            await CataloguedPathsAsync());

        // And the scan agrees, which is the half that matters. Nothing added,
        // nothing removed — so nothing was re-created empty.
        var summary = await ScanAsync();

        Assert.Equal(0, summary.Added);
        Assert.Equal(0, summary.Removed);
        Assert.Equal(0, summary.Updated);

        await using var db = PostgresFixture.CreateContext(_connectionString);

        var rows = await db.MediaFiles
            .AsNoTracking()
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.All(rows, row => Assert.Equal(Cluster, row.AcoustId));
        Assert.All(rows, row => Assert.Equal(AcoustIdOutcome.Identified, row.AcoustIdOutcome));
    }

    [Fact]
    public async Task TrashingMovesTheFilesOutOfTheLibraryRatherThanDeletingThem()
    {
        WriteFile("Brahms/Symphony 1 (mess)/01 Allegro.flac");
        WriteFile("Brahms/Symphony No. 1/01 Allegro.flac");

        await ScanAsync();

        var trashed = await Files().TrashAsync(
            ["Brahms/Symphony 1 (mess)"], TestContext.Current.CancellationToken);

        Assert.True(trashed.Applied);
        Assert.Equal(1, trashed.Entries);
        Assert.Equal(1, trashed.CatalogueRows);

        // Gone from the library.
        Assert.False(Directory.Exists(Path.Combine(_root, "Brahms", "Symphony 1 (mess)")));

        // Still on disk, keeping its layout, so putting it back is a mv.
        var recovered = Directory.EnumerateFiles(_trash, "*.flac", SearchOption.AllDirectories)
            .Single(path => path.Contains("Symphony 1 (mess)", StringComparison.Ordinal));

        Assert.EndsWith(
            Path.Combine("Brahms", "Symphony 1 (mess)", "01 Allegro.flac"),
            recovered,
            StringComparison.Ordinal);

        // The rows go in the same operation, because this process moved the
        // files itself and does not have to infer their absence from a walk.
        Assert.Equal(["Brahms/Symphony No. 1/01 Allegro.flac"], await CataloguedPathsAsync());

        // The album that was not trashed is untouched, and the artist folder
        // survives because it still holds one.
        Assert.True(Directory.Exists(Path.Combine(_root, "Brahms", "Symphony No. 1")));
    }

    [Fact]
    public async Task TrashingTheLastAlbumPrunesTheArtistFolderItEmptied()
    {
        WriteFile("Brahms/Symphony 1/01 Allegro.flac");
        await ScanAsync();

        await Files().TrashAsync(["Brahms/Symphony 1"], TestContext.Current.CancellationToken);

        Assert.False(Directory.Exists(Path.Combine(_root, "Brahms")));

        // And it stops at the root, which is the whole hazard in that loop.
        Assert.True(Directory.Exists(_root));
    }

    [Fact]
    public async Task AMoveOntoSomethingThatIsAlreadyThereIsRefused()
    {
        WriteFile("Brahms/Symphony 1 (mess)/01 Allegro.flac");
        WriteFile("Brahms/Symphony No. 1/01 Allegro.flac");

        await ScanAsync();

        var move = await Files().MoveAsync(
            "Brahms/Symphony 1 (mess)",
            "Brahms/Symphony No. 1",
            TestContext.Current.CancellationToken);

        // Merging two folders is a different act with a different confirmation,
        // and the silent version of it loses a file per colliding name.
        Assert.False(move.Applied);
        Assert.Contains("already exists", move.Detail, StringComparison.Ordinal);
        Assert.Equal(2, await CountAsync());
    }

    [Fact]
    public async Task AFolderCannotBeMovedInsideItself()
    {
        WriteFile("Brahms/Symphony 1/01 Allegro.flac");
        await ScanAsync();

        var move = await Files().MoveAsync(
            "Brahms", "Brahms/Archive", TestContext.Current.CancellationToken);

        Assert.False(move.Applied);
        Assert.Equal(["Brahms/Symphony 1/01 Allegro.flac"], await CataloguedPathsAsync());
    }

    [Fact]
    public async Task NothingOutsideTheLibraryRootCanBeReachedByAnyOfIt()
    {
        WriteFile("Brahms/Symphony 1/01 Allegro.flac");

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            Files().TrashAsync(["../escape"], TestContext.Current.CancellationToken));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            Files().ListAsync("../", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AnUploadedAlbumKeepsItsDiscFoldersAndTheScanFindsIt()
    {
        var files = Files();

        await files.SaveAsync(
            "Brahms/Symphony No. 1",
            "CD1/01 Allegro.flac",
            new MemoryStream("audio"u8.ToArray()),
            5,
            TestContext.Current.CancellationToken);

        Assert.True(File.Exists(
            Path.Combine(_root, "Brahms", "Symphony No. 1", "CD1", "01 Allegro.flac")));

        // No staging file left behind: the swap is what makes a socket closing
        // mid-upload leave nothing for the walk to catalogue.
        Assert.Empty(Directory.EnumerateFiles(_root, "*.tmp", SearchOption.AllDirectories));

        var summary = await ScanAsync();

        Assert.Equal(1, summary.Added);
    }

    [Fact]
    public async Task AnUploadNeverOverwritesWhatIsAlreadyHeld()
    {
        WriteFile("Brahms/Symphony No. 1/01 Allegro.flac", "the good rip");

        await Assert.ThrowsAsync<IOException>(() => Files().SaveAsync(
            "Brahms/Symphony No. 1",
            "01 Allegro.flac",
            new MemoryStream("something else"u8.ToArray()),
            14,
            TestContext.Current.CancellationToken));

        Assert.Equal(
            "the good rip",
            await File.ReadAllTextAsync(
                Path.Combine(_root, "Brahms", "Symphony No. 1", "01 Allegro.flac"),
                TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// <b>The upload must not rename the folder it is uploading into.</b>
    /// </summary>
    /// <remarks>
    /// <c>StagedFileName.Segment</c> drops <c>:</c>, and seven album folders in
    /// the target library have one — including the duplicated Brahms folder this
    /// screen exists to resolve. Run over the browsed prefix as well as over the
    /// file's own name, an upload into
    /// <c>Essential Brahms, Volume 1: 50 Tracks…</c> lands in a new sibling with
    /// the colon removed: a third duplicate, created silently by the feature
    /// meant to remove the second.
    /// </remarks>
    [Fact]
    public async Task UploadingIntoAFolderWhoseNameSegmentWouldRewriteUsesTheFolderThatIsThere()
    {
        const string folder = "Johannes Brahms/Essential Brahms, Volume 1: 50 Tracks";

        WriteFile($"{folder}/01 Allegro.flac");

        await Files().SaveAsync(
            folder,
            "02 Andante.flac",
            new MemoryStream("audio"u8.ToArray()),
            5,
            TestContext.Current.CancellationToken);

        var landed = Path.Combine(
            _root, "Johannes Brahms", "Essential Brahms, Volume 1: 50 Tracks", "02 Andante.flac");

        Assert.True(File.Exists(landed));

        // And no sibling was invented on the way.
        Assert.Equal(
            ["Essential Brahms, Volume 1: 50 Tracks"],
            Directory.EnumerateDirectories(Path.Combine(_root, "Johannes Brahms"))
                .Select(Path.GetFileName)
                .ToArray());
    }

    /// <summary>The file's own name is still sanitised — it is the half a browser sent.</summary>
    [Fact]
    public async Task AnUploadedNameThatWouldEscapeTheFolderIsFlattenedIntoIt()
    {
        await Files().SaveAsync(
            "Brahms",
            "../../etc/passwd",
            new MemoryStream("audio"u8.ToArray()),
            5,
            TestContext.Current.CancellationToken);

        Assert.True(File.Exists(Path.Combine(_root, "Brahms", "etc", "passwd")));
    }

    /// <summary>
    /// A folder with no home yet is the ordinary destination, since there is no
    /// "create folder" on the screen.
    /// </summary>
    [Fact]
    public async Task AFolderCanBeMovedSomewhereThatDoesNotExistYet()
    {
        WriteFile("Brahms/Symphony 1/01 Allegro.flac");
        await ScanAsync();

        var move = await Files().MoveAsync(
            "Brahms/Symphony 1",
            "Classical/Johannes Brahms/Symphony No. 1",
            TestContext.Current.CancellationToken);

        Assert.True(move.Applied);
        Assert.Equal(
            ["Classical/Johannes Brahms/Symphony No. 1/01 Allegro.flac"],
            await CataloguedPathsAsync());
    }

    /// <summary>
    /// One entry refusing must not report the batch as a failure.
    /// </summary>
    /// <remarks>
    /// The row deletes autocommit, so a throw part-way used to escape with the
    /// earlier albums already in the trash and their rows already gone — while
    /// the endpoint answered "the move failed" and nothing was journalled. Being
    /// told nothing happened when two albums have gone is worse than either
    /// outcome on its own.
    /// </remarks>
    [Fact]
    public async Task AnEntryThatWillNotMoveDoesNotTakeTheRestOfTheBatchWithIt()
    {
        WriteFile("Brahms/Symphony 1/01 Allegro.flac");
        WriteFile("Mahler/Symphony 2/01 Allegro.flac");

        await ScanAsync();

        var trashed = await Files().TrashAsync(
            ["Brahms/Symphony 1", "Brahms/Not There", "Mahler/Symphony 2"],
            TestContext.Current.CancellationToken);

        Assert.True(trashed.Applied);
        Assert.Equal(2, trashed.Entries);
        Assert.Empty(await CataloguedPathsAsync());
    }

    [Fact]
    public async Task TheListingShowsWhatIsOnDiskAndWhatTheCatalogueMadeOfIt()
    {
        WriteFile("Brahms/Symphony 1 (mess)/01 Allegro.mp3");
        WriteFile("Brahms/Symphony 1 (mess)/02 Andante.mp3");
        WriteFile("Brahms/Symphony No. 1/01 Allegro.flac");
        WriteFile("Brahms/cover.jpg");

        await ScanAsync();
        await IdentifyByHandAsync("Brahms/Symphony No. 1/01 Allegro.flac");

        var listing = await Files().ListAsync("Brahms", TestContext.Current.CancellationToken);

        Assert.True(listing.Exists);
        Assert.Equal(string.Empty, listing.Parent);

        // Folders first, then the artwork — which the catalogue cannot see at
        // all, and which is exactly why this screen reads the disk.
        Assert.Equal(
            ["Symphony 1 (mess)", "Symphony No. 1", "cover.jpg"],
            listing.Entries.Select(entry => entry.Name).ToArray());

        var mess = listing.Entries.Single(entry => entry.Name == "Symphony 1 (mess)");
        var good = listing.Entries.Single(entry => entry.Name == "Symphony No. 1");
        var art = listing.Entries.Single(entry => entry.Name == "cover.jpg");

        Assert.Equal(2, mess.CataloguedFiles);
        Assert.Equal(0, mess.Identified);

        Assert.Equal(1, good.CataloguedFiles);
        Assert.Equal(1, good.Identified);

        // 'Brahms/Symphony No. 1' must not have collected the sibling that
        // shares its prefix — the trailing slash, measured.
        Assert.Equal(1, good.CataloguedFiles);

        Assert.False(art.IsDirectory);
        Assert.False(art.IsAudio);
        Assert.Equal(0, art.CataloguedFiles);
    }

    /// <summary>Stands in for the identification pass, whose inputs are not the subject here.</summary>
    private async Task IdentifyByHandAsync(string? onlyPath = null)
    {
        await using var db = PostgresFixture.CreateContext(_connectionString);

        var rows = await db.MediaFiles
            .Where(file => onlyPath == null || file.Path == onlyPath)
            .ToListAsync(TestContext.Current.CancellationToken);

        foreach (var row in rows)
        {
            row.AcoustId = Cluster;
            row.AcoustIdCheckedUtc = DateTimeOffset.UtcNow;
            row.AcoustIdOutcome = AcoustIdOutcome.Identified;
        }

        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private FileManagerService Files() =>
        (_services ??= BuildServices()).GetRequiredService<IServiceScopeFactory>()
            .CreateScope().ServiceProvider.GetRequiredService<FileManagerService>();

    private async Task<LibraryScanSummary> ScanAsync()
    {
        var outcome = await (_services ??= BuildServices())
            .GetRequiredService<LibraryScanService>()
            .ScanAsync(TestContext.Current.CancellationToken);

        Assert.Equal(LibraryScanStatus.Completed, outcome.Status);
        Assert.NotNull(outcome.Summary);

        return outcome.Summary;
    }

    private ServiceProvider BuildServices()
    {
        var services = new ServiceCollection();

        services.AddLogging();
        services.AddDbContext<FonotecaDbContext>(options => options.UseNpgsql(_connectionString));
        services.AddSingleton<IClock, SystemClock>();
        services.AddScoped<ICallerContext, SingleUserCallerContext>();
        services.AddScoped<IEventLog, EventLog>();
        services.AddSingleton(new FileSystemAudioFileStore(_root));
        services.AddSingleton<IAudioFileStore>(
            sp => sp.GetRequiredService<FileSystemAudioFileStore>());
        services.AddSingleton<LibraryScanner>();
        services.AddSingleton<LibraryScanService>();
        services.AddSingleton<LibraryWorkGate>();
        services.AddScoped<FileManagerService>();
        services.AddSingleton(Options.Create(new FonotecaOptions
        {
            LibraryPath = _root,
            TrashPath = _trash,
        }));

        var provider = services.BuildServiceProvider();
        _providers.Add(provider);
        return provider;
    }

    private string WriteFile(string relativePath, string content = "not really audio")
    {
        var absolute = Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(absolute)!);
        File.WriteAllText(absolute, content);
        return absolute;
    }

    private async Task<List<string>> CataloguedPathsAsync()
    {
        await using var db = PostgresFixture.CreateContext(_connectionString);

        return await db.MediaFiles
            .AsNoTracking()
            .Select(file => file.Path)
            .OrderBy(path => path)
            .ToListAsync(TestContext.Current.CancellationToken);
    }

    private async Task<int> CountAsync()
    {
        await using var db = PostgresFixture.CreateContext(_connectionString);

        return await db.MediaFiles.CountAsync(TestContext.Current.CancellationToken);
    }
}
