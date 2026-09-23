namespace Fonoteca.Domain.Acquisition;

/// <summary>
/// Which of an artist's release groups count as missing from the library, and
/// which are noise.
/// </summary>
/// <remarks>
/// <b>The browse answers a different question from the one a person asks.</b>
/// MusicBrainz lists every release group credited to an artist — and for anyone
/// with a long career that is overwhelmingly not albums. A prolific artist's
/// browse comes back with the studio records somewhere inside a list of
/// greatest-hits packages assembled by licensees, live bootlegs, tribute
/// compilations they appear on for one track, remix EPs and karaoke editions.
/// Shown raw, "not in your library" is a list nobody can act on and the feature
/// is worse than nothing.
///
/// So the browse is stored whole and the cut is made here, at read time. That is
/// deliberate and it is this codebase's existing bargain, stated in
/// <c>CLAUDE.md</c> as <i>what is cached is answers, never rankings</i>: the
/// release groups are facts MusicBrainz stated, this is a rule about them, and a
/// rule that changes must not require re-asking the provider for every artist in
/// the catalogue at a turn each.
///
/// Everything here is pure — a description of a release group in, a decision
/// out. The caller has already established that no file in the library sits
/// under this group; the only question left is whether that absence is worth
/// printing.
/// </remarks>
public static class Discography
{
    /// <summary>
    /// True when not owning this release group is worth telling somebody about.
    /// </summary>
    /// <param name="group">
    /// What MusicBrainz says this release group is. The caller has already
    /// decided the library holds nothing under it.
    /// </param>
    /// <remarks>
    /// <b>The error to prefer is the one a person can see.</b> A studio album
    /// wrongly hidden is a gap nobody is ever offered and nothing on the screen
    /// says it was withheld; a compilation wrongly shown is one obviously silly
    /// row. That asymmetry is the same one <see cref="UpgradeScan"/> settles in
    /// its own words about <c>.m4a</c> — an unnecessary row costs a glance, a
    /// missing one costs the feature.
    /// </remarks>
    public static bool IsGap(ReleaseGroupFacts group)
    {
        // TODO(you): the rule. See the notes on ReleaseGroupFacts for exactly
        // what MusicBrainz puts in each field, and the remarks above for which
        // way to err.
        //
        // The questions worth deciding, roughly in order of how much they
        // change the list:
        //
        //   - PrimaryType. "Album" is the obvious keep. Is an EP a gap? A
        //     Single? Broadcast and Other are usually radio sessions and odds
        //     and ends. Null means MusicBrainz has not typed it at all, which
        //     is commoner on obscure artists than you would like — so refusing
        //     everything untyped quietly hides exactly the catalogue nobody
        //     else has curated.
        //
        //   - SecondaryTypes. This is where the noise lives: "Compilation",
        //     "Live", "Soundtrack", "Remix", "DJ-mix", "Demo", "Interview",
        //     "Mixtape/Street", "Audiobook", "Spokenword". A group can carry
        //     several at once. Note a record can be PrimaryType "Album" AND
        //     secondary "Compilation" — that is precisely the greatest-hits
        //     package, and it is the single biggest source of rows you do not
        //     want.
        //
        //   - Live is the genuinely contentious one. For most artists a live
        //     album is a real record they made on purpose; for a heavily
        //     bootlegged one it is fifty rows of the same tour.
        //
        //   - FirstReleaseYear is null on unreleased and announced records.
        //     Worth showing, or worth waiting until it has a date?
        //
        // Return true to show the row, false to hide it.
        //
        // What follows is a placeholder so the screen runs, NOT a considered
        // answer. Replace the body; the tests in DiscographyTests are written
        // against whatever you decide.
        if (group.SecondaryTypes.Any(Noise.Contains)) return false;

        return group.PrimaryType is null or "Album" or "EP";
    }

