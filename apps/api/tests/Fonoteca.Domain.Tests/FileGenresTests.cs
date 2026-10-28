using Fonoteca.Domain.Catalogue;

namespace Fonoteca.Domain.Tests;

public sealed class FileGenresTests
{
    /// <summary>One album, one set of genres: a track's own votes do not scatter it.</summary>
    [Fact]
    public void TheAlbumsGenresWinOverTheRecordings()
    {
        Assert.Equal(["Blues", "Rock", "Blues Rock"], FileGenres.Of("blues rock, blues", "pop"));
    }

    [Fact]
    public void TheRecordingsStandInWhereTheAlbumHasNone()
    {
        Assert.Equal(["Pop"], FileGenres.Of(null, "pop"));
        Assert.Empty(FileGenres.Of(null, null));
    }

    /// <summary>The cut is on what MusicBrainz states; the tops come on top of it.</summary>
    [Fact]
    public void AFileCarriesWhatItsFiveMostVotedGenresComeTo()
    {
        Assert.Equal(
            ["Jazz", "Swing", "Big Band", "Bebop", "Hard Bop", "Cool Jazz"],
            FileGenres.Of("swing, big band, bebop, hard bop, cool jazz, soul jazz", null));
    }

    /// <summary>An album nobody voted on is listed under what most of its tracks carry.</summary>
    [Fact]
    public void AnAlbumWithNoGenresOfItsOwnTakesItsTracksCommonestFirst()
    {
        Assert.Equal(
            ["Rock", "Blues", "R&B", "Soul"],
            FileGenres.OfAlbum(null, ["blues, rock", "rock", null, "soul, rock"]));

        Assert.Equal(["Jazz"], FileGenres.OfAlbum("jazz", ["rock", "rock"]));
    }

    /// <summary>The same choice in MusicBrainz's own words, which the classical rule reads.</summary>
    [Fact]
    public void AnAlbumsVotesAreItsGenresBeforeTheyAreFolded()
    {
        Assert.Equal(["classical", "symphony"], FileGenres.Voted(null, [null, "symphony, classical", "classical"]));
        Assert.Equal(["classical crossover"], FileGenres.Voted("classical crossover", ["classical"]));
        Assert.Empty(FileGenres.Voted(null, [null, null]));
    }
}
