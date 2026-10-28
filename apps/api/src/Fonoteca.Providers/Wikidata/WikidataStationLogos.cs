using System.Text.Json;
using Fonoteca.Domain.Abstractions;
using Fonoteca.Domain.Catalogue;
using Microsoft.Extensions.Options;

namespace Fonoteca.Providers.Wikidata;

/// <summary>
/// A radio station's logo (<c>P154</c>) from the Wikidata item whose
/// streaming media URL (<c>P963</c>) is the station's stream or whose official
/// website (<c>P856</c>) is its home page: one of the sources
/// <see cref="Radio.StationLogos"/> asks.
/// </summary>
/// <remarks>
/// <b>Keyed on the stream and the home page, never on the name</b>: "Radio 5"
/// names a dozen stations, and either address names one. Wikidata holds no
/// radio-browser id to key on. The stream is matched exactly; the page in any
/// of the ways Wikidata writes a site — either scheme, with or without
/// <c>www.</c>, with or without a closing slash — but never by climbing to its
/// site's root, which would hand a sister channel's page the network's logo.
///
/// <see cref="WikidataPortraits"/>' client, query and Commons check, and its
/// lowest-URL rule where several logos answer. Asked for at <see cref="Width"/>
/// pixels, one of the widths Wikimedia renders, which is also what turns an
/// SVG logo into a PNG.
/// </remarks>
public sealed class WikidataStationLogos(IHttpClientFactory clients, IOptions<WikidataOptions> options)
{
    private const int Width = 500;

    /// <returns>The logo, or null where no item with this stream or site has one.</returns>
    /// <exception cref="ProviderException">Wikidata could not be asked.</exception>
    public async Task<string?> FindAsync(string streamUrl, string? homepage, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(options.Value.Contact))
        {
            throw new ProviderRejectedException(
                WikidataPortraits.ProviderName,
                "Wikidata requires a contact in the User-Agent. Set Fonoteca:MusicBrainzContact to a URL or email address.");
        }

        var streams = Iris([streamUrl]);
        var sites = homepage is null ? [] : Sites(homepage);

        if (streams.Count == 0 && sites.Count == 0) return null;

        var query = "SELECT ?logo WHERE { "
            + $"{{ VALUES ?stream {{ {string.Join(' ', streams.Select(stream => $"<{stream}>"))} }} ?item wdt:P963 ?stream . }} UNION "
            + $"{{ VALUES ?site {{ {string.Join(' ', sites.Select(site => $"<{site}>"))} }} ?item wdt:P856 ?site . }} "
            + "?item wdt:P154 ?logo . }";
        var payload = await WikidataPortraits.SendAsync(clients, query, cancellationToken).ConfigureAwait(false);

        try
        {
            using var document = JsonDocument.Parse(payload);

            var logo = document.RootElement.GetProperty("results").GetProperty("bindings").EnumerateArray()
                .Select(binding => WikidataPortraits.Value(binding, "logo", out var value)
                    && WikidataPortraits.Image(value, out var image) ? image.AbsoluteUri : null)
                .OfType<string>()
                .Order(StringComparer.Ordinal)
                .FirstOrDefault();

            return logo is null ? null : PortraitRendition.Sized(logo, Width);
        }
        catch (Exception cause) when (cause is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            throw new ProviderUnavailableException(WikidataPortraits.ProviderName, "Wikidata lookup failed: the response carried no results.", cause);
        }
    }

    /// <summary>The ways Wikidata may write this page as a site, each one SPARQL can hold as an IRI.</summary>
    public static IReadOnlyList<string> Sites(string homepage)
    {
        if (!RadioStationFile.WebAddress(homepage)) return [];

        var page = new Uri(homepage);
        var host = page.Host;
        var bare = host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? host[4..] : host;
        var path = page.AbsolutePath.TrimEnd('/');
        var port = page.IsDefaultPort ? string.Empty : $":{page.Port}";

        return Iris(
            from scheme in (string[])["https", "http"]
            from name in (string[])[bare, $"www.{bare}"]
            from end in (string[])[path, $"{path}/"]
            select $"{scheme}://{name}{port}{end}");
    }

    /// <summary>The web addresses among these that SPARQL can hold between angle brackets as written.</summary>
    private static List<string> Iris(IEnumerable<string> addresses) =>
        [.. addresses.Where(address => RadioStationFile.WebAddress(address)
            && address.All(character => character > ' ' && !"<>\"{}|^`\\".Contains(character, StringComparison.Ordinal)))];
}
