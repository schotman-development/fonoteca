using System.Diagnostics;
using Fonoteca.Domain.Abstractions;
using Fonoteca.Fixtures;
using Fonoteca.Ingest;

namespace Fonoteca.Integration.Tests;

/// <summary>
/// The analysis decode, against audio made to have exactly the property each
/// reading is for — padded bits, silent ends, a clipped waveform.
/// </summary>
public sealed class FfmpegAudioMeasurerTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("fonoteca-measure-").FullName;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    /// <summary>A 24-bit container holding 16-bit audio reads as 16; real 24-bit audio as 24.</summary>
    [Fact]
    public async Task PaddedBitsAreToldFromRealOnes()
    {
        Assert.SkipUnless(Corpus.IsAvailable, "ffmpeg is not on PATH; source ~/.local/opt/env.sh.");

        Make("padded.flac", "-f", "lavfi", "-i", "sine=frequency=440:duration=3", "-c:a", "flac", "-sample_fmt", "s32", "-bits_per_raw_sample", "24");
        Make("real.flac", "-f", "lavfi", "-i", "anoisesrc=d=3:c=pink:a=0.3,aformat=sample_fmts=s32", "-c:a", "flac", "-bits_per_raw_sample", "24");

        Assert.Equal(16, (await MeasureAsync("padded.flac"))!.EffectiveBitDepth);
        Assert.Equal(24, (await MeasureAsync("real.flac"))!.EffectiveBitDepth);
    }

    /// <summary>Two seconds of nothing either side of three of tone.</summary>
    [Fact]
    public async Task SilenceAtEitherEndIsMeasured()
    {
        Assert.SkipUnless(Corpus.IsAvailable, "ffmpeg is not on PATH; source ~/.local/opt/env.sh.");

        Make("edges.flac", "-f", "lavfi", "-i", "sine=frequency=440:duration=7,volume='if(between(t,2,5),1,0)':eval=frame", "-c:a", "flac");

        var measured = await MeasureAsync("edges.flac");

        Assert.NotNull(measured);
        Assert.InRange(measured.LeadingSilenceSeconds, 1.9, 2.1);
        Assert.InRange(measured.TrailingSilenceSeconds, 1.9, 2.1);
        Assert.InRange(measured.DurationSeconds ?? 0, 6.9, 7.1);
    }

    /// <summary>A square wave at full scale sits at the peak over and over: the shape of clipping.</summary>
    [Fact]
    public async Task AFullScaleSquareWaveSitsAtThePeak()
    {
        Assert.SkipUnless(Corpus.IsAvailable, "ffmpeg is not on PATH; source ~/.local/opt/env.sh.");

        Make("square.flac", "-f", "lavfi", "-i", @"aevalsrc=if(lt(mod(t*100\,1)\,0.5)\,1\,-1):d=2:s=44100", "-c:a", "flac", "-sample_fmt", "s16");

        var measured = await MeasureAsync("square.flac");

        Assert.NotNull(measured);
        Assert.InRange(measured.SamplePeakDbfs ?? -99, -0.01, 0.01);
        Assert.True(measured.PeakCount > 1000, $"Peak count was {measured.PeakCount}.");
    }

    /// <summary>The peak to the thousandth of a dB, which ReplayGain's peak is written from.</summary>
    [Fact]
    public async Task ThePeakIsReadToMoreThanATenth()
    {
        Assert.SkipUnless(Corpus.IsAvailable, "ffmpeg is not on PATH; source ~/.local/opt/env.sh.");

        // 0.79 of full scale is -2.047 dBFS, which ebur128's summary prints as -2.0.
        Make("peak.flac", "-f", "lavfi", "-i", @"aevalsrc=0.79*sin(2*PI*440*t):d=2:s=44100", "-c:a", "flac", "-sample_fmt", "s16");

        Assert.InRange((await MeasureAsync("peak.flac"))!.SamplePeakDbfs ?? 0, -2.06, -2.04);
    }

    /// <summary>
    /// Ten seconds of noise has a dynamic range. A steady tone has none the DR
    /// meter can measure — ffmpeg says so — and that is no figure, not a zero.
    /// </summary>
    [Fact]
    public async Task DynamicRangeIsMeasuredWhereThereIsAny()
    {
        Assert.SkipUnless(Corpus.IsAvailable, "ffmpeg is not on PATH; source ~/.local/opt/env.sh.");

        Make("noise.flac", "-f", "lavfi", "-i", "anoisesrc=d=10:c=pink:a=0.5", "-c:a", "flac");

        Assert.InRange((await MeasureAsync("noise.flac"))!.DynamicRange ?? 0, 1, 30);

        Make("tone.flac", "-f", "lavfi", "-i", "sine=frequency=440:duration=4", "-c:a", "flac");

        Assert.Null((await MeasureAsync("tone.flac"))!.DynamicRange);
    }

    /// <summary>
    /// Nine seconds is three whole blocks, which ffmpeg 8.0's DR meter reads as
    /// no data at all unless it is handed a sample more.
    /// </summary>
    [Fact]
    public async Task AFileEndingOnAWholeBlockHasADynamicRange()
    {
        Assert.SkipUnless(Corpus.IsAvailable, "ffmpeg is not on PATH; source ~/.local/opt/env.sh.");

        Make("blocks.flac", "-f", "lavfi", "-i", "anoisesrc=d=9:c=pink:a=0.5:r=44100", "-c:a", "flac");

        Assert.InRange((await MeasureAsync("blocks.flac"))!.DynamicRange ?? 0, 1, 30);
    }

    /// <summary>Audio has a loudness, a range and a true peak; a lossy file is measured too.</summary>
    [Theory]
    [InlineData("flac")]
    [InlineData("mp3")]
    public async Task AudioHasALoudnessARangeAndATruePeak(string extension)
    {
        Assert.SkipUnless(Corpus.IsAvailable, "ffmpeg is not on PATH; source ~/.local/opt/env.sh.");

        var source = extension == "mp3" ? Corpus.Mp3 : Corpus.Flac;
        var measured = await new FfmpegAudioMeasurer(new FileSystemAudioFileStore(Corpus.Directory), "ffmpeg")
            .MeasureAsync(new LibraryPath(Path.GetFileName(source)), Token);

        Assert.NotNull(measured);
        Assert.InRange(measured.IntegratedLufs ?? 0, -40, -5);
        Assert.NotNull(measured.LoudnessRangeLu);
        Assert.NotNull(measured.TruePeakDbtp);
    }

    /// <summary>Bytes that are not audio are the file's fault; a missing ffmpeg is the tool's.</summary>
    [Fact]
    public async Task FailuresAreSplitByWhoseFaultTheyAre()
    {
        Assert.SkipUnless(Corpus.IsAvailable, "ffmpeg is not on PATH; source ~/.local/opt/env.sh.");

        var store = new FileSystemAudioFileStore(Corpus.Directory);
        var notAudio = new LibraryPath(Path.GetFileName(Corpus.NotAudioFlac));

        var file = await Assert.ThrowsAsync<AudioProbeFailedException>(
            () => new FfmpegAudioMeasurer(store, "ffmpeg").MeasureAsync(notAudio, Token));
        Assert.True(file.IsFileFault);

        var tool = await Assert.ThrowsAsync<AudioProbeFailedException>(
            () => new FfmpegAudioMeasurer(store, "/nonexistent/ffmpeg").MeasureAsync(notAudio, Token));
        Assert.False(tool.IsFileFault);
    }

    /// <summary>Digital silence is below the loudness gate and has no peak: no figure, not a zero.</summary>
    [Fact]
    public void SilenceHasNoLoudness()
    {
        const string log = """
            [Parsed_ebur128_0 @ 0x1] Summary:
              Integrated loudness:
                I:         -70.0 LUFS
                Threshold:   0.0 LUFS
              Loudness range:
                LRA:         0.0 LU
              Sample peak:
                Peak:       -inf dBFS
              True peak:
                Peak:       -inf dBFS
            """;

        var measured = FfmpegAudioMeasurer.Parse(log);

        Assert.NotNull(measured);
        Assert.Null(measured.IntegratedLufs);
        Assert.Null(measured.SamplePeakDbfs);
        Assert.Null(measured.TruePeakDbtp);
        Assert.Null(FfmpegAudioMeasurer.Parse("nothing measured here"));
    }

    private Task<Domain.Catalogue.AudioMeasurement?> MeasureAsync(string name) =>
        new FfmpegAudioMeasurer(new FileSystemAudioFileStore(_root), "ffmpeg").MeasureAsync(new LibraryPath(name), Token);

    private void Make(string name, params string[] arguments)
    {
        using var ffmpeg = Process.Start(new ProcessStartInfo("ffmpeg", ["-v", "error", "-y", .. arguments, Path.Combine(_root, name)])
        {
            RedirectStandardError = true,
        })!;

        var error = ffmpeg.StandardError.ReadToEnd();
        ffmpeg.WaitForExit();

        Assert.True(ffmpeg.ExitCode == 0, $"ffmpeg could not make {name}: {error}");
    }
}
