using System.Text;

namespace Fonoteca.Domain.Catalogue;

/// <summary>
/// Deciding whether a search result naming an artist <i>is</i> that artist.
/// </summary>
/// <remarks>
/// <b>Two picture sources are keyed on a name, and a name is not an
/// identifier.</b> Qobuz and Deezer have never heard of MusicBrainz, so they
/// are searched rather than looked up and every answer is a decision. Wikidata
/// and TheAudioDB take the MBID and need none of this — which is exactly why
/// they are the sources that reach an artist spelled in Cyrillic.
///
/// It lives in the domain because it is the same rule twice and the two callers
/// are in the providers project, where a shared rule would otherwise be one
/// class reaching into another's internals. Everything here is pure: strings
/// in, a choice out.
///
/// <b>The failure it exists to prevent is a stranger's face.</b> That is worse
/// than the mediocre photograph the preferred sources exist to replace and,
/// unlike the mediocre one, invisible — nothing on the screen says the picture
/// is of somebody else.
/// </remarks>
public static class ArtistNameMatch
{
    /// <summary>
    /// How far ahead on catalogue size a namesake must be before it stops being
    /// a tie.
    /// </summary>
    /// <remarks>
    /// A factor rather than a difference, because the quantity is a catalogue
    /// size and those differ by orders of magnitude rather than by counts: the
    /// case this exists for is Qobuz's two rows spelled <c>AC/DC</c>, at 500
    /// albums and 4. Two is the smallest factor that is plainly not a coin flip
    /// and it leaves the genuine one — two obscure namesakes with a handful of
    /// records each — refused exactly as before.
    ///
    /// It is a knob because there is no principled value: raise it and more
    /// famous artists keep the fallback photograph, lower it and a prolific
    /// stranger starts winning against the person a small catalogue means.
    /// </remarks>
    public const int CatalogueMargin = 2;

    /// <summary>
    /// The result that is this artist, out of what a search returned.
    /// </summary>
    /// <remarks>
    /// <b>An exact match on the normalised name, and nothing softer.</b> Both
    /// services rank by relevance and their top hit is routinely a different
    /// artist: measured over forty of this library's names, Qobuz answer
    /// "Daniel de Borah" with a Barenboim compilation credited to six people and
    /// "The Sy Oliver Choir" with a Louis Armstrong record that mentions them.
    /// Taking the first result puts a stranger on an artist page.
    ///
    /// The exact match is <b>not</b> reliably first, which is why the whole list
    /// is scanned: Qobuz return "Tom Petty" behind "Tom Petty &amp; The
    /// Heartbreakers".
    ///
    /// <b>Candidates carrying no picture are dropped before anything is
    /// counted</b>, not treated as rivals — they cannot be the answer either
    /// way, and counting them would refuse the commonest shape there is: a
    /// well-known artist beside a homonym the service holds a row for and no
    /// photograph of.
    ///
    /// <b>Where several survive, catalogue size decides and only when it
    /// plainly does.</b> Taking the first would be picking by a ranking of
    /// search results, which says nothing about which person this is; the
    /// catalogue count is the one field in a search result that describes the
    /// artist instead. See <see cref="Dwarfs"/> for what "plainly" means and why
    /// an unknown count cannot lose.
    ///
    /// <b>Two artists with one name and comparable catalogues is the failure
    /// this cannot see</b>, and it is worth stating rather than leaving to be
    /// discovered. Sampled over 25 artists, one was wrong this way: the
    /// catalogue's <i>Jan Jansen</i> is a Dutch clarinetist and Qobuz's is
    /// somebody else entirely, spelled identically. Nothing on the artist's row
    /// helps — MusicBrainz's disambiguation is prose neither service has a
    /// counterpart for — and the only real discriminator is whether they made a
    /// record this library holds, which is another request per artist. Left
    /// undone deliberately.
    /// </remarks>
    public static ArtistMatch Pick(string wanted, IReadOnlyList<ArtistCandidate> found)
    {
        ArgumentNullException.ThrowIfNull(found);

        if (string.IsNullOrWhiteSpace(wanted)) return default;

        var target = Normalise(wanted);

        var rivals = found
            .Where(candidate => !string.IsNullOrWhiteSpace(candidate.PictureUrl))
            .Where(candidate => string.Equals(
                Normalise(candidate.Name), target, StringComparison.Ordinal))
            .OrderByDescending(candidate => candidate.Catalogue)
            .ToList();

        if (rivals.Count == 0) return default;

        if (rivals.Count > 1 && !Dwarfs(rivals[0].Catalogue, rivals[1].Catalogue))
        {
            return new ArtistMatch(Chosen: null, Rivals: rivals.Count);
        }

        return new ArtistMatch(rivals[0], rivals.Count);
    }

    /// <summary>
    /// Whether one catalogue is so much larger than another that they cannot be
    /// two readings of the same question.
    /// </summary>
    /// <remarks>
    /// <b>An unknown runner-up is not a small one.</b> A count deserialises to
    /// zero when the service omits it, so comparing against the raw number makes
    /// <c>0</c> against <c>0</c> pass — the leader is not less than nothing —
    /// and the coin flip this rule exists to refuse is taken by whichever row
    /// the service's relevance ranking put first. Reversing the two results then
    /// produces a different face for the same artist, which is the one failure
    /// mode <c>WikidataPortraits.Parse</c> writes a stable tie-break to avoid.
    ///
    /// A one-album row beating a zero-album row is refused for the same reason
    /// rather than because one is small: nothing was measured, so there is
    /// nothing to be ahead of.
    /// </remarks>
    public static bool Dwarfs(int leader, int rival) =>
        rival > 0 && leader >= rival * CatalogueMargin;

