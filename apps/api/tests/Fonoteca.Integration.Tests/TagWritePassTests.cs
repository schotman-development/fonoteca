using System.Net;
using System.Net.Http.Headers;
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

    /// <summary>The sleeve a person picked, as bytes nothing here has to decode.</summary>
    private static readonly byte[] ChosenCover = [0xFF, 0xD8, 0xFF, 0xDB, 9, 8, 7, 6];

    /// <summary>The photograph a provider holds, as bytes nothing here has to decode.</summary>
    private static readonly byte[] PortraitBytes = [0xFF, 0xD8, 0xFF, 0xE0, 5, 4, 3, 2, 1];

    private const string PortraitUrl = "https://static.example/portraits/miles.jpg";

    private readonly List<ServiceProvider> _providers = [];

    /// <summary>What the CDN answers with, and every request that reached it.</summary>
    private StubHttpHandler _portraits = null!;

    /// <summary>Set to make the picture host fail instead of answering.</summary>
    private Exception? _portraitFailure;

    private string _connectionString = string.Empty;
    private string _root = string.Empty;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _connectionString = await postgres.CreateDatabaseAsync(Token);

        await using var db = PostgresFixture.CreateContext(_connectionString);
        await db.Database.MigrateAsync(Token);

        _root = Directory.CreateTempSubdirectory("fonoteca-tagwrite-pass-").FullName;

        // Built here rather than in a field initialiser so a test can make the
        // host fail: a lambda over `this` is not allowed in one.
        _portraits = new StubHttpHandler(_ => _portraitFailure is null
            ? new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(PortraitBytes)
                {
                    Headers = { ContentType = new MediaTypeHeaderValue("image/jpeg") },
                },
            }
            : throw _portraitFailure);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var provider in _providers) await provider.DisposeAsync();

        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);

        // The default trash is a sibling of the library rather than a folder
        // inside it, so deleting the root above does not take it with it.
        if (Directory.Exists(_root + "-trash")) Directory.Delete(_root + "-trash", recursive: true);
    }

    /// <summary>
    /// A cover for the seeded release, as choosing one in the app leaves it.
    /// </summary>
    /// <remarks>
    /// <paramref name="bytes"/> is null for the other row this table holds: a
    /// stamp saying both sources were asked and neither had a sleeve.
    /// </remarks>
    private async Task SeedCoverAsync(byte[]? bytes, string mediaType = "image/jpeg")
    {
        await using var db = PostgresFixture.CreateContext(_connectionString);

        var release = await db.Releases
            .Where(candidate => candidate.Mbid == ReleaseMbid)
            .Select(candidate => candidate.Id)
            .SingleAsync(Token);

        db.ReleaseCovers.Add(new ReleaseCover
        {
            ReleaseId = release,
            Bytes = bytes,
            MediaType = bytes is null ? null : mediaType,
            ArchiveImageId = bytes is null ? null : 1234,
            SavedUtc = DateTimeOffset.UtcNow,
        });

        await db.SaveChangesAsync(Token);
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
    /// A damaged MusicBrainz id is repaired, and the row keeps up with the file.
    /// </summary>
    /// <remarks>
    /// Against real PostgreSQL, because the repair's journal entry carries the
    /// NUL a UFID is made of, which <c>jsonb</c> refuses: refused, the save
    /// failed after the file was committed and the row kept its old size.
    /// </remarks>
    [Fact]
    public async Task ADamagedMusicBrainzIdIsRepairedAndTheRowKeepsUp()
    {
        SkipWithoutTools();
        await SeedAsync(("Miles Davis/Kind of Blue/01.mp3", 1));

        using (var file = TagLib.File.Create(Path.Combine(_root, "Miles Davis", "Kind of Blue", "01.mp3")))
        {
            var tag = (TagLib.Id3v2.Tag)file.GetTag(TagLib.TagTypes.Id3v2, true);
            tag.AddFrame(new TagLib.Id3v2.UniqueFileIdentifierFrame(
                "\u0003http://musicbrainz.org",
                TagLib.ByteVector.FromString(RecordingMbid.ToString(), TagLib.StringType.Latin1)));
            file.Save();
        }

        var services = Build();
        var summary = await RunAsync(TagWriteScope.Library, services);

        Assert.Equal(1, summary.Written);
        Assert.Equal(0, summary.Failed);

        await using (var db = PostgresFixture.CreateContext(_connectionString))
        {
            Assert.True(await db.DomainEvents.AnyAsync(entry => entry.Type == "tagging.catalogue.written", Token));
        }

        var rescan = await services.GetRequiredService<LibraryScanService>().ScanAsync(Token);
        Assert.Equal(0, rescan.Summary!.Updated);
        Assert.Equal(1, rescan.Summary.Unchanged);

        using var written = TagLib.File.Create(Path.Combine(_root, "Miles Davis", "Kind of Blue", "01.mp3"));
        Assert.Equal(RecordingMbid.ToString(), written.Tag.MusicBrainzTrackId);
    }

    /// <summary>
    /// An album's button writes that album and leaves everything else alone.
    /// </summary>
    [Fact]
    public async Task AnAlbumScopedRunTouchesNoOtherAlbum()
    {
        SkipWithoutTools();

        await SeedAsync(("Miles Davis/Kind of Blue/01 track.flac", 1));
        var other = await SeedOtherAlbumAsync("Someone Else/Another Record/01 track.flac");

        ReleaseGroupId chosen;

        await using (var db = PostgresFixture.CreateContext(_connectionString))
        {
            chosen = await db.Releases
                .Where(release => release.Mbid == ReleaseMbid)
                .Select(release => release.ReleaseGroupId!.Value)
                .SingleAsync(Token);
        }

        var summary = await RunAsync(TagWriteScope.ForAlbum(chosen, "Kind of Blue"));

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
    /// A file with no album is not on the worklist.
    /// </summary>
    /// <remarks>
    /// A title with no album produces a file that reads as a half-tagged rip in
    /// every player, and the catalogue has no answer worth writing for it.
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

    /// <summary>
    /// A file held to its album with no pressing proven gets the album's facts
    /// and nothing only a pressing has.
    /// </summary>
    /// <remarks>
    /// ADR 0013 leaves most of a library in this state, every new download
    /// included. The album artist is the display edition's billing line and the
    /// year the album page's — no track number, no disc, no release MBID.
    /// </remarks>
    [Fact]
    public async Task AFileWithOnlyItsAlbumGetsTheAlbumsFactsAndNoPressings()
    {
        SkipWithoutTools();
        await SeedAsync(("Miles Davis/Kind of Blue/01 track.flac", 1));

        await using (var db = PostgresFixture.CreateContext(_connectionString))
        {
            var file = await db.MediaFiles.SingleAsync(Token);
            file.ReleaseId = null;
            file.TrackId = null;
            file.FolderPosition = 4;

            // The album's own title and year, which the pressing's are not.
            var album = await db.ReleaseGroups.SingleAsync(Token);
            album.Title = "Kind of Blue (the album)";
            album.FirstReleaseYear = 1958;

            await db.SaveChangesAsync(Token);
        }

        var summary = await RunAsync(TagWriteScope.Library);

        Assert.Equal(1, summary.Written);

        var reading = await new TagReader(new FileSystemAudioFileStore(_root))
            .ReadAsync(new LibraryPath("Miles Davis/Kind of Blue/01 track.flac"), cancellationToken: Token);

        Assert.Equal("So What", reading.Fields["TITLE"]);
        Assert.Equal("Miles Davis", reading.Fields["ARTIST"]);
        Assert.Equal("Kind of Blue (the album)", reading.Fields["ALBUM"]);
        Assert.Equal("Miles Davis", reading.Find(CatalogueTags.AlbumArtist));
        Assert.Equal("1958", reading.Fields["YEAR"]);
        Assert.Equal(RecordingMbid.ToString(), reading.Find(CatalogueTags.RecordingId));
        Assert.Equal(GroupMbid.ToString(), reading.Find(CatalogueTags.ReleaseGroupId));
        Assert.Null(reading.Find(CatalogueTags.ReleaseId));
        Assert.False(reading.Fields.ContainsKey("DISCNUMBER"));

        // Its place in the folder, the file having no number of its own.
        Assert.Equal("4", reading.Fields["TRACKNUMBER"]);

        // And a number this pass filled in is ours to move when the folder's
        // order does — a track added before it, say — not the file's own.
        await using (var db = PostgresFixture.CreateContext(_connectionString))
        {
            var file = await db.MediaFiles.SingleAsync(Token);
            file.FolderPosition = 5;
            await db.SaveChangesAsync(Token);
        }

        await RunAsync(TagWriteScope.Library);

        var moved = await new TagReader(new FileSystemAudioFileStore(_root))
            .ReadAsync(new LibraryPath("Miles Davis/Kind of Blue/01 track.flac"), cancellationToken: Token);

        Assert.Equal("5", moved.Fields["TRACKNUMBER"]);

        // Still ours after being moved once: the journal follows the chain.
        await using (var db = PostgresFixture.CreateContext(_connectionString))
        {
            var file = await db.MediaFiles.SingleAsync(Token);
            file.FolderPosition = 6;
            await db.SaveChangesAsync(Token);
        }

        await RunAsync(TagWriteScope.Library);

        var again = await new TagReader(new FileSystemAudioFileStore(_root))
            .ReadAsync(new LibraryPath("Miles Davis/Kind of Blue/01 track.flac"), cancellationToken: Token);

        Assert.Equal("6", again.Fields["TRACKNUMBER"]);
    }

    /// <summary>
    /// A date ATL cannot parse is still the file's own, and no year replaces it.
    /// </summary>
    [Fact]
    public async Task AFileWithOnlyItsAlbumKeepsADateOnlyTagLibCanRead()
    {
        SkipWithoutTools();
        await SeedAsync(("Miles Davis/Kind of Blue/01 track.flac", 1));

        await using (var db = PostgresFixture.CreateContext(_connectionString))
        {
            var file = await db.MediaFiles.SingleAsync(Token);
            file.ReleaseId = null;
            file.TrackId = null;
            file.FolderPosition = 1;
            (await db.ReleaseGroups.SingleAsync(Token)).FirstReleaseYear = 1958;
            await db.SaveChangesAsync(Token);
        }

        var full = Path.Combine(_root, "Miles Davis/Kind of Blue/01 track.flac");

        using (var tagged = TagLib.File.Create(full))
        {
            ((TagLib.Ogg.XiphComment)tagged.GetTag(TagLib.TagTypes.Xiph, true)).SetField("DATE", "17/08/1959");
            tagged.Save();
        }

        await RunAsync(TagWriteScope.Library);

        using var written = TagLib.File.Create(full);
        Assert.Equal(
            ["17/08/1959"],
            ((TagLib.Ogg.XiphComment)written.GetTag(TagLib.TagTypes.Xiph, false)).GetField("DATE"));
    }

    /// <summary>
    /// A track number a pressing filled in stays beside its disc number when the
    /// pressing is lost.
    /// </summary>
    /// <remarks>
    /// Ours by the journal, so it would otherwise move to the folder's order and
    /// read "disc 1, track 14".
    /// </remarks>
    [Fact]
    public async Task ATrackNumberBesideADiscNumberIsNeverMoved()
    {
        SkipWithoutTools();
        await SeedAsync(("Miles Davis/Kind of Blue/01 track.flac", 1));

        await RunAsync(TagWriteScope.Library);

        var pressed = await new TagReader(new FileSystemAudioFileStore(_root))
            .ReadAsync(new LibraryPath("Miles Davis/Kind of Blue/01 track.flac"), cancellationToken: Token);

        Assert.False(string.IsNullOrWhiteSpace(pressed.Find("DISCNUMBER")));

        await using (var db = PostgresFixture.CreateContext(_connectionString))
        {
            var file = await db.MediaFiles.SingleAsync(Token);
            file.ReleaseId = null;
            file.TrackId = null;
            file.FolderPosition = 14;
            await db.SaveChangesAsync(Token);
        }

        await RunAsync(TagWriteScope.Library);

        var reading = await new TagReader(new FileSystemAudioFileStore(_root))
            .ReadAsync(new LibraryPath("Miles Davis/Kind of Blue/01 track.flac"), cancellationToken: Token);

        Assert.Equal(pressed.Find("TRACKNUMBER"), reading.Find("TRACKNUMBER"));
    }

    /// <summary>
    /// A file whose date the writer would re-render is not written at all.
    /// </summary>
    /// <remarks>
    /// ATL rebuilds a date from its own parse on every save, so "2008-10" comes
    /// out "2008-10-01" — a day nobody recorded — without the year ever being in
    /// the plan. The owner chose refusal, counted under failed: the file is left
    /// exactly as it was.
    /// </remarks>
    [Fact]
    public async Task AFileWhoseDateTheWriterWouldChangeIsLeftAlone()
    {
        SkipWithoutTools();
        await SeedAsync(("Miles Davis/Kind of Blue/01 track.flac", 1));

        var full = Path.Combine(_root, "Miles Davis/Kind of Blue/01 track.flac");

        using (var tagged = TagLib.File.Create(full))
        {
            ((TagLib.Ogg.XiphComment)tagged.GetTag(TagLib.TagTypes.Xiph, true)).SetField("DATE", "1959-08");
            tagged.Save();
        }

        var before = await System.IO.File.ReadAllBytesAsync(full, Token);

        var summary = await RunAsync(TagWriteScope.Library);

        Assert.Equal(0, summary.Written);
        Assert.Equal(1, summary.Failed);
        Assert.Equal(before, await System.IO.File.ReadAllBytesAsync(full, Token));
    }

    /// <summary>
    /// A full ISO date survives the writer, so a file carrying one is written and keeps it.
    /// </summary>
    [Fact]
    public async Task AFileWithAFullDateIsWrittenAndKeepsIt()
    {
        SkipWithoutTools();
        await SeedAsync(("Miles Davis/Kind of Blue/01 track.flac", 1));

        var full = Path.Combine(_root, "Miles Davis/Kind of Blue/01 track.flac");

        using (var tagged = TagLib.File.Create(full))
        {
            ((TagLib.Ogg.XiphComment)tagged.GetTag(TagLib.TagTypes.Xiph, true)).SetField("DATE", "1959-08-17");
            tagged.Save();
        }

        var summary = await RunAsync(TagWriteScope.Library);

        Assert.Equal(1, summary.Written);

        using var written = TagLib.File.Create(full);
        Assert.Equal(
            ["1959-08-17"],
            ((TagLib.Ogg.XiphComment)written.GetTag(TagLib.TagTypes.Xiph, false)).GetField("DATE"));
    }

    /// <summary>
    /// A file with a disc number and no track number is not given the folder's.
    /// </summary>
    /// <remarks>
    /// The folder's order runs across every disc, so "disc 2, track 14".
    /// </remarks>
    [Fact]
    public async Task AFileWithOnlyItsAlbumAndADiscNumberGetsNoTrackNumber()
    {
        SkipWithoutTools();
        await SeedAsync(("Miles Davis/Kind of Blue/01 track.flac", 1));

        await using (var db = PostgresFixture.CreateContext(_connectionString))
        {
            var file = await db.MediaFiles.SingleAsync(Token);
            file.ReleaseId = null;
            file.TrackId = null;
            file.FolderPosition = 14;
            await db.SaveChangesAsync(Token);
        }

        using (var tagged = TagLib.File.Create(Path.Combine(_root, "Miles Davis/Kind of Blue/01 track.flac")))
        {
            tagged.Tag.Disc = 2;
            tagged.Save();
        }

        await RunAsync(TagWriteScope.Library);

        var reading = await new TagReader(new FileSystemAudioFileStore(_root))
            .ReadAsync(new LibraryPath("Miles Davis/Kind of Blue/01 track.flac"), cancellationToken: Token);

        Assert.Null(reading.Find("TRACKNUMBER"));
        Assert.Equal("Kind of Blue", reading.Fields["ALBUM"]);
    }

    /// <summary>
    /// A file filed under a pressing whose track it has lost is not written.
    /// </summary>
    /// <remarks>
    /// It would carry the pressing's release MBID and track total with a folder
    /// position for a number. It waits for attribution to seat it again.
    /// </remarks>
    [Fact]
    public async Task AFileThatLostItsTrackOnAPressingIsNotWritten()
    {
        SkipWithoutTools();
        await SeedAsync(("Miles Davis/Kind of Blue/01 track.flac", 1));

        await using (var db = PostgresFixture.CreateContext(_connectionString))
        {
            var file = await db.MediaFiles.SingleAsync(Token);
            file.TrackId = null;
            await db.SaveChangesAsync(Token);
        }

        var summary = await RunAsync(TagWriteScope.Library);

        Assert.Equal(0, summary.Examined);
    }

    /// <summary>
    /// A file held to its album alone keeps the track number and the date it
    /// already carries, and still gets everything else.
    /// </summary>
    /// <remarks>
    /// Both are its pressing's facts. No disc number is written without a
    /// pressing, so a rip's own "disc 2, track 1" replaced with its place in the
    /// folder would read "disc 2, track 14"; a 2008 reissue given the album's year
    /// would claim a pressing it is not. Read off the file, not the catalogue: a
    /// tag the attribution pass could not read is still there.
    /// </remarks>
    [Fact]
    public async Task AFileWithOnlyItsAlbumKeepsItsOwnTrackNumberAndDate()
    {
        SkipWithoutTools();
        await SeedAsync(("Miles Davis/Kind of Blue/01 track.flac", 1));

        await using (var db = PostgresFixture.CreateContext(_connectionString))
        {
            var file = await db.MediaFiles.SingleAsync(Token);
            file.ReleaseId = null;
            file.TrackId = null;
            file.FolderPosition = 14;
            await db.SaveChangesAsync(Token);
        }

        using (var tagged = TagLib.File.Create(Path.Combine(_root, "Miles Davis/Kind of Blue/01 track.flac")))
        {
            tagged.Tag.Track = 1;
            tagged.Tag.Year = 2008;
            tagged.Save();
        }

        var summary = await RunAsync(TagWriteScope.Library);

        Assert.Equal(1, summary.Written);

        var reading = await new TagReader(new FileSystemAudioFileStore(_root))
            .ReadAsync(new LibraryPath("Miles Davis/Kind of Blue/01 track.flac"), cancellationToken: Token);

        Assert.Equal("1", reading.Find("TRACKNUMBER"));
        Assert.Equal("2008", reading.Find("YEAR"));
        Assert.Equal("Kind of Blue", reading.Fields["ALBUM"]);
    }

    /// <summary>
    /// The sleeve a person chose is written beside the album, once for the folder.
    /// </summary>
    /// <remarks>
    /// The reason this pass grew a cover at all: choosing one used to write a row
    /// in PostgreSQL and nothing else, so every other player on the same disk went
    /// on showing whatever the rip embedded. Two files, one cover — the unit here
    /// is the directory, not the file.
    /// </remarks>
    [Fact]
    public async Task TheChosenSleeveIsWrittenBesideTheAlbum()
    {
        SkipWithoutTools();

        await SeedAsync(
            ("Miles Davis/Kind of Blue/01 track.flac", 1),
            ("Miles Davis/Kind of Blue/02 track.flac", 2));

        await SeedCoverAsync(ChosenCover);

        var summary = await RunAsync(TagWriteScope.Library);

        Assert.Equal(2, summary.Examined);
        Assert.Equal(1, summary.CoversWritten);

        var written = await File.ReadAllBytesAsync(
            Path.Combine(_root, "Miles Davis/Kind of Blue/cover.jpg"), Token);

        Assert.Equal(ChosenCover, written);
    }

    /// <summary>
    /// A second run leaves the cover it wrote alone.
    /// </summary>
    /// <remarks>
    /// The tags have the diff for a worklist and the cover has its own bytes, for
    /// the same reason: a run over an already-written library must not rewrite
    /// every album's sleeve to say what it already says.
    /// </remarks>
    [Fact]
    public async Task ASecondRunDoesNotRewriteTheCover()
    {
        SkipWithoutTools();

        await SeedAsync(("Miles Davis/Kind of Blue/01 track.flac", 1));
        await SeedCoverAsync(ChosenCover);

        var services = Build();

        Assert.Equal(1, (await RunAsync(TagWriteScope.Library, services)).CoversWritten);

        var cover = Path.Combine(_root, "Miles Davis/Kind of Blue/cover.jpg");
        var stamped = File.GetLastWriteTimeUtc(cover);

        var again = await RunAsync(TagWriteScope.Library, services);

        Assert.Equal(0, again.CoversWritten);
        Assert.Equal(stamped, File.GetLastWriteTimeUtc(cover));
    }

    /// <summary>
    /// A multi-disc rip gets one sleeve, at the album root rather than per disc.
    /// </summary>
    /// <remarks>
    /// The whole point of writing a file rather than embedding a picture is that
    /// a player globs for it beside the album, and what it globs there is
    /// narrower than it looks: Navidrome's <c>CoverArtPriority</c> searches the
    /// album directory for <c>cover</c>, <c>folder</c> and <c>front</c>, while
    /// disc-level art is a separate <c>DiscArtPriority</c> matching <c>disc*</c>
    /// and <c>cd*</c> — not <c>cover.*</c>. A sleeve written into <c>CD 01</c> is
    /// therefore found by nothing and the embedded spread goes on winning, which
    /// is the exact report this feature came from.
    /// </remarks>
    [Fact]
    public async Task AMultiDiscRipGetsOneCoverAtTheAlbumRoot()
    {
        SkipWithoutTools();

        await SeedAsync(
            ("Miles Davis/Kind of Blue/CD 01/01 track.flac", 1),
            ("Miles Davis/Kind of Blue/CD 02/02 track.flac", 2));

        await SeedCoverAsync(ChosenCover);

        var summary = await RunAsync(TagWriteScope.Library);

        Assert.Equal(2, summary.Examined);
        Assert.Equal(1, summary.CoversWritten);

        Assert.Equal(
            ChosenCover,
            await File.ReadAllBytesAsync(
                Path.Combine(_root, "Miles Davis/Kind of Blue/cover.jpg"), Token));

        Assert.Empty(Directory.EnumerateFiles(
            Path.Combine(_root, "Miles Davis/Kind of Blue"), "cover.*", SearchOption.AllDirectories)
            .Where(path => Path.GetDirectoryName(path) != Path.Combine(_root, "Miles Davis/Kind of Blue")));
    }

    /// <summary>
    /// A sleeve under a different extension is displaced too, not left beside it.
    /// </summary>
    /// <remarks>
    /// The name comes from the stored image's own type, so a release whose cover
    /// was a PNG and is now a JPEG would otherwise end up with <c>cover.png</c>
    /// and <c>cover.jpg</c> in one folder. A player's glob then has two matches
    /// and nothing says the new one wins — the same failure, reintroduced by the
    /// fix for it.
    /// </remarks>
    [Fact]
    public async Task ACoverUnderAnotherExtensionIsDisplacedAsWell()
    {
        SkipWithoutTools();

        await SeedAsync(("Miles Davis/Kind of Blue/01 track.flac", 1));
        await SeedCoverAsync(ChosenCover);

        var stale = new byte[] { 0x89, 0x50, 0x4E, 0x47, 4, 5, 6 };
        await File.WriteAllBytesAsync(
            Path.Combine(_root, "Miles Davis/Kind of Blue/cover.png"), stale, Token);

        Assert.Equal(1, (await RunAsync(TagWriteScope.Library)).CoversWritten);

        Assert.Equal(
            new[] { "cover.jpg" },
            Directory.EnumerateFiles(Path.Combine(_root, "Miles Davis/Kind of Blue"), "cover.*")
                .Select(Path.GetFileName)
                .ToArray());

        var displaced = Directory
            .EnumerateFiles(_root + "-trash", "cover.png", SearchOption.AllDirectories)
            .Single();

        Assert.Equal(stale, await File.ReadAllBytesAsync(displaced, Token));
    }

    /// <summary>
    /// A cover already in the folder is moved to the trash, never overwritten.
    /// </summary>
    /// <remarks>
    /// The one destructive thing this pass does outside the write path, so it
    /// does it the way the file manager does: a move to <c>Fonoteca:TrashPath</c>
    /// under a stamped folder. A hand-made sleeve scan is not recoverable from
    /// any provider, and unlike a tag write there is no undo journal carrying the
    /// old bytes.
    /// </remarks>
    [Fact]
    public async Task ACoverAlreadyThereIsDisplacedRatherThanOverwritten()
    {
        SkipWithoutTools();

        await SeedAsync(("Miles Davis/Kind of Blue/01 track.flac", 1));
        await SeedCoverAsync(ChosenCover);

        var existing = new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3 };
        var cover = Path.Combine(_root, "Miles Davis/Kind of Blue/cover.jpg");
        await File.WriteAllBytesAsync(cover, existing, Token);

        Assert.Equal(1, (await RunAsync(TagWriteScope.Library)).CoversWritten);

        Assert.Equal(ChosenCover, await File.ReadAllBytesAsync(cover, Token));

        var displaced = Directory
            .EnumerateFiles(_root + "-trash", "cover.jpg", SearchOption.AllDirectories)
            .Single();

        Assert.Equal(existing, await File.ReadAllBytesAsync(displaced, Token));
    }

    /// <summary>
    /// With mutation off, the folder is left exactly as it was found.
    /// </summary>
    /// <remarks>
    /// The flag is what somebody turns on to agree that this application may
    /// change their files, and a cover written under it would be a file appearing
    /// in a library nobody agreed to touch.
    /// </remarks>
    [Fact]
    public async Task WithMutationOffNoCoverIsWritten()
    {
        SkipWithoutTools();

        await SeedAsync(("Miles Davis/Kind of Blue/01 track.flac", 1));
        await SeedCoverAsync(ChosenCover);

        var summary = await RunAsync(TagWriteScope.Library, Build(allowMutation: false));

        Assert.Equal(0, summary.CoversWritten);
        Assert.False(File.Exists(Path.Combine(_root, "Miles Davis/Kind of Blue/cover.jpg")));
    }

    /// <summary>
    /// A release nobody has chosen a cover for gets no file invented for it.
    /// </summary>
    /// <remarks>
    /// A <c>ReleaseCovers</c> row with null bytes is a stamp recording that both
    /// sources were asked and neither answered. Treating it as a cover would put
    /// an empty file in the folder and hide every sleeve found later.
    /// </remarks>
    [Fact]
    public async Task AStampWithNoBytesIsNotACover()
    {
        SkipWithoutTools();

        await SeedAsync(("Miles Davis/Kind of Blue/01 track.flac", 1));
        await SeedCoverAsync(bytes: null);

        var summary = await RunAsync(TagWriteScope.Library);

        Assert.Equal(0, summary.CoversWritten);
        Assert.False(File.Exists(Path.Combine(_root, "Miles Davis/Kind of Blue/cover.jpg")));
    }

    /// <summary>
    /// A folder held to its album alone gets the sleeve the album page shows.
    /// </summary>
    /// <remarks>
    /// Most of a library has no proven pressing. The album page still draws its
    /// display edition's sleeve for it, so a player reading the same disk has to
    /// find that one beside the files, or it shows nothing where this shows a cover.
    /// Two editions with a sleeve each, so it has to be the display edition's
    /// — the official one — and not merely any.
    /// </remarks>
    [Fact]
    public async Task AFolderWithOnlyItsAlbumGetsTheAlbumsSleeve()
    {
        SkipWithoutTools();

        await SeedAsync(("Miles Davis/Kind of Blue/01 track.flac", 1));
        await SeedCoverAsync(ChosenCover);

        var official = new byte[] { 0xFF, 0xD8, 0xFF, 0xE1, 7, 7, 7 };

        await using (var db = PostgresFixture.CreateContext(_connectionString))
        {
            var file = await db.MediaFiles.SingleAsync(Token);
            file.ReleaseId = null;
            file.TrackId = null;

            var edition = new Release
            {
                Id = ReleaseId.New(),
                Title = "Kind of Blue",
                Mbid = Mb("55555555-5555-4555-8555-555555555555"),
                ReleaseGroupId = file.ReleaseGroupId,
                Status = "Official",
            };

            db.Releases.Add(edition);
            db.ReleaseCovers.Add(new ReleaseCover
            {
                ReleaseId = edition.Id,
                Bytes = official,
                MediaType = "image/jpeg",
                ArchiveImageId = 5678,
                SavedUtc = DateTimeOffset.UtcNow,
            });

            await db.SaveChangesAsync(Token);
        }

        Assert.Equal(1, (await RunAsync(TagWriteScope.Library)).CoversWritten);

        Assert.Equal(
            official,
            await File.ReadAllBytesAsync(
                Path.Combine(_root, "Miles Davis/Kind of Blue/cover.jpg"), Token));
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

    /// <summary>
    /// The artist button leaves the artist's picture on their shelf.
    /// </summary>
    /// <remarks>
    /// The point of the whole thing: Navidrome, Jellyfin, Kodi and Plex all look
    /// for <c>artist.*</c> beside an artist's records before they ask anybody
    /// external, so this is what stops two catalogues on one disk showing two
    /// different faces for the same person.
    /// </remarks>
    [Fact]
    public async Task TheArtistsOwnShelfGetsTheirPicture()
    {
        SkipWithoutTools();

        await SeedAsync(("Miles Davis/Kind of Blue/01 track.flac", 1));
        await SeedPortraitAsync();

        var summary = await RunAsync(TagWriteScope.ForArtist(await MilesAsync(), "Miles Davis"));

        Assert.Equal(1, summary.PortraitsWritten);
        Assert.Equal(
            PortraitBytes,
            await File.ReadAllBytesAsync(Path.Combine(_root, "Miles Davis/artist.jpg"), Token));
    }

    /// <summary>
    /// A shelf that merely holds their playing is not their shelf.
    /// </summary>
    /// <remarks>
    /// <b>The failure this rule exists for, in the shape it was measured in.</b>
    /// 47 of Janine Jansen's 54 files sit under <c>Johann Sebastian Bach</c> and
    /// <c>Antonio Vivaldi</c>, because a classical library files a performance
    /// under its composer — and every one of them is on her page, so every one of
    /// them reaches this pass under her scope. Without the name check her
    /// photograph lands on two dead composers' shelves and every other player on
    /// the disk starts showing a violinist as Bach.
    /// </remarks>
    [Fact]
    public async Task ThePictureDoesNotLandOnAComposersShelf()
    {
        SkipWithoutTools();

        await SeedAsync(
            ("Miles Davis/Kind of Blue/01 track.flac", 1),
            ("Johann Sebastian Bach/Violin Concertos/01 track.flac", 2));

        await SeedPortraitAsync();

        var summary = await RunAsync(TagWriteScope.ForArtist(await MilesAsync(), "Miles Davis"));

        Assert.Equal(1, summary.PortraitsWritten);
        Assert.True(File.Exists(Path.Combine(_root, "Miles Davis/artist.jpg")));
        Assert.Empty(Directory.GetFiles(Path.Combine(_root, "Johann Sebastian Bach"), "artist.*"));
    }

    /// <summary>
    /// The other two buttons name nobody, so they write no picture.
    /// </summary>
    /// <remarks>
    /// Not an oversight and not a smaller version of the artist run: a library
    /// pass walks every shelf in the place, and "which artist is this folder for"
    /// is a question it has no answer to. The one thing that can answer it is a
    /// person pressing the button on one artist's page.
    /// </remarks>
    [Fact]
    public async Task TheLibraryButtonWritesNoPicture()
    {
        SkipWithoutTools();

        await SeedAsync(("Miles Davis/Kind of Blue/01 track.flac", 1));
        await SeedPortraitAsync();

        var summary = await RunAsync(TagWriteScope.Library);

        Assert.Equal(0, summary.PortraitsWritten);
        Assert.Empty(Directory.GetFiles(Path.Combine(_root, "Miles Davis"), "artist.*"));
        Assert.Empty(_portraits.Requests);
    }

    /// <summary>
    /// A picture already on the shelf is displaced by the one the catalogue shows.
    /// </summary>
    /// <remarks>
    /// <b>The same rule a sleeve follows, and it has to be.</b> This application
    /// shows the artist's picture and a person can change which one it is; a
    /// shelf that kept whatever was there first would be a one-way door, and the
    /// page and every other player on the disk would show different faces for
    /// the same person — which is the whole complaint this feature exists for.
    /// </remarks>
    [Fact]
    public async Task APictureAlreadyOnTheShelfIsDisplaced()
    {
        SkipWithoutTools();

        await SeedAsync(("Miles Davis/Kind of Blue/01 track.flac", 1));
        await SeedPortraitAsync();

        var existing = new byte[] { 1, 2, 3 };
        var stale = Path.Combine(_root, "Miles Davis/artist.png");
        await File.WriteAllBytesAsync(stale, existing, Token);

        var summary = await RunAsync(TagWriteScope.ForArtist(await MilesAsync(), "Miles Davis"));

        Assert.Equal(1, summary.PortraitsWritten);
        Assert.Equal(
            PortraitBytes,
            await File.ReadAllBytesAsync(Path.Combine(_root, "Miles Davis/artist.jpg"), Token));

        // Under a different extension, so leaving it would give a player's glob
        // two matches and let the stale one win.
        Assert.False(File.Exists(stale));

        var displaced = Directory
            .EnumerateFiles(_root + "-trash", "artist.png", SearchOption.AllDirectories)
            .Single();

        Assert.Equal(existing, await File.ReadAllBytesAsync(displaced, Token));
    }

    /// <summary>
    /// A second run over an unchanged shelf writes nothing and displaces nothing.
    /// </summary>
    /// <remarks>
    /// The bytes are the worklist, as the diff is for tags. Unlike a sleeve —
    /// which is read from the database for nothing — proving a shelf is already
    /// right means having the picture to compare it against, so the request is
    /// made and the write is what gets skipped. One request per run, not per
    /// file, and nothing lands in the trash for a picture that did not change.
    /// </remarks>
    [Fact]
    public async Task ASecondRunOverAnUnchangedShelfWritesNothing()
    {
        SkipWithoutTools();

        await SeedAsync(("Miles Davis/Kind of Blue/01 track.flac", 1));
        await SeedPortraitAsync();

        var scope = TagWriteScope.ForArtist(await MilesAsync(), "Miles Davis");

        Assert.Equal(1, (await RunAsync(scope)).PortraitsWritten);
        Assert.Single(_portraits.Requests);

        var written = Path.Combine(_root, "Miles Davis/artist.jpg");
        var stamped = File.GetLastWriteTimeUtc(written);

        Assert.Equal(0, (await RunAsync(scope)).PortraitsWritten);

        // Asked again, and that is the cost of comparing.
        Assert.Equal(2, _portraits.Requests.Count);

        // Not rewritten, and the old one not trashed on the way.
        Assert.Equal(stamped, File.GetLastWriteTimeUtc(written));
        Assert.False(Directory.Exists(_root + "-trash"));
    }

    /// <summary>
    /// With file mutation off, nothing is written and nothing is downloaded.
    /// </summary>
    /// <remarks>
    /// The flag gates this as it gates the tags and the covers. The download sits
    /// behind it rather than in front: a dry run that spent a request on a
    /// picture it cannot write would be spending somebody else's bandwidth to
    /// produce nothing.
    /// </remarks>
    [Fact]
    public async Task WithMutationOffNoPictureIsFetchedOrWritten()
    {
        SkipWithoutTools();

        await SeedAsync(("Miles Davis/Kind of Blue/01 track.flac", 1));
        await SeedPortraitAsync();

        var summary = await RunAsync(
            TagWriteScope.ForArtist(await MilesAsync(), "Miles Davis"),
            Build(allowMutation: false));

        Assert.Equal(0, summary.PortraitsWritten);
        Assert.Empty(Directory.GetFiles(Path.Combine(_root, "Miles Davis"), "artist.*"));
        Assert.Empty(_portraits.Requests);
    }

    /// <summary>
    /// An artist nobody has found a picture of is not a folder to write into.
    /// </summary>
    [Fact]
    public async Task AnArtistWithNoPictureGetsNoFile()
    {
        SkipWithoutTools();

        await SeedAsync(("Miles Davis/Kind of Blue/01 track.flac", 1));

        var summary = await RunAsync(TagWriteScope.ForArtist(await MilesAsync(), "Miles Davis"));

        Assert.Equal(0, summary.PortraitsWritten);
        Assert.Empty(Directory.GetFiles(Path.Combine(_root, "Miles Davis"), "artist.*"));
        Assert.Empty(_portraits.Requests);
    }

    /// <summary>
    /// A picture host that hangs costs the picture and nothing else.
    /// </summary>
    /// <remarks>
    /// <b>The regression test for the sharpest edge in this feature.</b>
    /// <c>HttpClient.Timeout</c> does not throw <c>TimeoutException</c> — it
    /// cancels its own source, so what comes out is a
    /// <c>TaskCanceledException</c>, which <i>is</i> an
    /// <c>OperationCanceledException</c> with nobody's token cancelled. Caught
    /// by the filter the sleeve can afford, it escaped past the caller's
    /// <c>SaveChanges</c> and left the row holding the old size against a file
    /// whose tags had already been written — rule 2, and the next scan then
    /// discards every derived column on it. The per-file backstop rethrows a
    /// cancellation, and the run reported a clean finish because nobody had
    /// cancelled anything. So: the rescan at the end is the assertion that
    /// matters.
    /// </remarks>
    [Fact]
    public async Task APictureHostThatHangsCostsOnlyThePicture()
    {
        SkipWithoutTools();

        await SeedAsync(("Miles Davis/Kind of Blue/01 track.flac", 1));
        await SeedPortraitAsync();

        _portraitFailure = new TaskCanceledException("timed out", new TimeoutException());

        var services = Build();

        var summary = await RunAsync(
            TagWriteScope.ForArtist(await MilesAsync(), "Miles Davis"), services);

        Assert.False(summary.Cancelled);
        Assert.Equal(1, summary.Examined);
        Assert.Equal(1, summary.Written);
        Assert.Equal(0, summary.Failed);
        Assert.Equal(0, summary.PortraitsWritten);

        // The row carries what was written, so the scan reads the file as the
        // one it already knows rather than as a modified one to strip.
        var rescan = await services.GetRequiredService<LibraryScanService>().ScanAsync(Token);

        Assert.Equal(0, rescan.Summary!.Updated);
        Assert.Equal(1, rescan.Summary.Unchanged);

        await using var db = PostgresFixture.CreateContext(_connectionString);
        var row = await db.MediaFiles.SingleAsync(Token);

        Assert.NotNull(row.RecordingId);
        Assert.NotNull(row.ReleaseId);
    }

    /// <summary>
    /// An uploaded picture is the one written, and nothing is fetched for it.
    /// </summary>
    /// <remarks>
    /// Rule 4: a person's answer and a provider's are different facts, and the
    /// person's wins. The assertion that matters is the second one — the bytes
    /// are already in the database, so a run that has an upload makes no request
    /// at all and cannot be held up by somebody else's CDN.
    /// </remarks>
    [Fact]
    public async Task AnUploadedPictureIsWrittenInsteadOfTheProvidersAndCostsNoRequest()
    {
        SkipWithoutTools();

        await SeedAsync(("Miles Davis/Kind of Blue/01 track.flac", 1));
        await SeedPortraitAsync();

        var chosen = new byte[] { 0xFF, 0xD8, 0xFF, 0xE1, 7, 7, 7 };

        await using (var db = PostgresFixture.CreateContext(_connectionString))
        {
            db.ArtistImages.Add(new ArtistImage
            {
                ArtistId = await MilesAsync(),
                Kind = ArtistImageKind.Portrait.Name,
                Bytes = chosen,
                MediaType = "image/jpeg",
                SavedUtc = DateTimeOffset.UtcNow,
            });

            await db.SaveChangesAsync(Token);
        }

        var summary = await RunAsync(TagWriteScope.ForArtist(await MilesAsync(), "Miles Davis"));

        Assert.Equal(1, summary.PortraitsWritten);
        Assert.Equal(
            chosen,
            await File.ReadAllBytesAsync(Path.Combine(_root, "Miles Davis/artist.jpg"), Token));

        Assert.Empty(_portraits.Requests);
    }

    /// <summary>
    /// Both of an artist's pictures go onto the shelf in one run.
    /// </summary>
    /// <remarks>
    /// The banner is written as <c>backdrop.*</c> because that is what this
    /// library already holds 123 of — the stated consequence being that
    /// whatever wrote those writes the same name, so the two tools displace
    /// each other's file. Navidrome shows no artist banner at all; this one is
    /// for Jellyfin, Kodi and Plex.
    /// </remarks>
    [Fact]
    public async Task BothPicturesReachTheShelfInOneRun()
    {
        SkipWithoutTools();

        await SeedAsync(("Miles Davis/Kind of Blue/01 track.flac", 1));
        await SeedPortraitAsync();

        var banner = new byte[] { 0xFF, 0xD8, 0xFF, 0xE2, 4, 4, 4 };

        await using (var db = PostgresFixture.CreateContext(_connectionString))
        {
            db.ArtistImages.Add(new ArtistImage
            {
                ArtistId = await MilesAsync(),
                Kind = ArtistImageKind.Banner.Name,
                Bytes = banner,
                MediaType = "image/jpeg",
                SavedUtc = DateTimeOffset.UtcNow,
            });

            await db.SaveChangesAsync(Token);
        }

        var summary = await RunAsync(TagWriteScope.ForArtist(await MilesAsync(), "Miles Davis"));

        // Two pictures, one shelf, one run.
        Assert.Equal(2, summary.PortraitsWritten);
        Assert.Equal(
            PortraitBytes,
            await File.ReadAllBytesAsync(Path.Combine(_root, "Miles Davis/artist.jpg"), Token));
        Assert.Equal(
            banner,
            await File.ReadAllBytesAsync(Path.Combine(_root, "Miles Davis/backdrop.jpg"), Token));
    }

    /// <summary>The picture a provider found, as the portraits stage leaves it.</summary>
    private async Task SeedPortraitAsync(string url = PortraitUrl)
    {
        await using var db = PostgresFixture.CreateContext(_connectionString);

        var artist = await db.Artists.SingleAsync(row => row.Mbid == ArtistMbid, Token);

        artist.PortraitUrl = url;
        artist.PortraitLookupUtc = DateTimeOffset.UtcNow;

        await db.SaveChangesAsync(Token);
    }

    private async Task<ArtistId> MilesAsync()
    {
        await using var db = PostgresFixture.CreateContext(_connectionString);

        return await db.Artists
            .Where(artist => artist.Mbid == ArtistMbid)
            .Select(artist => artist.Id)
            .SingleAsync(Token);
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

        // The portrait is the one thing this pass fetches rather than reads, so
        // the socket under it is stubbed the way the provider suites stub theirs.
        services.AddSingleton<IHttpClientFactory>(_ => new StubClients(_portraits));

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
        File.Copy(path.EndsWith(".mp3", StringComparison.Ordinal) ? Corpus.Mp3 : Corpus.Flac, full, overwrite: true);

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

    /// <summary>
    /// One handler behind every client, so a test can see what was fetched.
    /// </summary>
    /// <remarks>
    /// <c>IHttpClientFactory</c> rather than a registered named client, because
    /// the pass asks for the plain one — it is downloading a static image from a
    /// CDN, not calling a provider's API through its gate.
    /// </remarks>
    private sealed class StubClients(StubHttpHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            new(handler, disposeHandler: false);
    }
}
