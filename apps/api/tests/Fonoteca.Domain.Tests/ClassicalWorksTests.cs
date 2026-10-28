using Fonoteca.Domain.Catalogue;

namespace Fonoteca.Domain.Tests;

public sealed class ClassicalWorksTests
{
    /// <summary>The year begun decides; without one, the earliest-born composer at thirty.</summary>
    [Fact]
    public void APeriodIsTheYearBegunElseItsComposerAtThirty()
    {
        Assert.Equal("Classical", ClassicalWorks.Period(1804, [1770]));
        Assert.Equal("Modern", ClassicalWorks.Period(1910, [1700]));
        Assert.Equal("Romantic", ClassicalWorks.Period(null, [1840, 1833]));
        Assert.Equal("Baroque", ClassicalWorks.Period(null, [null, 1685]));
        Assert.Equal("Baroque", ClassicalWorks.Period(null, [1719]));
        Assert.Equal("Classical", ClassicalWorks.Period(null, [1720]));
        Assert.Null(ClassicalWorks.Period(null, [null]));
        Assert.Null(ClassicalWorks.Period(null, []));
    }

    [Theory]
    [InlineData(1399, "Medieval")]
    [InlineData(1400, "Renaissance")]
    [InlineData(1599, "Renaissance")]
    [InlineData(1600, "Baroque")]
    [InlineData(1749, "Baroque")]
    [InlineData(1750, "Classical")]
    [InlineData(1819, "Classical")]
    [InlineData(1820, "Romantic")]
    [InlineData(1909, "Romantic")]
    [InlineData(1910, "Modern")]
    [InlineData(1974, "Modern")]
    [InlineData(1975, "Contemporary")]
    public void EachPeriodBeginsAtItsBound(int year, string period) =>
        Assert.Equal(period, ClassicalWorks.Period(year, []));

    [Fact]
    public void AnUntypedOrClassicallyTypedWorkPerformedAsClassicalIsClassical()
    {
        Assert.True(ClassicalWorks.Is(null, ["classical, symphony"]));
        Assert.True(ClassicalWorks.Is("Concerto", [null, "baroque, classical"]));
        Assert.True(ClassicalWorks.Is(null, ["romantic classical"]));
    }

    /// <summary>A crossover album's pop song: the album is tagged classical, the work is a song.</summary>
    [Fact]
    public void ASongIsNotClassicalWhateverItsAlbumIsTagged()
    {
        Assert.False(ClassicalWorks.Is("Song", ["classical, pop", "classical"]));
        Assert.False(ClassicalWorks.Is("Soundtrack", ["classical"]));
    }

    /// <summary>A jazz standard is as untyped as a movement.</summary>
    [Fact]
    public void AnUntypedWorkNobodyTaggedClassicalIsNot()
    {
        Assert.False(ClassicalWorks.Is(null, ["jazz, cool jazz", null]));
        Assert.False(ClassicalWorks.Is(null, []));
    }

    /// <summary>An untyped Neapolitan song on a tenor's crossover album.</summary>
    [Fact]
    public void ACrossoverOrPopTaggedPerformanceAnswersNothing()
    {
        Assert.False(ClassicalWorks.Is(null, ["classical, pop"]));
        Assert.False(ClassicalWorks.Is(null, ["classical, classical crossover, opera"]));

        // Another performance of the same work, tagged plain classical, still answers.
        Assert.True(ClassicalWorks.Is(null, ["classical, pop", "classical"]));
    }

    /// <summary>A symphony nobody tagged, on a box its other tracks make classical.</summary>
    [Fact]
    public void APieceOfAFormIsClassicalByTheVotesItsAlbumBorrows()
    {
        Assert.True(ClassicalWorks.ByItsAlbums("Symphony", [null, "classical, symphony"]));
        Assert.False(ClassicalWorks.ByItsAlbums("Symphony", [null]));
        Assert.False(ClassicalWorks.ByItsAlbums("Opera", ["classical crossover, classical"]));
    }

    /// <summary>A film score's cue, or an untyped track, on a concert its neighbours tag classical.</summary>
    [Fact]
    public void BorrowedVotesAnswerNothingForASongAScoreOrAnUntypedPiece()
    {
        Assert.False(ClassicalWorks.ByItsAlbums(null, ["classical, instrumental"]));
        Assert.False(ClassicalWorks.ByItsAlbums("Soundtrack", ["classical"]));
        Assert.False(ClassicalWorks.ByItsAlbums("Song", ["classical"]));
    }
}
