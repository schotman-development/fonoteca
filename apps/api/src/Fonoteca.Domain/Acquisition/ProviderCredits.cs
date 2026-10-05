using Fonoteca.Domain.Catalogue;

namespace Fonoteca.Domain.Acquisition;

/// <summary>One artist a shop credits on a track, in the catalogue's own roles.</summary>
/// <param name="Position">Order on the billing line; 0 for everything not billed.</param>
/// <param name="JoinPhrase">What follows a billed name on the printed line, or null after the last.</param>
public sealed record ProviderCredit(string Name, CreditRole Role, int Position, string? JoinPhrase);

/// <summary>
/// What a shop's credit string says about a track, read into the roles
/// <see cref="PrimaryCredits"/> keeps for a MusicBrainz recording.
/// </summary>
/// <remarks>
/// Qobuz send everyone on a track as one string —
/// <c>Fleetwood Mac, MainArtist - Stevie Nicks, Producer, Vocals, Writer</c>
/// — a name and its roles, people apart by <c>" - "</c>. Only the four roles
/// the catalogue keeps for a MusicBrainz recording are read: billed, conductor,
/// ensemble, writer. A band's players, its producers and its engineers are on
/// MusicBrainz's recordings too and the catalogue stores none of them, so a
/// download stores none either; two sources filling an artist page by
/// different rules would be the drift nobody notices.
///
/// A featured artist is billed after the main ones, behind " feat. ", as
/// MusicBrainz prints the line.
///
/// "Various Artists" is never an artist: Qobuz bill a classical compilation's
/// every track to it, and an artist of that name would collect every one.
/// </remarks>
public static class ProviderCredits
{
    /// <summary>Read the credit string; where it bills nobody, <paramref name="billed"/> is the billing.</summary>
    /// <param name="performers">The shop's credit string, or null.</param>
    /// <param name="billed">The shop's own one-name credit for the track, used where the string bills nobody.</param>
    /// <param name="composer">The shop's composer for the track, used where the string names no writer.</param>
    /// <param name="names">Names the shop gives whole elsewhere — the album's artists — read whole here, commas and all.</param>
    public static IReadOnlyList<ProviderCredit> Parse(
        string? performers,
        string? billed = null,
        string? composer = null,
        IEnumerable<string>? names = null)
    {
        var main = new List<string>();
        var featured = new List<string>();
        var others = new List<ProviderCredit>();

        // A name with a comma in it is read whole where the shop names it on its
        // own elsewhere, and where what follows the comma is plainly still a
        // name (NameOf). Any other comma ends it.
        // Longest first, so "Crosby, Stills, Nash & Young" wins over "Crosby, Stills & Nash".
        string[] known =
        [
            .. new[] { billed, composer }
                .Concat(names ?? [])
                .OfType<string>()
                .Select(name => name.Trim())
                .Where(name => name.Contains(','))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(name => name.Length),
        ];

        foreach (var person in (performers ?? string.Empty).Split(" - ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var whole = known.FirstOrDefault(name => person.StartsWith(name + ",", StringComparison.OrdinalIgnoreCase));
            var parts = whole is null
                ? person.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                : [whole, .. person[(whole.Length + 1)..].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];

            if (parts.Length == 0) continue;

            var (name, length) = whole is null ? NameOf(parts) : (whole, 1);

            var roles = parts[length..];

            if (IsNobody(name)) continue;

            if (roles.Any(role => Word(role) is "FEATUREDARTIST" or "FEATURING")
                && !featured.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                featured.Add(name);
            }

            foreach (var role in roles.Select(RoleOf).OfType<CreditRole>().Distinct())
            {
                if (role == CreditRole.Billed)
                {
                    if (!main.Contains(name, StringComparer.OrdinalIgnoreCase)) main.Add(name);
                }
                else if (!others.Any(credit => credit.Role == role && Same(credit.Name, name)))
                {
                    others.Add(new ProviderCredit(name, role, 0, null));
                }
            }
        }

        if (main.Count == 0 && !string.IsNullOrWhiteSpace(billed) && !IsNobody(billed.Trim())) main.Add(billed.Trim());

        if (!others.Any(credit => credit.Role == CreditRole.Writer)
            && !string.IsNullOrWhiteSpace(composer)
            && !IsNobody(composer.Trim()))
        {
            others.Add(new ProviderCredit(composer.Trim(), CreditRole.Writer, 0, null));
        }

        featured.RemoveAll(name => main.Contains(name, StringComparer.OrdinalIgnoreCase));

        // "A, B & C feat. D & E": what MusicBrainz prints between billed names.
        static string? Joined(int index, int count) => index == count - 1 ? null : index == count - 2 ? " & " : ", ";

        List<ProviderCredit> line =
        [
            .. main.Select((name, index) => new ProviderCredit(
                name,
                CreditRole.Billed,
                index,
                index == main.Count - 1 && featured.Count > 0 ? " feat. " : Joined(index, main.Count))),
            .. featured.Select((name, index) => new ProviderCredit(
                name, CreditRole.Billed, main.Count + index, Joined(index, featured.Count))),
        ];

        // One entry per artist, in the strongest role it holds, as
        // PrimaryCredits keeps a MusicBrainz recording's: a billed conductor
        // is billed, and a composer who also conducts is the conductor.
        var rest = others
            .Where(credit => !line.Any(named => Same(named.Name, credit.Name)))
            .GroupBy(credit => credit.Name, StringComparer.OrdinalIgnoreCase)
            .Select(roles => roles.MinBy(credit => credit.Role)!);

        return [.. line, .. rest];
    }

    /// <summary>"Various Artists" and "Various Composers": a shop's placeholder, not a name.</summary>
    public static bool IsNobody(string name) =>
        name.Equals("Various Artists", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Various Composers", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Various", StringComparison.OrdinalIgnoreCase);

    private static bool Same(string one, string other) => string.Equals(one, other, StringComparison.OrdinalIgnoreCase);

    /// <summary>What may follow a comma and still be the name: "Sammy Davis, Jr.", "Hank Williams, III".</summary>
    private static readonly HashSet<string> Suffixes = new(StringComparer.OrdinalIgnoreCase)
    {
        "Jr.", "Jr", "Sr.", "Sr", "II", "III", "IV",
    };

    /// <summary>
    /// A credit's name, and how many of its comma-separated parts it took.
    /// </summary>
    /// <remarks>
    /// The name runs on past a comma to the last part that is plainly still a
    /// name — a suffix ("Sammy Davis, Jr."), a "The" ("Tyler, The Creator"), a
    /// joined pair ("Crosby, Stills, Nash &amp; Young", "Peter, Paul and Mary"),
    /// a number's thousands ("10,000 Maniacs"), or an ensemble's place ("Choir
    /// of King's College, Cambridge") — and never past a part that reads as a
    /// role, so "Christine McVie, Keyboards, Vocals" stays Christine McVie. A
    /// comma no rule can tell from a role's ("Hey, Rosetta!") still cuts the
    /// name, unless the shop lists the album's artists with it.
    /// </remarks>
    private static (string Name, int Length) NameOf(string[] parts)
    {
        // An ensemble "of" somewhere runs on to its place: "Choir of King's
        // College, Cambridge", "Orchestra of the Royal Opera House, Covent
        // Garden". "Kronos Quartet, Strings" is the quartet and a role.
        var ensemble = parts[0].Contains(" of ", StringComparison.OrdinalIgnoreCase)
            && parts[0].Split(' ', StringSplitOptions.RemoveEmptyEntries).Any(word => EnsembleWords.Contains(word.Trim('.', ',', '\'', '"')));

        var length = 1;

        for (var index = 1; index < parts.Length && !RoleLike(parts[index]); index++)
        {
            var part = parts[index];

            if (ensemble
                || Suffixes.Contains(part)
                || Thousands(parts[index - 1], part)
                || part.StartsWith("The ", StringComparison.OrdinalIgnoreCase)
                || part.Contains('&')
                || part.Contains(" and ", StringComparison.OrdinalIgnoreCase))
            {
                length = index + 1;
            }
        }

        var name = new System.Text.StringBuilder(parts[0]);

        for (var index = 1; index < length; index++)
        {
            name.Append(Thousands(parts[index - 1], parts[index]) ? "," : ", ").Append(parts[index]);
        }

        return (name.ToString(), length);
    }

    /// <summary>"10" then "000 Maniacs": a number's thousands, not a new part.</summary>
    private static bool Thousands(string before, string part) =>
        before.Length > 0 && char.IsAsciiDigit(before[^1]) && part.Length >= 3 && part[..3].All(char.IsAsciiDigit);

    /// <summary>Words that make a name an ensemble's, whose name may go on to a place.</summary>
    private static readonly HashSet<string> EnsembleWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "Choir", "Chorus", "Orchestra", "Ensemble", "Consort", "Philharmonic", "Symphony", "Sinfonia",
        "Camerata", "Singers", "Players", "Quartet", "Quintet", "Trio",
    };

