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
}
