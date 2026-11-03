namespace Fonoteca.Domain.Catalogue;

/// <summary>
/// The English Wikipedia article that describes each of <see cref="Genres"/>,
/// for a genre's page.
/// </summary>
/// <remarks>
/// <b>Picked by hand, as the tree is</b> (the owner's choice, over finding
/// them through MusicBrainz's Wikidata links): a genre here is a name of this
/// tree's, not MusicBrainz's, and several have no item of their own. Checked
/// on 2026-11-03: each resolves to an article and none to a disambiguation
/// page. Four follow Wikipedia's own redirect to a broader article, and so
/// share its words: Heavy Metal (Metal's), Piano Blues (Blues'),
/// Traditional Country (Country's) and Traditional Folk (Folk's). A genre
/// with no article of its own — Early R&amp;B, Contemporary Country, Stage
/// &amp; Screen, Other — is left out and has no description.
/// </remarks>
public static class GenreArticles
{
    public static IReadOnlyDictionary<string, string> Titles { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["Rock"] = "Rock music",
        ["Classic Rock"] = "Classic rock",
        ["Hard Rock"] = "Hard rock",
        ["Soft Rock"] = "Soft rock",
        ["Alternative Rock"] = "Alternative rock",
        ["Indie Rock"] = "Indie rock",
        ["Progressive Rock"] = "Progressive rock",
        ["Psychedelic Rock"] = "Psychedelic rock",
        ["Southern Rock"] = "Southern rock",
        ["Roots Rock"] = "Roots rock",
        ["Rock & Roll"] = "Rock and roll",
        ["Punk"] = "Punk rock",
        ["Pop"] = "Pop music",
        ["Pop Rock"] = "Pop rock",
        ["Dance Pop"] = "Dance-pop",
        ["Indie Pop"] = "Indie pop",
        ["Adult Contemporary"] = "Adult contemporary music",
        ["Singer-Songwriter"] = "Singer-songwriter",
        ["Synth-Pop"] = "Synth-pop",
        ["Metal"] = "Heavy metal music",
        ["Heavy Metal"] = "Traditional heavy metal",
        ["Alternative Metal"] = "Alternative metal",
        ["Symphonic Metal"] = "Symphonic metal",
        ["Glam Metal"] = "Glam metal",
        ["Extreme Metal"] = "Extreme metal",
        ["Blues"] = "Blues",
        ["Blues Rock"] = "Blues rock",
        ["Electric Blues"] = "Electric blues",
        ["Chicago Blues"] = "Chicago blues",
        ["Delta Blues"] = "Delta blues",
        ["Acoustic Blues"] = "Country blues",
        ["Piano Blues"] = "Piano blues",
        ["Soul Blues"] = "Soul blues",
        ["Jazz"] = "Jazz",
        ["Jazz Fusion"] = "Jazz fusion",
        ["Early Jazz"] = "Dixieland",
        ["Swing"] = "Swing music",
        ["Big Band"] = "Big band",
        ["Bebop"] = "Bebop",
        ["Hard Bop"] = "Hard bop",
        ["Cool Jazz"] = "Cool jazz",
        ["Post-Bop"] = "Post-bop",
        ["Soul Jazz"] = "Soul jazz",
        ["Vocal Jazz"] = "Vocal jazz",
        ["Smooth Jazz"] = "Smooth jazz",
        ["Free Jazz"] = "Free jazz",
        ["Latin Jazz"] = "Latin jazz",
        ["Classical"] = "Classical music",
        ["Classical Crossover"] = "Classical crossover",
        ["Early Music"] = "Early music",
        ["Baroque"] = "Baroque music",
        ["Classical Period"] = "Classical period (music)",
        ["Romantic"] = "Romantic music",
        ["Modern Classical"] = "20th-century classical music",
        ["Opera"] = "Opera",
        ["Orchestral"] = "Orchestra",
        ["Chamber Music"] = "Chamber music",
        ["Choral"] = "Choral music",
        ["Ballet"] = "Ballet (music)",
        ["Country"] = "Country music",
        ["Country Rock"] = "Country rock",
        ["Country Pop"] = "Country pop",
        ["Traditional Country"] = "Traditional country music",
        ["Outlaw Country"] = "Outlaw country",
        ["Bluegrass"] = "Bluegrass music",
        ["Americana"] = "Americana (music)",
        ["R&B"] = "Rhythm and blues",
        ["Soul"] = "Soul music",
        ["Funk"] = "Funk",
        ["Disco"] = "Disco",
        ["Contemporary R&B"] = "Contemporary R&B",
        ["Hip Hop"] = "Hip-hop",
        ["Rap Rock"] = "Rap rock",
        ["Pop Rap"] = "Pop rap",
        ["Hardcore Hip Hop"] = "Hardcore hip-hop",
        ["Alternative Hip Hop"] = "Alternative hip-hop",
        ["Electronic"] = "Electronic music",
        ["House"] = "House music",
        ["Techno"] = "Techno",
        ["Drum & Bass"] = "Drum and bass",
        ["Ambient"] = "Ambient music",
        ["Downtempo"] = "Downtempo",
        ["Industrial"] = "Industrial music",
        ["Folk"] = "Folk music",
        ["Folk Rock"] = "Folk rock",
        ["Traditional Folk"] = "Traditional folk music",
        ["Contemporary Folk"] = "Contemporary folk music",
        ["Celtic"] = "Celtic music",
        ["Latin"] = "Latin music",
        ["Brazilian"] = "Music of Brazil",
        ["Latin Pop"] = "Latin pop",
        ["Reggae"] = "Reggae",
        ["Ska"] = "Ska",
        ["International"] = "World music",
        ["Vocal"] = "Vocal music",
        ["Traditional Pop"] = "Traditional pop",
        ["Easy Listening"] = "Easy listening",
        ["Musicals"] = "Musical theatre",
        ["Soundtracks"] = "Soundtrack",
        ["Religious"] = "Religious music",
        ["Gospel"] = "Gospel music",
        ["Contemporary Christian"] = "Contemporary Christian music",
        ["New Age"] = "New-age music",
        ["Avant-Garde"] = "Avant-garde music",
        ["Children's"] = "Children's music",
        ["Holiday"] = "Christmas music",
        ["Comedy/Spoken"] = "Comedy album",
    };

    /// <summary>
    /// The genres, with their articles, to ask for: of those the library
    /// holds, never asked, asked under an article other than the one named
    /// now, or found nothing before <paramref name="nothingFoundCutoff"/>.
    /// </summary>
    /// <param name="held">The genres the library's songs carry, as <see cref="Genres.Carried"/> names them.</param>
    public static IReadOnlyList<KeyValuePair<string, string>> Unasked(
        IEnumerable<string> held,
        IEnumerable<GenreDescription> asked,
        DateTimeOffset nothingFoundCutoff)
    {
        var holds = held.ToHashSet(StringComparer.Ordinal);
        var rows = asked.ToDictionary(row => row.Genre, StringComparer.Ordinal);

        return
        [
            .. Titles
                .Where(genre => holds.Contains(genre.Key))
                .Where(genre => !rows.TryGetValue(genre.Key, out var row)
                    || row.Title != genre.Value
                    || (row.Text is null && row.LookupUtc < nothingFoundCutoff))
                .OrderBy(genre => genre.Key, StringComparer.Ordinal),
        ];
    }

    /// <summary>A stored description, where it is of the article named now.</summary>
    public static bool IsCurrent(GenreDescription description) =>
        Titles.TryGetValue(description.Genre, out var title) && description.Title == title;
}
