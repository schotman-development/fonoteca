using System.Text.Json;
using System.Text.Json.Serialization;
using Fonoteca.Domain.Abstractions;
using Fonoteca.Domain.Catalogue;
using Polly;

namespace Fonoteca.Providers.RadioBrowser;

/// <summary>
/// <see cref="IStationLogos"/> over radio-browser.info's station lists.
/// </summary>
/// <remarks>
/// <b>Up to two asks, both held to the stream.</b> First <c>byurl</c>, an exact
/// match on the address a station was added with. Then the name, which
/// radio-browser matches as a part of the station's, case aside, keeping a
/// hit whose stream or resolved stream is this one — what Fonoteca's own
/// client does, and how a station saved under the address a playlist
/// redirected to is found. The first hit with a logo that is a web address
/// answers.
/// </remarks>
public sealed class RadioBrowserLogos(IHttpClientFactory clients) : IStationLogos
{
    /// <summary>Name used in <see cref="ProviderException.Provider"/> and in log messages.</summary>
    public const string ProviderName = "radio-browser";

    // ponytail: the first hundred stations holding the name; a name a hundred
    // others contain finds its station by the stream's own ask or not at all.
    private const int NameHits = 100;

    public async Task<string?> FindAsync(string name, string streamUrl, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(streamUrl);

        List<string> asks = [$"json/stations/byurl?url={Uri.EscapeDataString(streamUrl)}"];

        if (!string.IsNullOrWhiteSpace(name))
        {
            asks.Add($"json/stations/byname/{Uri.EscapeDataString(name.Trim())}?limit={NameHits}");
        }

        foreach (var ask in asks)
        {
            var logo = (await GetAsync(ask, cancellationToken).ConfigureAwait(false))
                .Where(station => station.Url == streamUrl || station.UrlResolved == streamUrl)
                .Select(station => station.Favicon?.Trim())
                .FirstOrDefault(RadioStationFile.WebAddress);

            if (logo is not null) return logo;
        }

        return null;
    }

    private async Task<List<RadioBrowserStation>> GetAsync(string query, CancellationToken cancellationToken)
    {
        var http = clients.CreateClient(RadioBrowserOptions.HttpClientName);

        HttpResponseMessage response;

        try
        {
            response = await http.GetAsync(new Uri(query, UriKind.Relative), cancellationToken).ConfigureAwait(false);
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
            if (!response.IsSuccessStatusCode)
            {
                throw Unavailable($"the lookup answered {(int)response.StatusCode}", cause: null);
            }

            var payload = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            try
            {
                return JsonSerializer.Deserialize(payload, RadioBrowserJsonContext.Default.ListRadioBrowserStation)
                    ?? throw Unavailable("the lookup returned no list", cause: null);
            }
            catch (JsonException cause)
            {
                throw Unavailable("the response was not the JSON we expect", cause);
            }
        }
    }

    private static ProviderUnavailableException Unavailable(string reason, Exception? cause) =>
        cause is null
            ? new ProviderUnavailableException(ProviderName, $"radio-browser lookup failed: {reason}.")
            : new ProviderUnavailableException(ProviderName, $"radio-browser lookup failed: {reason}.", cause);
}

internal sealed record RadioBrowserStation
{
    [JsonPropertyName("url")]
    public string? Url { get; init; }

    [JsonPropertyName("url_resolved")]
    public string? UrlResolved { get; init; }

    [JsonPropertyName("favicon")]
    public string? Favicon { get; init; }
}

[JsonSerializable(typeof(List<RadioBrowserStation>))]
internal sealed partial class RadioBrowserJsonContext : JsonSerializerContext;
