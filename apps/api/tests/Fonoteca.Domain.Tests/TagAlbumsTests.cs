using Fonoteca.Domain.Catalogue;

namespace Fonoteca.Domain.Tests;

public sealed class TagAlbumsTests
{
    private static readonly Mbid PepitaSalim = new(Guid.Parse("3611e48b-9535-4387-9ea7-0c432d4d9717"));

    private static TagAlbumFile File(string path, TagReading tags) => new(MediaFileId.New(), path, tags);

    /// <summary>"Covers": six singles, each its own ALBUM tag, one folder, one album.</summary>
    [Fact]
    public void AFolderOfSinglesIsOneAlbumNamedAfterTheFolder()
    {
        var album = TagAlbums.Of("Pepita Salim/Covers",
        [
            File("Pepita Salim/Covers/02 I Want You Back - Pepita Salim.flac", new(
                Title: "I Want You Back", Artist: "Pepita Salim", ArtistIds: PepitaSalim.ToString(),
                AlbumArtist: "Pepita Salim", AlbumArtistIds: PepitaSalim.ToString(), Album: "I Want You Back", Track: "1", Genre: "International Pop")),
            File("Pepita Salim/Covers/01 Gravity - Pepita Salim.flac", new(
                Title: "Gravity", Artist: "Pepita Salim", ArtistIds: PepitaSalim.ToString(),
                AlbumArtist: "Pepita Salim", AlbumArtistIds: PepitaSalim.ToString(), Album: "Gravity", Track: "1", Genre: "International Pop")),
        ]);

        Assert.Equal("Covers", album.Title);
        Assert.Equal([new TagCredit("Pepita Salim", PepitaSalim, 0, null)], album.Billing);

        // Both say track 1, so the folder's order by name places them.
        Assert.Equal(["Gravity", "I Want You Back"], album.Songs.Select(song => song.Title));
        Assert.Equal([1, 2], album.Songs.Select(song => song.Position));

        // A genre the catalogue does not know is no genre.
        Assert.All(album.Songs, song => Assert.Empty(song.Genres));
    }

    [Fact]
    public void TheAlbumTagNamesTheAlbumWhereEveryFileStatesIt()
    {
        var album = TagAlbums.Of("Pepita Salim/The Greatest Showman",
        [
            File("Pepita Salim/The Greatest Showman/02 Never Enough.flac", new(Title: "Never Enough", Album: "Showman Covers", Track: "2", Year: "2019-04-02")),
            File("Pepita Salim/The Greatest Showman/01 A Million Dreams.flac", new(Title: "A Million Dreams", Album: "Showman Covers", Track: "1", Year: "2019")),
        ]);

        Assert.Equal("Showman Covers", album.Title);
        Assert.Equal(2019, album.Year);
        Assert.Equal([(1, "A Million Dreams"), (2, "Never Enough")], album.Songs.Select(song => (song.Position, song.Title)));
    }

    [Fact]
    public void ASongWithNoTagsIsNamedAfterItsFileAndBilledToTheAlbum()
    {
        var album = TagAlbums.Of("Emma Kok/5 Mei Concert",
        [
            File("Emma Kok/5 Mei Concert/Als Wij Niks Doen.flac", new(AlbumArtist: "Emma Kok", Genre: "Classical Crossover;International Pop")),
            File("Emma Kok/5 Mei Concert/Untagged.flac", new(AlbumArtist: "Emma Kok")),
        ]);

        Assert.Equal("5 Mei Concert", album.Title);
        Assert.Equal(["Als Wij Niks Doen", "Untagged"], album.Songs.Select(song => song.Title));
        Assert.All(album.Songs, song => Assert.Equal("Emma Kok", Assert.Single(song.Billing).Name));
        Assert.Equal(["classical crossover"], album.Songs[0].Genres);
    }

    [Fact]
    public void SeveralArtistsInOneTagAreOneBillingLine()
    {
        var album = TagAlbums.Of("Emma Kok/Carie op Vrijdag",
        [
            File("Emma Kok/Carie op Vrijdag/Imagine.flac", new(Title: "Imagine", Artist: "Emma Kok;Joep Paddenburg")),
        ]);

        Assert.Equal(
            [new TagCredit("Emma Kok", null, 0, " & "), new TagCredit("Joep Paddenburg", null, 1, null)],
            album.Songs[0].Billing);

        // The only file's artists are the album's too.
        Assert.Equal(album.Songs[0].Billing, album.Billing);
    }

    [Fact]
    public void FilesDisagreeingOnTheArtistBillTheAlbumToNobody()
    {
        var album = TagAlbums.Of("Various/Mixtape",
        [
            File("Various/Mixtape/1.mp3", new(Title: "One", Artist: "A")),
            File("Various/Mixtape/2.mp3", new(Title: "Two", Artist: "B")),
        ]);

        Assert.Empty(album.Billing);
        Assert.Equal(["A", "B"], album.Songs.Select(song => song.Billing.Single().Name));
    }

    [Fact]
    public void DiscAndTrackNumbersAreKeptWhereEveryFileHasADistinctSlot()
    {
        var album = TagAlbums.Of("Artist/Double",
        [
            File("Artist/Double/a.flac", new(Title: "A", Disc: "2", Track: "1/9")),
            File("Artist/Double/b.flac", new(Title: "B", Disc: "1", Track: "1")),
        ]);

        Assert.Equal([(2, 1), (1, 1)], album.Songs.Select(song => (song.Disc, song.Position)));
        Assert.Equal(2, album.DiscCount);
    }
}
