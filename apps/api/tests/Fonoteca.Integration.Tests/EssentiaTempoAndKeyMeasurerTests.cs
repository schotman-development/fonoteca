using System.Diagnostics;
using Fonoteca.Domain.Abstractions;
using Fonoteca.Fixtures;
using Fonoteca.Ingest;

namespace Fonoteca.Integration.Tests;

/// <summary>
/// The tempo and key reading, against audio made to have one — a click at a
/// known tempo, a chord held — through the venv docs/toolchain.md describes.
/// </summary>
public sealed class EssentiaTempoAndKeyMeasurerTests : IDisposable
{
    private static readonly string Python =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local/opt/essentia/bin/python");

    private readonly string _root = Directory.CreateTempSubdirectory("fonoteca-tempo-").FullName;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    /// <summary>A click every half second is 120 beats a minute, and Essentia is sure of it.</summary>
    [Fact]
    public async Task AClickEveryHalfSecondIsAHundredAndTwenty()
    {
        SkipWithoutEssentia();

        // Per sample: ffmpeg's volume filter gates per frame, which jitters the clicks.
        Make("click.flac", "-f", "lavfi", "-i", @"aevalsrc=if(lt(mod(t\,0.5)\,0.03)\,sin(2*PI*1000*t)\,0):d=30:s=44100", "-c:a", "flac");

        var measured = await MeasureAsync("click.flac");

        Assert.NotNull(measured);
        Assert.InRange(measured.Bpm, 119, 121);
        Assert.True(measured.BpmConfidence >= 1, $"Confidence was {measured.BpmConfidence}.");
    }

    /// <summary>An A minor triad held for ten seconds is in A minor.</summary>
    [Fact]
    public async Task AHeldTriadNamesItsKey()
    {
        SkipWithoutEssentia();

        Make("chord.flac", "-f", "lavfi", "-i", "aevalsrc=0.3*sin(2*PI*220*t)+0.3*sin(2*PI*261.63*t)+0.3*sin(2*PI*329.63*t):d=10:s=44100", "-c:a", "flac");

        var measured = await MeasureAsync("chord.flac");

        Assert.NotNull(measured);
        Assert.Equal("A", measured.Key);
        Assert.Equal("minor", measured.Scale);
    }

    /// <summary>
    /// Bytes that are not audio are the file's fault; an interpreter that will
    /// not run, or runs without Essentia, is the tool's.
    /// </summary>
    [Fact]
    public async Task FailuresAreSplitByWhoseFaultTheyAre()
    {
        SkipWithoutEssentia();

        var store = new FileSystemAudioFileStore(Corpus.Directory);
        var notAudio = new LibraryPath(Path.GetFileName(Corpus.NotAudioFlac));

        var file = await Assert.ThrowsAsync<AudioProbeFailedException>(
            () => new EssentiaTempoAndKeyMeasurer(store, Python).MeasureAsync(notAudio, Token));
        Assert.True(file.IsFileFault);

        var missing = await Assert.ThrowsAsync<AudioProbeFailedException>(
            () => new EssentiaTempoAndKeyMeasurer(store, "/nonexistent/python").MeasureAsync(notAudio, Token));
        Assert.False(missing.IsFileFault);

        // The system's own interpreter, which has no Essentia: the script's exit 3.
        var withoutEssentia = await Assert.ThrowsAsync<AudioProbeFailedException>(
            () => new EssentiaTempoAndKeyMeasurer(store, "/usr/bin/python3").MeasureAsync(notAudio, Token));
        Assert.False(withoutEssentia.IsFileFault);
    }

    /// <summary>The script's line is read whole, and its null is a file with no audio.</summary>
    [Fact]
    public void TheScriptsLineIsTheReading()
    {
        var reading = EssentiaTempoAndKeyMeasurer.Parse(
            """{"bpm": 136.2, "bpmConfidence": 3.1, "key": "F#", "scale": "minor", "keyStrength": 0.71}""");

        Assert.NotNull(reading);
        Assert.Equal(136.2, reading.Bpm);
        Assert.Equal(3.1, reading.BpmConfidence);
        Assert.Equal("F#", reading.Key);
        Assert.Equal("minor", reading.Scale);
        Assert.Equal(0.71, reading.KeyStrength);
        Assert.Null(EssentiaTempoAndKeyMeasurer.Parse("null"));
    }

    private static void SkipWithoutEssentia()
    {
        Assert.SkipUnless(Corpus.IsAvailable, "ffmpeg is not on PATH; source ~/.local/opt/env.sh.");
        Assert.SkipUnless(File.Exists(Python), "No Essentia venv at ~/.local/opt/essentia; see docs/toolchain.md.");
    }

    private Task<Domain.Catalogue.TempoAndKey?> MeasureAsync(string name) =>
        new EssentiaTempoAndKeyMeasurer(new FileSystemAudioFileStore(_root), Python).MeasureAsync(new LibraryPath(name), Token);

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