    /// <summary>
    /// Whether a record a shop named is still a gap once the catalogue is consulted.
    /// </summary>
    /// <param name="record">What the source said about it.</param>
    /// <param name="catalogued">
    /// Every record this artist is credited with, held or not. Not just the held
    /// ones: a shop's row that names the same album as a MusicBrainz row already
    /// on the shelf is not a second gap, and showing both is how this library
    /// came to list <i>Bach Concertos</i> beside
    /// <i>Johann Sebastian Bach : Violin Concertos &amp; Sonatas</i>.
    /// </param>
    /// <param name="barcodes">
    /// The UPCs of every release under those records, <b>already normalised
    /// through <see cref="Barcodes"/></b> — the caller normalises because it
    /// builds the set, and a set built literally would silently never match.
    /// <b>The only identifier here that both sides can be right about</b>, so it
    /// is checked before the titles and settles the case they cannot: a shop and
    /// a catalogue that name one record in two languages agree on nothing else.
    /// </param>
    /// <remarks>
    /// <b>Asked on every read, never written down.</b> Each clause is a rule
    /// about the row rather than a fact in it, and a rule's output stored beside
    /// the answers is wrong the day the rule changes — <c>CLAUDE.md</c>'s
    /// standing bargain. It is also what makes a shop's row disappear the moment
    /// somebody buys the record: the catalogue gains the album, this is asked
    /// again, and the row stops being a gap without anything having to go back
    /// and retract it.
    /// </remarks>
    public static bool IsGap(
        DiscoveredRecordFacts record,
        IReadOnlyCollection<CataloguedRecord> catalogued,
        IReadOnlySet<string> barcodes)
    {
        if (!IsWorthOffering(record.TrackCount)) return false;

        // **Normalised on both sides, never compared literally.** A shop sells
        // the 12-digit UPC and MusicBrainz holds the 13-digit EAN of it — the
        // same digits behind a zero — so a literal compare fails on exactly the
        // releases the barcode was reached for. `QobuzCovers` paid for that
        // lesson between these same two catalogues; see <see cref="Barcodes"/>.
        if (Catalogue.Barcodes.Normalise(record.Barcode) is { } upc
            && barcodes.Contains(upc))
        {
            return false;
        }

        // **The same qualifier strip the shelf folds by, applied here too.**
        // A shop sells "evermore" and "evermore (deluxe version)" as two
        // products. Compared raw, the plain row is recognised as one the
        // catalogue already names and drops out, the qualified one survives —
        // and then, being the only row left, it becomes the title printed on
        // the shelf. The shop half ends up offering a record you own, wearing
        // a name you have never seen. Stripping on both sides is the same
        // reading `Barcodes.Normalise` gives a UPC: fold what the two
        // catalogues merely spell differently before asking whether they agree.
        return !catalogued.Any(held => Catalogue.ReleaseTitleMatch.IsSameRecord(
            StripEdition(record.Title), record.Year, StripEdition(held.Title), held.Year));
    }

    /// <summary>
    /// The fewest tracks a discovered record may have before it is worth offering.
    /// </summary>
    /// <remarks>
    /// <b>A shop's artist page is not a discography, and without this the
    /// feature makes the problem it exists to solve worse.</b> Measured against
    /// this installation, one artist's <c>artist/get?extra=albums</c> reports
    /// <b>166</b> records — singles, EPs, one-track promos and compilations
    /// alongside the albums. A shop states no type at all, so nothing else in
    /// the row separates those and the shelf a person asked to be made shorter
    /// grows by two orders of magnitude.
    ///
    /// Four is a knob and not a principle, in <c>ArtistNameMatch.CatalogueMargin</c>'s
    /// sense: it keeps EPs, which are records somebody made on purpose, and drops
    /// the one-to-three-track rows that are overwhelmingly singles and promos.
    /// An unknown count is <b>kept</b> rather than dropped — a silence is not a
    /// small number, the same reading <c>Dwarfs</c> gives an absent album count —
    /// which errs towards a visible extra row over a hidden gap, the direction
    /// <see cref="Catalogue.ReleaseTitleMatch"/> states.
    ///
    /// <b>Read here rather than at the moment a shop is asked</b>, which is why
    /// it is a knob at all: the rows are stored whole, so moving this number
    /// re-cuts the shelf on the next page load instead of re-asking every shop
    /// about every artist. Applied on the way in it would be a threshold frozen
    /// at whatever it was the day each artist was first browsed.
    /// </remarks>
    public const int MinimumTracksToOffer = 4;

