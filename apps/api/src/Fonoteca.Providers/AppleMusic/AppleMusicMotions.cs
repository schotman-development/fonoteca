using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Fonoteca.Domain.Abstractions;
using Fonoteca.Providers.Logging;
using Microsoft.Extensions.Logging;

namespace Fonoteca.Providers.AppleMusic;

/// <summary>
/// <see cref="IAlbumMotions"/> over Apple Music's public pages.
/// </summary>
/// <remarks>
/// <b>Three steps, each on something a browser fetches without signing in.</b>
/// The documented search API turns a barcode into Apple's album id; the album's
/// public page names the motion artwork in the JSON it embeds for its own
/// header; the video CDN serves each video's HLS ladder, whose H.264 rungs are
/// single MP4 files read whole. ADR 0014 records why the web player's private
/// API, which answers the same question in one request, is not used.
///
/// <b>The record is recognised by <see cref="AppleMusicRecords"/></b>, which
/// the classical pages share: a record a barcode identified ends the asking
/// whether or not it has a video.
///
/// <b>Null is an answer and a throw is not</b> — see <see cref="IAlbumMotions"/>.
/// What is wrong with one record's video (a missing rung, an odd host, a file
/// too large) is that record having nothing usable, and reads as null, so one
/// album cannot stop a library's run. What is wrong with the service — an
/// outage, or a page that no longer has the shape this reads — throws.
/// </remarks>
public sealed partial class AppleMusicMotions(
    AppleMusicRecords records,
    IHttpClientFactory clients,
    ILogger<AppleMusicMotions> logger) : IAlbumMotions
{

    /// <summary>The largest video kept. Album motion runs 8 to 35 seconds; 1080p H.264 is about 25 MB.</summary>
    public const long MaxVideoBytes = 100L * 1024 * 1024;

    /// <summary>The only host a video, or the playlists naming it, may come from.</summary>
    private const string VideoHost = "mvod.itunes.apple.com";

    public async Task<AlbumMotionFound?> FindAsync(
        AlbumToFind album,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(album);

        var found = await records.FindAsync(album, SquareAsync, cancellationToken).ConfigureAwait(false);

        if (found is null)
        {
            ProviderLog.AppleMotionNotFound(logger, album.Artist, album.Title);
            return null;
        }

        ProviderLog.AppleMotionFound(logger, found.AlbumId, found.Storefront, album.Artist, album.Title, found.MatchedBy);

        return new AlbumMotionFound(found.AlbumId, found.Storefront, found.MatchedBy, found.Value);
    }

    /// <summary>A record's square video, or null where its page names none worth keeping.</summary>
    private async Task<byte[]?> SquareAsync(string storefront, long album, CancellationToken cancellationToken) =>
        await PageAsync(storefront, album, cancellationToken).ConfigureAwait(false) is { } link
            ? await VideoAsync(link, cancellationToken).ConfigureAwait(false)
            : null;

    /// <summary>
    /// The square motion artwork the album's own page names for its header, or
    /// null where it has no page or no square video.
    /// </summary>
    /// <remarks>
    /// <b>Only the header</b>: the page also lists the artist's other records,
    /// and a video found anywhere in the document could be one of theirs. A page
    /// whose embedded JSON is gone, or has no sections, is the page changing
    /// shape under this reader — every album would read as having none, so it
    /// throws instead and the caller stops asking. A page with sections and no
    /// header is this one page being different, and reads as nothing here.
    ///
    /// <b>Not the tall video</b> the header also names: only the square one is
    /// kept (ADR 0014, amended 2026-10-15).
    /// </remarks>
    private async Task<string?> PageAsync(
        string storefront,
        long album,
        CancellationToken cancellationToken)
    {
        string html;

        using (var response = await records.SendAsync(
                AppleMusicOptions.PageHttpClientName,
                new Uri($"{Uri.EscapeDataString(storefront)}/album/{album.ToString(CultureInfo.InvariantCulture)}", UriKind.Relative),
                cancellationToken)
            .ConfigureAwait(false))
        {
            if (response.StatusCode == HttpStatusCode.NotFound) return null;

            if (!response.IsSuccessStatusCode)
            {
                throw Unavailable($"the album page answered {(int)response.StatusCode}", cause: null);
            }

            html = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }

        if (AppleMusicRecords.ServerData().Match(html) is not { Success: true } script)
        {
            throw Changed($"album {album}'s page carries no serialized-server-data");
        }

        try
        {
            using var document = JsonDocument.Parse(script.Groups["json"].Value);

            if (!document.RootElement.TryGetProperty("data", out var data)
                || data.ValueKind != JsonValueKind.Array
                || data.GetArrayLength() == 0
                || !data[0].TryGetProperty("data", out var page)
                || !page.TryGetProperty("sections", out var sections)
                || sections.ValueKind != JsonValueKind.Array)
            {
                throw Changed($"album {album}'s page data has no sections");
            }

            foreach (var section in sections.EnumerateArray())
            {
                if (!section.TryGetProperty("itemKind", out var kind)
                    || kind.GetString() != "containerDetailHeaderLockup"
                    || !section.TryGetProperty("items", out var items)
                    || items.ValueKind != JsonValueKind.Array
                    || items.GetArrayLength() == 0)
                {
                    continue;
                }

                return Video(items[0], "videoArtwork", "motionDetailSquare");
            }

            ProviderLog.AppleMotionNoHeader(logger, album);
            return null;
        }
        catch (JsonException cause)
        {
            throw Changed($"album {album}'s page data is not JSON ({cause.Message})");
        }
    }

    private static string? Video(JsonElement header, string artwork, string variant) =>
        header.TryGetProperty(artwork, out var motion)
        && motion.ValueKind == JsonValueKind.Object
        && motion.TryGetProperty("dictionary", out var dictionary)
        && dictionary.ValueKind == JsonValueKind.Object
        && dictionary.TryGetProperty(variant, out var asset)
        && asset.ValueKind == JsonValueKind.Object
        && asset.TryGetProperty("video", out var video)
        && video.ValueKind == JsonValueKind.String
            ? video.GetString()
            : null;

    /// <summary>
    /// The best H.264 rung of one video, read whole, or null where it has none worth keeping.
    /// </summary>
    /// <remarks>
    /// <b>H.264 because everything plays it.</b> The ladder goes higher in HEVC
    /// (2160 square), which some browsers and Android devices cannot decode; the
    /// H.264 rungs top out at 1080 wide.
    ///
    /// <b>Each rung is one fragmented MP4, addressed by byte ranges</b>, so
    /// fetching the file the playlist names gives a video any player opens, with
    /// no remuxing. A rung split across files is refused rather than stitched.
    /// </remarks>
    private async Task<byte[]?> VideoAsync(string master, CancellationToken cancellationToken)
    {
        if (OnVideoHost(master) is not { } masterUri)
        {
            ProviderLog.AppleMotionRejected(logger, master, "not on Apple's video host");
            return null;
        }

        if (await TextAsync(masterUri, cancellationToken).ConfigureAwait(false) is not { } ladder) return null;

        if (BestH264(ladder, masterUri) is not { } rung || OnVideoHost(rung.AbsoluteUri) is null)
        {
            ProviderLog.AppleMotionRejected(logger, master, "no H.264 rung on Apple's video host");
            return null;
        }

        if (await TextAsync(rung, cancellationToken).ConfigureAwait(false) is not { } media) return null;

        if (SingleFile(media, rung) is not { } file || OnVideoHost(file.AbsoluteUri) is null)
        {
            ProviderLog.AppleMotionRejected(logger, rung.AbsoluteUri, "the rung is not one file on Apple's video host");
            return null;
        }

        using var response = await VideoSendAsync(file, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Forbidden or HttpStatusCode.Gone) return null;

        if (!response.IsSuccessStatusCode)
        {
            throw Unavailable($"a video answered {(int)response.StatusCode}", cause: null);
        }

        if (response.Content.Headers.ContentType?.MediaType != "video/mp4"
            || response.Content.Headers.ContentLength is > MaxVideoBytes)
        {
            ProviderLog.AppleMotionRejected(logger, file.AbsoluteUri, "not an MP4 of a size worth keeping");
            return null;
        }

        byte[] bytes;

        try
        {
            var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);

            await using (stream.ConfigureAwait(false))
            {
                using var buffer = new MemoryStream();
                var chunk = new byte[81_920];
                int read;

                while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    if (buffer.Length + read > MaxVideoBytes)
                    {
                        ProviderLog.AppleMotionRejected(logger, file.AbsoluteUri, "larger than it said");
                        return null;
                    }

                    buffer.Write(chunk, 0, read);
                }

                bytes = buffer.ToArray();
            }
        }
        catch (HttpRequestException cause)
        {
            throw Unavailable(cause.Message, cause);
        }
        catch (IOException cause)
        {
            throw Unavailable(cause.Message, cause);
        }

        // An MP4 opens with its `ftyp` box. Anything else is not a video,
        // whatever the CDN called it, and is not going into a library.
        if (bytes.Length < 8 || bytes[4] != (byte)'f' || bytes[5] != (byte)'t' || bytes[6] != (byte)'y' || bytes[7] != (byte)'p')
        {
            ProviderLog.AppleMotionRejected(logger, file.AbsoluteUri, "not an MP4");
            return null;
        }

        return bytes;
    }

    /// <summary>A playlist's text, or null where the CDN no longer has it.</summary>
    private async Task<string?> TextAsync(Uri playlist, CancellationToken cancellationToken)
    {
        using var response = await VideoSendAsync(playlist, HttpCompletionOption.ResponseContentRead, cancellationToken)
            .ConfigureAwait(false);

        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Forbidden or HttpStatusCode.Gone) return null;

        if (!response.IsSuccessStatusCode)
        {
            throw Unavailable($"a video playlist answered {(int)response.StatusCode}", cause: null);
        }

        try
        {
            return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException cause)
        {
            throw Unavailable(cause.Message, cause);
        }
    }

    /// <summary>The highest-bandwidth H.264 rung a master playlist lists.</summary>
    /// <remarks>
    /// Only <c>EXT-X-STREAM-INF</c>: the <c>EXT-X-I-FRAME-STREAM-INF</c> lines
    /// beside them are trick-play keyframes, not the video.
    /// </remarks>
    internal static Uri? BestH264(string playlist, Uri master)
    {
        var lines = playlist.Split('\n', StringSplitOptions.TrimEntries);
        Uri? best = null;
        long bandwidth = -1;

        for (var index = 0; index < lines.Length - 1; index++)
        {
            if (!lines[index].StartsWith("#EXT-X-STREAM-INF:", StringComparison.Ordinal)) continue;

            var attributes = Attributes(lines[index]);

            if (!attributes.TryGetValue("CODECS", out var codecs)
                || !codecs.StartsWith("avc1", StringComparison.Ordinal)
                || !attributes.TryGetValue("BANDWIDTH", out var declared)
                || !long.TryParse(declared, NumberStyles.None, CultureInfo.InvariantCulture, out var rate)
                || rate <= bandwidth)
            {
                continue;
            }

            if (lines[index + 1] is not { Length: > 0 } uri
                || uri.StartsWith('#')
                || !Uri.TryCreate(master, uri, out var resolved))
            {
                continue;
            }

            best = resolved;
            bandwidth = rate;
        }

        return best;
    }

    /// <summary>The one file a media playlist's every segment is a byte range of, or null.</summary>
    internal static Uri? SingleFile(string playlist, Uri media)
    {
        string? map = null;
        var segments = new HashSet<string>(StringComparer.Ordinal);

        foreach (var line in playlist.Split('\n', StringSplitOptions.TrimEntries))
        {
            if (line.StartsWith("#EXT-X-MAP:", StringComparison.Ordinal))
            {
                map = Attributes(line).GetValueOrDefault("URI");
            }
            else if (line.Length > 0 && !line.StartsWith('#'))
            {
                segments.Add(line);
            }
        }

        return map is not null
            && segments.Count > 0
            && segments.All(segment => segment == map)
            && Uri.TryCreate(media, map, out var file)
                ? file
                : null;
    }

    /// <summary>An HLS tag's attribute list: <c>NAME=value</c> or <c>NAME="value, with commas"</c>.</summary>
    private static Dictionary<string, string> Attributes(string line)
    {
        var attributes = new Dictionary<string, string>(StringComparer.Ordinal);

        // Last one wins rather than throwing: a playlist naming an attribute
        // twice is odd, not a reason to fail the album.
        foreach (Match match in AttributePattern().Matches(line[(line.IndexOf(':', StringComparison.Ordinal) + 1)..]))
        {
            attributes[match.Groups["name"].Value] = match.Groups["value"].Value.Trim('"');
        }

        return attributes;
    }

    private static Uri? OnVideoHost(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var parsed)
        && parsed.Scheme == Uri.UriSchemeHttps
        && string.Equals(parsed.Host, VideoHost, StringComparison.OrdinalIgnoreCase)
            ? parsed
            : null;

    private async Task<HttpResponseMessage> VideoSendAsync(
        Uri uri,
        HttpCompletionOption completion,
        CancellationToken cancellationToken)
    {
        var http = clients.CreateClient(AppleMusicOptions.VideoHttpClientName);

        try
        {
            return await http.GetAsync(uri, completion, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException cause)
        {
            throw Unavailable(cause.Message, cause);
        }
        catch (TaskCanceledException cause) when (!cancellationToken.IsCancellationRequested)
        {
            throw Unavailable("a video request timed out", cause);
        }
    }

    private static ProviderUnavailableException Unavailable(string reason, Exception? cause) =>
        AppleMusicRecords.Unavailable(reason, cause);

    /// <summary>The page no longer has the shape this reads. Retrying will not help; a code change will.</summary>
    private static ProviderRejectedException Changed(string reason) =>
        new(AppleMusicRecords.ProviderName, $"Apple Music's album page has changed shape: {reason}.");

    [GeneratedRegex("""(?<name>[A-Z0-9_-]+)=(?<value>"[^"]*"|[^,]*)""")]
    private static partial Regex AttributePattern();
}
