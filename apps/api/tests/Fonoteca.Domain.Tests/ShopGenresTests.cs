using Fonoteca.Domain.Catalogue;

namespace Fonoteca.Domain.Tests;

/// <summary>What a shop's genre for an album states, in MusicBrainz's terms.</summary>
public sealed class ShopGenresTests
{
    /// <summary>A name spelled out here that folds to nothing would be dropped without a word.</summary>
    [Fact]
    public void EveryNameSpelledOutFoldsIntoTheTree()
    {
        Assert.All(ShopGenres.AppleMusicNames.Values.SelectMany(names => names), name => Assert.NotEqual(Genres.Other, Genres.Of(name)));
        Assert.All(ShopGenres.QobuzIds.Values, name => Assert.NotEqual(Genres.Other, Genres.Of(name)));
    }

    [Theory]
    [InlineData("Classical", "classical")]
    [InlineData("Contemporary Blues", "blues")]
    [InlineData("Blues-Rock", "blues rock")]
    [InlineData("Singer/Songwriter", "singer-songwriter")]
    [InlineData("Classical Crossover", "classical crossover")]
    [InlineData("Romantic Era", "classical,romantic classical")]
    public void AppleMusicsGenreIsStatedAsMusicBrainzWould(string apple, string stated) =>
        Assert.Equal(stated.Split(','), ShopGenres.FromAppleMusic(apple));

    /// <summary>Every genre Apple gave for an album here MusicBrainz had none for, 2026-11-03, states one.</summary>
    [Theory]
    [InlineData("Blues")]
    [InlineData("Pop")]
    [InlineData("Classical")]
    [InlineData("Contemporary Blues")]
    [InlineData("Jazz")]
    [InlineData("Rock")]
    [InlineData("Classical Crossover")]
    [InlineData("Country")]
    [InlineData("Soundtrack")]
    [InlineData("Blues-Rock")]
    [InlineData("Singer/Songwriter")]
    [InlineData("Instrumental")]
    [InlineData("Classic Blues")]
    [InlineData("R&B/Soul")]
    [InlineData("Acoustic Blues")]
    [InlineData("Contemporary Folk")]
    [InlineData("Indie Pop")]
    [InlineData("Soul")]
    [InlineData("Vocal")]
    [InlineData("Contemporary Singer/Songwriter")]
    [InlineData("Standards")]
    [InlineData("New Age")]
    [InlineData("Fusion")]
    [InlineData("Alternative Folk")]
    public void EveryGenreAppleGaveHereStatesOne(string apple) =>
        Assert.NotEmpty(ShopGenres.FromAppleMusic(apple));

    /// <summary>Every genre path Qobuz gave for an album here MusicBrainz had none for, 2026-11-03, but its top alone.</summary>
    [Theory]
    [InlineData(2, 3)]
    [InlineData(10)]
    [InlineData(112, 117)]
    [InlineData(2, 4)]
    [InlineData(112, 119)]
    [InlineData(80)]
    [InlineData(127, 132)]
    [InlineData(112, 119, 113)]
    [InlineData(94)]
    [InlineData(91, 92)]
    [InlineData(112, 122)]
    [InlineData(64, 141)]
    [InlineData(2, 5)]
    public void EveryGenrePathQobuzGaveHereStatesOne(params int[] path) =>
        Assert.NotEmpty(ShopGenres.FromQobuz(path));

    /// <summary>A name that folds to nothing is no answer, not "Other".</summary>
    [Theory]
    [InlineData("Music")]
    [InlineData("")]
    [InlineData(null)]
    public void AnAppleNameWithNoCounterpartStatesNothing(string? apple) =>
        Assert.Empty(ShopGenres.FromAppleMusic(apple));

    /// <summary>Qobuz's path is read broadest first; an id it has no counterpart for adds nothing.</summary>
    [Fact]
    public void AQobuzGenreIsReadByItsPath()
    {
        Assert.Equal(["blues"], ShopGenres.FromQobuz([2, 3]));
        Assert.Equal(["classical", "orchestral"], ShopGenres.FromQobuz([10, 37, 43]));
        Assert.Equal(["rock", "alternative rock"], ShopGenres.FromQobuz([112, 119, 113]));
        Assert.Empty(ShopGenres.FromQobuz([112]));
        Assert.Empty(ShopGenres.FromQobuz([1]));
    }
}