    /// <summary>
    /// Whether a record a shop named is substantial enough to put on a shelf.
    /// </summary>
    /// <param name="trackCount">
    /// What the source said, or null where it did not say — which is kept, not
    /// read as zero. See <see cref="MinimumTracksToOffer"/>.
    /// </param>
    public static bool IsWorthOffering(int? trackCount) =>
        trackCount is not { } tracks || tracks >= MinimumTracksToOffer;

    /// <summary>
    /// Whether a record is one the artist put out after somebody started
    /// watching them — the rule that decides what lands on the acquire shelf by
    /// itself.
    /// </summary>
    /// <param name="followedUtc">
    /// <c>Artist.FollowedUtc</c>. Null is "nobody is watching", which monitors
    /// nothing.
    /// </param>
    /// <param name="year">
    /// The record's first release year, from whichever source named it. Null is
    /// an undated or unreleased record and is <b>not</b> a new release: an
    /// announced record with no date yet would otherwise be wanted on every
    /// source that lists it early.
    /// </param>
    /// <remarks>
    /// <b>A fact about the record, not about our worklist, and that is the whole
    /// point.</b> The obvious rule — "wanted if we had already browsed this
    /// artist when it appeared" — reads as a statement about releases and is
    /// really a statement about the order our own passes happened to run in. It
    /// was wrong the first time a second source was asked about an artist the
    /// first had already answered for: nothing had been released, but everything
    /// was new to the asker. <c>Artist.FollowedUtc</c> records the measured cost.
    ///
    /// Asked when a row is <i>minted</i> and never again, because the answer is a
    /// person's from then on — <c>Monitored</c> is the one column on these rows
    /// nothing may recompute, the same standing this codebase gives
    /// <c>Artist.Followed</c> itself. That is the one place the
    /// <i>what is cached is answers, never rankings</i> bargain is deliberately
    /// not taken: re-deciding on every read would overwrite what somebody
    /// unmarked by hand.
    ///
    /// <b>Compared by year, because a year is all either source states.</b>
    /// MusicBrainz gives <c>FirstReleaseYear</c> and a shop gives a year off a
    /// release date it may have rewritten for a reissue, so a finer comparison
    /// would be a precision neither of them supports. The visible consequence is
    /// that following somebody in December counts that whole year as new. That
    /// errs towards an extra row on a shelf a person is already reading, which is
    /// the direction <see cref="Catalogue.ReleaseTitleMatch"/> and
    /// <see cref="MinimumTracksToOffer"/> both take.
    /// </remarks>
    public static bool IsNewRelease(DateTimeOffset? followedUtc, int? year) =>
        followedUtc is { } since && year is { } released && released >= since.UtcDateTime.Year;

    /// <summary>
    /// Secondary types that are somebody else's record rather than a gap in
    /// yours. Placeholder — see <see cref="IsGap"/>.
    /// </summary>
    private static readonly HashSet<string> Noise = new(StringComparer.OrdinalIgnoreCase)
    {
        "Compilation", "Live", "Remix", "DJ-mix", "Demo", "Interview", "Audiobook", "Spokenword",
    };

