using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Fonoteca.Domain.Abstractions;
using Fonoteca.Domain.Catalogue;
using Fonoteca.Providers.Logging;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Polly;

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
/// <b>The record is recognised the way <c>QobuzCovers</c> recognises one, and
/// in its order.</b> This edition's barcode is an identifier; another
/// edition's names that edition, so it counts only under the same title; and a
/// title search counts only where a billed name, the title and the year all
/// agree. A record a barcode identified ends the asking whether or not it has
/// a video — a search could only find the same record, or a wrong one.
///
/// <b>Null is an answer and a throw is not</b> — see <see cref="IAlbumMotions"/>.
/// What is wrong with one record's video (a missing rung, an odd host, a file
/// too large) is that record having nothing usable, and reads as null, so one
/// album cannot stop a library's run. What is wrong with the service — an
/// outage, or a page that no longer has the shape this reads — throws.
/// </remarks>
public sealed partial class AppleMusicMotions(
    IHttpClientFactory clients,
    IOptions<AppleMusicOptions> options,
    ILogger<AppleMusicMotions> logger) : IAlbumMotions
{
    /// <summary>Name used in <see cref="ProviderException.Provider"/> and in log messages.</summary>
    public const string ProviderName = "Apple Music";

    /// <summary>The largest video kept. Album motion runs 8 to 35 seconds; 1080p H.264 is about 25 MB.</summary>
    public const long MaxVideoBytes = 100L * 1024 * 1024;

    /// <summary>The only host a video, or the playlists naming it, may come from.</summary>
    private const string VideoHost = "mvod.itunes.apple.com";

    /// <summary>
    /// How many search results to consider. Their ranking is not an answer, and
    /// ten costs what one does.
    /// </summary>
    private const int SearchLimit = 10;

    /// <summary>Album pages read per shop, so an album with many look-alike records stays a few requests.</summary>
    private const int MaxPages = 3;

    /// <summary>Other editions' barcodes asked about, in one request.</summary>
    private const int MaxEditions = 50;

    /// <summary>
    /// The suffixes Apple adds to a record's name and nobody else prints.
    /// </summary>
    private static readonly string[] KindSuffixes = [" - Single", " - EP"];

    public async Task<AlbumMotionFound?> FindAsync(
        AlbumToAnimate album,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(album);

        var fallback = options.Value.FallbackStorefront.Trim();

        string[] shops = fallback.Length == 0
            || string.Equals(fallback, AppleMusicOptions.Storefront, StringComparison.OrdinalIgnoreCase)
            ? [AppleMusicOptions.Storefront]
            : [AppleMusicOptions.Storefront, fallback];

        foreach (var storefront in shops)
        {
            var (found, identified) = await FindInAsync(album, storefront, cancellationToken)
                .ConfigureAwait(false);

            if (found is not null)
            {
                ProviderLog.AppleMotionFound(logger, found.AlbumId, storefront, album.Artist, album.Title, found.MatchedBy);
                return found;
            }

            // A record this shop knows is the same record in every other shop,
            // video and all. Only an album no shop has placed is worth a second.
            if (identified) break;
        }

        ProviderLog.AppleMotionNotFound(logger, album.Artist, album.Title);
        return null;
    }

    /// <summary>The video one shop has for this album, and whether it identified a record at all.</summary>
    private async Task<(AlbumMotionFound? Found, bool Identified)> FindInAsync(
        AlbumToAnimate album,
        string storefront,
        CancellationToken cancellationToken)
    {
        var asked = new HashSet<long>();
        var pages = 0;

        async Task<AlbumMotionFound?> FirstAnimatedAsync(IEnumerable<AppleRecord> records, string how)
        {
            foreach (var record in records)
            {
                if (pages >= MaxPages) return null;
                if (!asked.Add(record.Id)) continue;

                pages++;

                var links = await PageAsync(storefront, record.Id, cancellationToken).ConfigureAwait(false);

                if (links is null) continue;

                var square = links.Value.Square is { } s
                    ? await VideoAsync(s, cancellationToken).ConfigureAwait(false)
                    : null;
                var tall = links.Value.Tall is { } t
                    ? await VideoAsync(t, cancellationToken).ConfigureAwait(false)
                    : null;

                if (square is not null || tall is not null)
                {
                    return new AlbumMotionFound(
                        record.Id.ToString(CultureInfo.InvariantCulture),
                        storefront,
                        how,
                        square,
                        tall);
                }
            }

            return null;
        }

        var own = Digits(album.Barcode);

        if (own is not null)
        {
            var found = await FirstAnimatedAsync(
                    await LookupAsync([own], storefront, cancellationToken).ConfigureAwait(false),
                    "barcode")
                .ConfigureAwait(false);

            if (found is not null) return (found, true);
        }

        var others = album.Editions
            .Select(Digits)
            .OfType<string>()
            .Where(barcode => barcode != own)
            .Distinct(StringComparer.Ordinal)
            .Take(MaxEditions)
            .ToList();

        if (others.Count > 0)
        {
            // Another edition's barcode names that edition, so it counts only
            // under the same title: without it, Bewitched takes the video of
            // Bewitched: The Goddess Edition. The year is not asked, since the
            // barcode already says which record.
            var editions = (await LookupAsync(others, storefront, cancellationToken).ConfigureAwait(false))
                .Where(record => ReleaseTitleMatch.IsSameRecord(Title(record.Name), null, album.Title, null))
                .ToList();

            var found = await FirstAnimatedAsync(editions, "another edition's barcode").ConfigureAwait(false);

            if (found is not null) return (found, true);
        }

        if (asked.Count > 0) return (null, true);

        // No credit line is nothing to check a title against: a search for a
        // bare title matches somebody else's record as readily as this one.
        if (string.IsNullOrWhiteSpace(album.Artist)) return (null, false);

        var names = album.Credited
            .Prepend(album.Artist)
            .Select(ArtistNameMatch.Normalise)
            .Where(name => name.Length > 0)
            .ToHashSet(StringComparer.Ordinal);

        var titled = (await SearchAsync($"{album.Artist} {album.Title}", storefront, cancellationToken)
                .ConfigureAwait(false))
            .Where(record => record.Artist is { } billed
                && names.Contains(ArtistNameMatch.Normalise(billed))
                && ReleaseTitleMatch.IsSameRecord(Title(record.Name), record.Year, album.Title, album.Year))
            .ToList();

        return (await FirstAnimatedAsync(titled, "title").ConfigureAwait(false), titled.Count > 0);
    }

    /// <summary>The records these barcodes name in one shop, in one request.</summary>
    /// <remarks>
    /// The lookup takes a comma-separated list and answers in no particular
    /// order and without saying which barcode found which record — so a batch is
    /// only ever of barcodes that are treated alike.
    /// </remarks>
    private Task<List<AppleRecord>> LookupAsync(
        IReadOnlyList<string> barcodes,
        string storefront,
        CancellationToken cancellationToken) =>
        RecordsAsync(
            $"lookup?upc={string.Join(',', barcodes)}&entity=album&country={Uri.EscapeDataString(storefront)}",
            cancellationToken);

    private Task<List<AppleRecord>> SearchAsync(
        string term,
        string storefront,
        CancellationToken cancellationToken) =>
        RecordsAsync(
            $"search?term={Uri.EscapeDataString(term)}&entity=album&limit={SearchLimit}"
            + $"&country={Uri.EscapeDataString(storefront)}",
            cancellationToken);

    private async Task<List<AppleRecord>> RecordsAsync(string path, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(AppleMusicOptions.HttpClientName, path, cancellationToken)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw Unavailable($"the search API answered {(int)response.StatusCode}", cause: null);
        }

        ITunesResults? results;

        try
        {
            var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);

            await using (stream.ConfigureAwait(false))
            {
                results = await JsonSerializer
                    .DeserializeAsync(stream, AppleMusicJsonContext.Default.ITunesResults, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        catch (JsonException cause)
        {
            throw Unavailable("the search API did not answer with the JSON we expect", cause);
        }

        // A throttled answer is an empty body, which deserialises to nothing at
        // all — not to an empty list, which is how "no such record" is said.
        if (results?.Results is not { } rows)
        {
            throw Unavailable("the search API answered with no result list", cause: null);
        }

        return [.. rows
            .Where(row => row.WrapperType == "collection" && row.CollectionId is > 0 && row.CollectionName is not null)
            .Select(row => new AppleRecord(row.CollectionId!.Value, row.CollectionName!, row.ArtistName, Year(row.ReleaseDate)))];
    }

    /// <summary>The motion artwork the album's own page names for its header, or null where it has no page.</summary>
    /// <remarks>
    /// <b>Only the header</b>: the page also lists the artist's other records,
    /// and a video found anywhere in the document could be one of theirs. A page
    /// whose embedded JSON is gone, or has no sections, is the page changing
    /// shape under this reader — every album would read as having none, so it
    /// throws instead and the caller stops asking. A page with sections and no
    /// header is this one page being different, and reads as nothing here.
    /// </remarks>
    private async Task<(string? Square, string? Tall)?> PageAsync(
        string storefront,
        long album,
        CancellationToken cancellationToken)
    {
        string html;

        using (var response = await SendAsync(
                AppleMusicOptions.PageHttpClientName,
                $"{Uri.EscapeDataString(storefront)}/album/{album.ToString(CultureInfo.InvariantCulture)}",
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

        if (ServerData().Match(html) is not { Success: true } script)
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

                var header = items[0];

                return (
                    Video(header, "videoArtwork", "motionDetailSquare"),
                    Video(header, "tallVideoArtwork", "motionDetailTall"));
            }

            ProviderLog.AppleMotionNoHeader(logger, album);
            return (null, null);
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

    /// <summary>A barcode as the lookup takes it: its digits, leading zeros and all.</summary>
    /// <remarks>
    /// Not <see cref="Barcodes.Normalise"/>, which drops the zeros and the check
    /// digit to compare two catalogues' spellings — this is a key sent to one,
    /// and it answers a 13-digit EAN and its 12-digit UPC alike.
    /// </remarks>
    private static string? Digits(string? barcode) =>
        barcode is null ? null : new string([.. barcode.Where(char.IsAsciiDigit)]) is { Length: > 0 } digits ? digits : null;

    /// <summary>The record's name as other catalogues print it, without Apple's " - Single".</summary>
    private static string Title(string name)
    {
        foreach (var suffix in KindSuffixes)
        {
            if (name.EndsWith(suffix, StringComparison.Ordinal)) return name[..^suffix.Length];
        }

        return name;
    }

    /// <summary>The year out of an ISO date, or null — partial dates stay partial.</summary>
    private static int? Year(string? releaseDate) =>
        releaseDate is { Length: >= 4 } date
        && int.TryParse(date.AsSpan(0, 4), NumberStyles.None, CultureInfo.InvariantCulture, out var year)
        && year is > 1000 and < 3000
            ? year
            : null;

    private async Task<HttpResponseMessage> SendAsync(string client, string path, CancellationToken cancellationToken)
    {
        var http = clients.CreateClient(client);

        try
        {
            return await http.GetAsync(new Uri(path, UriKind.Relative), cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException cause)
        {
            throw Unavailable(cause.Message, cause);
        }
        catch (TaskCanceledException cause) when (!cancellationToken.IsCancellationRequested)
        {
            throw Unavailable("the request timed out", cause);
        }
        catch (ExecutionRejectedException cause)
        {
            throw Unavailable(cause.Message, cause);
        }
    }

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
        cause is null
            ? new ProviderUnavailableException(ProviderName, $"Apple Music request failed: {reason}.")
            : new ProviderUnavailableException(ProviderName, $"Apple Music request failed: {reason}.", cause);

    /// <summary>The page no longer has the shape this reads. Retrying will not help; a code change will.</summary>
    private static ProviderRejectedException Changed(string reason) =>
        new(ProviderName, $"Apple Music's album page has changed shape: {reason}.");

    [GeneratedRegex("""<script[^>]*\bid="serialized-server-data"[^>]*>(?<json>.*?)</script>""", RegexOptions.Singleline)]
    private static partial Regex ServerData();

    [GeneratedRegex("""(?<name>[A-Z0-9_-]+)=(?<value>"[^"]*"|[^,]*)""")]
    private static partial Regex AttributePattern();

    private sealed record AppleRecord(long Id, string Name, string? Artist, int? Year);
}
