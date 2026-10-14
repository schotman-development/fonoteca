using Fonoteca.Domain.Catalogue;

namespace Fonoteca.Domain.Tests;

public sealed class ClassicalWorksTests
{
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
}
