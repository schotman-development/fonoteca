using System.Text.Json;
using System.Text.Json.Serialization;
using Fonoteca.Domain.Abstractions;

namespace Fonoteca.Ingest;

/// <summary>
/// <see cref="IAudioFingerprinter"/> over Chromaprint's <c>fpcalc</c>.
/// </summary>
/// <remarks>
/// ADR 0001 settled the mechanism: Chromaprint has no usable .NET binding, so
/// fingerprinting shells out. That is true of every candidate stack except Rust,
/// so it cost nothing relative to the alternatives.
///
/// Two details are load-bearing:
///
/// - <b><c>-length 120</c> is passed explicitly</b>, even though it is fpcalc's
///   current default. AcoustID's index was built at 120 seconds; a future fpcalc
///   changing its default would silently produce fingerprints that match nothing,
///   with no error anywhere and no way to tell the difference from audio nobody
///   has ever submitted.
/// - <b>The duration reported is the whole file's</b>, not the 120 seconds
///   analysed, and that is what AcoustID wants — it is how a track is told apart
///   from a twelve-minute extended mix that opens identically.
///
/// Constructed with plain values rather than <c>IOptions</c>, exactly as
/// <see cref="FileSystemAudioFileStore"/> takes its root: this assembly carries
/// no NuGet references and keeping it that way means the caller does the
/// logging.
/// </remarks>
public sealed class FpcalcFingerprinter(
    FileSystemAudioFileStore files,
    string fpcalcPath,
    TimeSpan? timeout = null) : IAudioFingerprinter
{
    /// <summary>Seconds of audio analysed. AcoustID's index was built at this length.</summary>
    public const int AnalysisLengthSeconds = 120;

    /// <summary>
    /// Per-file ceiling. Generous — a 454 MB FLAC on a slow disk is nothing like
    /// the 0.19s a normal track takes — but finite, because one pathological file
    /// must not hold a pass over 100,000 of them.
    /// </summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(60);

    private static readonly string[] BaseArguments =
        ["-json", "-length", AnalysisLengthSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture)];

    private readonly FileSystemAudioFileStore _files =
        files ?? throw new ArgumentNullException(nameof(files));

    private readonly string _fpcalcPath = string.IsNullOrWhiteSpace(fpcalcPath)
        ? throw new ArgumentException("An fpcalc path is required.", nameof(fpcalcPath))
        : fpcalcPath;

    private readonly TimeSpan _timeout = timeout ?? DefaultTimeout;

    /// <summary>
    /// Runs the tool once with no file, to fail loudly at the start of a pass
    /// rather than identically on every one of 100,000 files.
    /// </summary>
    /// <returns>The version string fpcalc reports.</returns>
    /// <exception cref="FingerprintFailedException">
    /// The tool is missing or unrunnable. Never a file fault — there is no file.
    /// </exception>
    public async Task<string> VerifyAvailableAsync(CancellationToken cancellationToken = default)
    {
        ProcessResult result;

        try
        {
            result = await ProcessRunner
                .RunAsync(_fpcalcPath, ["-version"], _timeout, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (ProcessStartFailedException cause)
        {
            throw NotRunnable(default, cause);
        }

        if (!result.Succeeded)
        {
            throw new FingerprintFailedException(
                default,
                $"'{_fpcalcPath} -version' exited {result.ExitCode}. Check Fonoteca:FpcalcPath.",
                isFileFault: false);
        }

        return result.StandardOutput.Trim();
    }

    public async Task<AudioFingerprint> ComputeAsync(
        LibraryPath path,
        CancellationToken cancellationToken = default)
    {
        // Resolved here rather than by the caller so containment is checked in
        // the one place that owns the root.
        var absolute = _files.AbsolutePathFor(path);

        ProcessResult result;

        try
        {
            result = await ProcessRunner
                .RunAsync(_fpcalcPath, [.. BaseArguments, absolute], _timeout, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (ProcessStartFailedException cause)
        {
            throw NotRunnable(path, cause);
        }

        if (result.TimedOut)
        {
            // The tool ran, so this is about the file — a container that sends
            // the decoder into a loop is a property of these bytes.
            throw new FingerprintFailedException(
                path,
                $"fpcalc did not finish within {_timeout.TotalSeconds:0} seconds.",
                isFileFault: true);
        }

        if (result.ExitCode != 0)
        {
            throw new FingerprintFailedException(
                path,
                Describe(result),
                isFileFault: true);
        }

        FpcalcOutput? output;

        try
        {
            output = JsonSerializer.Deserialize(result.StandardOutput, FpcalcJson.Default.FpcalcOutput);
        }
        catch (JsonException cause)
        {
            throw new FingerprintFailedException(
                path, "fpcalc exited 0 but did not print JSON.", isFileFault: false, cause);
        }

        if (output is null || string.IsNullOrEmpty(output.Fingerprint))
        {
            throw new FingerprintFailedException(
                path, "fpcalc exited 0 but returned no fingerprint.", isFileFault: true);
        }

        if (output.Duration <= 0)
        {
            // AcoustID rejects a fingerprint with no duration, and the client
            // asserts the same thing. Catching it here names the file.
            throw new FingerprintFailedException(
                path, $"fpcalc reported a duration of {output.Duration} seconds.", isFileFault: true);
        }

        return new AudioFingerprint(output.Fingerprint, TimeSpan.FromSeconds(output.Duration));
    }

    private FingerprintFailedException NotRunnable(LibraryPath path, Exception cause) =>
        new(
            path,
            $"Could not run fpcalc at '{_fpcalcPath}'. Set Fonoteca:FpcalcPath to an absolute "
            + "path — a bare name resolves through PATH, which a long-running service started "
            + "before the toolchain existed will not have.",
            isFileFault: false,
            cause);

    /// <summary>fpcalc's own message when it has one, since it names the actual problem.</summary>
    private static string Describe(ProcessResult result)
    {
        var reported = result.StandardError.Trim();

        // fpcalc prints "ERROR: Could not open the input file (...)" and exits 2.
        return reported.Length > 0
            ? reported
            : $"fpcalc exited {result.ExitCode} without explaining why.";
    }
}

/// <summary>What <c>fpcalc -json</c> prints.</summary>
internal sealed record FpcalcOutput
{
    /// <summary>Whole-file duration in seconds, fractional.</summary>
    [JsonPropertyName("duration")]
    public double Duration { get; init; }

    /// <summary>Base64 Chromaprint, as AcoustID wants it.</summary>
    [JsonPropertyName("fingerprint")]
    public string? Fingerprint { get; init; }
}

/// <summary>
/// Source-generated, matching the AcoustID client. Identification runs once per
/// file over a library of 100,000, so the reflection-based serializer's startup
/// cost is paid on every one of them.
/// </summary>
[JsonSourceGenerationOptions(NumberHandling = JsonNumberHandling.AllowReadingFromString)]
[JsonSerializable(typeof(FpcalcOutput))]
internal sealed partial class FpcalcJson : JsonSerializerContext;
