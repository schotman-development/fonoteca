using Fonoteca.Domain.Acquisition;
using Fonoteca.Domain.Catalogue;

namespace Fonoteca.Domain.Tests;

/// <summary>
/// Which of a followed artist's records count as missing from the library.
/// </summary>
/// <remarks>
/// <b>Two halves, and only the first is settled.</b> <see cref="Discography.IsGap"/>
/// carries its own <c>TODO</c>: the body is a placeholder so the shelf runs, and
/// the questions it leaves open — EP, Single, Live, untyped, undated — are
/// decisions about somebody's library rather than facts about MusicBrainz.
///
/// So the cases below are split. <i>What the rule is for</i> is pinned outright:
/// an anthology is not a gap, an album is. <i>What is still open</i> asserts
/// today's placeholder answer and says so in as many words — so changing
/// <c>IsGap</c> fails a test that names the decision being made, rather than
/// passing silently and moving the shelf underneath somebody.
///
/// The asymmetry the rule is written against, in its own words: a studio album
/// wrongly hidden is a gap nobody is ever offered and nothing on the screen says
/// it was withheld; a compilation wrongly shown is one obviously silly row.
/// </remarks>
public sealed class DiscographyTests
{
    // ---------------------------------------------------------------------
    // Settled. These are what the rule exists to do.
    // ---------------------------------------------------------------------

    [Fact]
    public void AnAlbumIsAGap()
    {
        // The whole point of the shelf. If this ever goes false the feature has
        // no content at all.
        Assert.True(Discography.IsGap(Group("Tres Hombres", "Album")));
    }

    [Fact]
    public void AGreatestHitsPackageIsNotAGap()
    {
        // The single biggest source of rows nobody wants, and the shape that
        // makes it worth testing: primary type "Album" AND secondary
        // "Compilation" together. A rule reading only the primary type keeps
        // every anthology a licensee ever assembled.
        Assert.False(Discography.IsGap(Group("The Very Best Of", "Album", "Compilation")));
    }

    [Theory]
    [InlineData("Compilation")]
    [InlineData("Remix")]
    [InlineData("DJ-mix")]
    [InlineData("Demo")]
    [InlineData("Interview")]
    [InlineData("Audiobook")]
    [InlineData("Spokenword")]
    public void SomebodyElsesRecordIsNotAGapInYours(string secondary)
    {
        // Each of these is a record that exists because of the artist rather
        // than one they sat down and made. Named individually so that dropping
        // one from the set is a failure rather than a quietly longer shelf.
        Assert.False(Discography.IsGap(Group("Some Record", "Album", secondary)));
    }

    [Fact]
    public void OneNoisySecondaryTypeIsEnoughEvenAmongSeveral()
    {
        // A group carries several at once, so the test is "any", not "only".
        // A live compilation is both, and it is not a gap on either count.
        Assert.False(Discography.IsGap(Group("Live Anthology", "Album", "Live", "Compilation")));
    }

    [Fact]
    public void TheTitleIsNotEvidence()
    {
        // MusicBrainz types these; a title does not. A studio album called
        // "Live at Leeds" is a real record, and an anthology called "Album" is
        // still an anthology — reading the words is how a rule starts guessing.
        Assert.True(Discography.IsGap(Group("Live at Leeds", "Album")));
        Assert.False(Discography.IsGap(Group("Album", "Album", "Compilation")));
    }

    [Fact]
    public void TheAnswerDoesNotMoveBetweenTwoIdenticalGroups()
    {
        // Pure, and the shelf's ordering depends on it: the same record read
        // twice in one page load must not change sides.
        Assert.Equal(
            Discography.IsGap(Dated("Eliminator", "Album", 1983)),
            Discography.IsGap(Dated("Eliminator", "Album", 1983)));
    }

    // ---------------------------------------------------------------------
    // Open. Each of these asserts the PLACEHOLDER's answer, not a considered
    // one. Decide it in Discography.IsGap and change the expectation here; the
    // comment on each is the argument, not the conclusion.
    // ---------------------------------------------------------------------

    [Fact]
    public void PlaceholderDecision_AnEpIsAGap()
    {
        // For: an EP is a record they made and chose to release, and on artists
        // with short catalogues it is most of what there is to buy.
        // Against: on a prolific artist EPs outnumber albums and are mostly
        // singles with remixes attached.
        Assert.True(Discography.IsGap(Group("Some EP", "EP")));
    }

    [Fact]
    public void PlaceholderDecision_ASingleIsNotAGap()
    {
        // For hiding: a discography's singles are overwhelmingly tracks already
        // on an album you own, so they read as gaps that are not gaps.
        // Against: for artists who never made albums, singles are the catalogue.
        Assert.False(Discography.IsGap(Group("Some Single", "Single")));
    }