    /// <summary>
    /// Rows that are editions of one record, folded to one row and a count.
    /// </summary>
    /// <typeparam name="T">The caller's row; this rule never looks inside it.</typeparam>
    /// <param name="rows">The shelf, in the order the caller wants it read.</param>
    /// <param name="title">The printed title.</param>
    /// <param name="kind">
    /// What separates two records that share a title — the primary type for a
    /// release group, null where the source states none. An album and the
    /// single named after it are two records, and folding them is the one way
    /// this rule could take something away.
    /// </param>
    /// <param name="year">First release year, or null.</param>
    /// <param name="wanted">Whether somebody has asked to be told about this row.</param>
    /// <remarks>
    /// <b>This claims a grouping, never an identity.</b> Measured on this
    /// library, one artist's shelf carries <b>132</b> rows titled "The Four
    /// Seasons" and another 92 titled "Goldberg Variations" — 20,726 surplus
    /// rows across 8,649 titles. They are not duplicates in the data: they are
    /// different performances, correctly stored, which MusicBrainz bills to the
    /// composer because that is how it bills a classical recording. 123,026 of
    /// 142,265 groups carry exactly one credit and no performer, so on screen
    /// the 132 differ only by year and there is nothing to tell them apart by.
    ///
    /// <b>Why this and not a better matcher.</b> The honest key between a shop's
    /// catalogue and MusicBrainz's is the barcode, and it was measured at 3
    /// matches in 24 on the artist this work started from: shops sell the
    /// digital edition, which carries a UPC MusicBrainz does not hold. Loosening
    /// titles to containment is the other tempting move and
    /// <see cref="Catalogue.ReleaseTitleMatch"/> refuses it in its own words,
    /// because it folds <i>Greatest Hits</i> into <i>Greatest Hits, Volume 2</i>.
    /// So this groups what is already equal and asserts nothing new.
    ///
    /// <b>What it cannot do.</b> Same artist, same title once folded, same kind,
    /// or it does not group — so two different records can never become one.
    /// Measured against the followed artists here, who are not classical: Buddy
    /// Guy 159 rows to 152, Mark Knopfler 150 to 145, and eighteen of
    /// twenty-eight lose nothing at all. Every row it removes is a genuine
    /// same-title reissue.
    ///
    /// <b>The representative is a monitored row where there is one</b>, because
    /// <c>Monitored</c> is one of the facts nothing can recompute and the button
    /// on the folded row has to act on the row that carries it. Then a row whose
    /// title carries no qualifier, so a shelf offering <c>Thriller</c> does not
    /// print itself as <c>Thriller (Demo Version)</c>. Then the earliest — the
    /// original release rather than whichever reissue sorted first.
    ///
    /// <b>A group holding two monitored rows is not folded at all.</b> Folding
    /// it would leave one of them reachable from the artist page and the other
    /// standing, invisible, still on the acquire shelf — a person's standing
    /// intent hidden by a display rule, which is the one thing this may not do.
    /// Two deliberate clicks on two editions of one record is rare; silently
    /// swallowing one of them is not recoverable.
    ///
    /// <b>Folding changes a row's year, so the caller must sort afterwards.</b>
    /// An earlier draft folded in place and left the shelf reading 2014, 2010,
    /// 2022, 2008 — the group kept its first row's position and printed a
    /// different row's date.
    /// </remarks>
    public static IReadOnlyList<Edition<T>> Collapse<T>(
        IEnumerable<T> rows,
        Func<T, string> title,
        Func<T, string?> kind,
        Func<T, int?> year,
        Func<T, bool> wanted)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(title);
        ArgumentNullException.ThrowIfNull(kind);
        ArgumentNullException.ThrowIfNull(year);
        ArgumentNullException.ThrowIfNull(wanted);

