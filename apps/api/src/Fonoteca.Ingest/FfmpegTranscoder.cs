using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using Fonoteca.Domain.Abstractions;
using Fonoteca.Domain.Catalogue;

namespace Fonoteca.Ingest;

/// <summary>
/// A library file re-encoded while it is read, for a Subsonic stream.
/// </summary>
/// <remarks>
/// <b>A pipe, not a file.</b> ffmpeg writes to stdout and the caller copies it
/// to the response, so the first bytes leave within a fraction of a second
/// and nothing is kept. A slow reader holds ffmpeg back through the pipe; the
/// encode itself runs far faster than the music plays.
///
/// <b>Stereo or mono, at 44.1 or 48 kHz.</b> MP3 cannot carry more than two
/// channels or 48 kHz, and a phone plays two; ffmpeg picks the nearer rate.
/// Opus is constrained VBR, so its length can be estimated to within 2%; MP3
/// is CBR. No tags and no pictures go along: a client has them from the API.
///
/// <b>Only Opus stays gapless.</b> Ogg carries Opus's pre-skip and the end of
/// its last page; MP3's LAME header and AAC's priming need a seekable output.
/// </remarks>
public sealed class FfmpegTranscoder(FileSystemAudioFileStore files, string ffmpegPath)
{
    private const string Channels = "aformat=sample_rates=48000|44100:channel_layouts=stereo|mono";

    private readonly FileSystemAudioFileStore _files = files ?? throw new ArgumentNullException(nameof(files));

    private readonly string _ffmpegPath = string.IsNullOrWhiteSpace(ffmpegPath)
        ? throw new ArgumentException("An ffmpeg path is required.", nameof(ffmpegPath))
        : ffmpegPath;

    /// <summary>ffmpeg's command line for one transcode.</summary>
    public static IReadOnlyList<string> Arguments(string absolutePath, Transcode transcode, TimeSpan offset)
    {
        ArgumentNullException.ThrowIfNull(transcode);

        string[] seek = offset > TimeSpan.Zero
            ? ["-ss", offset.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture)]
            : [];

        string[] encoder = transcode.Codec switch
        {
            StreamCodec.Mp3 => ["-c:a", "libmp3lame", "-f", "mp3"],
            StreamCodec.Opus => ["-c:a", "libopus", "-vbr", "constrained", "-f", "ogg"],
            StreamCodec.Aac => ["-c:a", "aac", "-f", "adts"],
            _ => throw new ArgumentOutOfRangeException(nameof(transcode), transcode.Codec, null),
        };

        return
        [
            "-hide_banner", "-nostats", "-nostdin", "-v", "error",
            .. seek,
            "-i", absolutePath,
            "-map", "0:a:0", "-map_metadata", "-1", "-af", Channels,
            "-b:a", $"{transcode.KilobitsPerSecond.ToString(CultureInfo.InvariantCulture)}k",
            .. encoder,
            "pipe:1",
        ];
    }

    /// <summary>Starts ffmpeg on <paramref name="path"/>; the caller reads <see cref="Transcoding.Output"/>.</summary>
    /// <exception cref="UnauthorizedAccessException">The path leaves the library.</exception>
    /// <exception cref="AudioProbeFailedException">ffmpeg could not be run.</exception>
    public Transcoding Start(LibraryPath path, Transcode transcode, TimeSpan offset)
    {
        var startInfo = new ProcessStartInfo(_ffmpegPath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var argument in Arguments(_files.AbsolutePathFor(path), transcode, offset))
        {
            startInfo.ArgumentList.Add(argument);
        }

        var process = new Process { StartInfo = startInfo };

        try
        {
            process.Start();
        }
        catch (Win32Exception cause)
        {
            process.Dispose();

            throw new AudioProbeFailedException(
                path, $"'{_ffmpegPath}' could not be run. Check Fonoteca:FfmpegPath.", isFileFault: false, cause);
        }

        return new Transcoding(process);
    }
}

/// <summary>A running transcode. Disposing it stops ffmpeg.</summary>
public sealed class Transcoding : IAsyncDisposable
{
    private readonly Process _process;

    // Drained from the start, as ProcessRunner does: a full stderr pipe would
    // stop ffmpeg writing stdout.
    private readonly Task<string> _errors;

    internal Transcoding(Process process)
    {
        _process = process;
        _errors = process.StandardError.ReadToEndAsync();
    }

    /// <summary>The encoded bytes.</summary>
    public Stream Output => _process.StandardOutput.BaseStream;

    /// <summary>Waits for ffmpeg to end; null where it succeeded, else the last thing it said.</summary>
    public async Task<string?> FailureAsync(CancellationToken cancellationToken)
    {
        await _process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

        var errors = await _errors.ConfigureAwait(false);

        return _process.ExitCode == 0
            ? null
            : errors.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault()
                ?? $"ffmpeg exited {_process.ExitCode.ToString(CultureInfo.InvariantCulture)}.";
    }

    public async ValueTask DisposeAsync()
    {
        ProcessRunner.Kill(_process);

        try
        {
            await _errors.ConfigureAwait(false);
        }
        catch (IOException)
        {
            // The pipe went with the process; what it said no longer matters.
        }

        _process.Dispose();
    }
}