    [Fact]
    public void PlaceholderDecision_ALiveAlbumIsNotAGap()
    {
        // The genuinely contentious one, in the file's own words. For most
        // artists a live album is a real record they made on purpose; for a
        // heavily bootlegged one it is fifty rows of the same tour.
        Assert.False(Discography.IsGap(Group("Live in Texas", "Album", "Live")));
    }

    [Fact]
    public void PlaceholderDecision_AnUntypedGroupIsAGap()
    {
        // Null means nobody has typed it, not that it is none of them — and it
        // is commoner on obscure artists, so refusing everything untyped
        // quietly hides exactly the catalogue nobody else has curated. The tile
        // prints "Untyped" so the screen can say which it is.
        Assert.True(Discography.IsGap(Group("Unknown Record", null)));
    }

    [Fact]
    public void PlaceholderDecision_AnUndatedRecordIsStillAGap()
    {
        // FirstReleaseYear is null on unreleased and announced records. Shown,
        // a followed artist's forthcoming album appears the day MusicBrainz
        // hears about it; hidden, it waits for a date somebody has to add.
        Assert.True(Discography.IsGap(Dated("Announced", "Album", null)));
    }

    [Fact]
    public void PlaceholderDecision_BroadcastAndOtherAreNotGaps()
    {
        // Usually radio sessions and odds and ends. The placeholder keeps only
        // null, Album and EP, so both fall out by not being named rather than
        // by a decision — which is the part worth looking at.
        Assert.False(Discography.IsGap(Group("Radio Session", "Broadcast")));
        Assert.False(Discography.IsGap(Group("Odds and Ends", "Other")));
    }

    // ---------------------------------------------------------------------
    // A shop's row, cut against what the catalogue already names. Settled: this
    // half is a matching question rather than a taste one.
    // ---------------------------------------------------------------------

    [Fact]
    public void ARecordNobodyHereKnowsAboutIsAGap()
    {
        // The point of the discovery half: MusicBrainz learns about a record
        // when an editor adds it, a shop lists it on release day.
        Assert.True(Discography.IsGap(Shop("War In My Mind", 2019), [], Upcs()));
    }

    [Fact]
    public void ARecordTheCatalogueAlreadyNamesIsNotASecondGap()
    {
        // Spelt differently and dated a year out, which is the ordinary shape of
        // the disagreement. Showing both is how this library came to list the
        // same album twice on one shelf.
        Assert.False(Discography.IsGap(
            Shop("Blues DeLuxe", 2004),
            [new CataloguedRecord("Blues Deluxe", 2003)],
            Upcs()));
    }

    [Fact]
    public void AMatchingBarcodeSettlesWhatTheTitlesCannot()
    {
        // The case the title rule cannot reach, and the reason the barcode is
        // stored at all. MusicBrainz prints the terse sleeve title; the shop
        // sells on the composer and the works. Nothing about the two strings
        // agrees, and they are one record.
        //
        // **The two barcodes are deliberately different strings.** A shop sells
        // the 12-digit UPC, MusicBrainz holds the 13-digit EAN of it — the same
        // digits behind a zero. Passing one string on both sides would assert
        // nothing about the normalisation and would let a literal compare pass,
        // which is the bug `QobuzCovers` already paid for between these same two
        // catalogues.
        Assert.False(Discography.IsGap(
            Shop("Johann Sebastian Bach : Violin Concertos & Sonatas", 2013, "028947635222"),
            [new CataloguedRecord("Bach Concertos", 2013)],
            Upcs("0028947635222")));
    }

    [Fact]
    public void ABarcodeNobodyHoldsDoesNotSettleAnything()
    {
        // The other direction, so the clause above cannot pass by matching
        // everything: a real barcode that the catalogue does not hold leaves the
        // row a gap rather than hiding it.
        Assert.True(Discography.IsGap(
            Shop("Something Else", 2013, "5099920598525"),
            [new CataloguedRecord("Bach Concertos", 2013)],
            Upcs("0028947635222")));
    }

    [Fact]
    public void ARecordIsAGapAgainWhenTheCatalogueStopsNamingIt()
    {
        // <b>The whole reason this is asked on every read.</b> The same row, the
        // same shop answer, and the only thing that changed is what the
        // catalogue holds. Decided once and written down — as this used to be —
        // a row minted before you owned the record went on claiming you did not
        // own it, with no endpoint able to retract it.
        var record = Shop("Blues DeLuxe", 2004);

        Assert.False(Discography.IsGap(
            record, [new CataloguedRecord("Blues Deluxe", 2003)], Upcs()));

        Assert.True(Discography.IsGap(record, [], Upcs()));
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(3, false)]
    [InlineData(4, true)]
    [InlineData(null, true)]
    public void ASingleIsNotOfferedAndAnUncountedRecordIs(int? tracks, bool offered)
    {
        // Measured, one artist's shop page is 166 rows of singles, promos and
        // compilations beside the albums, and a shop states no type at all — so
        // the track count is the only signal there is.
        //
        // An uncounted record is kept: a silence is not a small number, the same
        // reading `ArtistNameMatch.Dwarfs` gives an absent album count.
        Assert.Equal(
            offered,
            Discography.IsGap(
                new DiscoveredRecordFacts("Something", 2019, null, tracks), [], Upcs()));
    }

