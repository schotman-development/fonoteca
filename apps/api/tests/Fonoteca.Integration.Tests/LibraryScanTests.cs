using Fonoteca.Api.Library;
using Fonoteca.Data;
using Fonoteca.Domain.Abstractions;
using Fonoteca.Domain.Catalogue;
using Fonoteca.Fixtures;
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
    /// A file whose audio changed invalidates everything derived from its bytes.
    /// A stale audio hash is worse than none: it is what makes dedupe delete the
    /// copy that was actually different.
    /// </summary>
    [Fact]
    public async Task ChangedFileIsUpdatedAndItsDerivedDataDiscarded()
    {
        Assert.SkipUnless(Corpus.IsAvailable, "ffmpeg is not on PATH; source ~/.local/opt/env.sh.");

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
            row.TagDiscNumber = 1;
            row.TagTrackNumber = 3;
            row.FolderPosition = 3;
            row.OrderOutcome = FolderOrderOutcome.Corroborated;
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

        // The numbers read out of the old bytes, and the order they were placed in.
        Assert.Null(updated.TagDiscNumber);
        Assert.Null(updated.TagTrackNumber);
        Assert.Null(updated.FolderPosition);
        Assert.Equal(FolderOrderOutcome.NotChecked, updated.OrderOutcome);

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

    /// <summary>
    /// Another program retagging a file changes its size and mtime and not its
    /// audio, so nothing the audio earned is forgotten.
    /// </summary>
    [Fact]
    public async Task AFileAnotherProgramRetaggedKeepsWhatItsAudioEarned()
    {
        Assert.SkipUnless(Corpus.IsAvailable, "ffmpeg is not on PATH; source ~/.local/opt/env.sh.");

        var absolute = CopyCorpus("Artist/Album/01.flac");
        await ScanAsync();
        var asked = await AnswerAsync("Artist/Album/01.flac", hashed: true);

        Remux(absolute, absolute, "title=Retagged elsewhere");

        var summary = await ScanAsync();

        Assert.Equal(1, summary.Updated);

        var row = (await RowsAsync()).Single();
        Assert.Equal(new FileInfo(absolute).Length, row.SizeBytes);
        Assert.NotNull(row.AudioHash);
        Assert.NotNull(row.AcoustId);
        Assert.NotNull(row.RecordingId);
        Assert.NotNull(row.IdentityDecidedUtc);
        Assert.NotNull(row.Quality);
        Assert.NotNull(row.LastVerifiedUtc);
        Assert.Equal(asked, row.ReleaseLookupUtc);

        // Whether it still carries its AcoustID is the file's to say again.
        Assert.Null(row.AcoustIdTaggedUtc);

        // The bytes did change.
        Assert.Null(row.ContentHash);
    }

    /// <summary>A hashed file whose audio was replaced is new audio, and keeps nothing the old audio earned.</summary>
    [Fact]
    public async Task AFileWhoseAudioWasReplacedKeepsNothingTheOldAudioEarned()
    {
        Assert.SkipUnless(Corpus.IsAvailable, "ffmpeg is not on PATH; source ~/.local/opt/env.sh.");

        var absolute = CopyCorpus("Artist/Album/01.flac");
        await ScanAsync();
        await AnswerAsync("Artist/Album/01.flac", hashed: true);

        OtherAudio(absolute);
        await ScanAsync();

        var row = (await RowsAsync()).Single();
        Assert.Null(row.AudioHash);
        Assert.Null(row.AcoustId);
        Assert.Null(row.RecordingId);
        Assert.Null(row.IdentityDecidedUtc);
        Assert.Null(row.Quality);
        Assert.Null(row.ReleaseLookupUtc);
    }

    /// <summary>The numbers a file carries are its folder's order, so new ones re-decide the folder.</summary>
    [Fact]
    public async Task ARetagThatRenumbersAFilePutsItsFolderBackThroughAttribution()
    {
        Assert.SkipUnless(Corpus.IsAvailable, "ffmpeg is not on PATH; source ~/.local/opt/env.sh.");

        var absolute = CopyCorpus("Artist/Album/01.flac");
        await ScanAsync();
        await AnswerAsync("Artist/Album/01.flac", hashed: true);

        Remux(absolute, absolute, "track=2");
        await ScanAsync();

        var row = (await RowsAsync()).Single();
        Assert.Equal(2, row.TagTrackNumber);
        Assert.Null(row.ReleaseLookupUtc);
        Assert.NotNull(row.RecordingId);
        Assert.NotNull(row.Quality);
    }

    /// <summary>
    /// A folder renamed by another program is the same files somewhere else, not
    /// a deletion and an arrival that take every answer with them.
    /// </summary>
    [Fact]
    public async Task AFolderAnotherProgramRenamedKeepsItsRows()
    {
        WriteFile("Artist/Album/01.flac", "one");
        WriteFile("Artist/Album/02.flac", "two two");
        await ScanAsync();
        await AnswerAsync("Artist/Album/01.flac");
        await AnswerAsync("Artist/Album/02.flac");
        var before = (await RowsAsync()).ToDictionary(row => Path.GetFileName(row.Path));

        Directory.Move(Path.Combine(_root, "Artist/Album"), Path.Combine(_root, "Artist/Album (1979)"));
        var summary = await ScanAsync();

        Assert.Equal(2, summary.Moved);
        Assert.Equal(0, summary.Added);
        Assert.Equal(0, summary.Removed);

        var rows = await RowsAsync();
        Assert.All(rows, row => Assert.StartsWith("Artist/Album (1979)/", row.Path, StringComparison.Ordinal));
        Assert.All(rows, row => Assert.Equal(before[Path.GetFileName(row.Path)].Id, row.Id));
        Assert.All(rows, row => Assert.NotNull(row.AcoustId));
        Assert.All(rows, row => Assert.Equal(before[Path.GetFileName(row.Path)].ReleaseLookupUtc, row.ReleaseLookupUtc));

        // Not one byte changed, so what the file says is still known.
        Assert.All(rows, row => Assert.NotNull(row.AcoustIdTaggedUtc));
    }

    /// <summary>
    /// One file moved into another album folder keeps its answers, and the
    /// folder it joined is decided again, since a folder is one album.
    /// </summary>
    [Fact]
    public async Task AFileMovedIntoAnotherFolderKeepsItsAnswersAndReopensItsAlbum()
    {
        WriteFile("Artist/Album/01.flac", "one");
        WriteFile("Artist/Album/02.flac", "two two");
        WriteFile("Artist/Other/01.flac", "three three three");
        await ScanAsync();

        foreach (var path in (await RowsAsync()).Select(row => row.Path)) await AnswerAsync(path);

        var id = (await RowsAsync()).Single(row => row.Path == "Artist/Album/02.flac").Id;

        File.Move(Path.Combine(_root, "Artist/Album/02.flac"), Path.Combine(_root, "Artist/Other/02.flac"));
        var summary = await ScanAsync();

        Assert.Equal(1, summary.Moved);

        var rows = (await RowsAsync()).ToDictionary(row => row.Path);
        var moved = rows["Artist/Other/02.flac"];
        Assert.Equal(id, moved.Id);
        Assert.NotNull(moved.AcoustId);
        Assert.NotNull(moved.RecordingId);
        Assert.Null(moved.ReleaseLookupUtc);
        Assert.NotNull(rows["Artist/Album/01.flac"].ReleaseLookupUtc);
        Assert.NotNull(rows["Artist/Other/01.flac"].ReleaseLookupUtc);
    }

    /// <summary>A folder's only file moved into a folder that holds others joins an album that is decided again.</summary>
    [Fact]
    public async Task AFileMovedIntoAFolderThatHoldsOthersReopensIt()
    {
        WriteFile("Artist/Single/01.flac", "one");
        WriteFile("Artist/Other/02.flac", "two two");
        await ScanAsync();
        await AnswerAsync("Artist/Single/01.flac");
        await AnswerAsync("Artist/Other/02.flac");

        File.Move(Path.Combine(_root, "Artist/Single/01.flac"), Path.Combine(_root, "Artist/Other/01.flac"));
        await ScanAsync();

        var rows = (await RowsAsync()).ToDictionary(row => row.Path);
        Assert.Null(rows["Artist/Other/01.flac"].ReleaseLookupUtc);
        Assert.NotNull(rows["Artist/Other/01.flac"].AcoustId);
        Assert.NotNull(rows["Artist/Other/02.flac"].ReleaseLookupUtc);
    }

    /// <summary>Part of a folder moved to a new one is not the album renamed.</summary>
    [Fact]
    public async Task PartOfAFolderMovedToANewOneReopensIt()
    {
        WriteFile("Artist/Album/01.flac", "one");
        WriteFile("Artist/Album/02.flac", "two two");
        await ScanAsync();
        await AnswerAsync("Artist/Album/01.flac");
        await AnswerAsync("Artist/Album/02.flac");

        Directory.CreateDirectory(Path.Combine(_root, "Artist/Bonus"));
        File.Move(Path.Combine(_root, "Artist/Album/02.flac"), Path.Combine(_root, "Artist/Bonus/02.flac"));
        await ScanAsync();

        var rows = (await RowsAsync()).ToDictionary(row => row.Path);
        Assert.Null(rows["Artist/Bonus/02.flac"].ReleaseLookupUtc);
        Assert.NotNull(rows["Artist/Bonus/02.flac"].AcoustId);
        Assert.NotNull(rows["Artist/Album/01.flac"].ReleaseLookupUtc);
    }

    /// <summary>A program that retags and renames in one go is found by the audio.</summary>
    [Fact]
    public async Task AFileRetaggedAndRenamedIsFoundByItsAudio()
    {
        Assert.SkipUnless(Corpus.IsAvailable, "ffmpeg is not on PATH; source ~/.local/opt/env.sh.");

        var absolute = CopyCorpus("Artist/Album/01.flac");
        await ScanAsync();
        await AnswerAsync("Artist/Album/01.flac", hashed: true);
        var id = (await RowsAsync()).Single().Id;

        var renamed = Path.Combine(_root, "Artist/Album (Remaster)/01 Tone.flac");
        Directory.CreateDirectory(Path.GetDirectoryName(renamed)!);
        Remux(absolute, renamed, "title=Tone");
        File.Delete(absolute);

        var summary = await ScanAsync();

        Assert.Equal(1, summary.Moved);
        Assert.Equal(0, summary.Added);
        Assert.Equal(0, summary.Removed);

        var row = (await RowsAsync()).Single();
        Assert.Equal(id, row.Id);
        Assert.Equal("Artist/Album (Remaster)/01 Tone.flac", row.Path);
        Assert.Equal(new FileInfo(renamed).Length, row.SizeBytes);
        Assert.NotNull(row.AcoustId);
        Assert.NotNull(row.Quality);

        // Renamed inside the folder, and names are evidence for its order.
        Assert.Null(row.ReleaseLookupUtc);
    }

    /// <summary>
    /// Renumbering moves each file onto a name another file had: the same
    /// paths with other bytes, and still every file is the row it was.
    /// </summary>
    [Fact]
    public async Task ARenumberingThatReusesNamesKeepsEveryRow()
    {
        WriteFile("A/B/01.flac", "one");
        WriteFile("A/B/02.flac", "two two");
        await ScanAsync();
        await AnswerAsync("A/B/01.flac");
        await AnswerAsync("A/B/02.flac");
        var ids = (await RowsAsync()).ToDictionary(row => row.Path, row => row.Id);

        File.Move(Path.Combine(_root, "A/B/02.flac"), Path.Combine(_root, "A/B/03.flac"));
        File.Move(Path.Combine(_root, "A/B/01.flac"), Path.Combine(_root, "A/B/02.flac"));
        WriteFile("A/B/01.flac", "a new first track");

        var summary = await ScanAsync();

        Assert.Equal(2, summary.Moved);
        Assert.Equal(1, summary.Added);
        Assert.Equal(0, summary.Updated);
        Assert.Equal(0, summary.Removed);

        var rows = (await RowsAsync()).ToDictionary(row => row.Path);
        Assert.Equal(ids["A/B/01.flac"], rows["A/B/02.flac"].Id);
        Assert.Equal(ids["A/B/02.flac"], rows["A/B/03.flac"].Id);
        Assert.NotNull(rows["A/B/02.flac"].AcoustId);
        Assert.NotNull(rows["A/B/03.flac"].AcoustId);
        Assert.Null(rows["A/B/01.flac"].AcoustId);

        // New names inside the folder are new evidence for its order.
        Assert.Null(rows["A/B/02.flac"].ReleaseLookupUtc);
    }

    /// <summary>A tagger renumbering an album rewrites every file on the way, so the audio finds them.</summary>
    [Fact]
    public async Task ARenumberingByATaggerIsFoundByTheAudio()
    {
        Assert.SkipUnless(Corpus.IsAvailable, "ffmpeg is not on PATH; source ~/.local/opt/env.sh.");

        var first = CopyCorpus("A/B/01.flac");
        var second = Path.Combine(_root, "A/B/02.flac");
        OtherAudio(second);
        await ScanAsync();
        await AnswerAsync("A/B/01.flac", hashed: true);
        await AnswerAsync("A/B/02.flac", hashed: true);
        var ids = (await RowsAsync()).ToDictionary(row => row.Path, row => row.Id);

        Remux(second, Path.Combine(_root, "A/B/03.flac"), "track=3");
        Remux(first, second, "track=2");
        OtherAudio(first, frequency: 330);

        var summary = await ScanAsync();

        Assert.Equal(2, summary.Moved);
        Assert.Equal(1, summary.Added);
        Assert.Equal(0, summary.Removed);

        var rows = (await RowsAsync()).ToDictionary(row => row.Path);
        Assert.Equal(ids["A/B/01.flac"], rows["A/B/02.flac"].Id);
        Assert.Equal(ids["A/B/02.flac"], rows["A/B/03.flac"].Id);
        Assert.NotNull(rows["A/B/02.flac"].Quality);
        Assert.Equal(2, rows["A/B/02.flac"].TagTrackNumber);
        Assert.Null(rows["A/B/01.flac"].RecordingId);
    }

    /// <summary>Two files that swapped names: a cycle no order of updates can make without parking one.</summary>
    [Fact]
    public async Task TwoFilesThatSwappedNamesKeepTheirRows()
    {
        WriteFile("A/B/01.flac", "one");
        WriteFile("A/B/02.flac", "two two");
        await ScanAsync();
        await AnswerAsync("A/B/01.flac");
        await AnswerAsync("A/B/02.flac");
        var ids = (await RowsAsync()).ToDictionary(row => row.Path, row => row.Id);

        File.Move(Path.Combine(_root, "A/B/01.flac"), Path.Combine(_root, "A/B/swap"));
        File.Move(Path.Combine(_root, "A/B/02.flac"), Path.Combine(_root, "A/B/01.flac"));
        File.Move(Path.Combine(_root, "A/B/swap"), Path.Combine(_root, "A/B/02.flac"));

        var summary = await ScanAsync();

        Assert.Equal(2, summary.Moved);

        var rows = (await RowsAsync()).ToDictionary(row => row.Path);
        Assert.Equal(ids["A/B/01.flac"], rows["A/B/02.flac"].Id);
        Assert.Equal(ids["A/B/02.flac"], rows["A/B/01.flac"].Id);
        Assert.All(rows.Values, row => Assert.NotNull(row.AcoustId));
    }

    /// <summary>A file moved over another's name: the one it replaced went nowhere, and goes.</summary>
    [Fact]
    public async Task AFileMovedOverAnotherTakesItsPlace()
    {
        WriteFile("A/B/01.flac", "one");
        WriteFile("A/B/02.flac", "two two");
        await ScanAsync();
        await AnswerAsync("A/B/01.flac");
        await AnswerAsync("A/B/02.flac");
        var id = (await RowsAsync()).Single(row => row.Path == "A/B/01.flac").Id;

        File.Move(Path.Combine(_root, "A/B/01.flac"), Path.Combine(_root, "A/B/02.flac"), overwrite: true);
        var summary = await ScanAsync();

        Assert.Equal(1, summary.Moved);
        Assert.Equal(1, summary.Removed);
        Assert.Equal(0, summary.Added);

        var row = Assert.Single(await RowsAsync());
        Assert.Equal(id, row.Id);
        Assert.Equal("A/B/02.flac", row.Path);
        Assert.NotNull(row.AcoustId);
    }

    /// <summary>
    /// A place one row already moved to is not offered again: a second row of
    /// the same audio that vanished with it is a deletion, not a second claim.
    /// </summary>
    [Fact]
    public async Task APlaceOneRowMovedToIsNotClaimedBySameAudioThatWentWithIt()
    {
        Assert.SkipUnless(Corpus.IsAvailable, "ffmpeg is not on PATH; source ~/.local/opt/env.sh.");

        CopyCorpus("X/A/01.flac");
        var copy = Path.Combine(_root, "Y/B/01.flac");
        Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
        Remux(Corpus.Flac, copy, "title=Another copy");
        await ScanAsync();
        await AnswerAsync("X/A/01.flac", hashed: true);
        await AnswerAsync("Y/B/01.flac", hashed: true);
        var id = (await RowsAsync()).Single(row => row.Path == "X/A/01.flac").Id;

        Directory.CreateDirectory(Path.Combine(_root, "Z/A"));
        File.Move(Path.Combine(_root, "X/A/01.flac"), Path.Combine(_root, "Z/A/01.flac"));
        File.Delete(copy);

        var summary = await ScanAsync();

        Assert.Equal(1, summary.Moved);
        Assert.Equal(1, summary.Removed);

        var row = Assert.Single(await RowsAsync());
        Assert.Equal(id, row.Id);
        Assert.Equal("Z/A/01.flac", row.Path);
    }

    /// <summary>Two files that could each be either are left as a deletion and an arrival.</summary>
    [Fact]
    public async Task AMoveThatCouldBeEitherOfTwoFilesIsNotGuessed()
    {
        var stamp = new DateTime(2026, 3, 4, 5, 6, 7, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(WriteFile("A/B/1.flac", "same"), stamp);
        File.SetLastWriteTimeUtc(WriteFile("A/B/2.flac", "same"), stamp);
        await ScanAsync();

        Directory.Move(Path.Combine(_root, "A/B"), Path.Combine(_root, "A/C"));
        var summary = await ScanAsync();

        Assert.Equal(0, summary.Moved);
        Assert.Equal(2, summary.Added);
        Assert.Equal(2, summary.Removed);
    }

    /// <summary>One file gone and two arrived that could each be it: no guess either.</summary>
    [Fact]
    public async Task AFileThatCouldHaveGoneToEitherOfTwoPlacesIsNotGuessed()
    {
        var stamp = new DateTime(2026, 3, 4, 5, 6, 7, DateTimeKind.Utc);
        var original = WriteFile("A/B/1.flac", "same");
        File.SetLastWriteTimeUtc(original, stamp);
        await ScanAsync();

        File.SetLastWriteTimeUtc(WriteFile("A/C/1.flac", "same"), stamp);
        File.SetLastWriteTimeUtc(WriteFile("A/D/1.flac", "same"), stamp);
        File.Delete(original);

        var summary = await ScanAsync();

        Assert.Equal(0, summary.Moved);
        Assert.Equal(2, summary.Added);
        Assert.Equal(1, summary.Removed);
    }

    /// <summary>The same bytes under another format's name are not the file that left.</summary>
    [Fact]
    public async Task AFileThatChangedItsExtensionIsNotAMove()
    {
        WriteFile("A/B/1.flac", "same");
        await ScanAsync();

        File.Move(Path.Combine(_root, "A/B/1.flac"), Path.Combine(_root, "A/B/1.mp3"));
        var summary = await ScanAsync();

        Assert.Equal(0, summary.Moved);
        Assert.Equal(1, summary.Added);
        Assert.Equal(1, summary.Removed);
    }

    /// <summary>
    /// A walk that missed a directory has not seen anything go, so it pairs
    /// nothing either — a move it cannot see both ends of is not one.
    /// </summary>
    [Fact]
    public async Task AWalkThatMissedADirectoryLooksForNoMove()
    {
        WriteFile("A/B/01.flac", "one");
        WriteFile("Private/02.flac", "two two");
        await ScanAsync();

        Directory.CreateDirectory(Path.Combine(_root, "A/C"));
        File.Move(Path.Combine(_root, "A/B/01.flac"), Path.Combine(_root, "A/C/01.flac"));

        var locked = Path.Combine(_root, "Private");
        File.SetUnixFileMode(locked, UnixFileMode.None);

        try
        {
            var summary = await ScanAsync();

            Assert.Equal(1, summary.UnreadableDirectories);
            Assert.Equal(0, summary.Moved);
            Assert.Equal(0, summary.Removed);
        }
        finally
        {
            File.SetUnixFileMode(locked, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    /// <summary>
    /// Arrivals are hashed only against a vanished file of their own format,
    /// and a hash read for a move that was not one is kept.
    /// </summary>
    [Fact]
    public async Task AnArrivalHashedForAMoveKeepsItsHash()
    {
        Assert.SkipUnless(Corpus.IsAvailable, "ffmpeg is not on PATH; source ~/.local/opt/env.sh.");

        var gone = CopyCorpus("A/B/01.flac");
        await ScanAsync();
        await AnswerAsync("A/B/01.flac", hashed: true);

        File.Delete(gone);
        var other = Path.Combine(_root, "X/Z/other.flac");
        Directory.CreateDirectory(Path.GetDirectoryName(other)!);
        OtherAudio(other);
        Directory.CreateDirectory(Path.Combine(_root, "X/Y"));
        File.Copy(Corpus.Mp3, Path.Combine(_root, "X/Y/new.mp3"));

        var summary = await ScanAsync();

        Assert.Equal(0, summary.Moved);
        Assert.Equal(2, summary.Added);
        Assert.Equal(1, summary.Removed);

        var rows = (await RowsAsync()).ToDictionary(row => row.Path);
        Assert.NotNull(rows["X/Z/other.flac"].AudioHash);
        Assert.Null(rows["X/Y/new.mp3"].AudioHash);
    }

    /// <summary>
    /// Without ffmpeg a retag cannot be told from new audio, so the scan refuses
    /// rather than discard what it cannot vouch for.
    /// </summary>
    [Fact]
    public async Task AScanThatCannotHashAChangedFileChangesNothing()
    {
        var absolute = WriteFile("a.flac", "original");
        await ScanAsync();

        await using (var db = PostgresFixture.CreateContext(_connectionString))
        {
            var row = await db.MediaFiles.SingleAsync(TestContext.Current.CancellationToken);
            row.AudioHash = "audio";
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await File.WriteAllTextAsync(absolute, "a longer body", TestContext.Current.CancellationToken);
        WriteFile("b.flac");

        var outcome = await BuildServices(_root, ffmpeg: Path.Combine(_root, "no-ffmpeg-here"))
            .GetRequiredService<LibraryScanService>()
            .ScanAsync(TestContext.Current.CancellationToken);

        Assert.Equal(LibraryScanStatus.HasherUnavailable, outcome.Status);

        var rows = await RowsAsync();
        Assert.Equal("audio", Assert.Single(rows).AudioHash);
        Assert.Equal("original".Length, rows[0].SizeBytes);
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
    private ServiceProvider BuildServices(string libraryRoot, string ffmpeg = "ffmpeg")
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
        services.AddSingleton(sp => new FfmpegAudioHasher(sp.GetRequiredService<FileSystemAudioFileStore>(), ffmpeg));
        services.AddSingleton<Fonoteca.Tagging.TagReader>();
        services.AddSingleton<LibraryWorkGate>();
        services.AddSingleton<PlaylistFiles>();

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

    /// <summary>
    /// Gives a catalogued file every kind of answer a pass or a person leaves,
    /// as the rule's album, and returns the album stamp.
    /// </summary>
    private async Task<DateTimeOffset> AnswerAsync(string path, bool hashed = false)
    {
        var now = StoreTime.ToStorePrecision(DateTimeOffset.UtcNow);
        var hash = hashed
            ? await (_services ??= BuildServices(_root)).GetRequiredService<FfmpegAudioHasher>()
                .HashAsync(new LibraryPath(path), TestContext.Current.CancellationToken)
            : null;

        await using var db = PostgresFixture.CreateContext(_connectionString);

        var recording = new Recording { Id = RecordingId.New(), Title = path };
        db.Recordings.Add(recording);

        var row = await db.MediaFiles.SingleAsync(f => f.Path == path, TestContext.Current.CancellationToken);
        row.AudioHash = hash;
        row.ContentHash = "content";
        row.Integrity = IntegrityState.Intact;
        row.LastVerifiedUtc = now;
        row.Quality = new AudioQuality
        {
            Codec = "flac",
            SampleRateHz = 44_100,
            Channels = 2,
            BitDepth = 16,
            BitrateBps = 900_000,
            IsLossless = true,
        };
        row.AcoustId = new AcoustId(Guid.CreateVersion7());
        row.AcoustIdCheckedUtc = now;
        row.AcoustIdTaggedUtc = now;
        row.AcoustIdOutcome = AcoustIdOutcome.Identified;
        row.IdentityDecidedUtc = now;
        row.RecordingId = recording.Id;
        row.RecordingLookupUtc = now;
        row.EnrichmentOutcome = EnrichmentOutcome.Linked;
        row.ReleaseLookupUtc = now;
        row.AttributionOutcome = ReleaseAttributionOutcome.GroupOnly;

        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return now;
    }

    private string CopyCorpus(string relativePath)
    {
        var absolute = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(absolute)!);
        File.Copy(Corpus.Flac, absolute);
        return absolute;
    }

    /// <summary>What a tagger does, as far as the scan can tell: new bytes around the same packets.</summary>
    private static void Remux(string source, string destination, params string[] metadata) =>
        Ffmpeg(destination, ["-i", source, "-map", "0", "-c", "copy", .. metadata.SelectMany(m => new[] { "-metadata", m })]);

    /// <summary>A FLAC of different audio from the corpus's.</summary>
    private static void OtherAudio(string destination, int frequency = 880) =>
        Ffmpeg(destination, ["-f", "lavfi", "-i", $"sine=frequency={frequency}:duration=3", "-c:a", "flac"]);

    private static void Ffmpeg(string destination, string[] arguments)
    {
        var staged = destination + ".staged.flac";

        using (var ffmpeg = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
            "ffmpeg",
            ["-v", "error", "-y", .. arguments, staged])
        {
            RedirectStandardError = true,
        })!)
        {
            var error = ffmpeg.StandardError.ReadToEnd();
            ffmpeg.WaitForExit();
            Assert.True(ffmpeg.ExitCode == 0, $"ffmpeg could not make {destination}: {error}");
        }

        File.Move(staged, destination, overwrite: true);
    }

    private async Task<List<MediaFile>> RowsAsync()
    {
        await using var db = PostgresFixture.CreateContext(_connectionString);

        return await db.MediaFiles.AsNoTracking().OrderBy(f => f.Path).ToListAsync(TestContext.Current.CancellationToken);
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
