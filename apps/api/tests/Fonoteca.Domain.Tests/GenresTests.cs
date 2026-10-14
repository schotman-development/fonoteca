using Fonoteca.Domain.Catalogue;

namespace Fonoteca.Domain.Tests;

public sealed class GenresTests
{
    [Fact]
    public void AMusicBrainzGenreFoldsIntoOneOfFonotecasWhateverItsCase()
    {
        Assert.Equal("Alternative Metal", Genres.Of("nu metal"));
        Assert.Equal("Soul", Genres.Of("Motown"));
        Assert.Equal("Rock", Genres.Of("rock"));
    }

    /// <summary>What a person types on an artist's page may already be one of these.</summary>
    [Fact]
    public void OneOfFonotecasOwnNamesIsItself()
    {
        Assert.Equal("Chicago Blues", Genres.Of("chicago BLUES"));
        Assert.Equal("Comedy/Spoken", Genres.Of("comedy/spoken"));
    }

    [Fact]
    public void ANameNobodyPlacedIsOther()
    {
        Assert.Equal(Genres.Other, Genres.Of("a genre MusicBrainz has not invented yet"));
        Assert.Equal([Genres.Other], Genres.Carried(["a genre MusicBrainz has not invented yet"]));
    }

    /// <summary>A fusion sits under both its tops, so a flat list still finds it under either.</summary>
    [Fact]
    public void AGenreIsCarriedWithItsTopsBeforeItOnceEach()
    {
        Assert.Equal(
            ["Blues", "Rock", "Blues Rock", "Electric Blues", "Hard Rock"],
            Genres.Carried(["blues rock", "blues", "electric blues", "rock", "hard rock"]));
    }

    [Fact]
    public void TheNamesStatingAGenreIncludeThoseOfItsSubgenres()
    {
        Assert.Equal(
            ["blues", "blues rock", "chicago blues"],
            Genres.Stating(["blues", "blues rock", "chicago blues", "hard rock", "unheard of"], genre => genre == "Blues"));

        Assert.Equal(["unheard of"], Genres.Stating(["rock", "unheard of"], genre => genre == Genres.Other));
    }

    /// <summary>Two levels: every top a subgenre names is a top, and no top sits under anything.</summary>
    [Fact]
    public void TheTreeIsTwoLevelsDeep()
    {
        var tops = Genres.All.Where(genre => Genres.TopsOf(genre).Count == 0).ToHashSet();

        Assert.Contains(Genres.Other, tops);
        Assert.All(Genres.All, genre => Assert.All(Genres.TopsOf(genre), top => Assert.Contains(top, tops)));
        Assert.InRange(Genres.All.Count, 90, 130);
    }

    /// <summary>
    /// Every genre MusicBrainz listed when the tree was drafted (WS/2
    /// <c>genre/all?fmt=txt</c>, 2026-10-14) has a place, the owner's choice.
    /// </summary>
    [Fact]
    public void EveryGenreMusicBrainzListsIsPlaced()
    {
        Assert.True(MusicBrainzGenres.Listed.Length > 2000);
        var unplaced = MusicBrainzGenres.Listed.Where(genre => Genres.Of(genre) == Genres.Other).ToList();

        Assert.Empty(unplaced);
    }
}
