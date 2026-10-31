using Fonoteca.Domain.Abstractions;

namespace Fonoteca.Domain.Catalogue;

/// <summary>
/// How Apple Music Classical's answers are read into the catalogue: which
/// track a file is, who plays on it in MusicBrainz's terms, and which artist
/// a composer is (ADR 0019).
/// </summary>
/// <remarks>
/// <b>Only where MusicBrainz has nothing.</b> A recording it links to a work
/// keeps that work, and one it names any performer for keeps those performers;
/// these rules decide what fills the gap and nothing else.
/// </remarks>
public static class ClassicalCatalogue
{
    /// <summary>
    /// How far a file's length may be from the track's and still be that track.
    /// </summary>
    /// <remarks>
    /// Two pressings of one performance differ by a second or two of silence at
    /// the ends; the next movement at the same position almost never comes
    /// within three seconds. Not <c>EditionProof</c>'s 100 ms, which proves a
    /// pressing — this only says which performance.
    /// </remarks>
    public static readonly TimeSpan Tolerance = TimeSpan.FromSeconds(3);

    /// <summary>Whether a MusicBrainz link type names somebody performing.</summary>
    /// <remarks>
    /// What "MusicBrainz names a performer" means wherever Apple's performers
    /// give way to MusicBrainz's: the conductor, an orchestra, a player, a
    /// singer, a chorus master, a leader.
    /// </remarks>
    public static bool IsPerformer(string type) => PerformerTypes.Contains(type);

    /// <summary><see cref="IsPerformer"/> as a list, for a query to send.</summary>
    public static readonly IReadOnlyList<string> PerformerTypes =
        ["conductor", "performing orchestra", "instrument", "vocal", "performer", "chorus master", "concertmaster"];

    /// <summary>
    /// The catalogue track each held file is: the same disc and number, and a
    /// length within <see cref="Tolerance"/>.
    /// </summary>
    /// <remarks>
    /// <b>Positions, never titles</b>: the page prints "I. Allegro con brio"
    /// where MusicBrainz prints the symphony in front of it. The length is the
    /// guard, because the record the barcode found can be another edition, a
    /// deluxe whose bonus disc renumbers nothing but adds a track in the middle.
    /// A file of unknown length, or a position the catalogue lists twice, is
    /// nobody's.
    /// </remarks>
    public static IReadOnlyDictionary<TKey, ClassicalTrack> Seat<TKey>(
        IEnumerable<HeldTrack<TKey>> held,
        IReadOnlyList<ClassicalTrack> tracks)
        where TKey : notnull
    {
        ArgumentNullException.ThrowIfNull(held);
        ArgumentNullException.ThrowIfNull(tracks);

        var places = tracks
            .GroupBy(track => (track.Disc, track.Number))
            .Where(place => place.Count() == 1)
            .ToDictionary(place => place.Key, place => place.Single());

        var seats = new Dictionary<TKey, ClassicalTrack>();

        foreach (var file in held)
        {
            if (file.Length is { } length
                && places.TryGetValue((file.Disc, file.Number), out var track)
                && (length - track.Length).Duration() <= Tolerance)
            {
                seats[file.Key] = track;
            }
        }

        return seats;
    }

    /// <summary>
    /// A performer's roles as MusicBrainz's link types and qualifiers, as
    /// <see cref="CreditTags.Role"/> reads them.
    /// </summary>
    /// <remarks>
    /// <b>MusicBrainz's spelling</b>, so the album page, the tag write and
    /// Subsonic read Apple's performers as they read MusicBrainz's: an
    /// orchestra is a <c>performing orchestra</c>, a choir sings
    /// <c>choir vocals</c>, a soprano <c>soprano vocals</c>, and anything else
    /// a person plays is an <c>instrument</c> named as the page names it.
    /// "Ensemble" or "Artist" says only that they perform, and is dropped
    /// beside a role that says how.
    ///
    /// <b>Writers are not performers</b>: a composer, a lyricist, an arranger
    /// on the album's list of who is on it is nobody's performance here. A
    /// performer the album credits with no role at all is a
    /// <c>performer</c>, unqualified.
    /// </remarks>
    public static IReadOnlyList<(string Type, string? Attributes)> Links(IReadOnlyCollection<string> roles)
    {
        ArgumentNullException.ThrowIfNull(roles);

        if (roles.Count == 0) return [("performer", null)];

        var links = new List<(string Type, string? Attributes)>();

        foreach (var role in roles)
        {
            if (Link(role.Trim()) is { } link && !links.Contains(link)) links.Add(link);
        }

        // "Orchestra, Ensemble, Symphony Orchestra" is one orchestra, not an
        // orchestra and an unqualified performer beside it.
        if (links.Count > 1) links.Remove(("performer", null));

        return links;
    }

