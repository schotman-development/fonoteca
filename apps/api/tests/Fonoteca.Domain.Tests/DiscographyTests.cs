using Fonoteca.Domain.Acquisition;

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
