using System.Globalization;
using Fonoteca.Api.Acquisition;
using Fonoteca.Api.Configuration;
using Fonoteca.Domain.Acquisition;
using Fonoteca.Domain.Abstractions;
using Fonoteca.Domain.Catalogue;
using Fonoteca.Fixtures;
using Fonoteca.Ingest;
using Fonoteca.Providers.Qobuz;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Fonoteca.Integration.Tests;

/// <summary>
/// Retiring the album an upgrade replaced, against real audio on a real disk.
/// </summary>
/// <remarks>
/// <see cref="UpgradeReplacement"/> has the rule's own tests and they are pure.
/// What is under test here is everything the rule cannot see: that the files
/// really move, that they land somewhere a person can get them back from, that
/// the library is left without them, and — the four cases that matter most —
/// that a refusal moves <b>nothing</b>.
///
/// The download half is not stubbed. It is already covered by
/// <c>QobuzDownloadTests</c>, and what makes this operation dangerous is not
/// where the bytes came from; it is what happens to the bytes that were already
/// there. So the "download" is real files placed on disk and an
/// <see cref="AlbumDownload"/> describing them, which is exactly what the
/// service receives.
/// </remarks>
[Collection(nameof(PostgresCollection))]
public sealed class AlbumReplacementTests(PostgresFixture postgres) : IAsyncLifetime
{
    private const string OldFolder = "Old Artist/Old Album (2003)";
    private const string NewFolder = "New Artist/New Album";
    private const string Fetched = QobuzDownloadService.ReplacementArea + "/1";

    private string _connectionString = string.Empty;
    private string _root = string.Empty;
    private string _archive = string.Empty;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _connectionString = await postgres.CreateDatabaseAsync(Token);
        _root = Directory.CreateTempSubdirectory("fonoteca-replace-").FullName;
        _archive = _root + "-replaced";

