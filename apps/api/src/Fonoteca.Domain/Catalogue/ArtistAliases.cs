using System.Text.Json;
using Fonoteca.Domain.Abstractions;

namespace Fonoteca.Domain.Catalogue;

/// <summary>An artist's other names, as <see cref="Artist.AliasesJson"/> stores them.</summary>
/// <remarks>
/// An empty array is an answer — MusicBrainz lists no aliases — where a null
/// column means nobody has asked, so <see cref="Write"/> never returns null.
/// </remarks>
public static class ArtistAliases
{
    public static string Write(IReadOnlyList<MusicBrainzAlias> aliases)
    {
        ArgumentNullException.ThrowIfNull(aliases);
        return JsonSerializer.Serialize(aliases.Select(alias => new Entry(alias.Name, alias.Locale, alias.Primary, alias.Type)));
    }

    public static IReadOnlyList<MusicBrainzAlias> Read(string? json) =>
        string.IsNullOrEmpty(json)
            ? []
            : [.. (JsonSerializer.Deserialize<List<Entry>>(json) ?? []).Select(entry => new MusicBrainzAlias(entry.Name, entry.Locale, entry.Primary, entry.Type))];

    /// <summary>One alias as stored: <see cref="MusicBrainzAlias"/> without the properties it computes.</summary>
    public sealed record Entry(string Name, string? Locale, bool Primary, string? Type);
}
