using System.Globalization;
using Fonoteca.Api.Logging;
using Fonoteca.Domain.Abstractions;
using Fonoteca.Domain.Catalogue;
using Fonoteca.Ingest;

namespace Fonoteca.Api.Subsonic;

/// <summary>
/// A song re-encoded as it is sent: <c>stream</c> with a format or a bitrate.
/// </summary>
/// <remarks>
/// <b>An error before the first byte, a cut connection after it.</b> ffmpeg is
/// read before anything is answered, so a file it cannot decode is a Subsonic
/// error a client can show. Once audio has gone out a failure can only abort
/// the response, which a client reads as a failed stream rather than a song
/// that ends early.
///
/// <b>An estimated length is kept to exactly.</b> With
/// <c>estimateContentLength</c> the length is a promise: Kestrel refuses to
/// send more and resets a response that sends less. The estimate runs over
/// every transcode measured (<see cref="Transcode.EstimatedLength"/>), so the
/// rest is zeros, which decoders step over while hunting for a next frame
/// that never comes; a transcode that runs past it is cut there and logged.
///
/// <b>No ranges.</b> A transcode is made afresh on every request, so it has no
/// byte to range over. Seeking in one is <c>timeOffset</c>, OpenSubsonic's
/// <c>transcodeOffset</c>; on the original, which cannot start at an offset,
/// it is ignored and a client ranges as before.
/// </remarks>
public static partial class SubsonicEndpoints
{
    private const int TranscodeBufferBytes = 64 * 1024;

    private static async Task<IResult> TranscodedAsync(
        HttpContext http,
        FfmpegTranscoder transcoder,
        string path,
        Transcode transcode,
        TimeSpan offset,
        TimeSpan? duration,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        Transcoding running;

        try
        {
            running = transcoder.Start(new LibraryPath(path), transcode, offset);
        }
        catch (UnauthorizedAccessException)
        {
            return SubsonicResult.Error(70, "No such song.");
        }
        catch (AudioProbeFailedException failure)
        {
            Log.TranscodeFailed(logger, path, transcode.Name, failure.Message);
            return SubsonicResult.Error(0, failure.Message);
        }

        await using (running.ConfigureAwait(false))
        {
            var buffer = new byte[TranscodeBufferBytes];

            var count = await running.Output
                .ReadAtLeastAsync(buffer, 1, throwOnEndOfStream: false, cancellationToken)
                .ConfigureAwait(false);

            if (count == 0)
            {
                var failure = await running.FailureAsync(cancellationToken).ConfigureAwait(false) ?? "ffmpeg sent nothing.";

                Log.TranscodeFailed(logger, path, transcode.Name, failure);
                return SubsonicResult.Error(0, $"Could not transcode: {failure}");
            }

            long? length = duration is { } whole
                && string.Equals(Text(http, "estimateContentLength"), "true", StringComparison.OrdinalIgnoreCase)
                    ? transcode.EstimatedLength(whole - offset)
                    : null;

            http.Response.ContentType = transcode.MediaType;
            http.Response.Headers.XContentTypeOptions = "nosniff";
            http.Response.ContentLength = length;

            await SendAsync(http, running, buffer, count, length, path, transcode, logger, cancellationToken)
                .ConfigureAwait(false);
        }

        return Results.Empty;
    }

    /// <summary>Copies the transcode out, held to <paramref name="length"/> where one was promised.</summary>
    private static async Task SendAsync(
        HttpContext http,
        Transcoding running,
        byte[] buffer,
        int count,
        long? length,
        string path,
        Transcode transcode,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var body = http.Response.Body;
        var limit = length ?? long.MaxValue;
        var sent = 0L;
        var overran = false;

        while (count > 0)
        {
            if (count > limit - sent)
            {
                count = (int)(limit - sent);
                overran = true;
            }

            await body.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
            sent += count;

            count = overran ? 0 : await running.Output.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        }

        if (overran)
        {
            Log.TranscodeOverran(logger, path, transcode.Name, limit);
            return;
        }

        if (await running.FailureAsync(cancellationToken).ConfigureAwait(false) is { } failure)
        {
            Log.TranscodeFailed(logger, path, transcode.Name, failure);
            http.Abort();
            return;
        }

        Array.Clear(buffer);

        while (sent < limit && length is not null)
        {
            var padding = (int)Math.Min(buffer.Length, limit - sent);

            await body.WriteAsync(buffer.AsMemory(0, padding), cancellationToken).ConfigureAwait(false);
            sent += padding;
        }
    }

    /// <summary>An offset past the end of any song, which a larger one is held to.</summary>
    private static readonly TimeSpan MaxTimeOffset = TimeSpan.FromDays(1);

    /// <summary><c>timeOffset</c> in seconds; anything unreadable or negative is the start.</summary>
    private static TimeSpan TimeOffset(HttpContext http) =>
        double.TryParse(Text(http, "timeOffset"), NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)
        && seconds > 0
        && double.IsFinite(seconds)
            ? TimeSpan.FromSeconds(Math.Min(seconds, MaxTimeOffset.TotalSeconds))
            : TimeSpan.Zero;
}