    /// <summary>
    /// Whether a part reads as one of the shop's roles: a word run together
    /// ("MainArtist", "AssistantEngineer"), or one ending as a role or an
    /// instrument does.
    /// </summary>
    private static bool RoleLike(string part)
    {
        var word = Word(part);

        return RoleOf(part) is not null
            || word is "FEATUREDARTIST" or "FEATURING"
            || (!part.Contains(' ') && part.Skip(1).Any(char.IsUpper) && part.Any(char.IsLower))
            || RoleEndings.Any(ending => word.EndsWith(ending, StringComparison.Ordinal));
    }

    private static readonly string[] RoleEndings =
    [
        "ARTIST", "ENGINEER", "PRODUCER", "MIXER", "PROGRAMMER", "ARRANGER", "WRITER", "LYRICIST", "COMPOSER",
        "CONDUCTOR", "DIRECTOR", "MASTER", "PERFORMER", "SOLOIST", "VOCALS", "VOCALIST", "VOCAL", "ORCHESTRA",
        "ENSEMBLE", "CHOIR", "CHORUS", "GUITAR", "BASS", "KEYBOARDS", "PIANO", "SYNTHESIZER", "ORGAN", "DRUMS",
        "PERCUSSION", "VIOLIN", "VIOLA", "CELLO", "HARP", "FLUTE", "CLARINET", "OBOE", "BASSOON", "SAXOPHONE",
        "TRUMPET", "TROMBONE", "HORN", "HARMONICA", "BANJO", "MANDOLIN", "RECORDING", "MASTERING", "MIXING",
        "ORCHESTRATOR", "MEMBER",
    ];

