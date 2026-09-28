using Fonoteca.Domain.Catalogue;

namespace Fonoteca.Domain.Tests;

/// <summary>
/// Where one album stops and the next begins.
/// </summary>
/// <remarks>
/// Every path here is a real shape from the target library, taken from a count
/// of its 8,192 files by directory depth. The three disc spellings are the whole
/// reason the rule is a depth cut rather than a pattern: two of them name a disc
/// and the third names a MusicBrainz medium format.
/// </remarks>
public sealed class AlbumFolderTests
{
    [Theory]
    [InlineData("Fleetwood Mac/Rumours/03 Never Going Back Again.flac", "Fleetwood Mac/Rumours")]
    [InlineData("Nat King Cole/Unforgettable/CD 01/01 Mona Lisa.flac", "Nat King Cole/Unforgettable")]
    [InlineData("Nat King Cole/Unforgettable/CD 02/01 Route 66.flac", "Nat King Cole/Unforgettable")]
    [InlineData("Sir Georg Solti/Der Ring des Nibelungen/Disc 3/04 Siegfried.flac", "Sir Georg Solti/Der Ring des Nibelungen")]
    public void EveryDiscOfOneAlbumIsOneQuestion(string path, string expected) =>
        Assert.Equal(expected, AlbumFolder.Of(path));

    [Fact]
    public void NumberedFilesSortByTheirNumbersNotTheirCharacters()
    {
        // Two real shapes: a box set numbered without padding, where ordinal
        // order read 109 before 11, and disc folders numbered the same way.
        string[] paths =
        [
            "Gustav Mahler/Symphonies/109 Finale.flac",
            "Gustav Mahler/Symphonies/11 Finale.flac",
            "Gustav Mahler/Symphonies/2 Scherzo.flac",
            "Nat King Cole/Unforgettable/CD 10/01 Mona Lisa.flac",
            "Nat King Cole/Unforgettable/CD 2/01 Route 66.flac",
        ];

        var sorted = paths.OrderBy(AlbumFolder.SortKey, StringComparer.Ordinal).ToList();

        Assert.Equal(
            [
                "Gustav Mahler/Symphonies/2 Scherzo.flac",
                "Gustav Mahler/Symphonies/11 Finale.flac",
                "Gustav Mahler/Symphonies/109 Finale.flac",
                "Nat King Cole/Unforgettable/CD 2/01 Route 66.flac",
                "Nat King Cole/Unforgettable/CD 10/01 Mona Lisa.flac",
            ],
            sorted);
    }

    [Fact]
    public void ADiscFolderNamedAfterItsMediumFormatCollapsesLikeAnyOther()
    {
        // 453 files across nine albums are foldered "Digital Media 01" .. "05"
        // rather than "CD 01". A pattern matching CD and Disc would have split
        // every one of them into five albums; a depth cut never sees the name.
        var first = AlbumFolder.Of("Andrea Bocelli/Vivere/Digital Media 01/01 Con Te Partirò.flac");
        var fifth = AlbumFolder.Of("Andrea Bocelli/Vivere/Digital Media 05/07 Time to Say Goodbye.flac");

        Assert.Equal("Andrea Bocelli/Vivere", first);
        Assert.Equal(first, fifth);
    }

    [Fact]
    public void TwoAlbumsByOneArtistAreTwoQuestions()
    {
        // The whole point. Under the expanding gather a compilation holding one
        // track of each pulled both into one component, and the box set was then
        // the best explanation of the merged set.
        Assert.NotEqual(
            AlbumFolder.Of("Michael Jackson/Off the Wall/03 Off the Wall.flac"),
            AlbumFolder.Of("Michael Jackson/Thriller/04 Thriller.flac"));
    }

    [Fact]
    public void AFileWithNoAlbumFolderKeepsTheFolderItHas() =>
        // One file in the library is loose under its artist. Cutting to a depth
        // it does not reach would file it with that artist's real albums.
        Assert.Equal(
            "Prince",
            AlbumFolder.Of("Prince/While My Guitar Gently Weeps - Prince, Tom Petty.flac"));

    [Fact]
    public void AFileLooseAtTheRootBelongsToTheRoot() =>
        Assert.Equal(string.Empty, AlbumFolder.Of("stray.flac"));

    [Fact]
    public void AnAlbumSitsOnItsArtistsShelf() =>
        Assert.Equal(
            "Michael Jackson",
            AlbumFolder.ParentOf("Michael Jackson/Thriller/04 Thriller.flac"));

    [Fact]
    public void ADiscOfAnAlbumReachesTheSameShelf() =>
        // Cut from the album rather than from the path, so the extra directory a
        // box set adds cannot push the shelf down into the album.
        Assert.Equal(
            "Andrea Bocelli",
            AlbumFolder.ParentOf("Andrea Bocelli/Vivere/Digital Media 01/01 Con Te Partirò.flac"));

    [Fact]
    public void TheShelfIsWhereTheRecordSitsAndNotWhoseItIs() =>
        // The measured shape, and the reason the caller checks the name: a
        // classical library files a performance under the composer, so this is
        // Bach's shelf holding a record of Janine Jansen's.
        Assert.Equal(
            "Johann Sebastian Bach",
            AlbumFolder.ParentOf("Johann Sebastian Bach/Violin Concertos/02 Allegro.flac"));

    [Theory]
    [InlineData("stray.flac")]
    [InlineData("Prince/While My Guitar Gently Weeps - Prince, Tom Petty.flac")]
    public void AFileShallowerThanAnAlbumHasNoShelf(string path) =>
        // Nothing above an album is nobody's shelf, and the library root least of
        // all. The loose file under Prince is the one in this library, and it
        // costs that artist nothing: the shelf is reached from their albums.
        Assert.Equal(string.Empty, AlbumFolder.ParentOf(path));
}
