using System.Globalization;
using System.Text.Json;
using Fonoteca.Domain.Abstractions;
using Fonoteca.Domain.Catalogue;
using Microsoft.Extensions.Options;
using Polly;

namespace Fonoteca.Providers.Wikidata;

/// <summary>
/// <see cref="IEncyclopedia"/> over Wikidata and the English Wikipedia.
/// </summary>
/// <remarks>
/// <b>Two hops, both batched.</b> Wikidata holds the MusicBrainz id
/// (<c>P434</c> for an artist, <c>P436</c> for a release group) and the
/// <c>enwiki</c> sitelink on the same item, so one SPARQL query turns a few
/// hundred ids into article titles, exactly as <see cref="WikidataPortraits"/>
/// turns them into pictures. Wikipedia's own API then returns each article as
/// plain text.
///
/// <b>The whole article, not the lead.</b> A lead is a one-line definition on
/// exactly the artists a catalogue like this is full of — measured, Janine
/// Jansen's is "Janine Jansen (born 7 January 1978) is a Dutch violinist and
/// violist." and nothing else, against 6,700 characters of biography below it.
/// A page offering "Biography" and a "Read more" has to have something to read.
///
/// <b>That costs one request per article, and the API enforces it</b>:
/// <c>exlimit</c> above 1 on a whole-article request comes back lowered, with
/// every page but the first carrying no extract at all — a warning in the body
/// and an otherwise successful response, so it reads as "Wikipedia has no
/// article about them". Hence one title per request here, and the small batch
/// the caller claims.
///
/// Wikipedia's appendices are dropped (<see cref="Appendices"/>) and the prose
/// is clamped to <see cref="MaxTextLength"/>.
/// </remarks>
public sealed class WikipediaArticles(IHttpClientFactory clients, IOptions<WikidataOptions> options)
    : IEncyclopedia
{
    public const string ProviderName = "Wikipedia";

    public const string HttpClientName = "wikipedia";

    public static readonly Uri Server = new("https://en.wikipedia.org/");

    /// <summary>The longest URL <c>Artists.BiographyUrl</c> can hold; longer is dropped.</summary>
    private const int MaxUrlLength = 1000;

    /// <summary>
    /// How much prose is kept, in characters, cut at a paragraph.
    /// </summary>
    /// <remarks>
    /// Whole articles run to 78,000 characters here (David Bowie's), which is
    /// not a biography any more — it is a book, on every artist page and in
    /// every JSON response that carries one. The first paragraph is always
    /// kept whatever its length, or an artist with one long lead paragraph
    /// would come back with no biography at all.
    /// </remarks>
    private const int MaxTextLength = 8_000;

    /// <summary>
    /// Section headings that end the article, whatever follows them.
    /// </summary>
    /// <remarks>
    /// Wikipedia's appendices are lists and link dumps, and
    /// <c>prop=extracts</c> strips most of their content but leaves the
    /// headings — so without this a biography ends "References Citations
    /// Bibliography External links".
    ///
    /// <b>Only the sections the Manual of Style puts last.</b> That is what
    /// makes stopping dead at one safe: nothing an article says after "See
    /// also" is prose anybody wants. A mid-article section does not belong
    /// here however list-like it is — see <see cref="ArtistListings"/>.
    /// </remarks>
    private static readonly HashSet<string> Appendices = new(StringComparer.OrdinalIgnoreCase)
    {
        "see also", "notes", "footnotes", "explanatory notes", "references",
        "notes and references", "references and notes", "citations", "sources",
        "works cited", "bibliography", "selected bibliography", "further reading",
        "external links",
    };

    /// <summary>
    /// Sections skipped on an artist: list-like, but mid-article.
    /// </summary>
    /// <remarks>
    /// <b>Skipped, never stopped at, and the difference was measured.</b>
    /// Wikipedia's Manual of Style puts a discography in the body, not the
    /// appendices, so a biography routinely runs "… Discography … Personal
    /// life … Awards". Breaking at the first one cost Sol Gabetta her personal
    /// life and Alison Balsom her awards — 2 of 9 mid-size musicians sampled,
    /// which is silently shortening a biography in the same way
    /// <c>exintro</c> did, on the same artists, in the same feature.
    ///
    /// A plain skip of the heading is not enough either: these sections come
    /// through <c>prop=extracts</c> as real lines — album titles, "Studio
    /// albums" — which would land in the prose. So the section's contents are
    /// skipped up to the next heading at its own level or shallower.
    /// </remarks>
    private static readonly HashSet<string> ArtistListings = new(StringComparer.OrdinalIgnoreCase)
    {
        "discography", "selected discography", "filmography", "selected filmography",
        "videography", "tours", "concert tours", "awards and nominations",
    };

    /// <summary>
    /// Sections skipped on an album, for <see cref="ArtistListings"/>' reason.
    /// </summary>
    /// <remarks>
    /// <b>An album article is not a biography and does not share its words.</b>
    /// Measured on <i>Blue Train</i>: with the artist set alone the stored
    /// review ended "Rudy Van Gelder – recording engineer, mastering / Reid
    /// Miles – design / Francis Wolff – photography" — a personnel list read as
    /// prose, which is exactly the failure <see cref="Appendices"/> exists to
    /// prevent, arriving on the other half. The bigger albums escaped it only
    /// because <see cref="MaxTextLength"/> happened to bind first.
    /// </remarks>
    /// <remarks>
    /// <b>The compound spellings are here because the plain ones were not
    /// enough.</b> Measured after the first run: 2 of 343 stored reviews ended
    /// in a credits list anyway — <i>Breezin'</i> on "== Production ==" and
    /// <i>Me and Mr. Johnson</i> on the same, both sitting after a "Personnel"
    /// section that was correctly skipped. A heading this set does not know is
    /// a section read as prose, and on an album article that is a list of
    /// names.
    /// </remarks>
    private static readonly HashSet<string> AlbumListings = new(StringComparer.OrdinalIgnoreCase)
    {
        "track listing", "track list", "tracklist", "track listings",
        "formats and track listings", "personnel", "credits",
        "credits and personnel", "personnel and production", "production",
        "charts", "weekly charts", "year-end charts", "decade-end charts",
        "charts and certifications", "certifications", "certifications and sales",
        "release history",
    };

    public Task<IReadOnlyDictionary<Mbid, Article>> ArtistsAsync(
        IReadOnlyCollection<Mbid> artists,
        CancellationToken cancellationToken = default) =>
        FindAsync("P434", ArtistListings, artists, cancellationToken);

    public Task<IReadOnlyDictionary<Mbid, Article>> ReleaseGroupsAsync(
        IReadOnlyCollection<Mbid> groups,
        CancellationToken cancellationToken = default) =>
        FindAsync("P436", AlbumListings, groups, cancellationToken);

    private async Task<IReadOnlyDictionary<Mbid, Article>> FindAsync(
        string property,
        HashSet<string> listings,
        IReadOnlyCollection<Mbid> ids,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ids);

        if (string.IsNullOrWhiteSpace(options.Value.Contact))
        {
            throw new ProviderRejectedException(
                ProviderName,
                "Wikipedia requires a contact in the User-Agent. Set Fonoteca:MusicBrainzContact "
                + "to a URL or email address.");
        }

        var found = new Dictionary<Mbid, Article>();
        var wanted = ids.Distinct().ToList();
        if (wanted.Count == 0) return found;

        var payload = await WikidataPortraits
            .SendAsync(clients, Query(property, wanted), cancellationToken)
            .ConfigureAwait(false);

        var titles = Sitelinks(payload, wanted);

        foreach (var (title, owners) in titles)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (await ExtractAsync(title, listings, cancellationToken).ConfigureAwait(false)
                is not { } text)
            {
                continue;
            }

            foreach (var (mbid, url) in owners)
            {
                found[mbid] = new Article(text, url);
            }
        }

        return found;
    }

    /// <summary>The ids as a <c>VALUES</c> list, formatted from <see cref="Guid"/> so nothing needs escaping.</summary>
    private static string Query(string property, IEnumerable<Mbid> ids)
    {
        var values = string.Join(
            ' ',
            ids.Select(id => $"\"{id.Value.ToString("D", CultureInfo.InvariantCulture)}\""));

        return "SELECT ?mbid ?article WHERE { VALUES ?mbid { " + values + " } "
            + "?item wdt:" + property + " ?mbid . "
            + "?article schema:about ?item ; schema:isPartOf <https://en.wikipedia.org/> . }";
    }

    /// <summary>
    /// Article title to the ids it answers for and the URL to credit.
    /// </summary>
    /// <remarks>
    /// Keyed by title because two ids can share an article — an album's two
    /// MusicBrainz groups, a duo and its one member — and the extracts are
    /// asked for by title. Only <c>https://en.wikipedia.org/wiki/</c> URLs are
    /// kept: the URL is printed as a link on this application's origin.
    /// </remarks>
    private static Dictionary<string, List<(Mbid Id, Uri Url)>> Sitelinks(
        string payload,
        IEnumerable<Mbid> wanted)
    {
        var asked = new HashSet<Mbid>(wanted);
        var titles = new Dictionary<string, List<(Mbid, Uri)>>(StringComparer.Ordinal);

        using var document = Parse(payload);

        if (!document.RootElement.TryGetProperty("results", out var results)
            || !results.TryGetProperty("bindings", out var bindings)
            || bindings.ValueKind != JsonValueKind.Array)
        {
            throw Unavailable("Wikidata's response carried no results");
        }

        var taken = new HashSet<Mbid>();

        // Sorted so an id with two sitelinks settles on the same one every time.
        var rows = bindings.EnumerateArray()
            .Select(binding => (
                Mbid: WikidataPortraits.Value(binding, "mbid", out var mbid) ? mbid : null,
                Article: WikidataPortraits.Value(binding, "article", out var article) ? article : null))
            .Where(row => row.Mbid is not null && row.Article is not null)
            .OrderBy(row => row.Article, StringComparer.Ordinal);

        foreach (var (mbid, article) in rows)
        {
            if (!Guid.TryParse(mbid, out var id) || !asked.Contains(new Mbid(id))) continue;
            if (!taken.Add(new Mbid(id))) continue;

            if (!Uri.TryCreate(article, UriKind.Absolute, out var url)
                || url.Scheme != Uri.UriSchemeHttps
                || !url.Host.Equals(Server.Host, StringComparison.OrdinalIgnoreCase)
                || !url.AbsolutePath.StartsWith("/wiki/", StringComparison.Ordinal)
                || url.AbsoluteUri.Length > MaxUrlLength)
            {
                continue;
            }

            var title = Uri.UnescapeDataString(url.AbsolutePath["/wiki/".Length..]).Replace('_', ' ');

            if (!titles.TryGetValue(title, out var owners)) titles[title] = owners = [];
            owners.Add((new Mbid(id), url));
        }

        return titles;
    }

    /// <summary>
    /// The biography in one article, or null where Wikipedia holds no text.
    /// </summary>
    /// <remarks>
    /// One title, so nothing has to be matched back to what was asked for:
    /// Wikipedia follows normalisation and redirects server-side and answers
    /// with a single page under whatever it is called now, which is the page
    /// wanted however it was reached.
    /// </remarks>
    private async Task<string?> ExtractAsync(
        string title,
        HashSet<string> listings,
        CancellationToken cancellationToken)
    {
        var payload = await GetAsync(
                "w/api.php?action=query&format=json&formatversion=2&prop=extracts&explaintext=1"
                + "&exsectionformat=wiki&redirects=1&titles="
                + Uri.EscapeDataString(title),
                cancellationToken)
            .ConfigureAwait(false);

        using var document = Parse(payload);

        if (!document.RootElement.TryGetProperty("query", out var answer)
            || !answer.TryGetProperty("pages", out var pages)
            || pages.ValueKind != JsonValueKind.Array)
        {
            throw Unavailable("Wikipedia's response carried no query");
        }

        foreach (var page in pages.EnumerateArray())
        {
            if (page.TryGetProperty("extract", out var extract)
                && extract.GetString() is { Length: > 0 } text)
            {
                return Body(text, listings);
            }
        }

        return null;
    }

    /// <summary>
    /// An article's prose, appendices dropped and clamped, as paragraphs.
    /// </summary>
    /// <remarks>
    /// Plain text separates paragraphs with one newline; the page splits on a
    /// blank line, so they are doubled here. Headings are dropped rather than
    /// kept: what is stored is a run of paragraphs with no way to say one of
    /// them is a heading, and a clamped article ending on a bare section title
    /// reads as a truncation rather than as a biography.
    /// </remarks>
    private static string? Body(string extract, HashSet<string> listings)
    {
        var paragraphs = new List<string>();
        var length = 0;

        // The depth of the listing section being skipped, or zero. Depth rather
        // than a flag, because a skipped section's own subsections must go with
        // it: "=== Studio albums ===" under "== Discography ==" is still the
        // discography, and only a heading at the same depth or shallower ends it.
        var skipping = 0;

        foreach (var line in extract.Split('\n'))
        {
            var text = line.Trim();
            if (text.Length == 0) continue;

            var (words, depth) = Heading(text);

            if (words is not null)
            {
                if (Appendices.Contains(words)) break;

                // Closing before opening, so one listing section can follow
                // another at the same depth without the second being missed.
                if (skipping != 0 && depth <= skipping) skipping = 0;
                if (listings.Contains(words)) skipping = depth;

                continue;
            }

            if (skipping != 0) continue;

            if (paragraphs.Count > 0 && length + text.Length > MaxTextLength) break;

            paragraphs.Add(text);
            length += text.Length;
        }

        return paragraphs.Count == 0 ? null : string.Join("\n\n", paragraphs);
    }

    /// <summary>
    /// The words and depth of a <c>== heading ==</c> line; null words for body text.
    /// </summary>
    /// <remarks>
    /// <c>exsectionformat=wiki</c> is asked for precisely so this is decidable:
    /// under <c>plain</c> a heading is a short line like any other, and telling
    /// them apart would mean guessing from length or punctuation.
    /// </remarks>
    private static (string? Words, int Depth) Heading(string line)
    {
        if (line is not ['=', '=', .., '=', '=']) return (null, 0);

        var depth = 0;
        while (depth < line.Length && line[depth] == '=') depth++;

        return (line.Trim('=').Trim(), depth);
    }

    private async Task<string> GetAsync(string path, CancellationToken cancellationToken)
    {
        var http = clients.CreateClient(HttpClientName);

        HttpResponseMessage response;

        try
        {
            response = await http.GetAsync(new Uri(path, UriKind.Relative), cancellationToken)
                .ConfigureAwait(false);
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

            return response.IsSuccessStatusCode
                ? payload
                : throw Unavailable($"Wikipedia answered {(int)response.StatusCode}");
        }
    }

    private static JsonDocument Parse(string payload)
    {
        try
        {
            return JsonDocument.Parse(payload);
        }
        catch (JsonException cause)
        {
            throw Unavailable("the response was not JSON", cause);
        }
    }

    private static ProviderUnavailableException Unavailable(string reason, Exception? cause = null) =>
        cause is null
            ? new ProviderUnavailableException(ProviderName, $"Wikipedia lookup failed: {reason}.")
            : new ProviderUnavailableException(ProviderName, $"Wikipedia lookup failed: {reason}.", cause);
}
