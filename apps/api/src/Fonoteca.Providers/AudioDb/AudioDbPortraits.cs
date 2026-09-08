using System.Text.Json;
using System.Text.Json.Serialization;
using Fonoteca.Domain.Abstractions;
using Fonoteca.Domain.Catalogue;
using Fonoteca.Providers.Logging;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Polly;

namespace Fonoteca.Providers.AudioDb;

/// <summary>
/// <see cref="IArtistPortraits"/> over TheAudioDB, looked up by MusicBrainz id.
/// </summary>
/// <remarks>
/// <b>The one per-artist source that does not have to decide.</b>
/// <c>artist-mb.php?i={mbid}</c> takes the MusicBrainz artist id directly, so
/// there is no name to fold, no namesake to rank and no tie to refuse — the
/// entire class of failure <see cref="ArtistNameMatch"/> exists to manage
/// simply does not arise. Wikidata is keyed the same way and this is the same
/// bargain: certainty about *who*, at the cost of a smaller collection.
///
/// <b>That is why it is worth having despite covering less.</b> Measured
/// against the forty album artists Qobuz could not place, it answers for
/// sixteen where Deezer answers for thirty — but the ones only it reaches are
/// the ones the name-keyed sources structurally cannot: Шостакович,
/// Стравинский, Чайковский, Глазунов, 内田光子. Qobuz and Deezer index those
/// under Latin spellings, and no folding table turns Cyrillic into "Shostakovich".
/// Together the two answer for 37 of the 40.
///
/// <c>strArtistThumb</c> is the portrait. <c>strArtistFanart</c> and
/// <c>strArtistLogo</c> arrive in the same document and are deliberately not
/// read: fanart is a wide backdrop, which is the exact failure that made
/// Wikidata's P18 unusable for AC/DC, and a logo is not a picture of anybody.
/// </remarks>
public sealed class AudioDbPortraits(
    IHttpClientFactory clients,
    IOptions<AudioDbOptions> options,
    ILogger<AudioDbPortraits> logger) : IArtistPortraits
{
    /// <summary>Name used in <see cref="ProviderException.Provider"/> and in log messages.</summary>
    public const string ProviderName = "TheAudioDB";

    /// <summary>Where a picture may be served from.</summary>
    /// <remarks>
    /// Their image CDN, which is a different host from the API. Both spellings
    /// are accepted because they have moved images between them once already and
    /// old rows in their database still point at the website.
    /// </remarks>
    private static readonly HashSet<string> ImageHosts =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "r2.theaudiodb.com",
            "www.theaudiodb.com",
            "theaudiodb.com",
        };

    /// <summary>The longest URL <c>Artists.PortraitUrl</c> can hold.</summary>
    private const int MaxUrlLength = 1000;

    public async Task<IReadOnlyDictionary<Mbid, Uri>> FindAsync(
        IReadOnlyCollection<ArtistToPicture> artists,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(artists);

        var found = new Dictionary<Mbid, Uri>();

        foreach (var artist in artists.DistinctBy(entry => entry.Id))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var thumb = await LookupAsync(artist.Id, cancellationToken).ConfigureAwait(false);

            if (thumb is null) continue;

            if (Image(thumb) is { } picture)
            {
                found[artist.Id] = picture;
            }
            else
            {
                ProviderLog.AudioDbPortraitRejected(logger, artist.Name, thumb);
            }
        }

        return found;
    }

    /// <summary>This artist's thumbnail, or null when they have none.</summary>
    private async Task<string?> LookupAsync(Mbid artist, CancellationToken cancellationToken)
    {
        var key = options.Value.ApiKey;

        if (string.IsNullOrWhiteSpace(key))
        {
            throw new ProviderRejectedException(
                ProviderName,
                "TheAudioDB needs an API key. Set Fonoteca:Providers:AudioDb:ApiKey, or clear it "
                + "to fall back to their published test key.");
        }

        // The id is formatted from a Guid rather than concatenated from anything
        // a person typed, so there is nothing here to escape — and the round
        // trip through Guid is what makes that true rather than merely likely.
        var path = $"api/v1/json/{Uri.EscapeDataString(key)}/artist-mb.php?i={artist.Value:D}";

        var http = clients.CreateClient(AudioDbOptions.HttpClientName);

        HttpResponseMessage response;

        try
        {
            response = await http.GetAsync(path, cancellationToken).ConfigureAwait(false);
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

            if (!response.IsSuccessStatusCode)
            {
                throw Unavailable($"the lookup answered {(int)response.StatusCode}", cause: null);
            }

            // An artist they hold nothing for comes back as the literal
            // `{"artists":null}`, which is an answer rather than a failure — and
            // the empty *string* comes back when the key is being throttled,
            // which is not. Deserialising an empty body yields null and would
            // otherwise read as "this artist has no picture", stamping the whole
            // batch as asked.
            if (string.IsNullOrWhiteSpace(payload))
            {
                throw Unavailable("the lookup returned an empty body", cause: null);
            }

            AudioDbArtistsBody? body;

            try
            {
                body = JsonSerializer.Deserialize(
                    payload, AudioDbJsonContext.Default.AudioDbArtistsBody);
            }
            catch (JsonException cause)
            {
                throw Unavailable("the response was not the JSON we expect", cause);
            }

            // `{"artists":null}` is how they say they hold nothing for this id,
            // and an empty array happens too.
            var artists = body?.Artists;
            var thumb = artists is { Count: > 0 } ? artists[0].Thumb : null;

            return string.IsNullOrWhiteSpace(thumb) ? null : thumb;
        }
    }

    /// <summary>A URL this application is willing to put in front of a browser.</summary>
    /// <remarks>
    /// Forced to https for <c>WikidataPortraits.Image</c>'s reason: they serve
    /// these over plain http on older rows, and served into a page delivered
    /// over https every one is blocked as mixed content — an artist tile that
    /// silently loses its picture. The same file is at both, so correcting the
    /// scheme is not rewriting the provider's answer.
    /// </remarks>
    private static Uri? Image(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var parsed)) return null;
        if (!ImageHosts.Contains(parsed.Host)) return null;

        if (parsed.Scheme == Uri.UriSchemeHttp)
        {
            parsed = new UriBuilder(parsed) { Scheme = Uri.UriSchemeHttps, Port = -1 }.Uri;
        }
        else if (parsed.Scheme != Uri.UriSchemeHttps)
        {
            return null;
        }

        return parsed.AbsoluteUri.Length > MaxUrlLength ? null : parsed;
    }

    private static ProviderUnavailableException Unavailable(string reason, Exception? cause) =>
        cause is null
            ? new ProviderUnavailableException(ProviderName, $"TheAudioDB lookup failed: {reason}.")
            : new ProviderUnavailableException(
                ProviderName,
                $"TheAudioDB lookup failed: {reason}.",
                cause);
}

internal sealed record AudioDbArtistsBody
{
    [JsonPropertyName("artists")]
    public IReadOnlyList<AudioDbArtistBody>? Artists { get; init; }
}

internal sealed record AudioDbArtistBody
{
    /// <summary>The portrait. Their other two images are a backdrop and a logo.</summary>
    [JsonPropertyName("strArtistThumb")]
    public string? Thumb { get; init; }
}

[JsonSourceGenerationOptions(NumberHandling = JsonNumberHandling.AllowReadingFromString)]
[JsonSerializable(typeof(AudioDbArtistsBody))]
internal sealed partial class AudioDbJsonContext : JsonSerializerContext;
