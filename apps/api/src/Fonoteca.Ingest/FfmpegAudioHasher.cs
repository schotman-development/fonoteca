using Fonoteca.Domain.Abstractions;

namespace Fonoteca.Ingest;

/// <summary>
/// The MD5 of a file's audio packets, copied out of the container rather than
/// decoded: what <c>MediaFile.AudioHash</c> holds.
/// </summary>
/// <remarks>
/// <b>Tags are not packets</b>, so a tagger can rewrite every frame of ID3, every
/// Vorbis comment and every MP4 atom and this answers the same — measured on
/// the library's own FLAC, MP3 and M4A remuxed with a 3 KB title added, where
/// each file changed size and kept its hash. That is the whole job: telling a
/// file another program retagged from one it replaced, which size and mtime
/// cannot.
///
/// Stream copy, so it costs a read of the file and no decode. Two encodes of
/// one recording differ, which is right for this and is not dedupe.
///
/// A file that will not demux answers null: the caller cannot vouch for its
/// audio. A tool that will not start throws, as the probe does.
/// </remarks>
public sealed class FfmpegAudioHasher(
    FileSystemAudioFileStore files,
    string ffmpegPath,
    TimeSpan? timeout = null)
{
    /// <summary>Per-file ceiling. A read without a decode; this is for a DSD box set over NFS.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(5);

    private readonly FileSystemAudioFileStore _files = files ?? throw new ArgumentNullException(nameof(files));

    private readonly string _ffmpegPath = string.IsNullOrWhiteSpace(ffmpegPath)
        ? throw new ArgumentException("An ffmpeg path is required.", nameof(ffmpegPath))
        : ffmpegPath;

    private readonly TimeSpan _timeout = timeout ?? DefaultTimeout;

    /// <returns>Lowercase hex, or null when the file holds no audio ffmpeg could copy.</returns>
    /// <exception cref="AudioProbeFailedException">ffmpeg could not be run (<c>IsFileFault</c> false).</exception>
    public async Task<string?> HashAsync(LibraryPath path, CancellationToken cancellationToken = default)
    {
        var absolute = _files.AbsolutePathFor(path);

        ProcessResult result;

        try
        {
            result = await ProcessRunner
                .RunAsync(
                    _ffmpegPath,
                    ["-hide_banner", "-nostats", "-nostdin", "-v", "error", "-i", absolute, "-map", "0:a:0", "-c", "copy", "-f", "md5", "-"],
                    _timeout,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (ProcessStartFailedException cause)
        {
            throw new AudioProbeFailedException(
                path, $"'{_ffmpegPath}' could not be run. Check Fonoteca:FfmpegPath.", isFileFault: false, cause);
        }

        var output = result.StandardOutput.Trim();

        return result.Succeeded && output.StartsWith("MD5=", StringComparison.Ordinal) && output.Length > 4
            ? output[4..]
            : null;
    }
}
