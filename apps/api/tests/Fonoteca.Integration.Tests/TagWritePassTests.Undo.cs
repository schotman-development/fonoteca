using Fonoteca.Api.Library;
using Fonoteca.Data;
using Fonoteca.Domain.Abstractions;
using Fonoteca.Domain.Catalogue;
using Fonoteca.Domain.Events;
using Fonoteca.Ingest;
using Fonoteca.Tagging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Fonoteca.Integration.Tests;

/// <summary>
/// Undo: one album folder stepped back by one edit to its files.
/// </summary>
/// <remarks>
/// Real files and real PostgreSQL for the same reason as the pass itself: an
/// undo is a tag write, renames and a reopen, and each of those fails at runtime
/// rather than at compile time — the moved rows most of all, which the next scan
/// would otherwise read as a library that vanished and arrived.
/// </remarks>
public sealed partial class TagWritePassTests
{
    private const string Album = "Miles Davis/Kind of Blue";

    private static readonly Guid Cluster = new("55555555-5555-4555-8555-555555555555");

    [Fact]
    public async Task UndoingATagWritePutsTheTagsBackAndReopensTheFolder()
    {
        SkipWithoutTools();
        await SeedAsync(($"{Album}/01 track.flac", 1));

        var services = Build();
        var before = await TagsAsync($"{Album}/01 track.flac");

        Assert.Equal(1, (await RunAsync(TagWriteScope.Library, services)).Written);

        var undone = await UndoAsync(services, Album);

        Assert.Equal(TagUndoStatus.Undone, undone.Status);
        Assert.Equal(FolderEditKind.TagWrite, undone.Edit!.Kind);
        Assert.Equal(1, undone.Restored);
        Assert.Equal(1, undone.Reopened);
        Assert.Empty(undone.Problems);

        Assert.Equal(before.Fields, (await TagsAsync($"{Album}/01 track.flac")).Fields);

        await using (var db = PostgresFixture.CreateContext(_connectionString))
        {
            var row = await db.MediaFiles.SingleAsync(Token);

            Assert.Null(row.RecordingId);
            Assert.Null(row.ReleaseGroupId);
            Assert.Equal(AcoustIdOutcome.ReopenedByPerson, row.AcoustIdOutcome);
        }

        // The size and mtime went in with the write, so the scan sees nothing new,
        // and the reopened folder is off the tag write's worklist.
        var rescan = await services.GetRequiredService<LibraryScanService>().ScanAsync(Token);
        Assert.Equal(0, rescan.Summary!.Updated);
        Assert.Equal(0, (await RunAsync(TagWriteScope.Library, services)).Examined);

        Assert.Equal(TagUndoStatus.NothingToUndo, (await UndoAsync(services, Album)).Status);
    }

    [Fact]
    public async Task UndoingARenameMovesTheAlbumAndItsFilesBack()
    {
        SkipWithoutTools();
        await SeedAsync(("Miles Davis/kob rip/CD1/track one.flac", 1));
        await File.WriteAllTextAsync(Path.Combine(_root, "Miles Davis", "kob rip", "cover.jpg"), "sleeve", Token);
        await File.WriteAllTextAsync(Path.Combine(_root, "Miles Davis", "kob rip", "CD1", "track one.lrc"), "lyrics", Token);

        var services = Build(fileNaming: FileNaming.DefaultPattern);
        Assert.Equal(1, (await RunAsync(TagWriteScope.Library, services)).Renamed);
        Assert.True(File.Exists(Path.Combine(_root, "Miles Davis/Kind of Blue (1959)/01 - So What.lrc")));

        var undone = await UndoAsync(services, "Miles Davis/Kind of Blue (1959)");

        Assert.Equal(TagUndoStatus.Undone, undone.Status);
        Assert.Equal("Miles Davis/kob rip", undone.Folder);
        Assert.Empty(undone.Problems);

        Assert.True(File.Exists(Path.Combine(_root, "Miles Davis/kob rip/CD1/track one.flac")));
        Assert.True(File.Exists(Path.Combine(_root, "Miles Davis/kob rip/CD1/track one.lrc")));
        Assert.True(File.Exists(Path.Combine(_root, "Miles Davis/kob rip/cover.jpg")));
        Assert.False(Directory.Exists(Path.Combine(_root, "Miles Davis/Kind of Blue (1959)")));

        await using (var db = PostgresFixture.CreateContext(_connectionString))
        {
            Assert.Equal("Miles Davis/kob rip/CD1/track one.flac", (await db.MediaFiles.SingleAsync(Token)).Path);
        }

        var rescan = await services.GetRequiredService<LibraryScanService>().ScanAsync(Token);
        Assert.Equal(0, rescan.Summary!.Added);
        Assert.Equal(0, rescan.Summary.Removed);
        Assert.Equal(0, rescan.Summary.Updated);
    }

    [Fact]
    public async Task AnOldNameSinceTakenKeepsTheAlbumWhereItIs()
    {
        SkipWithoutTools();
        await SeedAsync(("Miles Davis/kob rip/track one.flac", 1));

        var services = Build(fileNaming: FileNaming.DefaultPattern);
        Assert.Equal(1, (await RunAsync(TagWriteScope.Library, services)).Renamed);

        Directory.CreateDirectory(Path.Combine(_root, "Miles Davis", "kob rip"));

        var undone = await UndoAsync(services, "Miles Davis/Kind of Blue (1959)");

        Assert.Equal(TagUndoStatus.Undone, undone.Status);
        Assert.Equal("Miles Davis/Kind of Blue (1959)", undone.Folder);
        Assert.Contains(undone.Problems, problem => problem.Contains("taken", StringComparison.Ordinal));
        Assert.True(File.Exists(Path.Combine(_root, "Miles Davis/Kind of Blue (1959)/track one.flac")));
    }

