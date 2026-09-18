using Fonoteca.Domain.Catalogue;

namespace Fonoteca.Domain.Tests;

/// <summary>
/// Whether a record a shop names is one the catalogue already holds.
/// </summary>
/// <remarks>
/// The rule exists because the discography shelf is built from
/// <c>ReleaseGroup</c>, which has no barcode and cannot have one — so a title
/// and a year are the only comparable facts. These tests pin both halves of that
/// bargain: what it must match, and the two over-matches that would hide a real
/// gap rather than show a silly row.
/// </remarks>
public sealed class ReleaseTitleMatchTests
{
    [Fact]
    public void TheSameRecordMatches() =>
        Assert.True(ReleaseTitleMatch.IsSameRecord("Royal Tea", 2020, "Royal Tea", 2020));

    [Theory]
    [InlineData("Sgt. Pepper's Lonely Hearts Club Band", "Sgt Peppers Lonely Hearts Club Band")]
    [InlineData("Blues DeLuxe", "Blues Deluxe")]
    [InlineData("Dvořák: Symphony No. 9", "Dvorak: Symphony No 9")]
    [InlineData("Time / Breathe", "Time Breathe")]
    public void PunctuationCaseAndAccentsDoNotSeparateOneRecordFromItself(
        string discovered,
        string held)
    {
        // The artist normaliser, reused deliberately: two catalogues disagree
        // about a title's punctuation for exactly the reasons they disagree about
        // an artist's.
        Assert.True(ReleaseTitleMatch.IsSameRecord(discovered, 2003, held, 2003));
    }

    [Fact]
    public void AVolumeIsNotItsPredecessor()
    {
        // The tempting rule is "one title contains the other", and this is what
        // it costs: a real gap folded into a record already held, with nothing on
        // the screen to say so.
        Assert.False(ReleaseTitleMatch.IsSameRecord("Greatest Hits, Volume 2", 1998, "Greatest Hits", 1994));
        Assert.False(ReleaseTitleMatch.IsSameRecord("Greatest Hits", 1994, "Greatest Hits, Volume 2", 1998));
    }

    [Fact]
    public void ALiveRecordDoesNotAbsorbTheStudioAlbumItIsNamedAfter()
    {
        // Same normalised title, twenty-five years apart. The year is the only
        // thing separating them and it has to be enough.
        Assert.False(ReleaseTitleMatch.IsSameRecord("Sloe Gin", 1998, "Sloe Gin", 1973));
    }

    [Theory]
    [InlineData(2009, 2010, true)]
    [InlineData(2010, 2009, true)]
    [InlineData(2009, 2009, true)]
    [InlineData(2009, 2011, false)]
    [InlineData(1973, 1998, false)]
    public void YearsMayDisagreeByOneAndNoMore(int discovered, int held, bool same)
    {
        // One year of slack, because a shop dates by its own first availability.
        // CLAUDE.md already records the real shape of this: a folder named
        // "(2009)" holding a release MusicBrainz dates to 2010.
        Assert.Equal(same, ReleaseTitleMatch.IsSameRecord("Quitter", discovered, "Quitter", held));
    }

    [Fact]
    public void AnUnknownYearOnEitherSideDoesNotVeto()
    {
        // The shop did not say, which is a silence rather than a contradiction —
        // the same reading ArtistNameMatch.Dwarfs gives an absent album count.
        Assert.True(ReleaseTitleMatch.IsSameRecord("Blues Deluxe", null, "Blues Deluxe", 2003));
        Assert.True(ReleaseTitleMatch.IsSameRecord("Blues Deluxe", 2003, "Blues Deluxe", null));
    }

    [Fact]
    public void TwoUndatedRecordsOfOneNameAreTakenAsOne()
    {
        // Within a single artist's catalogue a title collision is overwhelmingly
        // the same album, and refusing here would put a duplicate row on every
        // undated back-catalogue title there is.
        Assert.True(ReleaseTitleMatch.IsSameRecord("Live", null, "Live", null));
    }

    [Theory]
    [InlineData("", "Royal Tea")]
    [InlineData("   ", "Royal Tea")]
    [InlineData("Royal Tea", "")]
    public void AnEmptyTitleMatchesNothing(string discovered, string held) =>
        // Two blanks normalise to one another, which would make every untitled
        // row the same record as every other.
        Assert.False(ReleaseTitleMatch.IsSameRecord(discovered, 2020, held, 2020));

    [Fact]
    public void ATitleThatNormalisesToNothingIsNotEverybodysRecord() =>
        // "!!!" and "???" both fold to the empty string. Matching them would be
        // the empty-title failure arriving by a different route.
        Assert.False(ReleaseTitleMatch.IsSameRecord("!!!", 2020, "???", 2020));

    [Theory]
    [InlineData(null, 2003, true)]
    [InlineData(2003, null, true)]
    [InlineData(null, null, true)]
    [InlineData(2003, 2004, true)]
    [InlineData(2003, 2005, false)]
    public void YearsAgreeIsTheNarrowingHalfOnItsOwn(int? left, int? right, bool agree) =>
        Assert.Equal(agree, ReleaseTitleMatch.YearsAgree(left, right));
}