    /// <summary>A role as one upper-case word: "Featured Artist" and "FeaturedArtist" alike.</summary>
    private static string Word(string role) => new string([.. role.Where(char.IsLetter)]).ToUpperInvariant();

    /// <summary>The catalogue's role for one of the shop's, or null for a role it does not keep.</summary>
    private static CreditRole? RoleOf(string role)
    {
        var word = Word(role);

        return word switch
        {
            "MAINARTIST" => CreditRole.Billed,

            // A chorus master is the choir's conductor, as PrimaryCredits has it.
            "CONDUCTOR" or "CHORUSMASTER" => CreditRole.Conductor,
            // By the ending: "MixedChoir" and "ChamberOrchestra" are ensembles,
            // an "Orchestrator" or a "ChoirDirector" is a person.
            _ when word.EndsWith("ORCHESTRA", StringComparison.Ordinal)
                || word.EndsWith("ENSEMBLE", StringComparison.Ordinal)
                || word.EndsWith("CHOIR", StringComparison.Ordinal)
                || word.EndsWith("CHORUS", StringComparison.Ordinal) => CreditRole.Ensemble,
            "COMPOSER" or "LYRICIST" or "WRITER" or "SONGWRITER" or "LIBRETTIST" or "COMPOSERLYRICIST" => CreditRole.Writer,
            _ => null,
        };
    }
}
