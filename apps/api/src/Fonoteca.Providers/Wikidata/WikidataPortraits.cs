using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Fonoteca.Domain.Abstractions;
using Fonoteca.Domain.Catalogue;
using Fonoteca.Providers.Logging;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Polly;

namespace Fonoteca.Providers.Wikidata;

/// <summary>
/// <see cref="IArtistPortraits"/> over the Wikidata Query Service.
/// </summary>
/// <remarks>
/// One SPARQL query, and the reason it is SPARQL rather than the ordinary
/// entity API is that the question runs backwards. The entity API answers "what
/// does <c>Q255</c> look like" and this application does not hold a
/// <c>Q</c>-number for anybody; it holds MusicBrainz ids. Going the other way
/// through MusicBrainz's own <c>wikidata</c> URL relation would work, cost a
/// gated MusicBrainz turn per artist, and <b>find fewer artists</b> — measured
/// on a sample of 18, MusicBrainz linked 15 to Wikidata while asking Wikidata
/// directly answered for 15 including Linkin Park, whose MusicBrainz page
/// carries no Wikidata link at all. The link is stored on the Wikidata side,
/// under <c>P434</c>, and that side is the one that can be asked about a
/// hundred artists at once.
///
/// Measured against the author's library: 234 of 307 artists have a picture,
/// two requests, thirteen seconds.
/// </remarks>
public sealed class WikidataPortraits(
    IHttpClientFactory clients,
    IOptions<WikidataOptions> options,
    ILogger<WikidataPortraits> logger) : IArtistPortraits
{
    /// <summary>Name used in <see cref="ProviderException.Provider"/> and in log messages.</summary>
    public const string ProviderName = "Wikidata";

    /// <summary>Where a picture may be served from.</summary>
    /// <remarks>
    /// <b>An allowlist, because this is a URL from a third party that goes
    /// straight into an <c>img src</c> on this application's origin.</b> P18 is
    /// documented as a Commons file and in practice always resolves to a
    /// <c>Special:FilePath</c> URL on one of these two hosts, so the check costs
    /// nothing and closes the case where it does not. The same instinct as
    /// <c>FilePreview.IsSafeImageMediaType</c>, which was written after an
    /// embedded <c>image/svg+xml</c> cover turned out to be a live XSS.
    /// </remarks>
    private static readonly HashSet<string> ImageHosts =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "commons.wikimedia.org",
            "upload.wikimedia.org",
        };

    /// <summary>The only path on those hosts that serves a file.</summary>
    /// <remarks>
    /// The host on its own lets through <c>commons.wikimedia.org/wiki/Main_Page</c>,
    /// which is a wiki page rather than a picture. Not a vector — an
    /// <c>img</c> cannot execute HTML, and even a genuine SVG is inert in one —
    /// but the check that already exists may as well be the check that is true,
    /// and a broken tile is a bug report about the catalogue.
    /// <c>upload.wikimedia.org</c> serves files from its own root, so it is
    /// matched on neither path nor prefix.
    /// </remarks>
    private const string FilePath = "/wiki/Special:FilePath/";

    /// <summary>The longest URL <c>Artists.PortraitUrl</c> can hold.</summary>
    /// <remarks>
    /// The column's own limit, restated here because this is where an overrun
    /// can still be turned into "no picture". Written through, it throws out of
    /// <c>SaveChangesAsync</c>, takes the stamp with it, and lands the artist on
    /// the worklist forever — <c>Artists.Genres</c> paid for this lesson first.
    /// </remarks>
    private const int MaxUrlLength = 1000;

    /// <summary>
    /// The query, with the ids substituted in.
    /// </summary>
    /// <remarks>
    /// <c>wdt:</c> rather than <c>p:</c>/<c>ps:</c> throughout, so Wikidata's own
    /// statement ranking does the first cut: a deprecated image, or a normal one
    /// beside a preferred one, never reaches us.
    /// </remarks>
    private const string QueryPrefix = "SELECT ?mbid ?image WHERE { VALUES ?mbid { ";

    private const string QuerySuffix = " } ?artist wdt:P434 ?mbid ; wdt:P18 ?image . }";

    public async Task<IReadOnlyDictionary<Mbid, Uri>> FindAsync(
        IReadOnlyCollection<ArtistToPicture> artists,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(artists);

        var config = options.Value;

        if (string.IsNullOrWhiteSpace(config.Contact))
        {
            throw new ProviderRejectedException(
                ProviderName,
                "Wikidata requires a contact in the User-Agent. Set Fonoteca:MusicBrainzContact "
                + "to a URL or email address; it identifies this application to both services.");
        }

        // The name is not read here: Wikidata holds the MusicBrainz id itself,
        // under P434, so this source looks up where the other one searches.
        var wanted = artists.Select(artist => artist.Id).Distinct().ToList();
        if (wanted.Count == 0) return new Dictionary<Mbid, Uri>();

        var payload = await SendAsync(Query(wanted), cancellationToken).ConfigureAwait(false);

        var found = Parse(payload, wanted);

        ProviderLog.WikidataPortraitsFound(logger, found.Count, wanted.Count);

        return found;
    }

    /// <summary>The MBIDs as a SPARQL <c>VALUES</c> list.</summary>
    /// <remarks>
    /// The ids are formatted from <see cref="Guid"/>, not concatenated from
    /// anything a user typed, so there is nothing here to escape — and the
    /// round-trip through <c>Guid</c> is what makes that true rather than
    /// merely likely.
    /// </remarks>
    private static string Query(IEnumerable<Mbid> artists)
    {
        var values = string.Join(
            ' ',
            artists.Select(id => $"\"{id.Value.ToString("D", CultureInfo.InvariantCulture)}\""));

        return QueryPrefix + values + QuerySuffix;
    }

    private async Task<string> SendAsync(string query, CancellationToken cancellationToken)
    {
        // POST, and not for tidiness: 300 ids is a 12 KB query string, and the
        // endpoint answers an over-long GET with an empty 200 body — which
        // parses cleanly as "none of these artists has a picture".
        using var content = new StringContent(query, Encoding.UTF8, "application/sparql-query");
        using var request = new HttpRequestMessage(HttpMethod.Post, "sparql") { Content = content };

        request.Headers.Accept.ParseAdd("application/sparql-results+json");

        var http = clients.CreateClient(WikidataOptions.HttpClientName);

        HttpResponseMessage response;

        try
        {
            response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
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

        using (response)
        {
            var payload = await response.Content.ReadAsStringAsync(cancellationToken)
                .ConfigureAwait(false);

            if (response.IsSuccessStatusCode) return payload;

            // A malformed query is ours to fix and would fail identically on
            // every retry; everything else is worth coming back for. 429 is
            // theirs to fix and is explicitly the transient one.
            throw response.StatusCode is HttpStatusCode.BadRequest
                ? new ProviderRejectedException(
                    ProviderName,
                    $"Wikidata refused the query: {Excerpt(payload)}")
                : Unavailable(
                    $"the query service answered {(int)response.StatusCode}: {Excerpt(payload)}",
                    cause: null);
        }
    }

    /// <summary>
    /// The bindings, collapsed to one picture per artist.
    /// </summary>
    /// <remarks>
    /// <b>An artist can have several, and which one shows must not change
    /// between two reads of the same page.</b> Wikidata holds two P18 claims for
    /// Beethoven at equal rank, and SPARQL result order is not defined — so the
    /// pick is the lowest URL by ordinal comparison, which is arbitrary, stated,
    /// and above all stable. <c>Cover</c> resolves its own ties the same way and
    /// for the same reason: a tile that changed its face on every reload reads
    /// as a bug in the catalogue.
    ///
    /// A row naming an artist that was not asked about cannot happen and is
    /// dropped anyway — the result is keyed by <see cref="Mbid"/> and a caller
    /// stamping "asked" against an id it never sent would be recording a lookup
    /// that did not happen.
    /// </remarks>
    private static Dictionary<Mbid, Uri> Parse(string payload, IEnumerable<Mbid> wanted)
    {
        var asked = new HashSet<Mbid>(wanted);
        var found = new Dictionary<Mbid, Uri>();

        JsonDocument document;

        try
        {
            document = JsonDocument.Parse(payload);
        }
        catch (JsonException cause)
        {
            throw Unavailable("the response was not JSON", cause);
        }

        using (document)
        {
            if (!document.RootElement.TryGetProperty("results", out var results)
                || !results.TryGetProperty("bindings", out var bindings)
                || bindings.ValueKind != JsonValueKind.Array)
            {
                throw Unavailable("the response carried no results", cause: null);
            }

            foreach (var binding in bindings.EnumerateArray())
            {
                if (!Value(binding, "mbid", out var mbid) || !Value(binding, "image", out var image))
                {
                    continue;
                }

                if (!Guid.TryParse(mbid, out var id) || !asked.Contains(new Mbid(id))) continue;
                if (!Image(image, out var picture)) continue;

                var artist = new Mbid(id);

                if (!found.TryGetValue(artist, out var chosen)
                    || string.CompareOrdinal(picture.AbsoluteUri, chosen.AbsoluteUri) < 0)
                {
                    found[artist] = picture;
                }
            }
        }

        return found;
    }

    private static bool Value(JsonElement binding, string name, out string value)
    {
        value = string.Empty;

        if (!binding.TryGetProperty(name, out var slot)
            || !slot.TryGetProperty("value", out var text)
            || text.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = text.GetString() ?? string.Empty;
        return value.Length > 0;
    }

    /// <summary>A URL this application is willing to put in front of a browser.</summary>
    /// <remarks>
    /// <b>Forced to https, and that is not cosmetic.</b> Wikidata returns these
    /// as <c>http://commons.wikimedia.org/…</c>; served into a page delivered
    /// over https, every one is blocked as mixed content and every artist tile
    /// silently loses its picture. The scheme is not part of the answer — the
    /// same file is at both — so correcting it is not the cache rewriting a
    /// provider's verdict.
    /// </remarks>
    private static bool Image(string value, out Uri image)
    {
        image = null!;

        if (!Uri.TryCreate(value, UriKind.Absolute, out var parsed)) return false;
        if (!ImageHosts.Contains(parsed.Host)) return false;

        if (parsed.Host.StartsWith("commons.", StringComparison.OrdinalIgnoreCase)
            && !parsed.AbsolutePath.StartsWith(FilePath, StringComparison.Ordinal))
        {
            return false;
        }

        if (parsed.Scheme == Uri.UriSchemeHttp)
        {
            parsed = new UriBuilder(parsed) { Scheme = Uri.UriSchemeHttps, Port = -1 }.Uri;
        }
        else if (parsed.Scheme != Uri.UriSchemeHttps)
        {
            return false;
        }

        if (parsed.AbsoluteUri.Length > MaxUrlLength) return false;

        image = parsed;
        return true;
    }

    private static string Excerpt(string payload) =>
        payload.Length <= 200 ? payload : payload[..200];

    private static ProviderUnavailableException Unavailable(string reason, Exception? cause) =>
        cause is null
            ? new ProviderUnavailableException(ProviderName, $"Wikidata lookup failed: {reason}.")
            : new ProviderUnavailableException(
                ProviderName,
                $"Wikidata lookup failed: {reason}.",
                cause);
}
