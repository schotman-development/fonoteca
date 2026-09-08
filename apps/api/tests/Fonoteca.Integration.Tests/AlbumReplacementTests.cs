using System.Globalization;
using Fonoteca.Api.Acquisition;
using Fonoteca.Api.Configuration;
using Fonoteca.Domain.Acquisition;
using Fonoteca.Domain.Abstractions;
using Fonoteca.Domain.Catalogue;
using Fonoteca.Fixtures;
using Fonoteca.Ingest;
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

    /// <summary>Gives files a track link, as the attribution pass would.</summary>
    private async Task GivenFiledAsync(params string[] paths)
    {
        await using var db = PostgresFixture.CreateContext(_connectionString);

        var release = new Release { Id = ReleaseId.New(), Title = "Filed" };
        var recording = new Recording { Id = RecordingId.New(), Title = "Track" };
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
        }

        await db.SaveChangesAsync(Token);
    }

    /// <summary>Puts a file on disk and a row in the catalogue for it.</summary>
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

            db.MediaFiles.Add(new MediaFile
            {
                Id = MediaFileId.New(),
                Path = path,
                SizeBytes = new FileInfo(absolute).Length,
                LastModifiedUtc = DateTimeOffset.Parse(
                    "2026-01-01T00:00:00Z", CultureInfo.InvariantCulture),
                Quality = quality,
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

    private static AudioQuality Mp3() => new()
    {
        Codec = "mp3",
        SampleRateHz = 44_100,
        Channels = 2,
        BitrateBps = 320_000,
        IsLossless = false,
    };
}
