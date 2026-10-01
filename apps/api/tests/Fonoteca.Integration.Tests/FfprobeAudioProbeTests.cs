using Fonoteca.Domain.Abstractions;
using Fonoteca.Fixtures;
using Fonoteca.Ingest;

namespace Fonoteca.Integration.Tests;

/// <summary>
/// The probe against a FLAC that closes with an ID3v1 tag, which ffmpeg's FLAC
/// demuxer reads as one more frame and objects to.
/// </summary>
public sealed class FfprobeAudioProbeTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AnId3v1TagAfterTheLastFrameIsNotDamage()
    {
        Assert.SkipUnless(Corpus.IsAvailable, "ffmpeg is not on PATH; source ~/.local/opt/env.sh.");

        var reading = await ProbeAsync(Corpus.Id3v1TrailedFlac);

        Assert.NotNull(reading);
        Assert.True(reading.DecodedCleanly, reading.Complaint);
        Assert.True(reading.Quality.IsLossless);
        Assert.Equal(44_100, reading.Quality.SampleRateHz);
    }

    [Fact]
    public async Task ADamagedFileIsStillDamagedWithAnId3v1TagAfterIt()
    {
        Assert.SkipUnless(Corpus.IsAvailable, "ffmpeg is not on PATH; source ~/.local/opt/env.sh.");

        var reading = await ProbeAsync(Corpus.TruncatedId3v1TrailedFlac);

        Assert.NotNull(reading);
        Assert.False(reading.DecodedCleanly);
    }

    [Fact]
    public async Task OnlyAnId3v1TagIsForgiven()
    {
        Assert.SkipUnless(Corpus.IsAvailable, "ffmpeg is not on PATH; source ~/.local/opt/env.sh.");

        var root = Directory.CreateTempSubdirectory("fonoteca-probe-").FullName;

        try
        {
            // The same 128 bytes with the marker spoiled: junk after the audio,
            // not a tag, and nothing says it is safe to cut.
            byte[] junk = [.. "XAG"u8, .. new byte[125]];
            await File.WriteAllBytesAsync(
                Path.Combine(root, "junk-trailed.flac"),
                [.. await File.ReadAllBytesAsync(Corpus.Flac, Token), .. junk],
                Token);

            var reading = await new FfprobeAudioProbe(new FileSystemAudioFileStore(root), "ffprobe")
                .ProbeAsync(new LibraryPath("junk-trailed.flac"), Token);

            Assert.NotNull(reading);
            Assert.False(reading.DecodedCleanly);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// A header over no audio is not a measurement, however whole the header is.
    /// </summary>
    /// <remarks>
    /// Nothing complains — no frame is ever found to object to — so without the
    /// frame count this read as an intact file of the header's declared length.
    /// </remarks>
    [Fact]
    public async Task AFileWhoseAudioDecodesToNothingIsNotAMeasurement()
    {
        Assert.SkipUnless(Corpus.IsAvailable, "ffmpeg is not on PATH; source ~/.local/opt/env.sh.");

        Assert.Null(await ProbeAsync(Corpus.NoFramesFlac));
    }

    private static Task<AudioProbeReading?> ProbeAsync(string source) =>
        new FfprobeAudioProbe(new FileSystemAudioFileStore(Corpus.Directory), "ffprobe")
            .ProbeAsync(new LibraryPath(Path.GetFileName(source)), Token);
}
