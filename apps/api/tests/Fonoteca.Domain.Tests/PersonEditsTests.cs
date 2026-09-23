using Fonoteca.Domain.Catalogue;

namespace Fonoteca.Domain.Tests;

public sealed class PersonEditsTests
{
    private static readonly Dictionary<string, string?> Provider = new()
    {
        ["name"] = "Herbert von Karajan",
        ["country"] = "AT",
        ["gender"] = null,
    };

    [Fact]
    public void OnlyWhatDiffersFromTheProviderIsAnEdit()
    {
        var edits = PersonEdits.Diff(
            new Dictionary<string, string?> { ["name"] = "Herbert von Karajan", ["country"] = "DE", ["gender"] = "" },
            Provider);

        Assert.Equal(new Dictionary<string, string?> { ["country"] = "DE" }, edits);
    }

    /// <summary>Clearing a field the provider filled is an answer, and it survives the round trip as null.</summary>
    [Fact]
    public void ClearingAFieldIsAnEditToNone()
    {
        var edits = PersonEdits.Read(PersonEdits.Write(PersonEdits.Diff(
            new Dictionary<string, string?> { ["country"] = "  " },
            Provider)));

        Assert.True(edits.ContainsKey("country"));
        Assert.Null(PersonEdits.Apply(edits, "country", "AT"));
        Assert.Equal("Herbert von Karajan", PersonEdits.Apply(edits, "name", "Herbert von Karajan"));
    }

    [Fact]
    public void NoEditsIsNoJson()
    {
        Assert.Null(PersonEdits.Write(new Dictionary<string, string?>()));
        Assert.Empty(PersonEdits.Read(null));
    }
}
