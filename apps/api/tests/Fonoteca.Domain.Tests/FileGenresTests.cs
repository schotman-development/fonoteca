using Fonoteca.Domain.Catalogue;

namespace Fonoteca.Domain.Tests;

public sealed class FileGenresTests
{
    /// <summary>One album, one set of genres: a track's own votes do not scatter it.</summary>
    [Fact]
    public void TheAlbumsGenresWinOverTheRecordings()
    {
        Assert.Equal(["blues rock", "blues"], FileGenres.Of("blues rock, blues", "pop"));
    }

    [Fact]
    public void TheRecordingsStandInWhereTheAlbumHasNone()
    {
        Assert.Equal(["pop"], FileGenres.Of(null, "pop"));
        Assert.Empty(FileGenres.Of(null, null));
    }

    [Fact]
    public void AFileCarriesAtMostFive()
    {
        Assert.Equal(
            ["a", "b", "c", "d", "e"],
            FileGenres.Of("a, b, c, d, e, f, g", null));
    }

    /// <summary>An album nobody voted on is listed under what most of its tracks carry.</summary>
    [Fact]
    public void AnAlbumWithNoGenresOfItsOwnTakesItsTracksCommonestFirst()
    {
        Assert.Equal(
            ["rock", "blues", "soul"],
            FileGenres.OfAlbum(null, ["blues, rock", "rock", null, "soul, rock"]));

        Assert.Equal(["jazz"], FileGenres.OfAlbum("jazz", ["rock", "rock"]));
    }
}
