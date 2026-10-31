using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Fonoteca.Domain.Abstractions;
using Fonoteca.Providers.Logging;
using Microsoft.Extensions.Logging;

namespace Fonoteca.Providers.AppleMusic;

/// <summary>
/// <see cref="IClassicalAlbums"/> over Apple Music Classical's public pages (ADR 0019).
/// </summary>
/// <remarks>
/// <b>Two requests an album, each something a browser fetches without
/// signing in</b>, after <see cref="AppleMusicRecords"/> has recognised the
/// record. The album's page on <c>classical.music.apple.com</c> embeds the JSON
/// it draws its track list from: a section per piece, headed by the piece and
/// its composers with Apple's ids, each track naming its performers, and a list
/// of everyone on the album with their roles. It numbers no track, so the
/// documented lookup gives each track's disc, number and length by the id the
/// page names it by.
///
/// <b>A track under no linked piece performs nothing here.</b> The page still
/// prints a <c>workName</c> for it, but that is whatever the label typed — a
/// composer's name, "Sir J. MacMillan: Saxophone Concerto", the track's own
/// title — and not a piece Apple's catalogue knows.
///
/// <b>Null is an answer and a throw is not</b>, as for the motion artwork: a
/// record with no classical page (Apple answers 404 for one it does not carry)
/// is nothing to say; an outage, or a page that has changed shape, throws.
/// </remarks>
public sealed partial class AppleMusicClassical(
    AppleMusicRecords records,
    ILogger<AppleMusicClassical> logger) : IClassicalAlbums
{
    /// <summary>Tracks the lookup answers at most; a longer album's later tracks are nobody's.</summary>
    private const int MaxTracks = 200;

    public async Task<ClassicalAlbum?> FindAsync(AlbumToFind album, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(album);

        var found = await records.FindAsync(album, TracksAsync, cancellationToken).ConfigureAwait(false);

        if (found is null)
        {
            ProviderLog.AppleClassicalNotFound(logger, album.Artist, album.Title);
            return null;
        }

        ProviderLog.AppleClassicalFound(
            logger, found.AlbumId, found.Storefront, found.Value.Count, album.Artist, album.Title, found.MatchedBy);

        return new ClassicalAlbum(found.AlbumId, found.Storefront, found.MatchedBy, found.Value);
    }

    public async Task<ClassicalBiography?> BiographyAsync(string composer, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(composer);

        // An id off Apple's own page, but a path segment all the same.
        if (composer.Length == 0 || !composer.All(char.IsAsciiDigit)) return null;

        var url = new Uri(AppleMusicOptions.ClassicalServer, $"{AppleMusicOptions.Storefront}/artist/{composer}");

        if (await SectionsAsync(url, $"composer {composer}", cancellationToken).ConfigureAwait(false) is not { } page)
        {
            return null;
        }

        using (page)
        {
            foreach (var section in Sections(page))
            {
                if (Text(section, "sectionKind") == "info"
                    && Text(section, "itemKind") == "paragraph"
                    && Text(section, "title") == "Biography"
                    && Text(section, "text") is { } text
                    && Plain(text) is { Length: > 0 } plain)
                {
                    return new ClassicalBiography(plain, url);
                }
            }
        }

        return null;
    }

    /// <summary>A record's tracks as its classical page and the lookup describe them, or null where it has no page.</summary>
    private async Task<IReadOnlyList<ClassicalTrack>?> TracksAsync(
        string storefront,
        long album,
        CancellationToken cancellationToken)
    {
        var url = new Uri(
            AppleMusicOptions.ClassicalServer,
            $"{Uri.EscapeDataString(storefront)}/album/{album.ToString(CultureInfo.InvariantCulture)}");

        if (await SectionsAsync(url, $"album {album}", cancellationToken).ConfigureAwait(false) is not { } page)
        {
            return null;
        }

        List<(long Song, ClassicalWork? Work, List<(string Name, string? Id)> Performers)> listed = [];
        Dictionary<string, IReadOnlyList<string>> roles = new(StringComparer.Ordinal);

        using (page)
        {
            foreach (var section in Sections(page))
            {
                if (Text(section, "id") == "list_on_this_album")
                {
                    foreach (var item in Items(section))
                    {
                        if (Destination(item) is { } id && Text(item, "subtitle") is { } subtitle)
                        {
                            roles[id] = subtitle.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                        }
                    }

                    continue;
                }

                if (Text(section, "itemKind") != "trackLockup") continue;

                var work = Work(section);

                foreach (var item in Items(section))
                {
                    if (Song(Text(item, "id")) is not { } song) continue;

                    List<(string Name, string? Id)> performers = [];

                    if (item.TryGetProperty("subtitleLinks", out var links) && links.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var link in links.EnumerateArray())
                        {
                            if (Text(link, "title") is { Length: > 0 } name) performers.Add((name, Destination(link)));
                        }
                    }

                    listed.Add((song, work, performers));
                }
            }
        }

        if (listed.Count == 0)
        {
            ProviderLog.AppleClassicalNoTracks(logger, album);
            return null;
        }

        var places = (await records.ResultsAsync(
                    $"lookup?id={album.ToString(CultureInfo.InvariantCulture)}&entity=song&limit={MaxTracks}"
                    + $"&country={Uri.EscapeDataString(storefront)}",
                    cancellationToken)
                .ConfigureAwait(false))
            .Where(row => row.WrapperType == "track"
                && row.TrackId is not null
                && row.DiscNumber is > 0
                && row.TrackNumber is > 0
                && row.TrackTimeMillis is > 0)
            .GroupBy(row => row.TrackId!.Value)
            .ToDictionary(rows => rows.Key, rows => rows.First());

        return
        [
            .. listed
                .Where(track => places.ContainsKey(track.Song))
                .Select(track =>
                {
                    var place = places[track.Song];

                    return new ClassicalTrack(
                        place.DiscNumber!.Value,
                        place.TrackNumber!.Value,
                        TimeSpan.FromMilliseconds(place.TrackTimeMillis!.Value),
                        track.Work,
                        [
                            .. track.Performers.Select(performer => new ClassicalPerformer(
                                performer.Name,
                                performer.Id is { } id && roles.TryGetValue(id, out var credited) ? credited : [])),
                        ]);
                }),
        ];
    }

    /// <summary>
    /// A classical page's embedded JSON, or null where Apple carries no such page.
    /// </summary>
    /// <remarks>
    /// A page that redirects off the classical host is not one: Apple sends a
    /// record it does not carry to its ordinary shop. A page whose embedded
    /// JSON is gone, or has no sections, is the page changing shape under this
    /// reader — every album would read as having nothing — so it throws.
    /// </remarks>
    private async Task<JsonDocument?> SectionsAsync(Uri url, string what, CancellationToken cancellationToken)
    {
        string html;

        using (var response = await records.SendAsync(AppleMusicOptions.PageHttpClientName, url, cancellationToken)
            .ConfigureAwait(false))
        {
            if (response.StatusCode == HttpStatusCode.NotFound) return null;

            if (!response.IsSuccessStatusCode)
            {
                throw AppleMusicRecords.Unavailable($"the classical page answered {(int)response.StatusCode}", cause: null);
            }

            if (response.RequestMessage?.RequestUri is { } landed
                && !string.Equals(landed.Host, AppleMusicOptions.ClassicalServer.Host, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            html = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }

        if (AppleMusicRecords.ServerData().Match(html) is not { Success: true } script)
        {
            throw Changed($"{what}'s page carries no serialized-server-data");
        }

        JsonDocument document;

        try
        {
            document = JsonDocument.Parse(script.Groups["json"].Value);
        }
        catch (JsonException cause)
        {
            throw Changed($"{what}'s page data is not JSON ({cause.Message})");
        }

        if (!Sections(document).Any())
        {
            document.Dispose();
            throw Changed($"{what}'s page data has no sections");
        }

        return document;
    }

    /// <summary>An empty array to enumerate where a page has none.</summary>
    private static readonly JsonElement None = JsonSerializer.Deserialize<JsonElement>("[]");

    private static JsonElement.ArrayEnumerator Sections(JsonDocument document) =>
        document.RootElement.TryGetProperty("data", out var data)
        && data.ValueKind == JsonValueKind.Array
        && data.GetArrayLength() > 0
        && data[0].TryGetProperty("data", out var page)
        && page.ValueKind == JsonValueKind.Object
        && page.TryGetProperty("sections", out var sections)
        && sections.ValueKind == JsonValueKind.Array
            ? sections.EnumerateArray()
            : None.EnumerateArray();

    private static JsonElement.ArrayEnumerator Items(JsonElement section) =>
        section.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array
            ? items.EnumerateArray()
            : None.EnumerateArray();

    /// <summary>The piece a track list section is headed by, where the heading links one.</summary>
    private static ClassicalWork? Work(JsonElement section)
    {
        if (!section.TryGetProperty("header", out var header)
            || header.ValueKind != JsonValueKind.Object
            || Text(header, "kind") != "work"
            || !header.TryGetProperty("item", out var item)
            || item.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var piece = Links(item, "secondRowLinks")
            .FirstOrDefault(link => link.Destination is { } destination && Text(destination, "pageType") == "work");

        if (piece.Destination is not { } work || Text(work, "id") is not { Length: > 0 } id || piece.Title is not { Length: > 0 } title)
        {
            return null;
        }

        return new ClassicalWork(
            id,
            title,
            [
                .. Links(item, "firstRowLinks")
                    .Where(link => link.Destination is { } destination
                        && Text(destination, "$kind") == "ArtistPageIntent"
                        && Text(destination, "id") is { Length: > 0 }
                        && link.Title is { Length: > 0 })
                    .Select(link => new ClassicalArtist(Text(link.Destination!.Value, "id")!, link.Title!)),
            ]);
    }

    private static IEnumerable<(string? Title, JsonElement? Destination)> Links(JsonElement item, string name) =>
        item.TryGetProperty(name, out var links) && links.ValueKind == JsonValueKind.Array
            ? links.EnumerateArray().Select(link => (Text(link, "title"), DestinationOf(link)))
            : [];

    private static JsonElement? DestinationOf(JsonElement link) =>
        link.ValueKind == JsonValueKind.Object
        && link.TryGetProperty("segue", out var segue)
        && segue.ValueKind == JsonValueKind.Object
        && segue.TryGetProperty("destination", out var destination)
        && destination.ValueKind == JsonValueKind.Object
            ? destination
            : null;

    /// <summary>The id of the page a link goes to: an artist's, a piece's.</summary>
    private static string? Destination(JsonElement link) =>
        DestinationOf(link) is { } destination ? Text(destination, "id") : null;

    /// <summary>The track's id out of the lockup's: <c>track-lockup - {album} - {track}</c>.</summary>
    private static long? Song(string? lockup) =>
        lockup is null
            ? null
            : long.TryParse(lockup[(lockup.LastIndexOf(' ') + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var id)
                ? id
                : null;

    private static string? Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>A biography as plain text: the page italicises titles, and nothing else here reads markup.</summary>
    private static string Plain(string text) =>
        WebUtility.HtmlDecode(Markup().Replace(text, string.Empty)).ReplaceLineEndings("\n").Trim();

    /// <summary>The page has changed shape. Retrying will not help; a code change will.</summary>
    private static ProviderRejectedException Changed(string reason) =>
        new(AppleMusicRecords.ProviderName, $"Apple Music Classical's page has changed shape: {reason}.");

    [GeneratedRegex("<[^>]*>")]
    private static partial Regex Markup();
}
