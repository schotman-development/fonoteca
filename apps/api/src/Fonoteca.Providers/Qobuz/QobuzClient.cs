using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Fonoteca.Domain.Abstractions;
using Fonoteca.Providers.Logging;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Polly;

namespace Fonoteca.Providers.Qobuz;

/// <summary>
/// Search Qobuz, read an album's track list, and open a track's audio.
/// </summary>
/// <remarks>
/// <b>Manual acquisition only.</b> Nothing here is on a pass or a schedule —
/// every call is a person who has chosen an album. That is a deliberate limit
/// rather than an unfinished one: this is a reverse-engineered API on a paid
/// personal subscription, and the failure mode of automating it is a suspended
/// account rather than a retry.
///
/// No <c>IQobuz…</c> interface in the domain, unlike AcoustID and MusicBrainz,
/// and the difference is who consumes it. Those two feed rules that live in
/// <c>Fonoteca.Domain</c> and therefore have to be substitutable there. This
/// feeds one endpoint, and its test seam is the socket underneath it — which is
/// the better seam anyway, since what is worth pinning is the request signature
/// and the headers, not a mock's say-so.
/// </remarks>
public sealed class QobuzClient(
    IHttpClientFactory clients,
    IOptions<QobuzOptions> options,
    ILogger<QobuzClient> logger)
{
    /// <summary>Name used in <see cref="ProviderException.Provider"/> and in log messages.</summary>
    public const string ProviderName = "Qobuz";

    /// <summary>Name of the unauthenticated client that fetches audio from the CDN.</summary>
    /// <remarks>
    /// A second client, because the two have opposite needs. API calls want the
    /// rate gate, the retries and a 30-second timeout; a 300 MB hi-res track
    /// wants none of them — a retry there re-downloads everything already
    /// received, and a 30-second cap fails every track over a few tens of
    /// megabytes. It also carries no credentials: the URL is already signed and
    /// time-limited, and sending the account token to a CDN host that Qobuz
    /// chose at request time is not a thing to do by default.
    /// </remarks>
    public const string ContentHttpClientName = "qobuz-content";

    /// <summary>Streaming, as opposed to <c>download</c> — what a subscription entitles.</summary>
    private const string Intent = "stream";

    /// <summary>How many tracks one <c>album/get</c> asks for.</summary>
    /// <remarks>
    /// Their track list paginates and defaults to far fewer than a box set has.
    /// 500 covers everything Qobuz sells as one album; a release with more would
    /// come back short, which <see cref="QobuzAlbum.TrackCount"/> makes visible
    /// rather than silent.
    /// </remarks>
    private const int TrackPageSize = 500;

    /// <summary>Albums matching a search, best first as Qobuz ranks them.</summary>
    public async Task<IReadOnlyList<QobuzAlbum>> SearchAlbumsAsync(
        string query,
        int limit = 25,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);

        var payload = await GetAsync(
            "album/search",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["query"] = query,
                ["limit"] = limit.ToString(CultureInfo.InvariantCulture),
            },
            cancellationToken).ConfigureAwait(false);

        var body = Deserialize(payload, QobuzJsonContext.Default.QobuzSearchBody, "album/search");
        var items = body?.Albums?.Items ?? [];

        var albums = new List<QobuzAlbum>(items.Count);

        foreach (var item in items)
        {
            // No id is nothing to act on. Skipping beats a row a person can
            // click that cannot resolve to anything.
            if (Album(item) is { } album) albums.Add(album);
        }

        ProviderLog.QobuzSearched(logger, query, albums.Count);
        return albums;
    }

    /// <summary>
    /// Artists matching a search, as Qobuz ranks them.
    /// </summary>
    /// <remarks>
    /// <b>Their ranking is not an answer to "is this the same artist".</b>
    /// Measured across forty of this library's artists, the top hit for
    /// "Tom Petty" is <i>Tom Petty &amp; The Heartbreakers</i> and the top hit
    /// for "Daniel de Borah" is a Barenboim compilation whose credit line runs
    /// to six names. So this returns the list and the caller decides — see
    /// <c>QobuzPortraits</c>, which accepts only an exact match on the name.
    ///
    /// Five rather than one for that reason: the exact match is frequently not
    /// first, and asking for more of a list costs the same single request.
    /// </remarks>
    public async Task<IReadOnlyList<QobuzArtist>> SearchArtistsAsync(
        string query,
        int limit = 5,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);

        var payload = await GetAsync(
            "artist/search",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["query"] = query,
                ["limit"] = limit.ToString(CultureInfo.InvariantCulture),
            },
            cancellationToken).ConfigureAwait(false);

        var body = Deserialize(payload, QobuzJsonContext.Default.QobuzArtistSearchBody, "artist/search");
        var items = body?.Artists?.Items ?? [];

        var artists = new List<QobuzArtist>(items.Count);

        foreach (var item in items)
        {
            if (string.IsNullOrWhiteSpace(item.Name)) continue;

            // Their canonical URL, which is the `large` one. It is NOT a fixed
            // size: measured across 251 of them the median is 211 KB and the
            // largest is 13.3 MB at 4480x6720, because for some artists `large`
            // is simply the original. The renditions are path segments rather
            // than query parameters — `/small/`, `/medium/`, `/large/` — so the
            // client picks the one it can use, exactly as it appends a width for
            // Commons. Storing the biggest is what keeps that choice open.
            //
            // `extralarge` is deliberately not read although the field arrives:
            // that URL 403s at their CDN.
            var picture = item.Image?.Large ?? item.Image?.Medium;

            artists.Add(new QobuzArtist(item.Name, picture, item.AlbumsCount ?? 0));
        }

        return artists;
    }

    /// <summary>One album with its track list.</summary>
    public async Task<QobuzAlbum?> GetAlbumAsync(
        string albumId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(albumId);

        var payload = await GetAsync(
            "album/get",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["album_id"] = albumId,
                ["limit"] = TrackPageSize.ToString(CultureInfo.InvariantCulture),
                ["offset"] = "0",
            },
            cancellationToken).ConfigureAwait(false);

        var body = Deserialize(payload, QobuzJsonContext.Default.QobuzAlbumBody, "album/get");
        return body is null ? null : Album(body);
    }

    /// <summary>
    /// A signed, time-limited URL for one track's audio.
    /// </summary>
    /// <remarks>
    /// The one call that needs <see cref="QobuzOptions.AppSecret"/>, and the one
    /// that can succeed at the HTTP level while failing at the useful one — see
    /// <see cref="QobuzFileUrlBody.Restrictions"/>.
    ///
    /// It <b>returns</b> the refusal rather than throwing it, so that everything
    /// this method still throws means the installation is wrong rather than the
    /// track. That is what lets a caller abandon an album on the first bad
    /// signature instead of asking 177 more times.
    /// </remarks>
    public async Task<QobuzTrackAudio> GetTrackFileUrlAsync(
        long trackId,
        CancellationToken cancellationToken = default)
    {
        var config = options.Value;

        if (string.IsNullOrWhiteSpace(config.AppSecret))
        {
            throw new ProviderRejectedException(
                ProviderName,
                "No Qobuz app secret is configured. Set "
                + "Fonoteca:Providers:Qobuz:AppSecret — it signs track URL requests, and is a "
                + "different thing from the app id and from the user auth token. Search and "
                + "album lookups work without it; downloads do not.");
        }

        var formatId = config.FormatId.ToString(CultureInfo.InvariantCulture);
        var track = trackId.ToString(CultureInfo.InvariantCulture);
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);

        var signed = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["format_id"] = formatId,
            ["intent"] = Intent,
            ["track_id"] = track,
        };

        var query = new Dictionary<string, string>(signed, StringComparer.Ordinal)
        {
            ["request_ts"] = timestamp,
            ["request_sig"] = Signature("track/getFileUrl", signed, timestamp, config.AppSecret),
        };

        var payload = await GetAsync("track/getFileUrl", query, cancellationToken)
            .ConfigureAwait(false);

        var body = Deserialize(payload, QobuzJsonContext.Default.QobuzFileUrlBody, "track/getFileUrl");

        if (body?.Url is not { Length: > 0 } url)
        {
            var codes = body?.Restrictions is { Count: > 0 } restrictions
                ? string.Join(", ", restrictions.Select(static r => r.Code ?? "unknown"))
                : "no reason given";

            // Returned, not thrown, and that is this file's most load-bearing
            // line. "The subscription does not cover this track" is the ordinary
            // outcome for a compilation, exactly as "AcoustID has never heard
            // this audio" is ordinary for a bootleg — and ProviderException's own
            // remarks refuse to make that an exception. Thrown, it is
            // indistinguishable at the catch from a wrong app secret, and a
            // caller cannot both skip one track and abandon the album.
            return QobuzTrackAudio.Unavailable(
                $"Qobuz offers no audio for this track at format {formatId} ({codes}).");
        }

        if (body.Sample == true)
        {
            return QobuzTrackAudio.Unavailable(
                "Qobuz returned a preview rather than the full track. The subscription does not "
                + $"cover this release at format {formatId}.");
        }

        return QobuzTrackAudio.Available(new QobuzFileUrl(
            new Uri(url, UriKind.Absolute),
            body.FormatId ?? config.FormatId,
            body.MimeType,
            body.BitDepth,
            body.SamplingRate));
    }

    /// <summary>Opens the audio for reading. The caller owns the response and the stream.</summary>
    /// <remarks>
    /// <c>ResponseHeadersRead</c>, so a 300 MB track is copied through rather
    /// than buffered whole — the difference between a download and an
    /// out-of-memory at the fourth track of a hi-res box set.
    /// </remarks>
    public async Task<HttpResponseMessage> OpenAudioAsync(
        Uri url,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(url);

        var http = clients.CreateClient(ContentHttpClientName);

        try
        {
            var response = await http
                .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                var status = response.StatusCode;
                response.Dispose();

                // Their CDN URLs expire, so a 403 here is far more often a slow
                // caller than a wrong one.
                throw new ProviderUnavailableException(
                    ProviderName,
                    $"Qobuz audio download returned {(int)status} {status}. Signed URLs are "
                    + "time-limited; ask for a fresh one.");
            }

            return response;
        }
        catch (HttpRequestException cause)
        {
            throw new ProviderUnavailableException(
                ProviderName, $"Qobuz audio download failed: {cause.Message}.", cause);
        }
    }

    /// <summary>
    /// The <c>request_sig</c> Qobuz check signed calls against.
    /// </summary>
    /// <remarks>
    /// Their scheme, reproduced: take the endpoint with its slashes removed,
    /// append every signed parameter as name-then-value in <b>ordinal name
    /// order</b>, then the timestamp, then the app secret, and MD5 the lot.
    ///
    /// Public and static because it is the one part of this class worth pinning
    /// with a test. Every component of it fails the same way when it is wrong —
    /// a 400 saying "invalid request signature" that names none of the four
    /// things that could have caused it — so a change to the parameter order,
    /// to the slash stripping or to what is included is invisible until a
    /// download stops working.
    /// </remarks>
    [SuppressMessage(
        "Security",
        "CA5351:Do not use broken cryptographic algorithms",
        Justification =
            "MD5 is Qobuz's choice, not ours, and this is a protocol conformance value rather " +
            "than a security control. Nothing here is protected by the hash.")]
    public static string Signature(
        string endpoint,
        IReadOnlyDictionary<string, string> parameters,
        string timestamp,
        string appSecret)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(endpoint);
        ArgumentNullException.ThrowIfNull(parameters);

        var material = new StringBuilder(endpoint.Replace("/", string.Empty, StringComparison.Ordinal));

        foreach (var name in parameters.Keys.Order(StringComparer.Ordinal))
        {
            material.Append(name).Append(parameters[name]);
        }

        material.Append(timestamp).Append(appSecret);

        return Convert.ToHexStringLower(
            MD5.HashData(Encoding.UTF8.GetBytes(material.ToString())));
    }

    private async Task<string> GetAsync(
        string path,
        Dictionary<string, string> query,
        CancellationToken cancellationToken)
    {
        var config = options.Value;

        if (string.IsNullOrWhiteSpace(config.AppId) || string.IsNullOrWhiteSpace(config.UserAuthToken))
        {
            // Refused here rather than sent. Their answer to a missing app id is
            // a 400 with an error document that names neither setting.
            throw new ProviderRejectedException(
                ProviderName,
                "Qobuz is not configured. Set Fonoteca:Providers:Qobuz:AppId and "
                + ":UserAuthToken — both come from a signed-in web player session and belong to "
                + "your own subscription.");
        }

        var http = clients.CreateClient(QobuzOptions.HttpClientName);

        var url = QueryHelpers(path, query);

        HttpResponseMessage response;

        try
        {
            response = await http.GetAsync(url, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException cause)
        {
            throw Unavailable(path, cause.Message, cause);
        }
        catch (TaskCanceledException cause) when (!cancellationToken.IsCancellationRequested)
        {
            throw Unavailable(path, "the request timed out", cause);
        }
        catch (ExecutionRejectedException cause)
        {
            throw Unavailable(path, cause.Message, cause);
        }

        using (response)
        {
            var payload = await response.Content.ReadAsStringAsync(cancellationToken)
                .ConfigureAwait(false);

            var failure = Diagnose(path, response.StatusCode, payload);
            if (failure is not null) throw failure;

            return payload;
        }
    }

    /// <summary>Relative URL with an escaped query string.</summary>
    /// <remarks>
    /// Hand-built rather than pulling in <c>Microsoft.AspNetCore.WebUtilities</c>:
    /// this project has no ASP.NET reference and a search term is the only value
    /// here that needs escaping at all.
    /// </remarks>
    private static string QueryHelpers(string path, Dictionary<string, string> query)
    {
        var parts = query.Select(static pair =>
            $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}");

        return $"{path}?{string.Join('&', parts)}";
    }

    private static ProviderException? Diagnose(string path, HttpStatusCode status, string payload)
    {
        if ((int)status is >= 200 and < 300) return null;

        var transient =
            status is HttpStatusCode.TooManyRequests or HttpStatusCode.RequestTimeout
            || (int)status >= 500;

        string? message = null;

        try
        {
            message = JsonSerializer
                .Deserialize(payload, QobuzJsonContext.Default.QobuzErrorBody)?.Message;
        }
        catch (JsonException)
        {
            // Not their error envelope. The status is all there is.
        }

        var reason = string.IsNullOrWhiteSpace(message)
            ? $"{(int)status} {status}"
            : $"{(int)status} {status}: {message}";

        return transient
            ? new ProviderUnavailableException(ProviderName, $"Qobuz {path} failed: {reason}.")
            : new ProviderRejectedException(
                ProviderName,
                $"Qobuz {path} was refused: {reason}. Check Fonoteca:Providers:Qobuz — a wrong "
                + "app SECRET, an expired user auth token and a rotated app id all look like "
                + "this, and the secret is the one that signs track URL requests.");
    }

    private static T? Deserialize<T>(
        string payload,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> shape,
        string path)
    {
        try
        {
            return JsonSerializer.Deserialize(payload, shape);
        }
        catch (JsonException cause)
        {
            throw Unavailable(path, "the response was not valid JSON", cause);
        }
    }

    /// <summary>The wire shape, narrowed to what an acquisition actually uses.</summary>
    private static QobuzAlbum? Album(QobuzAlbumBody body)
    {
        if (body.Id is not { Length: > 0 } id) return null;

        var items = body.Tracks?.Items ?? [];
        var tracks = new List<QobuzTrack>(items.Count);

        foreach (var item in items)
        {
            tracks.Add(new QobuzTrack(
                item.Id,
                Titled(item.Title, item.Version),
                item.MediaNumber ?? 1,
                item.TrackNumber,
                item.Performer?.Name,
                item.Duration is { } seconds ? TimeSpan.FromSeconds(seconds) : null,
                item.Streamable ?? true));
        }

        return new QobuzAlbum(
            id,
            Titled(body.Title, body.Version),
            body.Artist?.Name,
            body.ReleaseDate,
            // Their own count, not the list's: the two differing is how a
            // truncated track page announces itself.
            body.TracksCount ?? tracks.Count,
            body.MediaCount ?? 1,
            body.HiRes ?? false,
            body.MaximumBitDepth,
            body.MaximumSamplingRate,
            body.Streamable ?? false,
            body.Image?.Large,
            tracks);
    }

    /// <summary>
    /// The title with its edition suffix folded in.
    /// </summary>
    /// <remarks>
    /// Qobuz keep "Remastered 2015" and "Deluxe Edition" in a separate
    /// <c>version</c> field, and dropping it makes four editions of one album
    /// four identical rows on a chooser.
    /// </remarks>
    private static string Titled(string? title, string? version)
    {
        var name = string.IsNullOrWhiteSpace(title) ? "Untitled" : title.Trim();

        return string.IsNullOrWhiteSpace(version) ? name : $"{name} ({version.Trim()})";
    }

    private static ProviderUnavailableException Unavailable(
        string operation, string reason, Exception? cause) =>
        cause is null
            ? new ProviderUnavailableException(ProviderName, $"Qobuz {operation} failed: {reason}.")
            : new ProviderUnavailableException(
                ProviderName, $"Qobuz {operation} failed: {reason}.", cause);
}