    [Fact]
    public async Task UndoRemovesTheLinkTheWriteMade()
    {
        SkipWithoutTools();
        await SeedAsync(("Miles Davis, John Coltrane/Kind of Blue/01.flac", 1));
        await BillColtraneAsync();

        var services = Build(fileNaming: FileNaming.DefaultPattern);
        Assert.Equal(1, (await RunAsync(TagWriteScope.Library, services)).Linked);

        var link = Path.Combine(_root, "John Coltrane", "Kind of Blue (1959)");
        Assert.NotNull(new DirectoryInfo(link).LinkTarget);

        var undone = await UndoAsync(services, "Miles Davis/Kind of Blue (1959)");

        Assert.Equal(1, undone.Unlinked);
        Assert.Equal("Miles Davis, John Coltrane/Kind of Blue", undone.Folder);
        Assert.False(Directory.Exists(link) || File.Exists(link));
        Assert.True(File.Exists(Path.Combine(_root, "Miles Davis, John Coltrane/Kind of Blue/01.flac")));
    }

    [Fact]
    public async Task UndoTrashesTheSleeveItWroteAndPutsBackTheOneItDisplaced()
    {
        SkipWithoutTools();
        await SeedAsync(($"{Album}/01 track.flac", 1));
        await SeedCoverAsync(ChosenCover);

        var stale = new byte[] { 0x89, 0x50, 0x4E, 0x47, 4, 5, 6 };
        await File.WriteAllBytesAsync(Path.Combine(_root, Album, "cover.png"), stale, Token);

        var services = Build();
        Assert.Equal(1, (await RunAsync(TagWriteScope.Library, services)).CoversWritten);

        var undone = await UndoAsync(services, Album);

        Assert.Equal(1, undone.Covers);
        Assert.Equal(
            ["cover.png"],
            Directory.EnumerateFiles(Path.Combine(_root, Album), "cover.*").Select(Path.GetFileName).ToArray());
        Assert.Equal(stale, await File.ReadAllBytesAsync(Path.Combine(_root, Album, "cover.png"), Token));

        // Trashed, never deleted.
        Assert.Single(Directory.EnumerateFiles(_root + "-trash", "cover.jpg", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task UndoTrashesTheMotionArtworkItWroteAndPutsBackWhatItDisplaced()
    {
        SkipWithoutTools();
        await SeedAsync(($"{Album}/01 track.flac", 1));
        await SeedMotionAsync(SquareVideo, TallVideo);

        var stale = new byte[] { 0, 0, 0, 0x18, (byte)'f', (byte)'t', (byte)'y', (byte)'p', 9 };
        await File.WriteAllBytesAsync(Path.Combine(_root, Album, "square_animated_artwork.mp4"), stale, Token);

        var services = Build();
        Assert.Equal(1, (await RunAsync(TagWriteScope.Library, services)).MotionWritten);

        // The trash is stamped to the second, and the video the write displaced
        // has the name the undo trashes; a press in the same second would collide.
        await Task.Delay(TimeSpan.FromSeconds(1.1), Token);

        var undone = await UndoAsync(services, Album);

        Assert.Equal(TagUndoStatus.Undone, undone.Status);
        Assert.Empty(undone.Problems);
        Assert.Equal(
            ["square_animated_artwork.mp4"],
            Directory.EnumerateFiles(Path.Combine(_root, Album), "*.mp4").Select(Path.GetFileName).ToArray());
        Assert.Equal(stale, await File.ReadAllBytesAsync(Path.Combine(_root, Album, "square_animated_artwork.mp4"), Token));

        // Trashed, never deleted.
        Assert.Single(Directory.EnumerateFiles(_root + "-trash", "tall_animated_artwork.mp4", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task UndoTrashesTheBookletItWrote()
    {
        SkipWithoutTools();
        await SeedAsync(($"{Album}/01 track.flac", 1));
        await SeedBookletAsync();

        var services = Build();
        Assert.Equal(1, (await RunAsync(TagWriteScope.Library, services)).BookletsWritten);

        var undone = await UndoAsync(services, Album);

        Assert.Equal(TagUndoStatus.Undone, undone.Status);
        Assert.Empty(undone.Problems);
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(_root, Album), "booklet*"));

        // Trashed, never deleted.
        Assert.Single(Directory.EnumerateFiles(_root + "-trash", "booklet.pdf", SearchOption.AllDirectories));
        Assert.Single(Directory.EnumerateFiles(_root + "-trash", "booklet-02.png", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task AnEditThatOnlyWroteBookletsIsTheOneUndone()
    {
        SkipWithoutTools();
        await SeedAsync(($"{Album}/01 track.flac", 1));

        var services = Build();
        Assert.Equal(1, (await RunAsync(TagWriteScope.Library, services)).Written);
        var tagged = await TagsAsync($"{Album}/01 track.flac");

        await SeedBookletAsync();

        var booklets = await RunAsync(TagWriteScope.Library, services);
        Assert.Equal(0, booklets.Written);
        Assert.Equal(1, booklets.BookletsWritten);

        var undone = await UndoAsync(services, Album);

        Assert.Equal(TagUndoStatus.Undone, undone.Status);
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(_root, Album), "booklet*"));
        Assert.Equal(tagged.Fields, (await TagsAsync($"{Album}/01 track.flac")).Fields);
    }

    [Fact]
    public async Task AnEditThatOnlyWroteMotionArtworkIsTheOneUndone()
    {
        SkipWithoutTools();
        await SeedAsync(($"{Album}/01 track.flac", 1));

        var services = Build();
        Assert.Equal(1, (await RunAsync(TagWriteScope.Library, services)).Written);
        var tagged = await TagsAsync($"{Album}/01 track.flac");

        await SeedMotionAsync(SquareVideo, TallVideo);

        var videos = await RunAsync(TagWriteScope.Library, services);
        Assert.Equal(0, videos.Written);
        Assert.Equal(1, videos.MotionWritten);

        var undone = await UndoAsync(services, Album);

        Assert.Equal(TagUndoStatus.Undone, undone.Status);
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(_root, Album), "*.mp4"));
        Assert.Equal(tagged.Fields, (await TagsAsync($"{Album}/01 track.flac")).Fields);
    }

    [Fact]
    public async Task EachPressStepsOneEditFurtherBack()
    {
        SkipWithoutTools();
        await SeedAsync(($"{Album}/01 track.flac", 1));

        var services = Build();
        var untouched = await TagsAsync($"{Album}/01 track.flac");

        await IdentifyAsync(services, $"{Album}/01 track.flac");
        var identified = await TagsAsync($"{Album}/01 track.flac");

        Assert.Equal(1, (await RunAsync(TagWriteScope.Library, services)).Written);

        var first = await UndoAsync(services, Album);
        Assert.Equal(FolderEditKind.TagWrite, first.Edit!.Kind);
        Assert.Equal(identified.Fields, (await TagsAsync($"{Album}/01 track.flac")).Fields);

        var second = await UndoAsync(services, Album);
        Assert.Equal(FolderEditKind.Identification, second.Edit!.Kind);
        Assert.Equal(0, second.Reopened);
        Assert.Equal(untouched.Fields, (await TagsAsync($"{Album}/01 track.flac")).Fields);

        await using (var db = PostgresFixture.CreateContext(_connectionString))
        {
            Assert.Null((await db.MediaFiles.SingleAsync(Token)).AcoustIdTaggedUtc);
        }

        Assert.Equal(TagUndoStatus.NothingToUndo, (await UndoAsync(services, Album)).Status);
    }

    [Fact]
    public async Task ASecondTagWriteIsUndoneBeforeTheFirst()
    {
        SkipWithoutTools();
        await SeedAsync(($"{Album}/01 track.flac", 1));

        var services = Build();
        var untouched = await TagsAsync($"{Album}/01 track.flac");

        Assert.Equal(1, (await RunAsync(TagWriteScope.Library, services)).Written);

        await using (var db = PostgresFixture.CreateContext(_connectionString))
        {
            await db.Tracks.ExecuteUpdateAsync(set => set.SetProperty(track => track.Title, "So What?"), Token);
        }

        Assert.Equal(1, (await RunAsync(TagWriteScope.Library, services)).Written);
        Assert.Equal("So What?", (await TagsAsync($"{Album}/01 track.flac")).Fields["TITLE"]);

        await UndoAsync(services, Album);
        Assert.Equal("So What", (await TagsAsync($"{Album}/01 track.flac")).Fields["TITLE"]);

        await UndoAsync(services, Album);
        Assert.Equal(untouched.Fields, (await TagsAsync($"{Album}/01 track.flac")).Fields);
    }

    [Fact]
    public async Task UndoPutsATagBackEvenWhereAnotherTaggerChangedItSince()
    {
        SkipWithoutTools();
        await SeedAsync(($"{Album}/01 track.flac", 1));

        var services = Build();
        var untouched = await TagsAsync($"{Album}/01 track.flac");

        Assert.Equal(1, (await RunAsync(TagWriteScope.Library, services)).Written);

        using (var scope = services.CreateScope())
        {
            var writer = scope.ServiceProvider.GetRequiredService<TagWriter>();
            var path = new LibraryPath($"{Album}/01 track.flac");
            var plan = await writer.PlanAsync(path, new Dictionary<string, string> { [CatalogueTags.Title] = "Mine" }, Token);

            Assert.Equal(
                TagWriteStatus.Written,
                (await writer.ApplyAsync(plan, "elsewhere", "another-tagger", "someone", "test.outside", Token)).Status);
        }

        var undone = await UndoAsync(services, Album);

        Assert.Empty(undone.Problems);
        Assert.Equal(untouched.Fields, (await TagsAsync($"{Album}/01 track.flac")).Fields);
    }

    [Fact]
    public async Task WithFileWritingOffNothingIsUndone()
    {
        SkipWithoutTools();
        await SeedAsync(($"{Album}/01 track.flac", 1));

        Assert.Equal(1, (await RunAsync(TagWriteScope.Library)).Written);

        var written = await TagsAsync($"{Album}/01 track.flac");
        var undone = await UndoAsync(Build(allowMutation: false), Album);

        Assert.Equal(TagUndoStatus.MutationOff, undone.Status);
        Assert.Equal(written.Fields, (await TagsAsync($"{Album}/01 track.flac")).Fields);

        await using var db = PostgresFixture.CreateContext(_connectionString);
        Assert.NotNull((await db.MediaFiles.SingleAsync(Token)).RecordingId);
    }

    [Fact]
    public async Task AnArtistFolderIsNotAnAlbumFolder()
    {
        SkipWithoutTools();
        await SeedAsync(($"{Album}/01 track.flac", 1));

        var services = Build();
        Assert.Equal(1, (await RunAsync(TagWriteScope.Library, services)).Written);

        Assert.Equal(TagUndoStatus.NotAnAlbumFolder, (await UndoAsync(services, "Miles Davis")).Status);
        Assert.Null(await services.GetRequiredService<TagWriteService>().LastEditAsync("Miles Davis", Token));
        Assert.NotNull(await services.GetRequiredService<TagWriteService>().LastEditAsync(Album, Token));
    }

    [Fact]
    public async Task AnAgentsUndoIsRecordedAsTheAgents()
    {
        SkipWithoutTools();
        await SeedAsync(("Miles Davis/kob rip/track one.flac", 1));

        var services = Build(fileNaming: FileNaming.DefaultPattern);
        Assert.Equal(1, (await RunAsync(TagWriteScope.Library, services)).Renamed);

        var undone = await services.GetRequiredService<TagWriteService>()
            .UndoAsync("Miles Davis/Kind of Blue (1959)", new AgentCallerContext(), Token);

        Assert.Equal(TagUndoStatus.Undone, undone.Status);
        Assert.Equal(2, undone.Moved);

        await using var db = PostgresFixture.CreateContext(_connectionString);

        Assert.Equal(AcoustIdOutcome.ReopenedByAgent, (await db.MediaFiles.SingleAsync(Token)).AcoustIdOutcome);
        Assert.All(
            await db.DomainEvents.Where(entry => entry.Type.StartsWith(TagWriteService.UndoPrefix)).ToListAsync(Token),
            entry => Assert.Equal(AgentCallerContext.AgentId, entry.ActorId));
    }

    [Fact]
    public async Task AFileThatWillNotTakeItsTagsBackStopsTheUndoForAnotherTry()
    {
        SkipWithoutTools();
        await SeedAsync(($"{Album}/01 track.flac", 1));

        var services = Build();
        var untouched = await TagsAsync($"{Album}/01 track.flac");

        Assert.Equal(1, (await RunAsync(TagWriteScope.Library, services)).Written);

        var folder = Path.Combine(_root, Album);
        var mode = File.GetUnixFileMode(folder);

        // No staged sibling can be made beside the file.
        File.SetUnixFileMode(folder, UnixFileMode.UserRead | UnixFileMode.UserExecute);

        TagUndoResult stopped;

        try
        {
            stopped = await UndoAsync(services, Album);
        }
        finally
        {
            File.SetUnixFileMode(folder, mode);
        }

        Assert.Equal(TagUndoStatus.Incomplete, stopped.Status);
        Assert.Single(stopped.Problems);
        Assert.Equal("So What", (await TagsAsync($"{Album}/01 track.flac")).Fields["TITLE"]);

        await using (var db = PostgresFixture.CreateContext(_connectionString))
        {
            Assert.NotNull((await db.MediaFiles.SingleAsync(Token)).RecordingId);
        }

        // Still the edit to undo, and undone once the file can be written.
        Assert.NotNull(await services.GetRequiredService<TagWriteService>().LastEditAsync(Album, Token));
        Assert.Equal(TagUndoStatus.Undone, (await UndoAsync(services, Album)).Status);
        Assert.Equal(untouched.Fields, (await TagsAsync($"{Album}/01 track.flac")).Fields);
    }

    [Fact]
    public async Task AFolderNotOnDiskIsNotTouched()
    {
        SkipWithoutTools();
        await SeedAsync(($"{Album}/01 track.flac", 1));

        var services = Build();
        Assert.Equal(1, (await RunAsync(TagWriteScope.Library, services)).Written);

        Directory.Move(Path.Combine(_root, Album), Path.Combine(_root, "unmounted"));

        Assert.Equal(TagUndoStatus.NotOnDisk, (await UndoAsync(services, Album)).Status);

        await using var db = PostgresFixture.CreateContext(_connectionString);
        Assert.NotNull((await db.MediaFiles.SingleAsync(Token)).RecordingId);
        Assert.False(await db.DomainEvents.AnyAsync(entry => entry.Type.StartsWith(TagWriteService.UndoPrefix), Token));
    }

    [Fact]
    public async Task AnotherRipNowWhereTheWritePutAnAlbumIsNotUndone()
    {
        SkipWithoutTools();
        await SeedAsync(("Miles Davis/kob rip/track one.flac", 1));

        var services = Build(fileNaming: FileNaming.DefaultPattern);
        Assert.Equal(1, (await RunAsync(TagWriteScope.Library, services)).Renamed);

        const string renamed = "Miles Davis/Kind of Blue (1959)";

        // The album moved away by hand, and another rip put where it was.
        Directory.Move(Path.Combine(_root, renamed), Path.Combine(_root, "Miles Davis", "Away"));

        await using (var db = PostgresFixture.CreateContext(_connectionString))
        {
            var moved = await db.MediaFiles.SingleAsync(Token);
            moved.Path = "Miles Davis/Away/01 - So What.flac";

            db.MediaFiles.Add(Copy(
                $"{renamed}/other.flac", moved.RecordingId!.Value, moved.ReleaseId, moved.ReleaseGroupId, moved.TrackId));

            await db.SaveChangesAsync(Token);
        }

        Assert.Null(await services.GetRequiredService<TagWriteService>().LastEditAsync(renamed, Token));
        Assert.Equal(TagUndoStatus.NothingToUndo, (await UndoAsync(services, renamed)).Status);
        Assert.True(File.Exists(Path.Combine(_root, renamed, "other.flac")));

        await using (var db = PostgresFixture.CreateContext(_connectionString))
        {
            Assert.NotNull((await db.MediaFiles.SingleAsync(file => file.Path == $"{renamed}/other.flac", Token)).RecordingId);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnotherAlbumMovedByTheSameRunIsNotThisFoldersToUndo(bool olderJournal)
    {
        SkipWithoutTools();
        await SeedAsync(("Miles Davis/kob rip/a.flac", 1), ("Miles Davis/kob rip 2/b.flac", 2));

        var services = Build(fileNaming: FileNaming.DefaultPattern);
        await RunAsync(TagWriteScope.Library, services);

        // Moves and links journalled before they named their files.
        if (olderJournal) await ForgetFilesAsync();

        const string renamed = "Miles Davis/Kind of Blue (1959)";
        Assert.True(Directory.Exists(Path.Combine(_root, renamed)));
        Assert.True(Directory.Exists(Path.Combine(_root, "Miles Davis", "kob rip 2")));

        // The renamed album moved away by hand, and the second rip put in its place.
        Directory.Move(Path.Combine(_root, renamed), Path.Combine(_root, "Miles Davis", "Away"));
        Directory.Move(Path.Combine(_root, "Miles Davis", "kob rip 2"), Path.Combine(_root, renamed));

        await using (var db = PostgresFixture.CreateContext(_connectionString))
        {
            await db.MediaFiles
                .Where(file => file.Path.StartsWith(renamed + "/"))
                .ExecuteUpdateAsync(set => set.SetProperty(file => file.Path, "Miles Davis/Away/01 - So What.flac"), Token);
            await db.MediaFiles
                .Where(file => file.Path == "Miles Davis/kob rip 2/b.flac")
                .ExecuteUpdateAsync(set => set.SetProperty(file => file.Path, $"{renamed}/b.flac"), Token);
        }

        var undone = await UndoAsync(services, renamed);

        // Its own tag write is undone; the other album's move is not, so it stays.
        Assert.Equal(TagUndoStatus.Undone, undone.Status);
        Assert.Equal(renamed, undone.Folder);
        Assert.Equal(0, undone.Moved);
        Assert.True(File.Exists(Path.Combine(_root, renamed, "b.flac")));
        Assert.False(Directory.Exists(Path.Combine(_root, "Miles Davis", "kob rip")));
    }

    [Fact]
    public async Task ALyricOfAnotherRipAtTheRenamedPathKeepsItsName()
    {
        SkipWithoutTools();
        await SeedAsync(("Miles Davis/kob rip/a.flac", 1), ("Miles Davis/kob rip 2/b.flac", 2));
        await File.WriteAllTextAsync(Path.Combine(_root, "Miles Davis", "kob rip", "a.lrc"), "theirs", Token);

        var services = Build(fileNaming: FileNaming.DefaultPattern);
        await RunAsync(TagWriteScope.Library, services);

        const string renamed = "Miles Davis/Kind of Blue (1959)";
        Assert.True(File.Exists(Path.Combine(_root, renamed, "01 - So What.lrc")));

        Directory.Move(Path.Combine(_root, renamed), Path.Combine(_root, "Miles Davis", "Away"));
        Directory.Move(Path.Combine(_root, "Miles Davis", "kob rip 2"), Path.Combine(_root, renamed));
        await File.WriteAllTextAsync(Path.Combine(_root, renamed, "01 - So What.lrc"), "mine", Token);

        await using (var db = PostgresFixture.CreateContext(_connectionString))
        {
            await db.MediaFiles
                .Where(file => file.Path.StartsWith(renamed + "/"))
                .ExecuteUpdateAsync(set => set.SetProperty(file => file.Path, "Miles Davis/Away/01 - So What.flac"), Token);
            await db.MediaFiles
                .Where(file => file.Path == "Miles Davis/kob rip 2/b.flac")
                .ExecuteUpdateAsync(set => set.SetProperty(file => file.Path, $"{renamed}/b.flac"), Token);
        }

        Assert.Equal(TagUndoStatus.Undone, (await UndoAsync(services, renamed)).Status);
        Assert.Equal("mine", await File.ReadAllTextAsync(Path.Combine(_root, renamed, "01 - So What.lrc"), Token));
        Assert.False(File.Exists(Path.Combine(_root, renamed, "a.lrc")));
    }

    [Fact]
    public async Task ARunNamesTheFilesOfEveryFolderItMovesAndLinks()
    {
        SkipWithoutTools();
        await SeedAsync(("Miles Davis, John Coltrane/Kind of Blue/01.flac", 1));
        await BillColtraneAsync();

        await RunAsync(TagWriteScope.Library, Build(fileNaming: FileNaming.DefaultPattern));

        await using var db = PostgresFixture.CreateContext(_connectionString);

        var id = (await db.MediaFiles.SingleAsync(Token)).Id.ToString();
        var moved = await db.DomainEvents.SingleAsync(entry => entry.Type == "tagging.catalogue.renamed" && entry.SubjectType == "folder", Token);
        var linked = await db.DomainEvents.SingleAsync(entry => entry.Type == "tagging.catalogue.linked", Token);

        Assert.Contains(id, moved.PayloadJson, StringComparison.Ordinal);
        Assert.Contains(id, linked.PayloadJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFileWhoseOldNameIsTakenKeepsItsNewOneAndNothingIsOverwritten()
    {
        SkipWithoutTools();
        await SeedAsync(("Miles Davis/kob rip/CD1/track one.flac", 1));

        var services = Build(fileNaming: FileNaming.DefaultPattern);
        Assert.Equal(1, (await RunAsync(TagWriteScope.Library, services)).Renamed);

        const string renamed = "Miles Davis/Kind of Blue (1959)";
        Directory.CreateDirectory(Path.Combine(_root, renamed, "CD1"));
        await File.WriteAllTextAsync(Path.Combine(_root, renamed, "CD1", "track one.flac"), "somebody's", Token);

        var undone = await UndoAsync(services, renamed);

        Assert.Contains(undone.Problems, problem => problem.Contains("keeps its new name", StringComparison.Ordinal));
        Assert.Equal("somebody's", await File.ReadAllTextAsync(Path.Combine(_root, "Miles Davis/kob rip/CD1/track one.flac"), Token));
        Assert.True(File.Exists(Path.Combine(_root, "Miles Davis/kob rip/01 - So What.flac")));
    }

    [Fact]
    public async Task AnOldFolderNameTheCatalogueStillHoldsIsTaken()
    {
        SkipWithoutTools();
        await SeedAsync(("Miles Davis/kob rip/track one.flac", 1));

        var services = Build(fileNaming: FileNaming.DefaultPattern);
        Assert.Equal(1, (await RunAsync(TagWriteScope.Library, services)).Renamed);

        // Not on disk, but a row says something lives there.
        await using (var db = PostgresFixture.CreateContext(_connectionString))
        {
            db.MediaFiles.Add(new MediaFile
            {
                Id = MediaFileId.New(),
                Path = "Miles Davis/kob rip/ghost.flac",
                SizeBytes = 1,
                LastModifiedUtc = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync(Token);
        }

        var undone = await UndoAsync(services, "Miles Davis/Kind of Blue (1959)");

        Assert.Equal("Miles Davis/Kind of Blue (1959)", undone.Folder);
        Assert.Contains(undone.Problems, problem => problem.Contains("taken", StringComparison.Ordinal));
    }

    [Fact]
    public async Task OnlyALinkThatPointedHereComesBack()
    {
        SkipWithoutTools();
        await SeedAsync(("Miles Davis, John Coltrane/Kind of Blue/01.flac", 1));
        await BillColtraneAsync();

        var services = Build(fileNaming: FileNaming.DefaultPattern);
        await RunAsync(TagWriteScope.Library, services);

        // One of the pass's own links, pointing at an album since gone: the
        // next run sweeps it away.
        Directory.CreateDirectory(Path.Combine(_root, "Someone"));
        Directory.CreateSymbolicLink(Path.Combine(_root, "Someone", "Gone (1999)"), Path.Combine("..", "Nobody", "Gone (1999)"));

        await using (var db = PostgresFixture.CreateContext(_connectionString))
        {
            db.DomainEvents.Add(DomainEvent.Create(
                "tagging.catalogue.linked", "link", "Someone/Gone (1999)", "owner", DateTimeOffset.UtcNow,
                "{\"link\":\"Someone/Gone (1999)\",\"target\":\"Nobody/Gone (1999)\"}", "earlier-run"));
            await db.SaveChangesAsync(Token);

            await db.Releases.ExecuteUpdateAsync(set => set.SetProperty(release => release.Title, "Kind of Blue (Legacy)"), Token);
            await db.ReleaseGroups.ExecuteUpdateAsync(set => set.SetProperty(album => album.Title, "Kind of Blue (Legacy)"), Token);
        }

        await RunAsync(TagWriteScope.Library, services);
        Assert.Null(new FileInfo(Path.Combine(_root, "Someone", "Gone (1999)")).LinkTarget);

        var undone = await UndoAsync(services, "Miles Davis/Kind of Blue (Legacy) (1959)");

        Assert.Equal(1, undone.Relinked);
        Assert.Null(new FileInfo(Path.Combine(_root, "Someone", "Gone (1999)")).LinkTarget);
    }

    [Fact]
    public async Task ALinkSomebodyMadeWhereTheWriteOnceLinkedStays()
    {
        SkipWithoutTools();
        await SeedAsync(("Miles Davis, John Coltrane/Kind of Blue/01.flac", 1));
        await BillColtraneAsync();

        var services = Build(fileNaming: FileNaming.DefaultPattern);
        await RunAsync(TagWriteScope.Library, services);

        // The pass's link taken away, and a person's put in its place.
        var link = Path.Combine(_root, "John Coltrane", "Kind of Blue (1959)");
        new FileInfo(link).Delete();
        Directory.CreateSymbolicLink(link, Path.Combine("..", "Miles Davis", "Kind of Blue (1959)"));

        await using (var db = PostgresFixture.CreateContext(_connectionString))
        {
            db.DomainEvents.Add(DomainEvent.Create(
                "tagging.catalogue.unlinked", "link", "John Coltrane/Kind of Blue (1959)", "owner", DateTimeOffset.UtcNow,
                "{\"link\":\"John Coltrane/Kind of Blue (1959)\",\"target\":null}", "by-hand"));
            await db.SaveChangesAsync(Token);
        }

        var undone = await UndoAsync(services, "Miles Davis/Kind of Blue (1959)");

        Assert.Equal(0, undone.Unlinked);
        Assert.NotNull(new FileInfo(link).LinkTarget);
    }

    [Fact]
    public async Task AnUndoIsRefusedWhileTheLibraryIsBusy()
    {
        SkipWithoutTools();
        await SeedAsync(($"{Album}/01 track.flac", 1));

        var services = Build();
        Assert.Equal(1, (await RunAsync(TagWriteScope.Library, services)).Written);

        Assert.True(services.GetRequiredService<LibraryWorkGate>().TryEnter("library.scan", out var lease));

        using (lease)
        {
            Assert.Equal(TagUndoStatus.Busy, (await UndoAsync(services, Album)).Status);
        }

        Assert.Equal(TagUndoStatus.Undone, (await UndoAsync(services, Album)).Status);
    }

    /// <summary>
    /// A repaired MusicBrainz id stays repaired: the damage the write took out
    /// is not something an undo puts back.
    /// </summary>
    [Fact]
    public async Task ARepairedMusicBrainzIdIsNotDamagedAgainByTheUndo()
    {
        SkipWithoutTools();
        await SeedAsync(($"{Album}/01.mp3", 1));

        var path = Path.Combine(_root, Album, "01.mp3");

        using (var file = TagLib.File.Create(path))
        {
            var tag = (TagLib.Id3v2.Tag)file.GetTag(TagLib.TagTypes.Id3v2, true);
            tag.AddFrame(new TagLib.Id3v2.UniqueFileIdentifierFrame(
                "\u0003http://musicbrainz.org",
                TagLib.ByteVector.FromString(RecordingMbid.ToString(), TagLib.StringType.Latin1)));
            file.Save();
        }

        var services = Build();
        Assert.Equal(1, (await RunAsync(TagWriteScope.Library, services)).Written);

        var undone = await UndoAsync(services, Album);

        Assert.Equal(TagUndoStatus.Undone, undone.Status);
        Assert.Equal(1, undone.Restored);

        using var after = TagLib.File.Create(path);
        var owners = ((TagLib.Id3v2.Tag)after.GetTag(TagLib.TagTypes.Id3v2, false))
            .GetFrames<TagLib.Id3v2.UniqueFileIdentifierFrame>()
            .Select(frame => frame.Owner)
            .ToList();

        Assert.Equal(["http://musicbrainz.org"], owners);
        Assert.Equal(RecordingMbid.ToString(), after.Tag.MusicBrainzTrackId);
        Assert.Null(after.Tag.Title);
    }

    [Fact]
    public async Task AFillAnUndoPutBackIsStillOursToMove()
    {
        SkipWithoutTools();
        await SeedAsync(($"{Album}/01 track.flac", 1));

        RecordingId recording;
        ReleaseGroupId group;

        await using (var db = PostgresFixture.CreateContext(_connectionString))
        {
            var file = await db.MediaFiles.SingleAsync(Token);
            file.ReleaseId = null;
            file.TrackId = null;
            file.FolderPosition = 1;
            recording = file.RecordingId!.Value;
            group = file.ReleaseGroupId!.Value;
            await db.SaveChangesAsync(Token);
        }

        var services = Build();
        await RunAsync(TagWriteScope.Library, services);
        Assert.Equal("1", (await TagsAsync($"{Album}/01 track.flac")).Fields["TRACKNUMBER"]);

        await PlaceAsync(recording, group, 2);
        await RunAsync(TagWriteScope.Library, services);
        Assert.Equal("2", (await TagsAsync($"{Album}/01 track.flac")).Fields["TRACKNUMBER"]);

        Assert.Equal(TagUndoStatus.Undone, (await UndoAsync(services, Album)).Status);
        Assert.Equal("1", (await TagsAsync($"{Album}/01 track.flac")).Fields["TRACKNUMBER"]);

        // Filed again, and the folder's order moved: the 1 the undo put back
        // was this pass's own fill, so it moves too.
        await PlaceAsync(recording, group, 3);
        await RunAsync(TagWriteScope.Library, services);
        Assert.Equal("3", (await TagsAsync($"{Album}/01 track.flac")).Fields["TRACKNUMBER"]);
    }

    [Fact]
    public async Task ALinkTheWriteTookAwayComesBackWithTheAlbum()
    {
        SkipWithoutTools();
        await SeedAsync(("Miles Davis, John Coltrane/Kind of Blue/01.flac", 1));
        await BillColtraneAsync();

        var services = Build(fileNaming: FileNaming.DefaultPattern);
        Assert.Equal(1, (await RunAsync(TagWriteScope.Library, services)).Linked);

        await using (var db = PostgresFixture.CreateContext(_connectionString))
        {
            await db.Releases.ExecuteUpdateAsync(set => set.SetProperty(release => release.Title, "Kind of Blue (Legacy)"), Token);
            await db.ReleaseGroups.ExecuteUpdateAsync(set => set.SetProperty(album => album.Title, "Kind of Blue (Legacy)"), Token);
        }

        Assert.Equal(1, (await RunAsync(TagWriteScope.Library, services)).Linked);

        var old = new DirectoryInfo(Path.Combine(_root, "John Coltrane", "Kind of Blue (1959)"));
        var made = Path.Combine(_root, "John Coltrane", "Kind of Blue (Legacy) (1959)");
        Assert.False(old.Exists);
        Assert.NotNull(new DirectoryInfo(made).LinkTarget);

        var undone = await UndoAsync(services, "Miles Davis/Kind of Blue (Legacy) (1959)");

        Assert.Equal("Miles Davis/Kind of Blue (1959)", undone.Folder);
        Assert.Equal(1, undone.Unlinked);
        Assert.Equal(1, undone.Relinked);
        Assert.False(Directory.Exists(made) || File.Exists(made));
        Assert.Equal(Path.Combine("..", "Miles Davis", "Kind of Blue (1959)"), new DirectoryInfo(old.FullName).LinkTarget);
    }

    [Fact]
    public async Task ASleeveReplacedSinceIsSomebodysAndStays()
    {
        SkipWithoutTools();
        await SeedAsync(($"{Album}/01 track.flac", 1));
        await SeedCoverAsync(ChosenCover);

        var services = Build();
        Assert.Equal(1, (await RunAsync(TagWriteScope.Library, services)).CoversWritten);

        var mine = new byte[] { 0xFF, 0xD8, 0xFF, 0xDB, 1, 2, 3, 4, 5, 6, 7, 8, 9 };
        await File.WriteAllBytesAsync(Path.Combine(_root, Album, "cover.jpg"), mine, Token);

        var undone = await UndoAsync(services, Album);

        Assert.Equal(0, undone.Covers);
        Assert.Equal(mine, await File.ReadAllBytesAsync(Path.Combine(_root, Album, "cover.jpg"), Token));
    }

    [Fact]
    public async Task AFileMovedSinceStaysWhereItWasPut()
    {
        SkipWithoutTools();
        await SeedAsync(("Miles Davis/kob rip/track one.flac", 1));

        var services = Build(fileNaming: FileNaming.DefaultPattern);
        Assert.Equal(1, (await RunAsync(TagWriteScope.Library, services)).Renamed);

        const string renamed = "Miles Davis/Kind of Blue (1959)";
        File.Move(Path.Combine(_root, renamed, "01 - So What.flac"), Path.Combine(_root, renamed, "mine.flac"));

        await using (var db = PostgresFixture.CreateContext(_connectionString))
        {
            await db.MediaFiles.ExecuteUpdateAsync(set => set.SetProperty(file => file.Path, $"{renamed}/mine.flac"), Token);
        }

        var undone = await UndoAsync(services, renamed);

        Assert.Equal(TagUndoStatus.Undone, undone.Status);
        Assert.Contains(undone.Problems, problem => problem.Contains("moved since", StringComparison.Ordinal));
        Assert.True(File.Exists(Path.Combine(_root, "Miles Davis/kob rip/mine.flac")));
    }

    [Fact]
    public async Task ATotalUnderTwoKeysSurvivesTheUndo()
    {
        SkipWithoutTools();
        await SeedAsync(($"{Album}/01 track.flac", 1));

        var path = Path.Combine(_root, Album, "01 track.flac");
        var doubled = path + ".tmp.flac";

        using (var ffmpeg = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
            "ffmpeg",
            ["-v", "error", "-y", "-i", path, "-c", "copy", "-metadata", "TRACKTOTAL=18", "-metadata", "TOTALTRACKS=18", doubled])))
        {
            await ffmpeg!.WaitForExitAsync(Token);
            Assert.Equal(0, ffmpeg.ExitCode);
        }

        File.Move(doubled, path, overwrite: true);

        var services = Build();
        Assert.Equal(1, (await RunAsync(TagWriteScope.Library, services)).Written);

        using (var written = TagLib.File.Create(path))
        {
            Assert.Equal(5u, written.Tag.TrackCount);
        }

        var result = await UndoAsync(services, Album);
        Assert.True(result.Status == TagUndoStatus.Undone, string.Join("; ", result.Problems));

        // The total back, and the number kept for it: ATL writes no total without one.
        using (var undone = TagLib.File.Create(path))
        {
            Assert.Equal(18u, undone.Tag.TrackCount);
            Assert.Equal(1u, undone.Tag.Track);
        }

        Assert.Contains(result.Problems, problem => problem.Contains("kept TRACKNUMBER", StringComparison.Ordinal));
    }

    /// <summary>
    /// An entry from before totals were journalled as TagLib# reads them says
    /// "none" where ATL failed to read one, so its total is not removed.
    /// </summary>
    [Fact]
    public async Task AnOlderEntrysMissingTotalIsNotTakenAtItsWord()
    {
        SkipWithoutTools();
        await SeedAsync(($"{Album}/01 track.flac", 1));

        // The rip's own number, so the total cannot go with it.
        var path = Path.Combine(_root, Album, "01 track.flac");

        using (var ffmpeg = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
            "ffmpeg", ["-v", "error", "-y", "-i", path, "-c", "copy", "-metadata", "TRACKNUMBER=3", path + ".tmp.flac"])))
        {
            await ffmpeg!.WaitForExitAsync(Token);
            Assert.Equal(0, ffmpeg.ExitCode);
        }

        File.Move(path + ".tmp.flac", path, overwrite: true);

        var services = Build();
        Assert.Equal(1, (await RunAsync(TagWriteScope.Library, services)).Written);

        await using (var db = PostgresFixture.CreateContext(_connectionString))
        {
            var entry = await db.DomainEvents.SingleAsync(entry => entry.Type == "tagging.catalogue.written", Token);
            var payload = System.Text.Json.JsonSerializer.Deserialize(entry.PayloadJson, TaggingJson.Default.TagWritePayload)!;

            Assert.True(payload.TotalsVerified);
            Assert.Contains(payload.Changes, change => change.Field == CatalogueTags.TrackTotal && change.Previous is null);

            var older = System.Text.Json.JsonSerializer.Serialize(payload with { TotalsVerified = false }, TaggingJson.Default.TagWritePayload);

            await db.DomainEvents
                .Where(candidate => candidate.Id == entry.Id)
                .ExecuteUpdateAsync(set => set.SetProperty(candidate => candidate.PayloadJson, older), Token);
        }

        var result = await UndoAsync(services, Album);
        Assert.True(result.Status == TagUndoStatus.Undone, string.Join("; ", result.Problems));

        var undone = await TagsAsync($"{Album}/01 track.flac");
        Assert.Equal("3", undone.Fields["TRACKNUMBER"]);
        Assert.Equal("5", undone.Fields["TRACKTOTAL"]);
        Assert.False(undone.Fields.ContainsKey("TITLE"));
    }

    /// <summary>Every folder move and link as the journal held them before they named their files.</summary>
    private async Task ForgetFilesAsync()
    {
        await using var db = PostgresFixture.CreateContext(_connectionString);

        await db.Database.ExecuteSqlRawAsync(
            "UPDATE \"DomainEvents\" SET \"PayloadJson\" = \"PayloadJson\" - 'files' "
                + "WHERE \"Type\" IN ('tagging.catalogue.renamed', 'tagging.catalogue.linked')",
            Token);
    }

    /// <summary>The file filed under its album alone again, at a place in the folder's order.</summary>
    private async Task PlaceAsync(RecordingId recording, ReleaseGroupId group, int position)
    {
        await using var db = PostgresFixture.CreateContext(_connectionString);

        var file = await db.MediaFiles.SingleAsync(Token);
        file.RecordingId = recording;
        file.ReleaseGroupId = group;
        file.FolderPosition = position;

        await db.SaveChangesAsync(Token);
    }

    private async Task<TagUndoResult> UndoAsync(ServiceProvider services, string folder) =>
        await services.GetRequiredService<TagWriteService>()
            .UndoAsync(folder, new SingleUserCallerContext(), Token);

    private async Task<TagSnapshot> TagsAsync(string path) =>
        await new TagReader(new FileSystemAudioFileStore(_root))
            .ReadAsync(new LibraryPath(path), cancellationToken: Token);

    /// <summary>What the identification pass leaves: the AcoustID in the file, under its own journal prefix.</summary>
    private async Task IdentifyAsync(ServiceProvider services, string path)
    {
        using var scope = services.CreateScope();

        var writer = scope.ServiceProvider.GetRequiredService<TagWriter>();
        var db = scope.ServiceProvider.GetRequiredService<FonotecaDbContext>();
        var row = await db.MediaFiles.SingleAsync(file => file.Path == path, Token);

        var plan = await writer.PlanAsync(
            new LibraryPath(path),
            new Dictionary<string, string> { [CatalogueTags.AcoustIdField] = Cluster.ToString() },
            Token);

        var write = await writer.ApplyAsync(
            plan, row.Id.ToString(), "identify-run", SystemCallerContext.SystemId, AcoustIdTagWriter.EventPrefix, Token);

        Assert.Equal(TagWriteStatus.Written, write.Status);

        row.AcoustId = new AcoustId(Cluster);
        row.AcoustIdTaggedUtc = DateTimeOffset.UtcNow;
        row.SizeBytes = write.Committed!.SizeBytes;
        row.LastModifiedUtc = StoreTime.ToStorePrecision(write.Committed.LastModifiedUtc);

        await db.SaveChangesAsync(Token);
    }
}