        return
        [
            .. rows
                // Ordinal on the folded title and the kind together. A title
                // that folds to nothing — "!!!" is a band, and every classical
                // shelf has a row of punctuation somewhere — keeps its own
                // identity through the fallback, or every such row on an
                // artist's shelf would fold into one.
                .GroupBy(
                    row => (
                        Title: EditionTitle(title(row)) is { Length: > 0 } folded
                            ? folded
                            : title(row).Trim(),
                        Kind: kind(row) ?? string.Empty),
                    ValueTupleComparer)
                .SelectMany(group =>
                    // Two people's-answers in one group: leave every row alone.
                    group.Count(wanted) > 1
                        ? group.Select(row => new Edition<T>(row, 1))
                        : [new Edition<T>(
                            group
                                .OrderByDescending(wanted)
                                .ThenBy(row => StripEdition(title(row)).Length)
                                .ThenBy(row => year(row) ?? int.MaxValue)
                                .First(),
                            group.Count())]),
        ];
    }

    /// <summary>
    /// A title folded to what two printings of one record share.
    /// </summary>
    /// <remarks>
    /// <see cref="Catalogue.ArtistNameMatch.Normalise"/> does the folding, for
    /// the reason <see cref="Catalogue.ReleaseTitleMatch"/> gives for reusing
    /// it: case, accents, punctuation and spacing are exactly what two
    /// catalogues legitimately disagree about on a title, and a second table
    /// here is how one of them stops folding "ř".
    ///
    /// <b>A trailing edition qualifier goes first</b>, because a shop sells
    /// "Brothers in Arms" and "Brothers in Arms (Remastered)" as two products
    /// and they are one record. The list is deliberately short and every word
    /// in it describes a <i>reissue of the same music</i>: "(Live)" and
    /// "(Remix)" are absent because those are different recordings, which is
    /// the same line <see cref="Noise"/> draws.
    /// </remarks>
    public static string EditionTitle(string? title) =>
        Catalogue.ArtistNameMatch.Normalise(StripEdition(title));

    /// <summary>
    /// A title with a trailing reissue qualifier removed, otherwise unchanged.
    /// </summary>
    /// <remarks>
    /// <b>Every word inside the bracket must be one this rule knows, and one of
    /// them must say "reissue".</b> The first version of this asked only
    /// whether the bracket <i>contained</i> an edition word, and that folded
    /// <c>1989 (Taylor's Version)</c> into <c>1989</c> — a re-recording, a
    /// separate release group, separately marketed, and the shelf would have
    /// offered the original while never saying the other exists. It did the
    /// same to <c>Thriller (Demo Version)</c>, <c>Unholy (live version)</c>,
    /// <c>Pétrouchka (1947 Version)</c> against the 1911 score, and
    /// <c>Das Lied von der Erde (piano version)</c>. One word, "version", and
    /// it broke exactly the popular records this was promised not to touch.
    ///
    /// <b>It was also nearly worthless where it was risky.</b> Measured across
    /// the whole missing shelf: the strip accounts for 70 rows of a 10,744-row
    /// reduction — 0.65%. Plain title equality does the rest. On a <i>shop</i>
    /// shelf it earns its place, where one album is sold as eight products
    /// (Linkin Park's <c>Minutes to Midnight</c>, 8 rows to 1), which is why it
    /// is kept at all rather than deleted.
    ///
    /// So the test is a whitelist and errs at not folding. A bracket holding
    /// one unknown word is left alone, and the two rows stay two.
    /// </remarks>
    public static string StripEdition(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return string.Empty;

        var trimmed = title.Trim();

        // One qualifier, not a loop: "(Deluxe Edition) (Remastered)" is rare
        // enough not to be worth the risk of eating a real title one bracket
        // at a time. `> 0` rather than `>= 0`, so a title that is *entirely* a
        // qualifier keeps itself and does not fold to nothing.
        if (trimmed[^1] != ')' || trimmed.LastIndexOf('(') is not ( > 0 and var open))
        {
            return trimmed;
        }

        var words = trimmed[(open + 1)..^1]
            .Split(NotAWord, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (words.Length == 0) return trimmed;

        var saysReissue = false;

        foreach (var word in words)
        {
            if (ReissueWords.Contains(word)) saysReissue = true;
            else if (!QualifierFiller.Contains(word) && !IsNumber(word)) return trimmed;
        }

        return saysReissue ? trimmed[..open].TrimEnd() : trimmed;
    }

    /// <summary>What separates two words inside a bracket.</summary>
    private static readonly char[] NotAWord =
        [' ', ',', '/', '-', '–', '—', ':', ';', '.', '\'', '’', '+', '&', '"'];

    /// <summary>
    /// Words that say a bracket is a reissue of the same music.
    /// </summary>
    /// <remarks>
    /// Not "live", "demo", "remix", "acoustic", "instrumental", "mono" or a
    /// language — each of those names a <i>different recording</i>, and folding
    /// one into the record it sits beside hides a gap nobody is ever told
    /// about. That is the error this whole file is written to avoid, and it is
    /// the same line <see cref="Noise"/> draws.
    /// </remarks>
    /// <remarks>
    /// <b>"version" and "edition" are not in here, and that is the point.</b>
    /// Neither says reissue on its own: <c>Pétrouchka (1947 Version)</c> is a
    /// different score from the 1911 one, <c>Midnights (3am Edition)</c> is a
    /// different record, and both would fold on a rule that accepted the bare
    /// word beside a year. They are filler — allowed to sit next to a word that
    /// does mean reissue, never sufficient by themselves.
    /// </remarks>
    private static readonly HashSet<string> ReissueWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "remaster", "remastered", "remastering", "remasters",
        "deluxe", "expanded", "anniversary", "reissue", "bonus",
    };

    /// <summary>
    /// Words allowed inside a qualifier that do not themselves make it one.
    /// </summary>
    /// <remarks>
    /// "(Bonus Track Version)" needs "track"; "(The Remastered Edition)" needs
    /// "the". Alone they say nothing — a bracket of nothing but these is left
    /// where it is, because <see cref="ReissueWords"/> must also appear.
    /// </remarks>
    private static readonly HashSet<string> QualifierFiller = new(StringComparer.OrdinalIgnoreCase)
    {
        "edition", "editions", "version", "versions",
        "track", "tracks", "disc", "discs", "disk", "cd", "lp",
        "the", "and", "a", "of", "vol", "volume",
    };

    /// <summary>A year or an ordinal — "2007", "20th" — which qualify nothing on their own.</summary>
    private static bool IsNumber(string word)
    {
        var digits = word.AsSpan();

        foreach (var suffix in (ReadOnlySpan<string>)["st", "nd", "rd", "th"])
        {
            if (digits.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                digits = digits[..^2];
                break;
            }
        }

        return digits.Length > 0 && !digits.ContainsAnyExcept(Digits);
    }

    private static readonly System.Buffers.SearchValues<char> Digits =
        System.Buffers.SearchValues.Create("0123456789");

    private static readonly IEqualityComparer<(string Title, string Kind)> ValueTupleComparer =
        new EditionKeyComparer();

    private sealed class EditionKeyComparer : IEqualityComparer<(string Title, string Kind)>
    {
        public bool Equals((string Title, string Kind) left, (string Title, string Kind) right) =>
            string.Equals(left.Title, right.Title, StringComparison.Ordinal)
            && string.Equals(left.Kind, right.Kind, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((string Title, string Kind) key) =>
            HashCode.Combine(
                StringComparer.Ordinal.GetHashCode(key.Title),
                StringComparer.OrdinalIgnoreCase.GetHashCode(key.Kind));
    }
}

