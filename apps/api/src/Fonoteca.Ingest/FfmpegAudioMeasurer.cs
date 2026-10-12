using System.Globalization;
using System.Text.RegularExpressions;
using Fonoteca.Domain.Abstractions;
using Fonoteca.Domain.Catalogue;

namespace Fonoteca.Ingest;

/// <summary>
/// <see cref="IAudioMeasurer"/> over <c>ffmpeg</c>'s analysis filters, in one decode.
/// </summary>
/// <remarks>
/// Four filters on one pass over the samples, each reporting a summary on
/// stderr at <c>-v info</c> when the stream ends:
///
/// <list type="bullet">
/// <item><c>ebur128</c> — integrated loudness, loudness range and true peak:
/// what ReplayGain 2.0 is computed from. Its sample peak is printed to a tenth
/// of a dB, so the peak is astats'.</item>
/// <item><c>astats</c> — the peak level and how many samples sit at it, and the
/// bits the samples actually use. Its "Bit depth: a/b/c/d" is undocumented;
/// measured on ffmpeg 8.0, the second figure is 16 for 16-bit audio padded into
/// a 24-bit FLAC and 24 for real 24-bit audio, where the container says 24 for
/// both. That figure is the one kept.</item>
/// <item><c>drmeter</c> — the dynamic range figure the DR database quotes.
/// Where foobar2000's DR Meter left a figure per track in the files here (37
/// tracks; two other albums carry one figure on every track), it gives the
/// same figure on every one. On ffmpeg 8.0 it reports "No data" for audio that ends on a whole
/// three-second block — it empties the last block before reading it, and
/// 76 tracks here have such a length — so it is handed one silent sample
/// more, after the other filters, which leaves every other track's figure
/// as it was.</item>
/// <item><c>silencedetect</c> at −60 dB for half a second — a run starting at
/// zero is leading silence, one ending at the last sample trailing.</item>
/// </list>
///
/// Only the first audio stream is mapped, so a cover attached as a video stream
/// is never decoded. A file that will not decode is the file's fault; one this
/// process cannot start is the tool's, and ends the pass.
/// </remarks>
public sealed class FfmpegAudioMeasurer(
    FileSystemAudioFileStore files,
    string ffmpegPath,
    TimeSpan? timeout = null) : IAudioMeasurer
{
    /// <summary>
    /// Per-file ceiling. A decode with true-peak oversampling measured 1.1s for a
    /// four-minute CD-quality FLAC; five minutes is for the box set's longest track.
    /// </summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(5);

    private const string Filters =
        "ebur128=peak=true+sample:framelog=quiet,"
        + "astats=measure_perchannel=none:measure_overall=Bit_depth+Peak_count+Peak_level+Number_of_samples,"
        + "silencedetect=n=-60dB:d=0.5,"
        + "apad=pad_len=1,"
        + "drmeter";

    private static readonly TimeSpan RegexBudget = TimeSpan.FromSeconds(1);

    private const string Number = @"(-?inf|-?\d+(?:\.\d+)?)";

    private static readonly Regex Integrated = new(@"I:\s+" + Number + @" LUFS", RegexOptions.CultureInvariant, RegexBudget);
    private static readonly Regex Range = new(@"LRA:\s+" + Number + @" LU\b", RegexOptions.CultureInvariant, RegexBudget);
    private static readonly Regex SamplePeak = new(@"Peak level dB: " + Number, RegexOptions.CultureInvariant, RegexBudget);
    private static readonly Regex TruePeak = new(@"True peak:\s*\n\s*Peak:\s+" + Number + " dBFS", RegexOptions.CultureInvariant, RegexBudget);
    private static readonly Regex PeakCount = new(@"Peak count: " + Number, RegexOptions.CultureInvariant, RegexBudget);
    private static readonly Regex Depth = new(@"Bit depth: (\d+)/(\d+)/(\d+)/(\d+)", RegexOptions.CultureInvariant, RegexBudget);
    private static readonly Regex Samples = new(@"Number of samples: " + Number, RegexOptions.CultureInvariant, RegexBudget);
    private static readonly Regex Dr = new(@"Overall DR: " + Number, RegexOptions.CultureInvariant, RegexBudget);
    private static readonly Regex SilenceStart = new(@"silence_start: " + Number, RegexOptions.CultureInvariant, RegexBudget);
    private static readonly Regex SilenceEnd = new(@"silence_end: " + Number + @" \| silence_duration: " + Number, RegexOptions.CultureInvariant, RegexBudget);

    /// <summary>The decoded stream's rate, from the output side: <c>Audio: pcm_s16le, 44100 Hz</c>.</summary>
    private static readonly Regex Rate = new(@"Audio: [^,\n]+, (\d+) Hz", RegexOptions.CultureInvariant, RegexBudget);

    private readonly FileSystemAudioFileStore _files = files ?? throw new ArgumentNullException(nameof(files));

    private readonly string _ffmpegPath = string.IsNullOrWhiteSpace(ffmpegPath)
        ? throw new ArgumentException("An ffmpeg path is required.", nameof(ffmpegPath))
        : ffmpegPath;

    private readonly TimeSpan _timeout = timeout ?? DefaultTimeout;

    public async Task<AudioMeasurement?> MeasureAsync(LibraryPath path, CancellationToken cancellationToken = default)
    {
        var absolute = _files.AbsolutePathFor(path);

        ProcessResult result;

        try
        {
            result = await ProcessRunner
                .RunAsync(
                    _ffmpegPath,
                    ["-hide_banner", "-nostats", "-nostdin", "-v", "info", "-i", absolute, "-map", "0:a:0", "-af", Filters, "-f", "null", "-"],
                    _timeout,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (ProcessStartFailedException cause)
        {
            throw new AudioProbeFailedException(
                path, $"'{_ffmpegPath}' could not be run. Check Fonoteca:FfmpegPath.", isFileFault: false, cause);
        }

        if (result.TimedOut)
        {
            throw new AudioProbeFailedException(
                path, $"ffmpeg did not finish within {_timeout.TotalSeconds:0} seconds.", isFileFault: true);
        }

        if (!result.Succeeded)
        {
            throw new AudioProbeFailedException(
                path,
                LastLine(result.StandardError) ?? $"ffmpeg exited {result.ExitCode}.",
                isFileFault: true);
        }

        return Parse(result.StandardError);
    }

    /// <summary>The filters' summaries, read off ffmpeg's stderr; null where nothing was measured.</summary>
    /// <remarks>
    /// The loudness summary is the test: <c>ebur128</c> prints one for any audio
    /// that decoded, so its absence is a file with no measurable stream.
    /// </remarks>
    public static AudioMeasurement? Parse(string log)
    {
        ArgumentNullException.ThrowIfNull(log);

        if (!Integrated.IsMatch(log)) return null;

        var rate = Rate.Matches(log) is { Count: > 0 } rates ? Value(rates[^1].Groups[1].Value) : null;
        var samples = Read(Samples, log);
        var duration = rate is > 0 && samples is > 0 ? samples / rate : null;

        double leading = 0, trailing = 0;

        foreach (Match end in SilenceEnd.Matches(log))
        {
            var stop = Value(end.Groups[1].Value) ?? 0;
            var length = Value(end.Groups[2].Value) ?? 0;
            var start = stop - length;

            if (start <= 0.01) leading = length;

            // A run ffmpeg closed at the stream's end, which it reports as ending there.
            if (duration is { } total && total - stop <= 0.05) trailing = length;
        }

        // A run still open when the stream ended, on a build that does not close it.
        if (duration is { } whole
            && SilenceStart.Matches(log) is { Count: > 0 } starts
            && SilenceEnd.Count(log) < starts.Count
            && Value(starts[^1].Groups[1].Value) is { } open)
        {
            trailing = Math.Max(trailing, whole - open);
        }

        var lufs = Read(Integrated, log);

        return new AudioMeasurement
        {
            // −70 LUFS is the absolute gate: what silence reports.
            IntegratedLufs = lufs is > -70 ? lufs : null,
            LoudnessRangeLu = Read(Range, log),
            SamplePeakDbfs = Read(SamplePeak, log),
            TruePeakDbtp = Read(TruePeak, log),
            PeakCount = Read(PeakCount, log) is { } count ? (long)count : null,
            DynamicRange = Read(Dr, log),
            EffectiveBitDepth = Depth.Match(log) is { Success: true } depth
                ? int.Parse(depth.Groups[2].Value, CultureInfo.InvariantCulture)
                : null,
            LeadingSilenceSeconds = Math.Round(leading, 3),
            TrailingSilenceSeconds = Math.Round(trailing, 3),
            DurationSeconds = duration is { } seconds ? Math.Round(seconds, 3) : null,
        };
    }

    private static double? Read(Regex pattern, string log) =>
        pattern.Match(log) is { Success: true } match ? Value(match.Groups[1].Value) : null;

    /// <summary>A figure as ffmpeg prints it; "-inf" — digital silence — is no figure.</summary>
    private static double? Value(string text) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
        && double.IsFinite(value)
            ? value
            : null;

    private static string? LastLine(string? log) =>
        log?.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) is { Length: > 0 } lines
            ? lines[^1]
            : null;
}