    /// <summary>The UPCs the catalogue holds under this artist's records.</summary>
    /// <remarks>
    /// Normalised, because the callers normalise where they build the set — see
    /// the <c>barcodes</c> parameter on <see cref="Discography.IsGap"/>. A test
    /// helper that skipped it would be asserting against a set the application
    /// never passes.
    /// </remarks>
    private static IReadOnlySet<string> Upcs(params string[] barcodes) =>
        new HashSet<string>(
            barcodes.Select(Barcodes.Normalise).OfType<string>(), StringComparer.Ordinal);

    /// <summary>
    /// The back catalogue of somebody you have just followed is not new.
    /// </summary>
    /// <remarks>
    /// <b>The measured failure this rule replaced.</b> Monitoring used to ask
    /// "had we browsed this artist before this record turned up", which is a
    /// statement about the order our own passes ran in wearing the clothes of a
    /// statement about releases. The first time a second source was asked about
    /// artists another had already answered for, it marked 1,224 back-catalogue
    /// rows across 28 artists as new releases in one run. A record's own date
    /// cannot be wrong about this in the way a worklist stamp can.
    /// </remarks>
    [Theory]
    [InlineData(1968)]
    [InlineData(2015)]
    [InlineData(2025)]
    public void ARecordOlderThanTheFollowIsNotNew(int year) =>
        Assert.False(Discography.IsNewRelease(Followed(2026), year));

    /// <summary>A record out since you followed them is what the shelf is for.</summary>
    [Theory]
    [InlineData(2026)]
    [InlineData(2027)]
    public void ARecordReleasedSinceTheFollowIsNew(int year) =>
        Assert.True(Discography.IsNewRelease(Followed(2026), year));

    /// <summary>
    /// The year of the follow counts, because a year is all either source states.
    /// </summary>
    /// <remarks>
    /// MusicBrainz gives <c>FirstReleaseYear</c> and a shop gives a year off a
    /// date it may have rewritten for a reissue, so this is the finest
    /// comparison either supports. Stated as a test rather than left implicit:
    /// following somebody in December counts that whole year as new, and that is
    /// the accepted cost rather than an oversight.
    /// </remarks>
    [Fact]
    public void FollowingLateInAYearCountsThatWholeYear() =>
        Assert.True(Discography.IsNewRelease(
            new DateTimeOffset(2026, 12, 31, 23, 59, 0, TimeSpan.Zero), 2026));

    /// <summary>
    /// An artist nobody follows has no line to measure from, so nothing is new.
    /// </summary>
    /// <remarks>
    /// Silence is the safe direction. An unmarked record costs one click on the
    /// artist's page; a wrongly marked one costs the shelf, which is the whole
    /// complaint that produced this rule. It also covers an artist followed
    /// before the column existed, whose date is null for the same reason.
    /// </remarks>
    [Theory]
    [InlineData(1968)]
    [InlineData(2027)]
    public void AnUnfollowedArtistHasNoNewReleases(int year) =>
        Assert.False(Discography.IsNewRelease(null, year));

    /// <summary>
    /// An undated record is not a new release.
    /// </summary>
    /// <remarks>
    /// Null is an announced or unreleased record, and both sources list those
    /// early. Read as new, every source that mentions one would put it on the
    /// shelf before anybody could buy it — and it would sit there, since the
    /// column is a person's from the moment it is written and nothing recomputes
    /// it when the date finally arrives. The opposite reading to
    /// <see cref="Discography.IsWorthOffering"/>'s absent track count, and for
    /// the opposite reason: there, silence hides a record somebody could buy.
    /// </remarks>
    [Fact]
    public void AnUndatedRecordIsNotNew() =>
        Assert.False(Discography.IsNewRelease(Followed(2026), null));

    /// <summary>The instant somebody followed an artist, at the start of a year.</summary>
    private static DateTimeOffset Followed(int year) =>
        new(year, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>A record a shop named, with an optional barcode.</summary>
    private static DiscoveredRecordFacts Shop(string title, int? year, string? barcode = null) =>
        new(title, year, barcode, 12);

    /// <summary>A release group MusicBrainz has dated, with any secondary types.</summary>
    private static ReleaseGroupFacts Group(
        string title,
        string? primaryType,
        params string[] secondaryTypes) =>
        new(title, primaryType, secondaryTypes, 1973);

    /// <summary>A release group whose date is the thing under test.</summary>
    private static ReleaseGroupFacts Dated(string title, string? primaryType, int? year) =>
        new(title, primaryType, [], year);
}