/// <summary>One artist as Qobuz knows them, for the one thing we ask them.</summary>
/// <param name="Name">
/// As Qobuz spell it. The whole of the matching decision is made on this, so it
/// is carried rather than discarded.
/// </param>
/// <param name="ImageUrl">
/// Their press photograph, or null. Null is common and is not a failure: they
/// carry pictures for the artists they sell records by.
/// </param>
/// <param name="AlbumCount">
/// How many albums Qobuz carry by them. <b>Zero means the field was absent, not
/// that they have none</b> — so it is an unknown rather than a small number, and
/// <c>QobuzPortraits.Dwarfs</c> refuses to compare against one.
/// </param>
public sealed record QobuzArtist(string Name, string? ImageUrl, int AlbumCount);

/// <summary>One album as an acquisition sees it.</summary>
/// <param name="TrackCount">Qobuz's own count. Higher than <c>Tracks.Count</c> means the list was cut.</param>
/// <param name="Streamable">Whether the account may play it at all — region and tier.</param>
public sealed record QobuzAlbum(
    string Id,
    string Title,
    string? Artist,
    string? ReleaseDate,
    int TrackCount,
    int DiscCount,
    bool HiRes,
    int? MaximumBitDepth,
    double? MaximumSamplingRate,
    bool Streamable,
    string? CoverUrl,
    IReadOnlyList<QobuzTrack> Tracks);

