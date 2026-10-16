using System.Globalization;
using System.Security.Cryptography;

namespace Fonoteca.Ingest;

/// <summary>
/// A cover no larger than a client asked for, and an album's motion artwork as
/// an animated WebP, each made by ffmpeg once and kept.
/// </summary>
/// <remarks>
/// <b>Keyed on the picture, not on where it came from.</b> A kept copy is named
/// by the SHA-256 of the bytes it was made from and the size, so a cover that
/// changes — another archive image chosen, an upload, a retagged file — is a
/// different name, and a copy of the old one can never be served for it. The
/// old copies are left behind as orphans. ponytail: kept in the temp folder
/// with no eviction; that is tens of MB a library per size asked for, and a
/// cleared folder only costs scaling again. Evict when a client is found
/// asking for many sizes.
///
/// <b>Never larger.</b> A picture already within the size is served as it is,
/// and an empty marker remembers that so ffprobe is not asked again.
///
/// <b>A picture ffmpeg cannot read is served as it is</b>, as is every picture
/// when ffmpeg will not start or the folder cannot be written: a cover too big
/// beats no cover. None of these is remembered, so a fix takes effect at once.
///
/// <b>A video is always converted</b>, at its own size where that is smaller
/// than asked: a client that can draw pictures but not play an MP4 is why it
/// is asked for, so there is no "as it is" for it.
/// </remarks>
public sealed class FfmpegCoverScaler(
    string ffmpegPath,
    string ffprobePath,
    string directory,
    TimeSpan? timeout = null) : IDisposable
{
    /// <summary>Per-picture ceiling. A decode and an encode of one image.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Per-video ceiling. A 15-second 1080 video takes about 6 s at 440 px.</summary>
    public static readonly TimeSpan MotionTimeout = TimeSpan.FromMinutes(5);

    private readonly string _ffmpegPath = string.IsNullOrWhiteSpace(ffmpegPath)
        ? throw new ArgumentException("An ffmpeg path is required.", nameof(ffmpegPath))
        : ffmpegPath;

    private readonly string _ffprobePath = string.IsNullOrWhiteSpace(ffprobePath)
        ? throw new ArgumentException("An ffprobe path is required.", nameof(ffprobePath))
        : ffprobePath;

    private readonly TimeSpan _timeout = timeout ?? DefaultTimeout;

    // A grid of tiles asks for a page of covers at once. One ffmpeg per core,
    // the rest wait, rather than a process per tile.
    private readonly SemaphoreSlim _slots = new(Environment.ProcessorCount);

    /// <summary>Where the kept copies are.</summary>
    public string Directory { get; } = directory;

    /// <summary>The kept copy's name, without its extension.</summary>
    public static string KeyOf(byte[] picture, int size) =>
        $"{Convert.ToHexStringLower(SHA256.HashData(picture))}-{size.ToString(CultureInfo.InvariantCulture)}";

    /// <returns>
    /// A JPEG at most <paramref name="size"/> pixels on its longest side, or null
    /// when the picture should be served as it is.
    /// </returns>
    public async Task<byte[]?> ScaleAsync(byte[] picture, int size, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(picture);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(size);

        return await MakeAsync(
                picture,
                size,
                ".jpg",
                _timeout,
                longest => longest <= size ? null : ["-vf", Fit(size), "-frames:v", "1", "-q:v", "3"],
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <returns>
    /// The video as an animated WebP that loops forever, silent, at most
    /// <paramref name="size"/> pixels on its longest side — its own size where
    /// that is smaller or <paramref name="size"/> is 0 — at no more than 30
    /// frames a second; or null when ffmpeg could not make one.
    /// </returns>
    public async Task<byte[]?> AnimateAsync(byte[] video, int size, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(video);
        ArgumentOutOfRangeException.ThrowIfNegative(size);

        // Quality 80 keeps a sky's gradient smooth; lanczos keeps text sharp.
        return await MakeAsync(
                video,
                size,
                ".webp",
                MotionTimeout,
                longest =>
                [
                    "-an",
                    "-vf", $"fps=fps='min(source_fps,30)',{Fit(size == 0 ? longest : Math.Min(size, longest))}:flags=lanczos",
                    "-c:v", "libwebp_anim",
                    "-loop", "0",
                    "-quality", "80",
                ],
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// The kept copy of <paramref name="input"/> for this size, made by ffmpeg
    /// with the output arguments <paramref name="arguments"/> gives for the
    /// input's longest side, or null where they are null (kept as it is) or
    /// ffmpeg failed.
    /// </summary>
    private async Task<byte[]?> MakeAsync(
        byte[] input,
        int size,
        string extension,
        TimeSpan timeout,
        Func<int, string[]?> arguments,
        CancellationToken cancellationToken)
    {
        var key = KeyOf(input, size);
        var kept = Path.Combine(Directory, key + extension);
        var asIs = Path.Combine(Directory, key + ".as-is");

        if (File.Exists(kept) && new FileInfo(kept).Length > 0) return await File.ReadAllBytesAsync(kept, cancellationToken).ConfigureAwait(false);
        if (File.Exists(asIs)) return null;

        await _slots.WaitAsync(cancellationToken).ConfigureAwait(false);

        // Unique per request: two asking for the same cover at once each make
        // their own and the second move wins, which is the same picture.
        var unique = Guid.NewGuid().ToString("N");
        var source = Path.Combine(Directory, $"{key}.{unique}.in");
        var made = Path.Combine(Directory, $"{key}.{unique}{extension}");

        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            await File.WriteAllBytesAsync(source, input, cancellationToken).ConfigureAwait(false);

            var probe = await ProcessRunner
                .RunAsync(
                    _ffprobePath,
                    ["-v", "error", "-select_streams", "v:0", "-show_entries", "stream=width,height", "-of", "csv=p=0", source],
                    timeout,
                    cancellationToken)
                .ConfigureAwait(false);

            if (!probe.Succeeded || Longest(probe.StandardOutput) is not { } longest) return null;

            if (arguments(longest) is not { } output)
            {
                await File.WriteAllBytesAsync(asIs, [], cancellationToken).ConfigureAwait(false);
                return null;
            }

            var result = await ProcessRunner
                .RunAsync(
                    _ffmpegPath,
                    ["-hide_banner", "-nostdin", "-v", "error", "-i", source, .. output, "-y", made],
                    timeout,
                    cancellationToken)
                .ConfigureAwait(false);

            if (!result.Succeeded || !File.Exists(made)) return null;

            File.Move(made, kept, overwrite: true);

            return await File.ReadAllBytesAsync(kept, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception cause) when (cause is ProcessStartFailedException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
        finally
        {
            if (File.Exists(source)) File.Delete(source);
            if (File.Exists(made)) File.Delete(made);
            _slots.Release();
        }
    }

    public void Dispose() => _slots.Dispose();

    /// <summary>A scale filter taking the longest side to the size, the other in proportion.</summary>
    private static string Fit(int size) => string.Create(
        CultureInfo.InvariantCulture,
        $"scale=w='if(gte(iw,ih),{size},-1)':h='if(gte(iw,ih),-1,{size})'");

    /// <summary>The longer side from ffprobe's <c>width,height</c>, or null.</summary>
    private static int? Longest(string output)
    {
        var sides = output.Trim().Split(',');

        return sides.Length == 2
            && int.TryParse(sides[0], NumberStyles.None, CultureInfo.InvariantCulture, out var width)
            && int.TryParse(sides[1], NumberStyles.None, CultureInfo.InvariantCulture, out var height)
            ? Math.Max(width, height)
            : null;
    }
}
