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
}