/// <summary>
/// One record on a shelf, and how many rows the source had for it.
/// </summary>
/// <param name="Row">The row to print — see <see cref="Discography.Collapse"/>.</param>
/// <param name="Count">
/// Rows folded into it, 1 where nothing was folded. Printed so the shelf can
/// say "132 recordings" rather than silently dropping 131 of them: the rows are
/// real and somebody looking for a particular performance has to be told they
/// exist.
/// </param>
public readonly record struct Edition<T>(T Row, int Count);

/// <summary>
/// What MusicBrainz states about one release group, as the browse returns it.
/// </summary>
/// <param name="Title">The group's title. Never empty.</param>
/// <param name="PrimaryType">
/// <c>Album</c>, <c>EP</c>, <c>Single</c>, <c>Broadcast</c>, <c>Other</c> — or
/// null, which means nobody has typed it rather than that it is none of them.
/// </param>
/// <param name="SecondaryTypes">
/// Zero or more of <c>Compilation</c>, <c>Live</c>, <c>Soundtrack</c>,
/// <c>Remix</c>, <c>DJ-mix</c>, <c>Demo</c> and friends. Independent of
/// <paramref name="PrimaryType"/> — an anthology is typically both
/// <c>Album</c> and <c>Compilation</c>.
/// </param>
/// <param name="FirstReleaseYear">
/// The year of the earliest release in the group, or null when MusicBrainz
/// holds no date — an unreleased or newly announced record.
/// </param>
public readonly record struct ReleaseGroupFacts(
    string Title,
    string? PrimaryType,
    IReadOnlyList<string> SecondaryTypes,
    int? FirstReleaseYear);

/// <summary>What a discovery source stated about one record.</summary>
/// <param name="Title">As the source prints it.</param>
/// <param name="Year">Its date, or null where the source gave none.</param>
/// <param name="Barcode">The UPC, or null.</param>
/// <param name="TrackCount">
/// How many tracks the source says it has, or null where it did not say — which
/// is kept rather than read as zero. See <see cref="Discography.MinimumTracksToOffer"/>.
/// </param>
public readonly record struct DiscoveredRecordFacts(
    string Title,
    int? Year,
    string? Barcode,
    int? TrackCount);

/// <summary>One record the catalogue already knows this artist made.</summary>
/// <remarks>
/// A title and a year, because that is all a shop's row and a catalogue's row
/// reliably share — see <see cref="Catalogue.ReleaseTitleMatch"/> for what that
/// costs and why it errs towards showing a duplicate rather than hiding a gap.
/// Held or not: the question here is "does the catalogue already name this
/// record", not "do you own it".
/// </remarks>
public readonly record struct CataloguedRecord(string Title, int? Year);
