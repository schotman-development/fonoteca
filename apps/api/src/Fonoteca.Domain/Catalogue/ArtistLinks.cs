using System.Text.Json;
using Fonoteca.Domain.Abstractions;

namespace Fonoteca.Domain.Catalogue;

/// <summary>An artist's pages elsewhere, as <see cref="Artist.LinksJson"/> stores them.</summary>
/// <remarks>
/// An empty array is an answer — MusicBrainz lists no links — where a null
/// column means nobody has asked, so <see cref="Write"/> never returns null.
/// </remarks>
public static class ArtistLinks
{
    public static string Write(IReadOnlyList<MusicBrainzLink> links)
    {
        ArgumentNullException.ThrowIfNull(links);
        return JsonSerializer.Serialize(links.Select(link => new Entry(link.Type, link.Url.AbsoluteUri)));
    }

    public static IReadOnlyList<Entry> Read(string? json) =>
        string.IsNullOrEmpty(json) ? [] : JsonSerializer.Deserialize<List<Entry>>(json) ?? [];

    /// <param name="Type">MusicBrainz's relationship type: "discogs", "official homepage".</param>
    public sealed record Entry(string Type, string Url);
}
