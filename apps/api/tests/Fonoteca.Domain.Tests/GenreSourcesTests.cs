using Fonoteca.Domain.Catalogue;

namespace Fonoteca.Domain.Tests;

/// <summary>What a person may say an album's genres are, and which answers no rule replaces.</summary>
public sealed class GenreSourcesTests
{
    [Fact]
    public void APersonsGenresAreSpelledAsMusicBrainzSpellsThemOnceEach() =>
        Assert.Equal(["blues rock", "blues", "classical"], GenreSources.Stated([" Blues Rock ", "blues", "", null, "Blues", "Classical"], out _));

    /// <summary>A name the tree does not fold would be stored as nothing anyone can browse.</summary>
    [Fact]
    public void ANameNoGenreFoldsIsRefused()
    {
        Assert.Null(GenreSources.Stated(["blues", "not a genre"], out var unknown));
        Assert.Equal("not a genre", unknown);
    }

    [Theory]
    [InlineData(GenreSources.Person, true)]
    [InlineData(GenreSources.Agent, true)]
    [InlineData("Apple Music", false)]
    [InlineData(null, false)]
    public void OnlyAPersonsOrAnAgentsAnswerIsDecided(string? source, bool decided) =>
        Assert.Equal(decided, GenreSources.IsDecided(source));
}