    private static (string Type, string? Attributes)? Link(string role)
    {
        var lower = role.ToLowerInvariant();

        if (lower.Length == 0) return null;

        if (lower is "conductor") return ("conductor", null);
        if (lower is "chorus master" or "chorus director" or "choirmaster" or "choir director") return ("chorus master", null);
        if (lower is "concertmaster" or "concertmistress") return ("concertmaster", null);
        if (Writers.Any(writer => lower.Contains(writer, StringComparison.Ordinal))) return null;
        if (lower.Contains("orchestra", StringComparison.Ordinal)) return ("performing orchestra", null);
        if (lower.Contains("choir", StringComparison.Ordinal) || lower.Contains("chorus", StringComparison.Ordinal)) return ("vocal", "choir vocals");
        if (lower is "vocals" or "voice") return ("vocal", null);
        if (lower is "narrator" or "speaker" or "reciter") return ("vocal", "spoken vocals");
        if (lower.EndsWith(" vocals", StringComparison.Ordinal)) return ("vocal", lower);
        if (Voices.Contains(lower)) return ("vocal", $"{lower} vocals");
        if (Players.Contains(lower)) return ("performer", null);

        return ("instrument", lower);
    }

    /// <summary>Roles on the list of who is on an album that are writing, not performing.</summary>
    private static readonly string[] Writers =
    [
        "composer", "lyricist", "librettist", "arranger", "orchestrat", "songwriter", "writer", "author",
        "producer", "engineer", "editor", "translat",
    ];

    /// <summary>The voice types a page names as a role, which MusicBrainz qualifies a <c>vocal</c> link with.</summary>
    private static readonly HashSet<string> Voices = new(StringComparer.Ordinal)
    {
        "soprano", "mezzo-soprano", "alto", "contralto", "countertenor", "tenor", "baritone", "bass-baritone",
        "bass", "treble", "boy soprano",
    };

    /// <summary>Roles that say somebody performs and not what with.</summary>
    private static readonly HashSet<string> Players = new(StringComparer.Ordinal)
    {
        "artist", "primary artist", "featured artist", "performer", "soloist", "ensemble",
    };

    /// <summary>
    /// The one catalogue artist a classical catalogue's composer is, or null.
    /// </summary>
    /// <remarks>
    /// <b>By name, and only among composers</b>: the artists MusicBrainz
    /// already credits as composer of a work here, compared as
    /// <see cref="ArtistNameMatch.Normalise"/> compares, under their name or
    /// their Latin name. Two who answer to it are nobody — the name is printed
    /// and the work is linked, but no page claims it.
    /// </remarks>
    public static Mbid? Composer(string name, IEnumerable<KnownComposer> composers)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(composers);

        var wanted = ArtistNameMatch.Normalise(name);

        if (wanted.Length == 0) return null;

        var found = composers
            .Where(composer => ArtistNameMatch.Normalise(composer.Name) == wanted
                || (composer.LatinName is { } latin && ArtistNameMatch.Normalise(latin) == wanted))
            .Select(composer => composer.Mbid)
            .Distinct()
            .Take(2)
            .ToList();

        return found.Count == 1 ? found[0] : null;
    }
}

/// <summary>One held file, by where it sits on its album.</summary>
/// <param name="Length">As measured, or null where it was not.</param>
public readonly record struct HeldTrack<TKey>(TKey Key, int Disc, int Number, TimeSpan? Length);

/// <summary>An artist MusicBrainz credits as a composer here.</summary>
public readonly record struct KnownComposer(Mbid Mbid, string Name, string? LatinName);
