using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Fonoteca.Domain.Abstractions;
using Fonoteca.Domain.Catalogue;

namespace Fonoteca.Ingest;

/// <summary>
/// <see cref="IAudioProbe"/> over <c>ffprobe</c>.
/// </summary>
/// <remarks>
/// The third tool, for the reason the tag-write tests already use it as one:
/// two libraries agreeing only proves they agree. What is measured here is what
/// a decoder found in the stream, not what a tag parser inferred from a header
/// it walked past — and the gap between those two is not academic. Measured
/// against real library files:
///
/// <list type="bullet">
/// <item>a VBR MP3 with no Xing header: <c>ffprobe</c> 128 kbps and 2:58, the
/// tag library 64 kbps and 5:35 — half the bitrate and a duration extrapolated
/// from a first frame it read as MPEG-2;</item>
/// <item>a FLAC damaged badly enough to lose its metadata blocks: <c>ffprobe</c>
/// exits non-zero with "Invalid data found", the tag library reports the
/// original duration off whatever it could still parse and computes 3 kbps
/// against what is left — and types it lossless CD.</item>
/// </list>
///
/// <b>What <c>-count_frames</c> buys, precisely.</b> Not better numbers: codec,
/// rate, depth, duration and bitrate still come from the container's own
/// declarations, and a file truncated <i>after</i> its metadata blocks goes on
/// declaring its original length while exiting <b>zero</b>. What decoding buys
/// is that ffmpeg reads the frames and <i>says something</i> when they are wrong
/// — and that sentence is the only thing distinguishing a damaged file from a
/// short one. It is carried back as <see cref="AudioProbeReading.Complaint"/>
/// rather than turned into a verdict: whether these bytes are intact is
/// <c>IntegrityState</c>'s question and needs a pass of its own.
///
/// Both of those get written into <see cref="AudioQuality"/>, which exists to
/// decide which duplicate to keep and whether a candidate is an upgrade.
///
/// The details that are load-bearing:
///
/// <list type="bullet">
/// <item><b>The bitrate comes from the stream where <c>ffprobe</c> reports one
/// and from the format only as a fallback.</b> A format bitrate is the whole
/// file over its duration, so it counts ID3 frames and embedded artwork as
/// audio — 327 kbps for a 320 kbps MP3. FLAC has no per-stream bitrate at all,
/// which is why the fallback is not optional.</item>
/// <item><b>Losslessness comes from the codec, never from the extension.</b> An
/// <c>.m4a</c> is ALAC about as often as it is AAC, and the whole point of
/// asking a decoder is that it knows which.</item>
/// <item><b>Bit depth is <c>bits_per_raw_sample</c>.</b> <c>bits_per_sample</c>
/// is the container's field and reads 0 for FLAC and for every lossy codec, so
/// preferring it would silently drop the depth from exactly the files that have
/// one.</item>
/// <item><b>A zero sample rate is "no audio here", not a failure.</b> A text
/// file named <c>.flac</c> and an empty one both make <c>ffprobe</c> exit
/// <i>zero</i> having reported a stream with <c>sample_rate: 0</c> and no
/// duration. Read as a measurement that would be a file typed as silent CD
/// audio; read as null it is the truth.</item>
/// </list>
///
/// Constructed with plain values rather than <c>IOptions</c>, exactly as
/// <see cref="FpcalcFingerprinter"/> is: this assembly carries no NuGet
/// references and keeping it that way is worth a lambda in <c>Program.cs</c>.
/// </remarks>
public sealed class FfprobeAudioProbe(
    FileSystemAudioFileStore files,
    string ffprobePath,
    TimeSpan? timeout = null) : IAudioProbe
{
    /// <summary>
    /// Per-file ceiling. Generous, and finite for the reason fingerprinting's is.
    /// </summary>
    /// <remarks>
    /// Sixty rather than thirty because <c>-count_frames</c> decodes the stream
    /// instead of reading its header. Measured: an 87 MB 24/96 FLAC takes 0.70s
    /// against 0.03s for a header read, so the whole library's largest files are
    /// still seconds — but a container that sends the decoder into a loop must
    /// not hold the request open.
    /// </remarks>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Codecs whose output is bit-exact.
    /// </summary>
    /// <remarks>
    /// <c>ffprobe</c>'s own codec names, so this is a list of identifiers rather
    /// than a guess about containers. <c>wavpack</c> is deliberately absent: it
    /// has a lossy mode and nothing in the stream header distinguishes it, so it
    /// falls through to the prefix rules below and then to false — under-claiming
    /// rather than promising a bit-exact copy that may not be one.
    /// </remarks>
    private static readonly HashSet<string> LosslessCodecs =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "flac", "alac", "ape", "tta", "tak", "mlp", "truehd", "shorten",
            "ralf", "wmalossless", "atrac3al", "atrac3pal",
        };

    private static readonly string[] BaseArguments =
    [
        "-v", "error",
        "-select_streams", "a:0",

        // Read the frames, so ffmpeg has the chance to object to them. It does
        // not change any number below — those are the container's declarations
        // either way — but a file truncated after its metadata blocks declares
        // its original length and exits zero, and the decoder's complaint is the
        // only thing that tells it from a short track. Measured: 3.9s on a
        // 475 MB FLAC against 0.03s for a header read.
        "-count_frames",

        "-show_entries",
        "stream=codec_name,codec_long_name,sample_rate,channels,bits_per_raw_sample,bit_rate,duration"
            + ":format=duration,bit_rate,size",
        "-of", "json",
    ];

    /// <summary>
    /// <c>[mp3float @ 0x628a58d9f100] </c> — ffmpeg's component tag, with a live
    /// allocation address in it.
    /// </summary>
    private static readonly System.Text.RegularExpressions.Regex ComponentPrefix =
        new(@"^\[[^\]]*@\s*0x[0-9a-fA-F]+\]\s*",
            System.Text.RegularExpressions.RegexOptions.CultureInvariant,
            TimeSpan.FromSeconds(1));

    private static readonly JsonSerializerOptions Json =
        new(JsonSerializerDefaults.Web) { PropertyNameCaseInsensitive = true };

    private readonly FileSystemAudioFileStore _files =
        files ?? throw new ArgumentNullException(nameof(files));

    private readonly string _ffprobePath = string.IsNullOrWhiteSpace(ffprobePath)
        ? throw new ArgumentException("An ffprobe path is required.", nameof(ffprobePath))
        : ffprobePath;

    private readonly TimeSpan _timeout = timeout ?? DefaultTimeout;

    public async Task<AudioProbeReading?> ProbeAsync(
        LibraryPath path,
        CancellationToken cancellationToken = default)
    {
        // Resolved here rather than by the caller, so containment is checked in
        // the one place that owns the root.
        var absolute = _files.AbsolutePathFor(path);

        ProcessResult result;

        try
        {
            result = await ProcessRunner
                .RunAsync(_ffprobePath, [.. BaseArguments, absolute], _timeout, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (ProcessStartFailedException cause)
        {
            throw new AudioProbeFailedException(
                path,
                $"'{_ffprobePath}' could not be run. Check Fonoteca:FfprobePath.",
                isFileFault: false,
                cause);
        }

        if (result.TimedOut)
        {
            throw new AudioProbeFailedException(
                path,
                $"ffprobe did not finish within {_timeout.TotalSeconds:0} seconds.",
                isFileFault: true);
        }

        if (!result.Succeeded)
        {
            // ffprobe puts one line on stderr and exits non-zero: "Invalid data
            // found when processing input", "No such file or directory". Both are
            // about these bytes, and both are worth showing verbatim.
            throw new AudioProbeFailedException(
                path,
                Summarise(result.StandardError) ?? $"ffprobe exited {result.ExitCode}.",
                isFileFault: true);
        }

        FfprobeDocument? document;

        try
        {
            document = JsonSerializer.Deserialize<FfprobeDocument>(result.StandardOutput, Json);
        }
        catch (JsonException cause)
        {
            throw new AudioProbeFailedException(
                path,
                "ffprobe's output could not be read as JSON.",
                isFileFault: false,
                cause);
        }

        if (document?.Streams is not [var stream, ..]) return null;

        var sampleRate = Number(stream.SampleRate);

        // Zero rate, zero channels, exit code zero: an empty file, or a text
        // file with an audio extension. See the remarks — this must not become a
        // measurement.
        if (sampleRate is not > 0 || stream.Channels is not > 0) return null;

        var duration = Seconds(stream.Duration) ?? Seconds(document.Format?.Duration);

        var quality = new AudioQuality
        {
            // The canonical identifier rather than the prose one, because this
            // column is read by rules — dedupe and upgrade ranking — before it is
            // read by a person, and "flac" is stable where a long name is not.
            Codec = stream.CodecName is { Length: > 0 } name ? name : "unknown",

            SampleRateHz = (int)sampleRate.Value,
            Channels = stream.Channels.Value,
            BitDepth = Number(stream.BitsPerRawSample) is > 0 and var depth ? (int)depth! : null,
            // Three sources, and the third is not decoration. Zero would be a
            // measurement — `QualityTier` reads a zero bitrate on a lossy file
            // as `LossyLow`, the worst tier there is, and hands that to the rule
            // that decides which duplicate to keep. Arithmetic over the file is
            // a worse answer than the container's own number and a far better
            // one than the bottom of the scale.
            BitrateBps = Number(stream.BitRate)
                ?? Number(document.Format?.BitRate)
                ?? Derived(Number(document.Format?.Size), duration)
                ?? 0,
            IsLossless = IsLossless(stream.CodecName),
            Duration = duration,
        };

        // At `-v error` a healthy stream says nothing at all. Anything here is
        // the decoder objecting to these bytes while it read them — "invalid
        // residual", "decode_frame() failed", "Header missing" — and it exits
        // zero having done so, which is why the exit code above is not enough.
        return new AudioProbeReading(quality, Summarise(result.StandardError));
    }

    /// <summary>Whether this codec's output is bit-exact.</summary>
    private static bool IsLossless(string? codec) =>
        codec is { Length: > 0 }
        && (LosslessCodecs.Contains(codec)

            // Every PCM flavour, and every DSD one. Prefixes rather than a list,
            // because ffprobe names them by layout — pcm_s24le, dsd_msbf_planar —
            // and the list would be forty entries that all mean the same thing.
            || codec.StartsWith("pcm_", StringComparison.OrdinalIgnoreCase)
            || codec.StartsWith("dsd_", StringComparison.OrdinalIgnoreCase));

    /// <summary>ffprobe writes every number as a string, and omits the ones it does not know.</summary>
    private static long? Number(string? value) =>
        long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;

    /// <summary>Bits per second over the whole file, when nothing else says.</summary>
    private static long? Derived(long? sizeBytes, TimeSpan? duration) =>
        sizeBytes is > 0 && duration is { TotalSeconds: > 0 } span
            ? (long)(sizeBytes.Value * 8 / span.TotalSeconds)
            : null;

    private static TimeSpan? Seconds(string? value) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            && parsed > 0
            ? TimeSpan.FromSeconds(parsed)
            : null;

    /// <summary>
    /// ffprobe's own sentence, cleaned up enough to show somebody.
    /// </summary>
    /// <remarks>
    /// <b>The first line, not the last.</b> ffmpeg reports the cause and then
    /// the consequence: a truncated FLAC says <c>invalid residual</c> and then
    /// <c>decode_frame() failed</c>, and only the first of those tells anybody
    /// anything. The failure path prints one line either way.
    ///
    /// <b>The <c>[mp3float @ 0x628a58d9f100]</c> prefix is stripped.</b> It is
    /// an allocation address, so it differs on every run over the same file —
    /// which would make this note change under a person who reloaded the page,
    /// and make two readings of one file look like two different findings.
    /// </remarks>
    private static string? Summarise(string standardError)
    {
        var line = standardError
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault();

        if (string.IsNullOrWhiteSpace(line)) return null;

        line = ComponentPrefix.Replace(line, string.Empty).Trim();

        if (line.Length == 0) return null;

        return line.Length <= 200 ? line : line[..199] + "…";
    }

    private sealed record FfprobeDocument(
        [property: JsonPropertyName("streams")] IReadOnlyList<FfprobeStream>? Streams,
        [property: JsonPropertyName("format")] FfprobeFormat? Format);

    private sealed record FfprobeStream(
        [property: JsonPropertyName("codec_name")] string? CodecName,
        [property: JsonPropertyName("codec_long_name")] string? CodecLongName,
        [property: JsonPropertyName("sample_rate")] string? SampleRate,
        [property: JsonPropertyName("channels")] int? Channels,
        [property: JsonPropertyName("bits_per_raw_sample")] string? BitsPerRawSample,
        [property: JsonPropertyName("bit_rate")] string? BitRate,
        [property: JsonPropertyName("duration")] string? Duration);

    private sealed record FfprobeFormat(
        [property: JsonPropertyName("duration")] string? Duration,
        [property: JsonPropertyName("bit_rate")] string? BitRate,
        [property: JsonPropertyName("size")] string? Size);
}
