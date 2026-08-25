using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using Fonoteca.Api.Endpoints;
using Fonoteca.Data;
using Fonoteca.Domain.Catalogue;
using Fonoteca.Fixtures;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;

namespace Fonoteca.Integration.Tests;

/// <summary>
/// What one file can be told to say about itself, through the real host.
/// </summary>
/// <remarks>
/// Its own class and its own database because it is the only part of the
/// matching screen that reads <b>bytes</b>. Everything else there is a query
/// against seeded rows; this needs real audio on a real disk, and the four
/// interesting cases are all about what happens when that goes wrong — a file
/// that moved, a volume that is not mounted, a container nothing will parse.
///
/// The rule it exists to hold is that none of those may fail the request. A file
/// nothing can read is disproportionately a file nothing could identify, so the
/// screen that exists for unidentified files is the last one that should 500 on
/// them.
/// </remarks>
[Collection(nameof(PostgresCollection))]
public sealed class MatchingFileTests(PostgresFixture postgres) : IAsyncLifetime
{
    private string _connectionString = string.Empty;
    private string _root = string.Empty;
    private WebApplicationFactory<Program>? _factory;

    private MediaFileId _onDisk;
    private MediaFileId _lossy;
    private MediaFileId _tagged;
    private MediaFileId _missing;
    private MediaFileId _measuredBefore;
    private MediaFileId _truncated;
    private MediaFileId _empty;
    private MediaFileId _notAudio;
    private MediaFileId _id3Prefixed;
    private MediaFileId _withArtwork;
    private MediaFileId _neverFingerprinted;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _connectionString = await postgres.CreateDatabaseAsync(Token);
        _root = Directory.CreateTempSubdirectory("fonoteca-matching-file-").FullName;

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:Fonoteca", _connectionString);
            builder.UseSetting("Fonoteca:LibraryPath", _root);

