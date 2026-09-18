using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using Fonoteca.Domain.Abstractions;
using Fonoteca.Domain.Catalogue;
using Polly;

namespace Fonoteca.Providers.CoverArt;

/// <summary>How to talk to the Cover Art Archive.</summary>
public sealed class CoverArtArchiveOptions
{
    /// <summary>Name of the configured <c>HttpClient</c>.</summary>
    public const string HttpClientName = "coverartarchive";

    public static readonly Uri Server = new("https://coverartarchive.org/");

    /// <summary>Sent in the User-Agent; the MusicBrainz contact, since it is their service.</summary>
    public string Contact { get; set; } = string.Empty;
}

/// <summary><see cref="ICoverArtArchive"/> over coverartarchive.org.</summary>
/// <remarks>
/// Images are fetched at the 500px rendition: the originals run to several
/// megabytes of scan, and nothing here draws a box bigger than that.
/// </remarks>
public sealed class CoverArtArchiveClient(IHttpClientFactory clients) : ICoverArtArchive
{
    public const string ProviderName = "Cover Art Archive";

    public async Task<IReadOnlyList<CoverArtImage>> ListAsync(
        Mbid release,
        CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync($"release/{release.Value:D}", cancellationToken)
            .ConfigureAwait(false);

        // How the archive says it holds nothing for this release.
        if (response.StatusCode == HttpStatusCode.NotFound) return [];

        if (!response.IsSuccessStatusCode)
        {
            throw Unavailable($"the listing answered {(int)response.StatusCode}", cause: null);
        }

        CoverArtListing? listing;

        try
        {
            var stream = await response.Content.ReadAsStreamAsync(cancellationToken)
                .ConfigureAwait(false);

            await using (stream.ConfigureAwait(false))
            {
                listing = await JsonSerializer
                    .DeserializeAsync(stream, CoverArtJsonContext.Default.CoverArtListing, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        catch (JsonException cause)
        {
            throw Unavailable("the listing was not the JSON we expect", cause);
        }

        return (listing?.Images ?? [])
            .Select(image => new CoverArtImage(
                image.Id,
                image.Front,
                image.Types ?? [],
                string.IsNullOrWhiteSpace(image.Comment) ? null : image.Comment))
            .ToList();
    }

    public async Task<CoverArtBytes> DownloadAsync(
        Mbid release,
        long imageId,
        CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(
                $"release/{release.Value:D}/{imageId}-500.jpg",
                cancellationToken)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw Unavailable($"the image answered {(int)response.StatusCode}", cause: null);
        }

        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken)
            .ConfigureAwait(false);

        return new CoverArtBytes(
            bytes,
            response.Content.Headers.ContentType?.MediaType ?? "image/jpeg");
    }

    private async Task<HttpResponseMessage> SendAsync(string path, CancellationToken cancellationToken)
    {
        var http = clients.CreateClient(CoverArtArchiveOptions.HttpClientName);

        try
        {
            return await http.GetAsync(path, cancellationToken).ConfigureAwait(false);
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

    private static ProviderUnavailableException Unavailable(string reason, Exception? cause) =>
        cause is null
            ? new ProviderUnavailableException(ProviderName, $"Cover Art Archive request failed: {reason}.")
            : new ProviderUnavailableException(
                ProviderName,
                $"Cover Art Archive request failed: {reason}.",
                cause);
}

internal sealed record CoverArtListing
{
    [JsonPropertyName("images")]
    public IReadOnlyList<CoverArtListingImage>? Images { get; init; }
}

internal sealed record CoverArtListingImage
{
    [JsonPropertyName("id")]
    public long Id { get; init; }

    [JsonPropertyName("front")]
    public bool Front { get; init; }

    [JsonPropertyName("types")]
    public IReadOnlyList<string>? Types { get; init; }

    [JsonPropertyName("comment")]
    public string? Comment { get; init; }
}

// Older listings carry the id as a string.
[JsonSourceGenerationOptions(NumberHandling = JsonNumberHandling.AllowReadingFromString)]
[JsonSerializable(typeof(CoverArtListing))]
internal sealed partial class CoverArtJsonContext : JsonSerializerContext;