    /// <summary>
    /// The comparable form of a name.
    /// </summary>
    /// <remarks>
    /// Case, accents, punctuation and spacing all go, because they are all
    /// places two catalogues legitimately disagree about one artist:
    /// MusicBrainz writes "BBC National Orchestra of Wales" and Qobuz writes
    /// "BBC National Orchestra Of Wales", and neither is wrong. What survives is
    /// letters and digits, which is strict enough that the measured false
    /// matches are all refused and loose enough that a capital O does not cost
    /// an orchestra its photograph.
    ///
    /// <b>The accents are folded by a table, and the obvious way does not work
    /// in this application.</b> The one-liner for this is
    /// <c>Normalize(FormKD)</c> followed by dropping the combining marks — and
    /// <c>Directory.Build.props</c> sets <c>InvariantGlobalization</c>, under
    /// which <c>String.Normalize</c> <i>returns the string unchanged</i>. It
    /// does not throw and it is not documented at the call site, so the folding
    /// silently does nothing and "Liège" simply stops matching "Liege". Measured
    /// in a scratch console with the same setting: <c>"Liège".Normalize(FormKD)</c>
    /// is five characters in and five characters out, the third still U+00E8.
    /// Casing is unaffected — <c>ToLowerInvariant</c> maps À and Ř correctly in
    /// the same mode — so only the decomposition had to be replaced.
    ///
    /// The table is Latin-1 and Latin Extended-A, which is what artist names on
    /// this library actually contain: of 326, twenty-three carry a non-ASCII
    /// character and nine of those are Latin with diacritics — Dvořák, Dueñas,
    /// Àlainn, Liège. The rest are Cyrillic and Japanese, which no folding can
    /// help with and which both name-keyed services index under Latin names
    /// anyway; they fall back to the MBID-keyed sources, which is the right
    /// outcome and the reason there are two kinds of source.
    ///
    /// Ligatures are deliberately absent. "ß", "æ" and "œ" fold to two letters
    /// rather than one and would need a different shape of table for a case this
    /// library does not contain; unfolded they simply fail to match, which is
    /// the safe direction.
    /// </remarks>
    public static string Normalise(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        var letters = new StringBuilder(name.Length);

        foreach (var character in name)
        {
            var lower = char.ToLowerInvariant(character);

            if (!char.IsLetterOrDigit(lower)) continue;

            var accented = Accented.IndexOf(lower, StringComparison.Ordinal);

            letters.Append(accented < 0 ? lower : Plain[accented]);
        }

        return letters.ToString();
    }

    /// <summary>Lower-case accented letters, paired with <see cref="Plain"/>.</summary>
    /// <remarks>
    /// The two strings are read by index, so a table that slipped by one would
    /// fold every accent onto its neighbour's letter — "dvorbk" rather than
    /// "dvorak" — and would go on matching every unaccented name, which is most
    /// of them. <c>TheAccentTableFoldsEachLetterOntoItsOwnPlainForm</c> is the
    /// guard, and it is a sample rather than a proof: it covers the letters this
    /// library's artists actually carry.
    /// </remarks>
    private const string Accented =
        "àáâãäåçèéêëìíîïðñòóôõöøùúûüýÿ"
        + "āăąćĉċčďđēĕėęěĝğġģĥħĩīĭįıĵķĺļľłńņňōŏőŕŗřśŝşšţťŧũūŭůűųŵŷźżž";

    private const string Plain =
        "aaaaaaceeeeiiiidnoooooouuuuyy"
        + "aaaccccddeeeeegggghhiiiiijkllllnnnooorrrsssstttuuuuuuwyzzz";
}

/// <summary>One row a name search returned, reduced to what deciding needs.</summary>
/// <param name="Name">As the service spells it. The whole of the decision is made on this.</param>
/// <param name="Catalogue">
/// How many albums the service carries by them. <b>Zero means the field was
/// absent, not that they have none</b> — an unknown rather than a small number,
/// which is why <see cref="ArtistNameMatch.Dwarfs"/> refuses to compare against
/// one.
/// </param>
/// <param name="PictureUrl">
/// Their photograph, or null. Null is common and is not a failure: these
/// services carry pictures for the artists they sell records by.
/// </param>
public readonly record struct ArtistCandidate(string Name, int Catalogue, string? PictureUrl);

/// <summary>What a search came to.</summary>
/// <param name="Chosen">The artist, or null when nothing matched or the match was a tie.</param>
/// <param name="Rivals">
/// How many pictured results carried the name exactly. Above one with no
/// <paramref name="Chosen"/> is the tie, and worth a log line: the consequence —
/// this artist keeps a lesser picture — is otherwise invisible.
/// </param>
public readonly record struct ArtistMatch(ArtistCandidate? Chosen, int Rivals);
