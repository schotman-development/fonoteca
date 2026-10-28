using Fonoteca.Domain.Catalogue;

namespace Fonoteca.Domain.Tests;

public sealed class WorkTreeTests
{
    [Fact]
    public void AVariationClimbsPastItsNumberAndItsActToTheBallet()
    {
        var ballet = WorkId.New();
        var act = WorkId.New();
        var number = WorkId.New();
        var variation = WorkId.New();

        var parents = new Dictionary<WorkId, WorkId>
        {
            [variation] = number,
            [number] = act,
            [act] = ballet,
        };

        Assert.Equal(ballet, WorkTree.Top(variation, parents));
        Assert.Equal(ballet, WorkTree.Top(act, parents));
        Assert.Equal(ballet, WorkTree.Top(ballet, parents));
    }

    [Fact]
    public void ACycleStopsAtTheLastWorkBeforeItRepeats()
    {
        var first = WorkId.New();
        var second = WorkId.New();
        var parents = new Dictionary<WorkId, WorkId> { [first] = second, [second] = first };

        Assert.Equal(second, WorkTree.Top(first, parents));
    }

    /// <summary>The Ring gathers four operas; each is the piece, its scenes the movements.</summary>
    [Fact]
    public void ACollectionsPartsAreEachAPiece()
    {
        var ring = WorkId.New();
        var walkure = WorkId.New();
        var act = WorkId.New();
        var scene = WorkId.New();
        var rheingold = WorkId.New();

        var parents = WorkTree.Cut(
            new Dictionary<WorkId, WorkId>
            {
                [scene] = act,
                [act] = walkure,
                [walkure] = ring,
                [rheingold] = ring,
            },
            new HashSet<WorkId> { ring });

        Assert.Equal(walkure, WorkTree.Top(scene, parents));
        Assert.Equal(rheingold, WorkTree.Top(rheingold, parents));
        Assert.False(parents.ContainsKey(rheingold));
        Assert.Equal(act, parents[scene]);
    }

    /// <summary>A collection inside a piece stays inside it: only a top is cut away.</summary>
    [Fact]
    public void ACollectionBelowAPieceIsNotCut()
    {
        var opera = WorkId.New();
        var numbers = WorkId.New();
        var aria = WorkId.New();

        var parents = WorkTree.Cut(
            new Dictionary<WorkId, WorkId> { [aria] = numbers, [numbers] = opera },
            new HashSet<WorkId> { numbers });

        Assert.Equal(opera, WorkTree.Top(aria, parents));
    }

    [Fact]
    public void ACycleOfCollectionsIsLeftAsItIs()
    {
        var first = WorkId.New();
        var second = WorkId.New();
        var links = new Dictionary<WorkId, WorkId> { [first] = second, [second] = first };

        Assert.Equal(links, WorkTree.Cut(links, new HashSet<WorkId> { first, second }));
    }
}