            // The background warmer would put its own questions to the providers,
            // out of a thread nothing here waits for.
            builder.UseSetting("Fonoteca:WarmCandidates", "false");
            builder.UseSetting("Fonoteca:MusicBrainzContact", string.Empty);
        });

        using var warm = _factory.CreateClient();

        if (Corpus.IsAvailable)
        {
            Corpus.CopyInto(
                _root,
                Corpus.Flac,
                Corpus.Mp3,
                Corpus.AlreadyTaggedFlac,
                Corpus.TruncatedFlac,
                Corpus.EmptyFlac,
                Corpus.NotAudioFlac,
                Corpus.Id3PrefixedFlac,
                Corpus.FlacWithArtwork);
        }

        await SeedAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (_factory is not null) await _factory.DisposeAsync();

        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    /// <summary>
    /// The bytes are read, and what they say is written back to the catalogue.
    /// </summary>
    /// <remarks>
    /// Both halves matter. The reading is the point of the endpoint — a person
    /// choosing between recordings needs the length and the bitrate — and the
    /// write-back is what stops the second person to open the file paying for it
    /// again. <see cref="MediaFile.Quality"/> has been in the schema since the
    /// first migration with nothing ever putting a value in it; this is the
    /// first thing that does.
    /// </remarks>
    [Fact]
    public async Task AFileIsDescribedFromItsBytesAndTheMeasurementIsKept()
    {
        Assert.SkipUnless(Corpus.IsAvailable, "ffmpeg is not on PATH; source ~/.local/opt/env.sh.");

        var file = await DescribeAsync(_onDisk.Value);

        Assert.Equal("FLAC", file.Format);

        Assert.NotNull(file.Audio);
        var audio = file.Audio;
        Assert.True(audio.Lossless);
        Assert.Equal(44_100, audio.SampleRateHz);
        Assert.Equal(16, audio.BitDepth);
        Assert.Equal(2, audio.Channels);
        Assert.True(audio.BitrateKbps > 0, $"bitrate was {audio.BitrateKbps}");
        Assert.Equal(nameof(QualityTier.LosslessCd), audio.Tier);

        // The container's own duration, which is a different reading from the
        // fingerprint's and is why both are returned.
        Assert.NotNull(file.Duration);

        await using var db = PostgresFixture.CreateContext(_connectionString);

        var stored = await db.MediaFiles
            .AsNoTracking()
            .FirstAsync(row => row.Id == _onDisk, Token);

        Assert.NotNull(stored.Quality);
        var quality = stored.Quality;
        Assert.Equal(44_100, quality.SampleRateHz);
        Assert.True(quality.IsLossless);
    }

    /// <summary>
    /// A file that already names itself says so, and that outranks every score.
    /// </summary>
    /// <remarks>
    /// The finding this endpoint was written for. Identification asks AcoustID
    /// what the <i>audio</i> is and never asks the file what it claims to be —
    /// two different questions — so a file carrying an <c>ACOUSTID_ID</c> or a
    /// <c>MUSICBRAINZ_TRACKID</c> could sit on the worklist as unidentifiable
    /// with the answer written inside it. Measured against the library this was
    /// built on, that is the common case: every sampled file that all three
    /// passes refused carried a full Picard tag set.
    /// </remarks>
    [Fact]
    public async Task AFileCarryingItsOwnIdentifiersHandsThemBack()
    {
        Assert.SkipUnless(Corpus.IsAvailable, "ffmpeg is not on PATH; source ~/.local/opt/env.sh.");

        var file = await DescribeAsync(_tagged.Value);

        var acoustId = Assert.Single(
            file.Tags,
            tag => tag.Name.Contains("ACOUSTID", StringComparison.OrdinalIgnoreCase));

        Assert.Equal(Corpus.PreExistingAcoustId, acoustId.Value, ignoreCase: true);
    }

    /// <summary>
    /// A file that is not there is described anyway, from what the catalogue holds.
    /// </summary>
    /// <remarks>
    /// The ordinary state of this endpoint on an unmounted volume, and the whole
    /// reason it does not throw. Everything the catalogue knows — the path, the
    /// size, the length identification measured, what each pass concluded — is
    /// still correct and still the evidence a person came to read; only the
    /// bytes are missing, and the note says so.
    /// </remarks>
    [Fact]
    public async Task AFileThatIsNotOnDiskStillDescribesWhatTheCatalogueHolds()
    {
        var file = await DescribeAsync(_missing.Value);

        Assert.Null(file.Audio);
        Assert.NotNull(file.Note);
        Assert.Empty(file.Tags);

        // The catalogue's half, unaffected.
        Assert.Equal("3:45", file.Measured);
        Assert.Equal("Bootlegs/gone.flac", file.Path);
        Assert.Equal("Ambiguous", file.Identification);
        Assert.True(file.Fingerprinted);
    }

    /// <summary>
    /// A failed read never erases a measurement that succeeded earlier.
    /// </summary>
    /// <remarks>
    /// The failure is a fact about this moment — the volume was not mounted —
    /// and blanking the columns on it would make the audio properties flicker
    /// with the mount state rather than with the file. So the reading is only
    /// ever filled in, and a stale value is corrected by the next successful
    /// read rather than by a null.
    /// </remarks>
    [Fact]
    public async Task AFailedReadDoesNotEraseAMeasurementAlreadyTaken()
    {
        var file = await DescribeAsync(_measuredBefore.Value);

        Assert.NotNull(file.Audio);
        var audio = file.Audio;
        Assert.Equal("FLAC (remembered)", audio.Codec);
        Assert.Equal(1_411, audio.BitrateKbps);

        // And it is plainly the catalogue's copy: nothing on disk answered.
        Assert.NotNull(file.Note);
        Assert.Empty(file.Tags);
    }

    /// <summary>
    /// The measurement comes from a decoder, and on a VBR MP3 that is the whole
    /// difference.
    /// </summary>
    /// <remarks>
    /// The reading that made this endpoint stop trusting tag libraries. Asked
    /// about a real library MP3, one of them answered 64 kbps and 5:35 where the
    /// audio is 128 kbps and 2:58 — half the bitrate and a duration extrapolated
    /// from a first frame read as MPEG-2. Duration is what this asserts, because
    /// it is the failure that cannot be argued with: the corpus is twelve
    /// seconds long, and the wrong answer is roughly twice that.
    ///
    /// It is also the value that ends up in <see cref="MediaFile.Quality"/>,
    /// which decides which duplicate to keep.
    /// </remarks>
    [Fact]
    public async Task ALossyFileIsMeasuredByTheDecoderRatherThanByATagParser()
    {
        Assert.SkipUnless(Corpus.IsAvailable, "ffmpeg is not on PATH; source ~/.local/opt/env.sh.");

        var file = await DescribeAsync(_lossy.Value);

        Assert.NotNull(file.Audio);
        var audio = file.Audio;

        Assert.False(audio.Lossless);
        Assert.Null(audio.BitDepth);
        Assert.True(audio.BitrateKbps > 0, $"bitrate was {audio.BitrateKbps}");

        await using var db = PostgresFixture.CreateContext(_connectionString);

        var stored = await db.MediaFiles.AsNoTracking().FirstAsync(row => row.Id == _lossy, Token);

        Assert.NotNull(stored.Quality);
        Assert.NotNull(stored.Quality.Duration);

        Assert.True(
            Math.Abs(stored.Quality.Duration.Value.TotalSeconds - Corpus.DurationSeconds) < 1.0,
            $"measured {stored.Quality.Duration.Value.TotalSeconds:0.00}s for a "
                + $"{Corpus.DurationSeconds}s file");
    }

    /// <summary>
    /// A file with no audio in it is described as one, and nothing is written down.
    /// </summary>
    /// <remarks>
    /// Both of these make <c>ffprobe</c> exit <b>zero</b> having reported a
    /// stream with <c>sample_rate: 0</c> and no duration, which is why the exit
    /// code alone is not the test. Read as a measurement, an empty file becomes
    /// a silent CD-quality track; read as null it is the truth.
    /// </remarks>
    [Theory]
    [InlineData(nameof(Corpus.EmptyFlac))]
    [InlineData(nameof(Corpus.NotAudioFlac))]
    public async Task AFileWithNoAudioInItIsDescribedAndNotMeasured(string which)
    {
        Assert.SkipUnless(Corpus.IsAvailable, "ffmpeg is not on PATH; source ~/.local/opt/env.sh.");

        var id = which == nameof(Corpus.EmptyFlac) ? _empty : _notAudio;

        var file = await DescribeAsync(id.Value);

        Assert.Null(file.Audio);
        Assert.Null(file.Duration);
        Assert.NotNull(file.Note);

        await using var db = PostgresFixture.CreateContext(_connectionString);

        var stored = await db.MediaFiles.AsNoTracking().FirstAsync(row => row.Id == id, Token);

        Assert.Null(stored.Quality);
    }

    /// <summary>
    /// A truncated file measures plausibly, says so, and is not remembered.
    /// </summary>
    /// <remarks>
    /// <b>The case a header read cannot catch, and the reason the stream is
    /// decoded rather than declared.</b> A FLAC cut to a quarter of its bytes
    /// keeps an intact STREAMINFO, so it goes on stating its original duration
    /// while its bitrate is computed against what remains — a quarter of the
    /// real one, still typed lossless CD. Nothing about those numbers looks
    /// wrong. Asked to count frames, the decoder reads the stream and objects,
    /// and that objection is the only thing that distinguishes this file from a
    /// short one.
    ///
    /// So the reading is shown — it is genuinely useful here, since a file
    /// nothing could identify and a file nothing can fully decode are frequently
    /// the same file — and the catalogue is not told about it.
    /// </remarks>
    [Fact]
    public async Task ATruncatedFileIsShownWithTheDecodersComplaintAndNotRemembered()
    {
        Assert.SkipUnless(Corpus.IsAvailable, "ffmpeg is not on PATH; source ~/.local/opt/env.sh.");

        var file = await DescribeAsync(_truncated.Value);

        // The declaration survives, and on its own it is convincing.
        Assert.NotNull(file.Audio);
        Assert.True(file.Audio.Lossless);
        Assert.NotNull(file.Note);

        // The decoder's own words, minus the part of them that is an allocation
        // address: ffmpeg prefixes every line with `[flac @ 0x…]`, which differs
        // on every run over the same file and would make one finding read as two
        // to anybody who reloaded the page.
        Assert.DoesNotContain("0x", file.Note, StringComparison.Ordinal);

        await using var db = PostgresFixture.CreateContext(_connectionString);

        var stored = await db.MediaFiles
            .AsNoTracking()
            .FirstAsync(row => row.Id == _truncated, Token);

        Assert.Null(stored.Quality);
    }

    /// <summary>
    /// The forty-file shape that stops a pass dead does not stop this.
    /// </summary>
    /// <remarks>
    /// A FLAC with an unsynchronised ID3v2 header glued to the front: playable
    /// audio, and the file whose embedded pictures make ATL dereference null.
    /// The describer's remarks claim TagLib# and the decoder both get through it,
    /// which is the reason those two do the work here rather than ATL — and this
    /// is that claim, asserted rather than stated.
    /// </remarks>
    [Fact]
    public async Task TheId3PrefixedFlacThatBreaksAtlIsDescribedAnyway()
    {
        Assert.SkipUnless(Corpus.IsAvailable, "ffmpeg is not on PATH; source ~/.local/opt/env.sh.");

        var file = await DescribeAsync(_id3Prefixed.Value);

        Assert.NotNull(file.Audio);
        Assert.True(file.Audio.Lossless);
        Assert.NotNull(file.Duration);
    }

    /// <summary>
    /// A worklist row falls back to the length this endpoint measured earlier.
    /// </summary>
    /// <remarks>
    /// <b>Not an edge case: a third of the target library's file-level questions
    /// have no fingerprint duration at all.</b> Their AcoustID was adopted from
    /// a tag the file already carried, so <c>fpcalc</c> never ran and nothing
    /// ever measured them. Reading only <c>FingerprintDuration</c> leaves those
    /// rows printing a size and a container and no length, forever — and length
    /// is half of what the screen was asked for.
    /// </remarks>
    [Fact]
    public async Task AWorklistRowUsesTheStoredMeasurementWhenNothingFingerprintedTheFile()
    {
        using var client = _factory!.CreateClient();

        var queue = await client.GetFromJsonAsync<MatchingQueueResponse>(
            new Uri("/api/catalogue/matching?take=100", UriKind.Relative),
            Token);

        Assert.NotNull(queue);

        var row = Assert.Single(
            queue.Items,
            item => item.Id == $"recording:{_neverFingerprinted.Value}");

        Assert.Equal("4:12", row.Length);
        Assert.Equal("FLAC", row.Format);
    }

    /// <summary>
    /// A file's own cover comes back as an image, and a file without one 404s.
    /// </summary>
    /// <remarks>
    /// Both halves are the endpoint's contract. The picture is served as it was
    /// stored — the point of it is that a person can compare it with a
    /// candidate's sleeve — and "no picture" is an ordinary answer rather than a
    /// failure, because the screen draws the same monogram for a file with no
    /// art, a file no parser will open and an unmounted volume.
    /// </remarks>
    [Fact]
    public async Task TheCoverInAFileIsServedAndAFileWithoutOneIsNotFound()
    {
        Assert.SkipUnless(Corpus.IsAvailable, "ffmpeg is not on PATH; source ~/.local/opt/env.sh.");

        using var client = _factory!.CreateClient();

        var art = await client.GetAsync(
            new Uri($"/api/catalogue/matching/files/{_withArtwork.Value}/art", UriKind.Relative),
            Token);

        Assert.Equal(HttpStatusCode.OK, art.StatusCode);
        Assert.StartsWith(
            "image/",
            art.Content.Headers.ContentType?.MediaType,
            StringComparison.OrdinalIgnoreCase);

        var bytes = await art.Content.ReadAsByteArrayAsync(Token);
        Assert.NotEmpty(bytes);

        // The corpus attaches a PNG, and the bytes must be the ones that were
        // stored rather than anything re-encoded on the way out.
        Assert.Equal<byte[]>([0x89, (byte)'P', (byte)'N', (byte)'G'], bytes[..4]);

        var none = await client.GetAsync(
            new Uri($"/api/catalogue/matching/files/{_onDisk.Value}/art", UriKind.Relative),
            Token);

        Assert.Equal(HttpStatusCode.NotFound, none.StatusCode);
    }

    [Fact]
    public async Task AFileTheCatalogueDoesNotHoldIsNotFound()
    {
        using var client = _factory!.CreateClient();

        var response = await client.GetAsync(
            new Uri($"/api/catalogue/matching/files/{Guid.CreateVersion7()}", UriKind.Relative),
            Token);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private async Task<SubjectFileResponse> DescribeAsync(Guid mediaFileId)
    {
        using var client = _factory!.CreateClient();

        var file = await client.GetFromJsonAsync<SubjectFileResponse>(
            new Uri($"/api/catalogue/matching/files/{mediaFileId}", UriKind.Relative),
            Token);

        Assert.NotNull(file);
        return file;
    }

    private async Task SeedAsync()
    {
        await using var db = PostgresFixture.CreateContext(_connectionString);

        var onDisk = Unidentified(Path.GetFileName(Corpus.IsAvailable ? Corpus.Flac : "plain.flac"));
        _onDisk = onDisk.Id;
        db.MediaFiles.Add(onDisk);

        var lossy = Unidentified(Path.GetFileName(Corpus.IsAvailable ? Corpus.Mp3 : "plain.mp3"));
        _lossy = lossy.Id;
        db.MediaFiles.Add(lossy);

        var tagged = Unidentified(
            Path.GetFileName(Corpus.IsAvailable ? Corpus.AlreadyTaggedFlac : "tagged.flac"));

        _tagged = tagged.Id;
        db.MediaFiles.Add(tagged);

        // The four the describer's own remarks promise to survive.
        var truncated = Unidentified(
            Path.GetFileName(Corpus.IsAvailable ? Corpus.TruncatedFlac : "truncated.flac"));

        _truncated = truncated.Id;
        db.MediaFiles.Add(truncated);

        var empty = Unidentified(Path.GetFileName(Corpus.IsAvailable ? Corpus.EmptyFlac : "empty.flac"));
        _empty = empty.Id;
        db.MediaFiles.Add(empty);

        var notAudio = Unidentified(
            Path.GetFileName(Corpus.IsAvailable ? Corpus.NotAudioFlac : "not-audio.flac"));

        _notAudio = notAudio.Id;
        db.MediaFiles.Add(notAudio);

        var id3Prefixed = Unidentified(
            Path.GetFileName(Corpus.IsAvailable ? Corpus.Id3PrefixedFlac : "id3.flac"));

        _id3Prefixed = id3Prefixed.Id;
        db.MediaFiles.Add(id3Prefixed);

        var withArtwork = Unidentified(
            Path.GetFileName(Corpus.IsAvailable ? Corpus.FlacWithArtwork : "artwork.flac"));

        _withArtwork = withArtwork.Id;
        db.MediaFiles.Add(withArtwork);

        var missing = Unidentified("Bootlegs/gone.flac");
        _missing = missing.Id;
        db.MediaFiles.Add(missing);

        // Never fingerprinted — its AcoustID was adopted from a tag it already
        // carried — but measured on some earlier visit to this endpoint.
        var neverFingerprinted = Unidentified("Bootlegs/adopted.flac");
        neverFingerprinted.Fingerprint = null;
        neverFingerprinted.FingerprintDuration = null;
        neverFingerprinted.Quality = new AudioQuality
        {
            Codec = "flac",
            SampleRateHz = 44_100,
            Channels = 2,
            BitDepth = 16,
            BitrateBps = 900_000,
            IsLossless = true,
            Duration = TimeSpan.FromSeconds(252),
        };

        _neverFingerprinted = neverFingerprinted.Id;
        db.MediaFiles.Add(neverFingerprinted);

        // A file measured on some earlier visit, and no longer readable.
        var remembered = Unidentified("Bootlegs/also-gone.flac");
        remembered.Quality = new AudioQuality
        {
            Codec = "FLAC (remembered)",
            SampleRateHz = 44_100,
            Channels = 2,
            BitDepth = 16,
            BitrateBps = 1_411_000,
            IsLossless = true,
            Duration = TimeSpan.FromSeconds(225),
        };

        _measuredBefore = remembered.Id;
        db.MediaFiles.Add(remembered);

        await db.SaveChangesAsync(Token);
    }

    private static MediaFile Unidentified(string path) => new()
    {
        Id = MediaFileId.New(),
        Path = path,
        SizeBytes = 12_000_000,
        LastModifiedUtc = DateTimeOffset.Parse("2026-01-01T00:00:00Z", CultureInfo.InvariantCulture),
        Fingerprint = "AQAAmockfingerprint",
        FingerprintDuration = TimeSpan.FromSeconds(225),
        AcoustIdOutcome = AcoustIdOutcome.Ambiguous,
        AcoustIdCheckedUtc = DateTimeOffset.Parse("2026-02-01T10:00:00Z", CultureInfo.InvariantCulture),
    };
}