/// <param name="Performer">The track's own credit, which on a compilation is not the album's.</param>
public sealed record QobuzTrack(
    long Id,
    string Title,
    int DiscNumber,
    int TrackNumber,
    string? Performer,
    TimeSpan? Duration,
    bool Streamable);

/// <summary>
/// Either a signed URL, or the reason Qobuz will not serve this track.
/// </summary>
/// <remarks>
/// Two states in one record rather than a nullable return, because the refusal
/// has to carry its reason to the screen — "no audio at format 27" and "this is
/// a preview" send a person to different settings.
/// </remarks>
public sealed record QobuzTrackAudio(QobuzFileUrl? Url, string? Refusal)
{
    public static QobuzTrackAudio Available(QobuzFileUrl url) => new(url, null);

    public static QobuzTrackAudio Unavailable(string why) => new(null, why);
}

/// <summary>A signed CDN URL and what Qobuz says is behind it.</summary>
/// <remarks>
/// The format that came back, not the one asked for. Qobuz serve the best
/// encoding a release has up to the request, so a 24/192 ask against a CD master
/// returns 16/44.1 and says so — and what gets recorded has to be the answer.
/// </remarks>
public sealed record QobuzFileUrl(
    Uri Url,
    int FormatId,
    string? MimeType,
    int? BitDepth,
    double? SamplingRate);