        // No host here — the download half is not under test and starting one
        // just to migrate would drag every background service in with it.
        await using var db = PostgresFixture.CreateContext(_connectionString);
        await db.Database.MigrateAsync(Token);
    }

    public ValueTask DisposeAsync()
    {
        foreach (var directory in new[] { _root, _archive })
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }

        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task ALosslessDownloadRetiresTheLossyAlbumItReplaces()
    {
        Assert.SkipUnless(Corpus.IsAvailable, "ffmpeg is not on PATH; source ~/.local/opt/env.sh.");

        await GivenHeldAsync(Mp3(), $"{OldFolder}/01 Track.mp3");
        var download = GivenDownloaded(Corpus.Flac, $"{NewFolder}/01 Track.flac");

        var result = await Service().ReplaceAsync(OldFolder, 0, download, Token);

        Assert.Equal(ReplacementVerdict.Replace, result.Verdict);
        Assert.Equal(1, result.Archived);

        // Out of the library...
        Assert.False(File.Exists(Path.Combine(_root, OldFolder.Replace('/', Path.DirectorySeparatorChar), "01 Track.mp3")));

        // ...and into somewhere a person can get it back from, keeping the layout
        // so undoing this is a mv rather than a restore.
        Assert.NotNull(result.ArchivedTo);
        Assert.True(File.Exists(Path.Combine(result.ArchivedTo, "01 Track.mp3")));

        // The new album is untouched.
        Assert.True(File.Exists(Path.Combine(_root, "New Artist", "New Album", "01 Track.flac")));

        // And the emptied directories are gone, so the library does not fill up
        // with the skeletons of replaced albums.
        Assert.False(Directory.Exists(Path.Combine(_root, "Old Artist")));
    }

    [Fact]
    public async Task AShortDownloadMovesNothing()
    {
        Assert.SkipUnless(Corpus.IsAvailable, "ffmpeg is not on PATH; source ~/.local/opt/env.sh.");

        await GivenHeldAsync(Mp3(), $"{OldFolder}/01 Track.mp3", $"{OldFolder}/02 Track.mp3");
        var download = GivenDownloaded(Corpus.Flac, $"{NewFolder}/01 Track.flac");

        var result = await Service().ReplaceAsync(OldFolder, 0, download, Token);

        Assert.Equal(ReplacementVerdict.Incomplete, result.Verdict);
        Assert.Equal(0, result.Archived);

        // Both of them still there. A licensing gap must never cost a track.
        Assert.Equal(2, Directory.GetFiles(
            Path.Combine(_root, "Old Artist"), "*.mp3", SearchOption.AllDirectories).Length);
    }

    [Fact]
    public async Task ADownloadNoBetterThanWhatIsHeldMovesNothing()
    {
        Assert.SkipUnless(Corpus.IsAvailable, "ffmpeg is not on PATH; source ~/.local/opt/env.sh.");

        // The same file on both sides, so the reading really is identical
        // rather than approximately so — AudioQuality.Compare breaks a tie
        // between two lossless files on size, and a hand-written bitrate would
        // decide this test rather than the rule under it. A tie is not an
        // upgrade: re-downloading the master already on disk and deleting the
        // original is a pure loss of provenance.
        await GivenHeldAsync(null, [$"{OldFolder}/01 Track.flac"], Corpus.Flac);
        await MeasureHeldAsync($"{OldFolder}/01 Track.flac");

        var download = GivenDownloaded(Corpus.Flac, $"{NewFolder}/01 Track.flac");

        var result = await Service().ReplaceAsync(OldFolder, 0, download, Token);

        Assert.Equal(ReplacementVerdict.NotBetter, result.Verdict);
        Assert.Equal(0, result.Archived);
        Assert.True(File.Exists(Path.Combine(_root, "Old Artist", "Old Album (2003)", "01 Track.flac")));
    }

    [Fact]
    public async Task AnUnmeasuredAlbumIsRefusedRatherThanAssumed()
    {
        Assert.SkipUnless(Corpus.IsAvailable, "ffmpeg is not on PATH; source ~/.local/opt/env.sh.");

        await GivenHeldAsync(quality: null, $"{OldFolder}/01 Track.mp3");
        var download = GivenDownloaded(Corpus.Flac, $"{NewFolder}/01 Track.flac");

        var result = await Service().ReplaceAsync(OldFolder, 0, download, Token);

        Assert.Equal(ReplacementVerdict.NotMeasured, result.Verdict);
        Assert.Equal(0, result.Archived);
        Assert.Contains("measure pass", result.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WithTheFlagOffTheDownloadStillHappensAndNothingIsMoved()
    {
        Assert.SkipUnless(Corpus.IsAvailable, "ffmpeg is not on PATH; source ~/.local/opt/env.sh.");

        await GivenHeldAsync(Mp3(), $"{OldFolder}/01 Track.mp3");
        var download = GivenDownloaded(Corpus.Flac, $"{NewFolder}/01 Track.flac");

        var result = await Service(allowReplacement: false).ReplaceAsync(OldFolder, 0, download, Token);

        // A complete dry run: the verdict says it would have replaced, and both
        // copies are on disk. The same property AcoustIdTaggedUtc gives
        // identification when file mutation is off.
        Assert.Equal(ReplacementVerdict.Replace, result.Verdict);
        Assert.Equal(0, result.Archived);
        Assert.Contains("AllowFileReplacement", result.Detail, StringComparison.Ordinal);

        Assert.True(File.Exists(Path.Combine(_root, "Old Artist", "Old Album (2003)", "01 Track.mp3")));
        Assert.True(File.Exists(Path.Combine(_root, "New Artist", "New Album", "01 Track.flac")));
    }

    [Fact]
    public async Task ADownloadThatLandedInsideTheAlbumBeingReplacedIsRefused()
    {
        Assert.SkipUnless(Corpus.IsAvailable, "ffmpeg is not on PATH; source ~/.local/opt/env.sh.");

        // Qobuz's own metadata decides where a download lands, and nothing stops
        // that being the folder being replaced. Archiving it moves the new album
        // into the bin and reports an upgrade.
        await GivenHeldAsync(Mp3(), $"{OldFolder}/01 Track.mp3");
        var download = GivenDownloaded(Corpus.Flac, $"{OldFolder}/02 Track.flac");

        var result = await Service().ReplaceAsync(OldFolder, 0, download, Token);

        Assert.Equal(0, result.Archived);
        Assert.True(File.Exists(Path.Combine(_root, "Old Artist", "Old Album (2003)", "02 Track.flac")));
        Assert.True(File.Exists(Path.Combine(_root, "Old Artist", "Old Album (2003)", "01 Track.mp3")));
    }

    [Fact]
    public async Task AFolderTheCatalogueKnowsNothingAboutIsRefused()
    {
        Assert.SkipUnless(Corpus.IsAvailable, "ffmpeg is not on PATH; source ~/.local/opt/env.sh.");

        var download = GivenDownloaded(Corpus.Flac, $"{NewFolder}/01 Track.flac");

        var result = await Service().ReplaceAsync("Someone/Nothing Here", 0, download, Token);

        Assert.Equal(0, result.Archived);
        Assert.False(Directory.Exists(_archive));
    }

    [Fact]
    public async Task APartlyFiledAlbumIsMeasuredByItsWholeSizeNotItsFiledPart()
    {
        Assert.SkipUnless(Corpus.IsAvailable, "ffmpeg is not on PATH; source ~/.local/opt/env.sh.");

        // The bug this test exists for, and it failed open. Counting only the
        // files that carry a TrackId made a folder with one filed file among
        // twenty-one collapse to "one track held" — so a one-track download
        // satisfied "every track arrived" and archived all twenty-one. Partial
        // attribution is the normal state of a catalogue: measured on the real
        // library it was 156 of 519 rows and 535 files that nothing replaced.
        await GivenHeldAsync(
            Mp3(),
            $"{OldFolder}/01 Track.mp3",
            $"{OldFolder}/02 Track.mp3",
            $"{OldFolder}/03 Track.mp3");

        await GivenFiledAsync($"{OldFolder}/01 Track.mp3");

        var download = GivenDownloaded(Corpus.Flac, $"{NewFolder}/01 Track.flac");

        var result = await Service().ReplaceAsync(OldFolder, 0, download, Token);

        Assert.Equal(ReplacementVerdict.Incomplete, result.Verdict);
        Assert.Equal(0, result.Archived);
        Assert.Equal(3, Directory.GetFiles(
            Path.Combine(_root, "Old Artist"), "*.mp3", SearchOption.AllDirectories).Length);
    }

    [Fact]
    public async Task DuplicateEncodingsOfOneTrackStillCountOnce()
    {
        Assert.SkipUnless(Corpus.IsAvailable, "ffmpeg is not on PATH; source ~/.local/opt/env.sh.");

        // The other side of the same rule, and the reason it is not just a file
        // count: two encodings sharing a TrackId are one track of the album, so
        // a one-track download really is complete.
        await GivenHeldAsync(Mp3(), $"{OldFolder}/01 Track.mp3", $"{OldFolder}/01 Track alt.mp3");
        await GivenFiledAsync($"{OldFolder}/01 Track.mp3", $"{OldFolder}/01 Track alt.mp3");

        var download = GivenDownloaded(Corpus.Flac, $"{NewFolder}/01 Track.flac");

        var result = await Service().ReplaceAsync(OldFolder, 0, download, Token);

        Assert.Equal(ReplacementVerdict.Replace, result.Verdict);
        Assert.Equal(2, result.Archived);
    }

    [Fact]
    public async Task AFolderHoldingMoreThanTheRowSaidIsRefused()
    {
        Assert.SkipUnless(Corpus.IsAvailable, "ffmpeg is not on PATH; source ~/.local/opt/env.sh.");

        // A row's folder is not always an album: a loose file at
        // "Artist/track.flac" produces "Artist", and on a Genre/Artist/Album
        // library every row's folder is "Genre/Artist" — where the prefix
        // matches a whole discography while the screen said eleven files.
        await GivenHeldAsync(Mp3(), $"{OldFolder}/01 Track.mp3", $"{OldFolder}/02 Track.mp3");
        var download = GivenDownloaded(Corpus.Flac, $"{NewFolder}/01 Track.flac");

        var result = await Service().ReplaceAsync(OldFolder, expectedFiles: 1, download, Token);

        Assert.Equal(ReplacementVerdict.NotHeld, result.Verdict);
        Assert.Equal(0, result.Archived);
    }

    [Fact]
    public async Task TheArchiveIsStampedSoASecondReplacementDoesNotEatTheFirst()
    {
        Assert.SkipUnless(Corpus.IsAvailable, "ffmpeg is not on PATH; source ~/.local/opt/env.sh.");

        // Replaced twice, which is the only thing that can catch this. Unstamped,
        // the archive path for an album was fixed forever and
        // File.Move(overwrite: true) moved the second copy over the first — the
        // one path in this feature where bytes actually went away. A test that
        // only asserts the path *looks* stamped would pass against that.
        var first = new FixedClock(DateTimeOffset.Parse(
            "2026-03-01T10:00:00Z", CultureInfo.InvariantCulture));

        var second = new FixedClock(DateTimeOffset.Parse(
            "2026-03-01T10:00:01Z", CultureInfo.InvariantCulture));

        await GivenHeldAsync(Mp3(), $"{OldFolder}/01 Track.mp3");
        await File.WriteAllTextAsync(
            Path.Combine(_root, "Old Artist", "Old Album (2003)", "01 Track.mp3"),
            "the original rip", Token);

        var one = await Service(clock: first)
            .ReplaceAsync(OldFolder, 0, GivenDownloaded(Corpus.Flac, $"{NewFolder}/01 Track.flac"), Token);

        Assert.Equal(1, one.Archived);
        Assert.NotNull(one.ArchivedTo);
        Assert.StartsWith(_archive, one.ArchivedTo, StringComparison.Ordinal);

        // The same album comes back — a different rip at the same path. Only the
        // file: the catalogue row survived the first replacement untouched,
        // which is the design (the scan is what reconciles rows against disk).
        Directory.CreateDirectory(Path.Combine(_root, "Old Artist", "Old Album (2003)"));
        await File.WriteAllTextAsync(
            Path.Combine(_root, "Old Artist", "Old Album (2003)", "01 Track.mp3"),
            "the second rip", Token);

        var two = await Service(clock: second)
            .ReplaceAsync(OldFolder, 0, GivenDownloaded(Corpus.Flac, $"{NewFolder}/01 Track.flac"), Token);

        Assert.Equal(1, two.Archived);
        Assert.NotEqual(one.ArchivedTo, two.ArchivedTo);

        // Both are still there, and each is the rip it was.
        Assert.Equal(
            "the original rip",
            await File.ReadAllTextAsync(Path.Combine(one.ArchivedTo, "01 Track.mp3"), Token));

        Assert.Equal(
            "the second rip",
            await File.ReadAllTextAsync(Path.Combine(two.ArchivedTo!, "01 Track.mp3"), Token));
    }

    [Fact]
    public async Task AResumedUpgradeCanStillReplace()
    {
        Assert.SkipUnless(Corpus.IsAvailable, "ffmpeg is not on PATH; source ~/.local/opt/env.sh.");

        // An interrupted upgrade re-run reports its already-present tracks as
        // Skipped rather than Downloaded — with a path. Counting only Downloaded
        // made the feature permanently unusable after any interruption, and said
        // "no track downloaded" with the whole album sitting on disk.
        await GivenHeldAsync(Mp3(), $"{OldFolder}/01 Track.mp3");

        var download = GivenDownloaded(Corpus.Flac, $"{NewFolder}/01 Track.flac");
        var resumed = download with
        {
            Tracks = [download.Tracks[0] with { Outcome = TrackOutcome.Skipped }],
        };

        var result = await Service().ReplaceAsync(OldFolder, 0, resumed, Token);

        Assert.Equal(ReplacementVerdict.Replace, result.Verdict);
        Assert.Equal(1, result.Archived);
    }

    [Fact]
    public async Task ATrackQobuzDoesNotOfferDoesNotCountAsOneThatArrived()
    {
        Assert.SkipUnless(Corpus.IsAvailable, "ffmpeg is not on PATH; source ~/.local/opt/env.sh.");

        // The other half of "has a path" — a licensing gap is Skipped too, and
        // carries no path, which is exactly the one that must not count.
        await GivenHeldAsync(Mp3(), $"{OldFolder}/01 Track.mp3", $"{OldFolder}/02 Track.mp3");

        var download = GivenDownloaded(Corpus.Flac, $"{NewFolder}/01 Track.flac");
        var gap = download with
        {
            Tracks =
            [
                download.Tracks[0],
                new TrackDownload(2, 1, 2, "Track 2", TrackOutcome.Skipped, null, null, null, null, null,
                    "Qobuz does not offer this track."),
            ],
        };

        var result = await Service().ReplaceAsync(OldFolder, 0, gap, Token);

        Assert.Equal(ReplacementVerdict.Incomplete, result.Verdict);
        Assert.Equal(0, result.Archived);
    }

    [Fact]
    public async Task ADownloadFetchedToReplaceTakesTheOldAlbumsPlace()
    {
        Assert.SkipUnless(Corpus.IsAvailable, "ffmpeg is not on PATH; source ~/.local/opt/env.sh.");

        // The upgrade that used to fail outright: Qobuz names the album the
        // same as the folder it replaces, so it is fetched into the hidden area
        // and moved in once the old files are archived.
        await GivenHeldAsync(Mp3(), $"{OldFolder}/01 Track.mp3");
        await File.WriteAllTextAsync(Path.Combine(_root, "Old Artist", "Old Album (2003)", "cover.jpg"), "sleeve", Token);
        await File.WriteAllTextAsync(Path.Combine(_root, "Old Artist", "Old Album (2003)", "Old Album.cue"), "FILE \"01 Track.mp3\"", Token);

        var download = GivenDownloaded(Corpus.Flac, $"{Fetched}/01 Track.flac");

        var result = await Service().ReplaceAsync(OldFolder, 1, download, Token);

        Assert.Equal(ReplacementVerdict.Replace, result.Verdict);
        Assert.Equal(1, result.Archived);
        Assert.Equal(OldFolder, result.DownloadedTo);

        var folder = Path.Combine(_root, "Old Artist", "Old Album (2003)");
        Assert.True(File.Exists(Path.Combine(folder, "01 Track.flac")));
        Assert.False(File.Exists(Path.Combine(folder, "01 Track.mp3")));
        Assert.True(File.Exists(Path.Combine(folder, "cover.jpg")));
        Assert.False(File.Exists(Path.Combine(folder, "Old Album.cue")));
        Assert.True(File.Exists(Path.Combine(result.ArchivedTo!, "Old Album.cue")));
        Assert.False(Directory.Exists(Path.Combine(_root, Fetched)));
    }

    [Fact]
    public async Task ARefusedReplacementRemovesWhatWasFetched()
    {
        Assert.SkipUnless(Corpus.IsAvailable, "ffmpeg is not on PATH; source ~/.local/opt/env.sh.");

        await GivenHeldAsync(null, [$"{OldFolder}/01 Track.flac"], Corpus.Flac);
        await MeasureHeldAsync($"{OldFolder}/01 Track.flac");

        var download = GivenDownloaded(Corpus.Flac, $"{Fetched}/01 Track.flac");

        var result = await Service().ReplaceAsync(OldFolder, 1, download, Token);

        Assert.Equal(ReplacementVerdict.NotBetter, result.Verdict);
        Assert.Contains("removed", result.Detail, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(_root, "Old Artist", "Old Album (2003)", "01 Track.flac")));
        Assert.False(Directory.Exists(Path.Combine(_root, Fetched)));
    }

    [Fact]
    public async Task AFetchedDownloadWithAFailedTrackIsKeptToResume()
    {
        Assert.SkipUnless(Corpus.IsAvailable, "ffmpeg is not on PATH; source ~/.local/opt/env.sh.");

        await GivenHeldAsync(Mp3(), $"{OldFolder}/01 Track.mp3", $"{OldFolder}/02 Track.mp3");

        var download = GivenDownloaded(Corpus.Flac, $"{Fetched}/01 Track.flac");
        var broken = download with
        {
            Tracks =
            [
                download.Tracks[0],
                new TrackDownload(2, 1, 2, "Track 2", TrackOutcome.Failed, null, null, null, null, null,
                    "The transfer was cut short."),
            ],
        };

        var result = await Service().ReplaceAsync(OldFolder, 2, broken, Token);

        Assert.Equal(ReplacementVerdict.Incomplete, result.Verdict);
        Assert.Contains("resumes", result.Detail, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(_root, Fetched, "01 Track.flac")));
    }

    [Fact]
    public async Task AFetchedTrackIsNotMovedOverAFileTheCatalogueDoesNotHold()
    {
        Assert.SkipUnless(Corpus.IsAvailable, "ffmpeg is not on PATH; source ~/.local/opt/env.sh.");

        await GivenHeldAsync(Mp3(), $"{OldFolder}/01 Track.mp3");
        File.Copy(Corpus.Flac, Path.Combine(_root, "Old Artist", "Old Album (2003)", "01 Track.flac"));

        var download = GivenDownloaded(Corpus.Flac, $"{Fetched}/01 Track.flac");

        var result = await Service().ReplaceAsync(OldFolder, 1, download, Token);

        // Refused before the old album is archived, so the library still has it.
        Assert.Equal(0, result.Archived);
        Assert.True(File.Exists(Path.Combine(_root, "Old Artist", "Old Album (2003)", "01 Track.mp3")));
    }

    [Fact]
    public async Task TheOfferIsJudgedBeforeAnythingIsFetched()
    {
        await GivenHeldAsync(Mp3(), $"{OldFolder}/01 Track.mp3");

        // Lossless for a lossy album: go ahead.
        Assert.Null(await Service().RefuseOfferAsync(OldFolder, 1, Offer(1, depth: 16, rate: 44.1), 27, true, Token));

        // An MP3 for an MP3 is no upgrade, whatever the shop's album could do.
        var mp3 = await Service().RefuseOfferAsync(OldFolder, 1, Offer(1, depth: 24, rate: 96), 5, true, Token);
        Assert.Equal(ReplacementVerdict.NotBetter, mp3?.Verdict);

        // Fewer tracks offered than held: refused, nothing fetched.
        var shorter = await Service().RefuseOfferAsync(OldFolder, 1, Offer(0, depth: 24, rate: 96), 27, true, Token);
        Assert.Equal(ReplacementVerdict.NothingArrived, shorter?.Verdict);

        // And with replacement off, refused rather than fetched only to be removed.
        var off = await Service(allowReplacement: false).RefuseOfferAsync(OldFolder, 1, Offer(1, 16, 44.1), 27, true, Token);
        Assert.Contains("AllowFileReplacement", off?.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnAlbumMissingTracksIsCompletedAtTheSameQuality()
    {
        await GivenHeldAsync(Cd(), $"{OldFolder}/01 Track.flac");

        Assert.Null(await Service().RefuseOfferAsync(OldFolder, 1, Offer(2, 16, 44.1), 27, true, Token));

        var same = await Service().RefuseOfferAsync(OldFolder, 1, Offer(1, 16, 44.1), 27, true, Token);
        Assert.Equal(ReplacementVerdict.NotBetter, same?.Verdict);

        // A hi-res album fetched as CD is a CD, and the CD is already here.
        var capped = await Service().RefuseOfferAsync(OldFolder, 1, Offer(1, 24, 96), 6, true, Token);
        Assert.Equal(ReplacementVerdict.NotBetter, capped?.Verdict);
    }

    [Fact]
    public async Task TheAlbumADownloadReplacesIsFoundInTheCatalogue()
    {
        // Filed under the album, in a folder Qobuz would not name.
        await GivenHeldAsync(Mp3(), $"{OldFolder}/01 Track.mp3", $"{OldFolder}/02 Track.mp3");
        await FileUnderAsync("Old Album");

        var found = await Service().HeldAsync(Offer(2, 16, 44.1), Token);

        Assert.Equal(new HeldFolder(OldFolder, 2), Assert.Single(found));

        // The shop's title carries a remaster note the album does not; a live
        // record of the same name is another record.
        Assert.Single(await Service().HeldAsync(Offer(2, 16, 44.1) with { Title = "Old Album (Remastered)" }, Token));
        Assert.Empty(await Service().HeldAsync(Offer(2, 16, 44.1) with { Title = "Old Album (Live)" }, Token));

        // Another record by the same artist is not it.
        Assert.Empty(await Service().HeldAsync(Offer(2, 16, 44.1) with { Title = "Another Album" }, Token));
    }

    [Fact]
    public async Task AFileLooseUnderTheArtistNeverNamesTheWholeShelf()
    {
        await GivenHeldAsync(Mp3(), "Old Artist/01 Track.mp3");

        await using (var db = PostgresFixture.CreateContext(_connectionString))
        {
            var artist = new Artist { Id = ArtistId.New(), Name = "Old Artist" };
            var group = new ReleaseGroup { Id = ReleaseGroupId.New(), Title = "Old Album", FirstReleaseYear = 2003 };

            db.Artists.Add(artist);
            db.ReleaseGroups.Add(group);
            db.ArtistCredits.Add(new ArtistCredit
            {
                Id = Guid.CreateVersion7(),
                ArtistId = artist.Id,
                ReleaseGroupId = group.Id,
                Position = 0,
            });

            (await db.MediaFiles.SingleAsync(Token)).ReleaseGroupId = group.Id;
            await db.SaveChangesAsync(Token);
        }

        Assert.Empty(await Service().HeldAsync(Offer(1, 16, 44.1), Token));
    }

    [Fact]
    public async Task AudioTheCatalogueDoesNotHoldRefusesBeforeAnythingIsFetched()
    {
        await GivenHeldAsync(Mp3(), $"{OldFolder}/01 Track.mp3");
        File.Copy(Corpus.Mp3, Path.Combine(_root, "Old Artist", "Old Album (2003)", "07 Stray.mp3"));

        var refused = await Service().RefuseOfferAsync(OldFolder, 1, Offer(1, 16, 44.1), 27, true, Token);

        Assert.Equal(ReplacementVerdict.NotHeld, refused?.Verdict);
        Assert.Contains("07 Stray.mp3", refused!.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AudioTheCatalogueDoesNotHoldIsNeverLeftBesideTheNewAlbum()
    {
        Assert.SkipUnless(Corpus.IsAvailable, "ffmpeg is not on PATH; source ~/.local/opt/env.sh.");

        await GivenHeldAsync(Mp3(), $"{OldFolder}/01 Track.mp3");
        File.Copy(Corpus.Mp3, Path.Combine(_root, "Old Artist", "Old Album (2003)", "07 Stray.mp3"));

        var download = GivenDownloaded(Corpus.Flac, $"{Fetched}/01 Track.flac");

        var result = await Service().ReplaceAsync(OldFolder, 1, download, Token);

        Assert.Equal(0, result.Archived);
        Assert.Contains("07 Stray.mp3", result.Detail, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(_root, "Old Artist", "Old Album (2003)", "01 Track.mp3")));
    }

    [Fact]
    public async Task AnUnfiledFolderADownloadWouldLandInIsTheOneItReplaces()
    {
        await GivenHeldAsync(Mp3(), "Old Artist/Old Album/01 Track.mp3");

        var found = await Service().HeldAsync(Offer(1, 16, 44.1), Token);

        Assert.Equal(new HeldFolder("Old Artist/Old Album", 1), Assert.Single(found));
    }

    [Fact]
    public async Task TracksTheShopTitlesDifferentlyAreCountedNotMatched()
    {
        await GivenHeldAsync(Mp3(), $"{OldFolder}/01 Track.mp3", $"{OldFolder}/02 Track.mp3");

        // As many tracks, better, every one titled otherwise: go ahead.
        var album = Offer(2, 16, 44.1);
        var retitled = album with
        {
            Tracks = [.. album.Tracks.Select(track => track with { Title = $"{track.Title} (Album Version)" })],
        };

        Assert.Null(await Service().RefuseOfferAsync(OldFolder, 2, retitled, 27, true, Token));
    }

    [Fact]
    public async Task AFolderFiledUnderAnotherAlbumIsReplacedOnlyWhenConfirmed()
    {
        // Where the download would land, and filed under another album: held,
        // and asked about before anything is fetched.
        await GivenHeldAsync(Mp3(), "Old Artist/Old Album/01 Track.mp3");
        await FileUnderAsync("Another Album");

        Assert.Single(await Service().HeldAsync(Offer(1, 16, 44.1), Token));

        var asked = await Service().RefuseOfferAsync("Old Artist/Old Album", 1, Offer(1, 16, 44.1), 27, false, Token);
        Assert.Equal(ReplacementVerdict.Unconfirmed, asked?.Verdict);

        // And confirmed, it goes ahead.
        Assert.Null(await Service().RefuseOfferAsync("Old Artist/Old Album", 1, Offer(1, 16, 44.1), 27, true, Token));
    }

    [Fact]
    public async Task AFolderFiledUnderNoAlbumIsReplacedOnlyWhenConfirmed()
    {
        await GivenHeldAsync(Mp3(), $"{OldFolder}/01 Track.mp3");

        var asked = await Service().RefuseOfferAsync(OldFolder, 1, Offer(1, 16, 44.1), 27, false, Token);
        Assert.Equal(ReplacementVerdict.Unconfirmed, asked?.Verdict);

        // Asked before the offer is judged, and judged once confirmed.
        var mp3 = await Service().RefuseOfferAsync(OldFolder, 1, Offer(1, 24, 96), 5, false, Token);
        Assert.Equal(ReplacementVerdict.Unconfirmed, mp3?.Verdict);

        mp3 = await Service().RefuseOfferAsync(OldFolder, 1, Offer(1, 24, 96), 5, true, Token);
        Assert.Equal(ReplacementVerdict.NotBetter, mp3?.Verdict);

        Assert.Null(await Service().RefuseOfferAsync(OldFolder, 1, Offer(1, 16, 44.1), 27, true, Token));
    }

    [Fact]
    public async Task AnEditionOfTheAlbumIsTheAlbum()
    {
        await GivenHeldAsync(Mp3(), $"{OldFolder}/01 Track.mp3");
        await FileUnderAsync("Old Album", barcode: "0602557383867");

        Assert.Single(await Service().HeldAsync(Offer(1, 16, 44.1) with { Title = "Old Album (Deluxe Edition)" }, Token));

        // Matched outright, so nobody is asked.
        Assert.Null(await Service().RefuseOfferAsync(OldFolder, 1, Offer(1, 16, 44.1) with { Title = "Old Album (Deluxe Edition)" }, 27, false, Token));
        Assert.Single(await Service().HeldAsync(Offer(1, 16, 44.1) with { Title = "The Collection", Upc = "0602557383867" }, Token));
        Assert.Empty(await Service().HeldAsync(Offer(1, 16, 44.1) with { Title = "Old Album (Live)" }, Token));
    }

    [Fact]
    public async Task AnAlbumOfTheSameTitleBilledOtherwiseIsAskedAbout()
    {
        // Billed to a name the shop does not print, and spelt with "and".
        await GivenHeldAsync(Mp3(), "Someone/Porgy and Bess (1959)/01 Track.mp3");
        await FileUnderAsync("Porgy and Bess", artist: "Someone Else");

        var offer = Offer(1, 16, 44.1) with { Title = "Porgy & Bess" };
        var found = Assert.Single(await Service().HeldAsync(offer, Token));

        Assert.Equal("Someone/Porgy and Bess (1959)", found.Folder);
        Assert.Equal(
            ReplacementVerdict.SameTitle,
            (await Service().RefuseOfferAsync(found.Folder, 1, offer, 27, false, Token))?.Verdict);

        // Asked before the offer is judged: no upgrade of that album, it may
        // still be another one to download.
        Assert.Equal(
            ReplacementVerdict.SameTitle,
            (await Service().RefuseOfferAsync(found.Folder, 1, offer with { Tracks = [] }, 27, false, Token))?.Verdict);
    }

    [Fact]
    public async Task HalfAFolderFiledUnderTheAlbumIsNotEnough()
    {
        await GivenHeldAsync(Mp3(), $"{OldFolder}/01 Track.mp3");
        await FileUnderAsync("Old Album");
        await GivenHeldAsync(Mp3(), $"{OldFolder}/02 Track.mp3");
        await FileUnderAsync("Another Album", artist: "Someone Else");

        var asked = await Service().RefuseOfferAsync(OldFolder, 2, Offer(2, 16, 44.1), 27, false, Token);
        Assert.Equal(ReplacementVerdict.Unconfirmed, asked?.Verdict);
    }

    [Fact]
    public async Task ATitleHeldTwiceOtherwiseIsNoQuestion()
    {
        await GivenHeldAsync(Mp3(), "One/Greatest Hits/01 Track.mp3");
        await FileUnderAsync("Greatest Hits", artist: "One");
        await GivenHeldAsync(Mp3(), "Two/Greatest Hits/01 Track.mp3");
        await FileUnderAsync("Greatest Hits", artist: "Two");

        Assert.Empty(await Service().HeldAsync(Offer(1, 16, 44.1) with { Title = "Greatest Hits" }, Token));
    }

    /// <summary>Files every row not yet filed under an album, as attribution would, with an edition carrying the barcode.</summary>
    private async Task FileUnderAsync(string title, string? barcode = null, string artist = "Old Artist")
    {
        await using var db = PostgresFixture.CreateContext(_connectionString);

        var billed = new Artist { Id = ArtistId.New(), Name = artist };
        var group = new ReleaseGroup { Id = ReleaseGroupId.New(), Title = title, FirstReleaseYear = 2003 };

        db.Artists.Add(billed);
        db.ReleaseGroups.Add(group);
        db.ArtistCredits.Add(new ArtistCredit
        {
            Id = Guid.CreateVersion7(),
            ArtistId = billed.Id,
            ReleaseGroupId = group.Id,
            Position = 0,
        });

        if (barcode is not null)
        {
            db.Releases.Add(new Release { Id = ReleaseId.New(), Title = title, ReleaseGroupId = group.Id, Barcode = barcode });
        }

        foreach (var row in await db.MediaFiles.Where(row => row.ReleaseGroupId == null).ToListAsync(Token))
        {
            row.ReleaseGroupId = group.Id;
        }

        await db.SaveChangesAsync(Token);
    }

    [Fact]
    public async Task OneTrackOfTheAlbumOnACompilationDoesNotMakeTheCompilationIt()
    {
        await GivenHeldAsync(Mp3(), "Various Artists/Now 2003/01.mp3", "Various Artists/Now 2003/02.mp3", "Various Artists/Now 2003/03.mp3");

        await using (var db = PostgresFixture.CreateContext(_connectionString))
        {
            var artist = new Artist { Id = ArtistId.New(), Name = "Old Artist" };
            var album = new ReleaseGroup { Id = ReleaseGroupId.New(), Title = "Old Album", FirstReleaseYear = 2003 };
            var compilation = new ReleaseGroup { Id = ReleaseGroupId.New(), Title = "Now 2003" };

            db.Artists.Add(artist);
            db.ReleaseGroups.AddRange(album, compilation);
            db.ArtistCredits.Add(new ArtistCredit
            {
                Id = Guid.CreateVersion7(),
                ArtistId = artist.Id,
                ReleaseGroupId = album.Id,
                Position = 0,
            });

            var rows = await db.MediaFiles.OrderBy(row => row.Path).ToListAsync(Token);
            rows[0].ReleaseGroupId = album.Id;
            rows[1].ReleaseGroupId = compilation.Id;
            rows[2].ReleaseGroupId = compilation.Id;

            await db.SaveChangesAsync(Token);
        }

        Assert.Empty(await Service().HeldAsync(Offer(2, 16, 44.1), Token));
    }

    [Fact]
    public async Task TheDiscFoldersAReplacementEmptiesGoWithIt()
    {
        Assert.SkipUnless(Corpus.IsAvailable, "ffmpeg is not on PATH; source ~/.local/opt/env.sh.");

        await GivenHeldAsync(Mp3(), $"{OldFolder}/CD1/01 Track.mp3", $"{OldFolder}/CD2/02 Track.mp3");

        var download = GivenDownloaded(Corpus.Flac, $"{Fetched}/1-01 Track.flac", $"{Fetched}/2-01 Track.flac");

        var result = await Service().ReplaceAsync(OldFolder, 2, download, Token);

        Assert.Equal(ReplacementVerdict.Replace, result.Verdict);

        var folder = Path.Combine(_root, "Old Artist", "Old Album (2003)");
        Assert.Empty(Directory.GetDirectories(folder));
        Assert.Equal(2, Directory.GetFiles(folder).Length);
        Assert.False(Directory.Exists(Path.Combine(_root, QobuzDownloadService.ReplacementArea)));
    }

    /// <summary>Gives files a track link, as the attribution pass would.</summary>
    private async Task GivenFiledAsync(params string[] paths)
    {
        await using var db = PostgresFixture.CreateContext(_connectionString);

        var release = new Release { Id = ReleaseId.New(), Title = "Filed" };
        var recording = new Recording { Id = RecordingId.New(), Title = "Track 1" };
        var track = new Track
        {
            Id = TrackId.New(),
            ReleaseId = release.Id,
            RecordingId = recording.Id,
            Position = 1,
        };

        db.Releases.Add(release);
        db.Recordings.Add(recording);
        db.Tracks.Add(track);

        foreach (var path in paths)
        {
            var row = await db.MediaFiles.SingleAsync(file => file.Path == path, Token);
            row.TrackId = track.Id;
            row.RecordingId = recording.Id;
        }

        await db.SaveChangesAsync(Token);
    }

    /// <summary>
    /// Puts a file on disk and a row in the catalogue for it, identified as
    /// "Track N" by the number its name starts with — the title the download
    /// fixtures give track N.
    /// </summary>
    private Task GivenHeldAsync(AudioQuality? quality, params string[] paths) =>
        GivenHeldAsync(quality, paths, Corpus.Mp3);

    private async Task GivenHeldAsync(AudioQuality? quality, string[] paths, string source)
    {
        await using var db = PostgresFixture.CreateContext(_connectionString);

        foreach (var path in paths)
        {
            var absolute = Path.Combine(_root, path.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(absolute)!);
            File.Copy(source, absolute, overwrite: true);

            var number = System.Text.RegularExpressions.Regex.Match(Path.GetFileName(path), @"^(?:\d-)?(\d+)");
            var recording = new Recording
            {
                Id = RecordingId.New(),
                Title = $"Track {int.Parse(number.Groups[1].Value, CultureInfo.InvariantCulture)}",
            };
            db.Recordings.Add(recording);

            db.MediaFiles.Add(new MediaFile
            {
                Id = MediaFileId.New(),
                Path = path,
                SizeBytes = new FileInfo(absolute).Length,
                LastModifiedUtc = DateTimeOffset.Parse(
                    "2026-01-01T00:00:00Z", CultureInfo.InvariantCulture),
                Quality = quality,
                RecordingId = recording.Id,
            });
        }

        await db.SaveChangesAsync(Token);
    }

    /// <summary>Writes the file's own measurement onto its row, as the pass would.</summary>
    private async Task MeasureHeldAsync(string path)
    {
        var probe = new FfprobeAudioProbe(new FileSystemAudioFileStore(_root), "ffprobe");
        var reading = await probe.ProbeAsync(new LibraryPath(path), Token);

        Assert.NotNull(reading);

        await using var db = PostgresFixture.CreateContext(_connectionString);

        var row = await db.MediaFiles.SingleAsync(file => file.Path == path, Token);
        row.Quality = reading.Quality;

        await db.SaveChangesAsync(Token);
    }

    /// <summary>Real bytes on disk, and the download result that describes them.</summary>
    private AlbumDownload GivenDownloaded(string source, params string[] paths)
    {
        var tracks = new List<TrackDownload>();
        var number = 1;

        foreach (var path in paths)
        {
            var absolute = Path.Combine(_root, path.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(absolute)!);
            File.Copy(source, absolute, overwrite: true);

            tracks.Add(new TrackDownload(
                number, 1, number, $"Track {number}", TrackOutcome.Downloaded,
                path, new FileInfo(absolute).Length, 27, 24, 96, null));

            number++;
        }

        var folder = string.Join('/', paths[0].Split('/')[..2]);

        return new AlbumDownload(
            "1", "New Album", "New Artist", folder, tracks.Count, tracks.Count,
            DateTimeOffset.UtcNow, tracks);
    }

    private AlbumReplacementService Service(bool allowReplacement = true, IClock? clock = null) =>
        new(PostgresFixture.CreateContext(_connectionString),
            new FfprobeAudioProbe(new FileSystemAudioFileStore(_root), "ffprobe"),
            new FileSystemAudioFileStore(_root),
            Options.Create(new FonotecaOptions
            {
                LibraryPath = _root,
                ReplacedPath = _archive,
                AllowFileReplacement = allowReplacement,
            }),
            clock ?? new SystemClock(),
            NullLogger<AlbumReplacementService>.Instance);

    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow => now;
    }

    /// <summary>What Qobuz says of an album called Old Album by Old Artist.</summary>
    private static QobuzAlbum Offer(int tracks, int depth, double rate) =>
        new("1", "Old Album", "Old Artist", "2003-01-01", tracks, 1, depth > 16, depth, rate, true, null,
            [.. Enumerable.Range(1, tracks).Select(number =>
                new QobuzTrack(number, $"Track {number}", 1, number, null, null, Streamable: true))]);

    private static AudioQuality Cd() => new()
    {
        Codec = "flac",
        SampleRateHz = 44_100,
        Channels = 2,
        BitDepth = 16,
        BitrateBps = 900_000,
        IsLossless = true,
    };

    private static AudioQuality Mp3() => new()
    {
        Codec = "mp3",
        SampleRateHz = 44_100,
        Channels = 2,
        BitrateBps = 320_000,
        IsLossless = false,
    };
}
