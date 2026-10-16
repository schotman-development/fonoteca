using System.Text.Json;
using Fonoteca.Domain.Abstractions;
using Fonoteca.Domain.Catalogue;

namespace Fonoteca.Ingest;

/// <summary>
/// <see cref="ITempoAndKeyMeasurer"/> over Essentia, through the Python
/// interpreter it is installed for.
/// </summary>
/// <remarks>
/// Essentia ships as a Python package, so the tool is an interpreter and the
/// program is <see cref="Script"/>, handed over with <c>-c</c>: nothing to copy
/// beside the binaries, and <c>-I</c> keeps the user's site-packages and the
/// working directory off its import path. One process per file, like ffmpeg's;
/// the import costs 0.7s against 3–5s of listening.
///
/// <list type="bullet">
/// <item>Tempo: <c>RhythmExtractor2013</c>, multifeature, whose confidence
/// is the one Essentia gives guidelines for.</item>
/// <item>Key: <c>KeyExtractor</c> with its default profile, <c>bgate</c>. On
/// 16 first movements here whose title prints the key, it and
/// <c>krumhansl</c> named 11 exactly, <c>edma</c> 10 and <c>temperley</c> 9.</item>
/// </list>
///
/// Exit 3 is the script saying Essentia would not import — the tool's fault,
/// which ends the pass. Any other failure is the file's.
/// </remarks>
public sealed class EssentiaTempoAndKeyMeasurer(
    FileSystemAudioFileStore files,
    string pythonPath,
    TimeSpan? timeout = null) : ITempoAndKeyMeasurer
{
    /// <summary>Per-file ceiling. A 32-minute symphony movement is the longest file here.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(5);

    private const int EssentiaMissing = 3;

    internal const string Script = """
        import json, sys
        try:
            import essentia
            import essentia.standard as es
        except ImportError as cause:
            print(f"Essentia would not import: {cause}", file=sys.stderr)
            sys.exit(3)
        essentia.log.infoActive = False
        essentia.log.warningActive = False
        audio = es.MonoLoader(filename=sys.argv[1], sampleRate=44100)()
        if len(audio) == 0:
            print("null")
            sys.exit(0)
        bpm, _, confidence, _, _ = es.RhythmExtractor2013(method="multifeature")(audio)
        key, scale, strength = es.KeyExtractor(profileType="bgate")(audio)
        print(json.dumps({"bpm": float(bpm), "bpmConfidence": float(confidence), "key": key, "scale": scale, "keyStrength": float(strength)}))
        """;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly FileSystemAudioFileStore _files = files ?? throw new ArgumentNullException(nameof(files));

    private readonly string _pythonPath = string.IsNullOrWhiteSpace(pythonPath)
        ? throw new ArgumentException("A Python path is required.", nameof(pythonPath))
        : pythonPath;

    private readonly TimeSpan _timeout = timeout ?? DefaultTimeout;

    public async Task<TempoAndKey?> MeasureAsync(LibraryPath path, CancellationToken cancellationToken = default)
    {
        var absolute = _files.AbsolutePathFor(path);

        ProcessResult result;

        try
        {
            result = await ProcessRunner
                .RunAsync(_pythonPath, ["-I", "-c", Script, absolute], _timeout, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (ProcessStartFailedException cause)
        {
            throw new AudioProbeFailedException(
                path, $"'{_pythonPath}' could not be run. Check Fonoteca:EssentiaPythonPath.", isFileFault: false, cause);
        }

        if (result.TimedOut)
        {
            throw new AudioProbeFailedException(
                path, $"Essentia did not finish within {_timeout.TotalSeconds:0} seconds.", isFileFault: true);
        }

        if (!result.Succeeded)
        {
            throw new AudioProbeFailedException(
                path,
                LastLine(result.StandardError) ?? $"Essentia exited {result.ExitCode}.",
                isFileFault: result.ExitCode != EssentiaMissing);
        }

        return Parse(result.StandardOutput);
    }

    /// <summary>The script's one line of JSON; null for a file that held no audio.</summary>
    public static TempoAndKey? Parse(string output) => JsonSerializer.Deserialize<TempoAndKey>(output, Json);

    private static string? LastLine(string? log) =>
        log?.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) is { Length: > 0 } lines
            ? lines[^1]
            : null;
}
