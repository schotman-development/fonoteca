using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using Fonoteca.Domain.Abstractions;
using Polly;

namespace Fonoteca.Providers.Lrclib;

/// <summary>
/// <see cref="ISongLyrics"/> over LRCLIB's <c>/api/get</c>.
/// </summary>
/// <remarks>
/// <b>An exact match on folded names, never a search.</b> LRCLIB lowercases
/// the title and the artist, drops punctuation and apostrophes, and looks for
/// those exactly; an album, where one is sent, is a further exact filter; the
/// length is held within two seconds either way. Nothing fuzzier is offered,
/// and nothing fuzzier is asked for here.
///
/// <b>Up to three asks, each wider than the last, all held to the length.</b>
/// First with the album, because a compilation's track can differ from the
/// album's. Then any album: the same title by the same artist, within two
/// seconds, is the same song wherever it was released — a Hungarian Sinatra
/// compilation found nothing by album and "Fly Me to the Moon" by any. Then,
/// where the line bills several, the first artist alone, since a "feat." is
/// often not on LRCLIB's row. A miss on all of them is "no such song".
///
/// An answer marked instrumental is an answer. A refusal or a busy server's 503 is retried
/// by the pipeline under the gate; one that stays busy is an outage.
/// </remarks>
public sealed class LrclibLyrics(IHttpClientFactory clients) : ISongLyrics
{
    /// <summary>Name used in <see cref="ProviderException.Provider"/> and in log messages.</summary>
    public const string ProviderName = "LRCLIB";

    public async Task<SongLyricsFound?> FindAsync(SongToFind song, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(song);

        // The range LRCLIB accepts. A track outside it is not one it holds,
        // and asking without the length would drop the one thing that tells a
        // live take from the studio one.
        var seconds = song.Duration.TotalSeconds;

        if (seconds is < 1 or > 3600 || string.IsNullOrWhiteSpace(song.Title) || string.IsNullOrWhiteSpace(song.Artist))
        {
            return null;
        }

        var asks = new List<(string MatchedBy, string Artist, string? Album)>();

        if (!string.IsNullOrWhiteSpace(song.Album)) asks.Add(("album", song.Artist, song.Album));
        asks.Add(("any album", song.Artist, null));

        if (!string.IsNullOrWhiteSpace(song.FirstArtist) && song.FirstArtist != song.Artist)
        {
            asks.Add(("first artist", song.FirstArtist, null));
        }

        foreach (var (matchedBy, artist, album) in asks)
        {
            var query = $"api/get?track_name={Uri.EscapeDataString(song.Title)}&artist_name={Uri.EscapeDataString(artist)}"
                + (album is null ? string.Empty : $"&album_name={Uri.EscapeDataString(album)}")
                + $"&duration={seconds.ToString("0.###", CultureInfo.InvariantCulture)}";

            if (await GetAsync(query, cancellationToken).ConfigureAwait(false) is not { } track) continue;

            var synced = Text(track.SyncedLyrics);
            var plain = Text(track.PlainLyrics);

            // A row whose words were taken down is no answer; a wider ask may find another.
            if (synced is null && plain is null && !track.Instrumental) continue;

            return new SongLyricsFound(track.Id, synced, plain, track.Instrumental, matchedBy);
        }

        return null;
    }

    /// <summary>The track LRCLIB holds for this ask, or null when it holds none.</summary>
    private async Task<LrclibTrack?> GetAsync(string query, CancellationToken cancellationToken)
    {
        var http = clients.CreateClient(LrclibOptions.HttpClientName);

        HttpResponseMessage response;

        try
        {
            response = await http.GetAsync(query, cancellationToken).ConfigureAwait(false);
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
            if (response.StatusCode == HttpStatusCode.NotFound) return null;

            if (!response.IsSuccessStatusCode)
            {
                throw Unavailable($"the lookup answered {(int)response.StatusCode}", cause: null);
            }

            var payload = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            try
            {
                return JsonSerializer.Deserialize(payload, LrclibJsonContext.Default.LrclibTrack)
                    ?? throw Unavailable("the lookup returned no track", cause: null);
            }
            catch (JsonException cause)
            {
                throw Unavailable("the response was not the JSON we expect", cause);
            }
        }
    }

    private static string? Text(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private static ProviderUnavailableException Unavailable(string reason, Exception? cause) =>
        cause is null
            ? new ProviderUnavailableException(ProviderName, $"LRCLIB lookup failed: {reason}.")
            : new ProviderUnavailableException(ProviderName, $"LRCLIB lookup failed: {reason}.", cause);
}

internal sealed record LrclibTrack
{
    [JsonPropertyName("id")]
    public long Id { get; init; }

    [JsonPropertyName("instrumental")]
    public bool Instrumental { get; init; }

    [JsonPropertyName("plainLyrics")]
    public string? PlainLyrics { get; init; }

    [JsonPropertyName("syncedLyrics")]
    public string? SyncedLyrics { get; init; }
}

[JsonSerializable(typeof(LrclibTrack))]
internal sealed partial class LrclibJsonContext : JsonSerializerContext;
