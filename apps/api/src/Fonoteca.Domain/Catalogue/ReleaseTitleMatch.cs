namespace Fonoteca.Domain.Catalogue;

/// <summary>
/// Deciding whether a record a shop names is one the catalogue already holds.
/// </summary>
/// <remarks>
/// <b>This is the weakest joint in release discovery and it is weak for a
/// structural reason, not for want of trying.</b> ADR 0011 keys a
/// provider-sourced release on its UPC, and that works because
/// <c>Release.Barcode</c> exists. The discography shelf is not built from
/// releases — it is built from <c>ReleaseGroup</c>, the album as an idea, which
/// has no barcode and cannot have one: a group is precisely the thing that spans
/// every pressing, and every pressing has a different UPC. So at this level the
/// only comparable facts are a title and a year.
///
/// <b>What that costs, stated plainly.</b> A remaster, a deluxe edition and an
/// anniversary reissue are one group in MusicBrainz and three rows in a shop,
/// and this rule will let the second and third through as records the library
/// does not hold. The shelf then offers somebody an album they own in a form
/// they may not want — a visible, dismissible error. The opposite mistake,
/// folding a genuinely different record into an existing group because the
/// titles rhyme, hides a real gap and nothing on the screen says so. This rule
/// is therefore deliberately reluctant to declare a match: where it is unsure it
/// says no, and the cost is a tile somebody glances at.
///
/// That is the same asymmetry <see cref="Acquisition.Discography"/> settles in
/// its own words, and the same one <see cref="ArtistNameMatch"/> settles in the
/// opposite direction — there, a wrong answer is a stranger's face on an artist
/// page, which nothing reveals, so that rule refuses instead. The direction is
/// not a style; it follows from which error a person can see.
///
/// Everything here is pure: two descriptions in, a verdict out.
/// </remarks>
public static class ReleaseTitleMatch
{
    /// <summary>
    /// How many years apart two datings of one record may be.
    /// </summary>
    /// <remarks>
    /// Not zero, and the reason is measured elsewhere in this codebase:
    /// <c>CLAUDE.md</c> records a folder named <c>(2009)</c> holding a release
    /// MusicBrainz dates to 2010. Shops date by their own first availability,
    /// which for a catalogue reissue is neither the recording nor the original
    /// release. One year absorbs that without letting a 1973 album match its 1998
    /// live namesake.
    /// </remarks>
    public const int YearSlack = 1;

    /// <summary>
    /// Whether these are the same record, as far as a title and a year can say.
    /// </summary>
    /// <param name="discovered">What the shop printed.</param>
    /// <param name="discoveredYear">Its date, or null where the shop gives none.</param>
    /// <param name="held">The title the catalogue holds.</param>
    /// <param name="heldYear">The catalogue's first release year, or null.</param>
    /// <remarks>
    /// <b>The titles must be equal once normalised, and nothing softer.</b> No
    /// prefix matching, no edit distance, no "one contains the other" — the last
    /// is the tempting one and it is what would fold <i>Greatest Hits</i> into
    /// <i>Greatest Hits, Volume 2</i>, and <i>Live</i> into almost everything.
    ///
    /// <b>Years narrow a match, they never make one.</b> Two records with the
    /// same normalised title and incompatible years are not the same record;
    /// two with the same title and no usable year on either side are taken as
    /// the same, because a title collision within one artist's catalogue is
    /// overwhelmingly the same album and the alternative is a duplicate row on
    /// every undated back-catalogue title there is.
    ///
    /// An unknown year on <i>one</i> side is not evidence of difference — the
    /// shop simply did not say — so it does not veto. That is the same reading
    /// <see cref="ArtistNameMatch.Dwarfs"/> gives an absent album count: an
    /// unknown is not a small number.
    /// </remarks>
    public static bool IsSameRecord(
        string discovered,
        int? discoveredYear,
        string held,
        int? heldYear)
    {
        if (discovered is null || held is null) return false;

        // The artist normaliser, deliberately reused rather than copied. It folds
        // case, accents, punctuation and spacing — which is exactly the set of
        // things two catalogues legitimately disagree about on a title too:
        // "Sgt. Pepper's" against "Sgt Peppers", "Blues Deluxe" against "Blues
        // DeLuxe". A second table here is how one of them stops folding "ř".
        var left = ArtistNameMatch.Normalise(discovered);
        var right = ArtistNameMatch.Normalise(held);

        // **Emptiness is checked after folding, not before, and that is the
        // whole point of checking it here.** The normaliser keeps letters and
        // digits and drops everything else, so a title made entirely of
        // punctuation folds to nothing — and `!!!` is a real band, not a corner
        // case somebody invented. Guarding the raw string instead lets every
        // punctuation-only title compare equal to every other, which is the
        // empty-title failure arriving by the one route the obvious guard does
        // not cover.
        if (left.Length == 0 || right.Length == 0) return false;

        if (!string.Equals(left, right, StringComparison.Ordinal)) return false;

        return YearsAgree(discoveredYear, heldYear);
    }

    /// <summary>
    /// Whether two datings of one title can describe the same record.
    /// </summary>
    /// <remarks>
    /// True when either side is unknown, because an absent date is a silence
    /// rather than a contradiction — and on a shop's back catalogue it is
    /// routine. Where both are known they must sit within
    /// <see cref="YearSlack"/>, which is what keeps a 1973 studio album from
    /// absorbing the 1998 live record of the same name.
    /// </remarks>
    public static bool YearsAgree(int? left, int? right) =>
        left is not { } a || right is not { } b || Math.Abs(a - b) <= YearSlack;
}
