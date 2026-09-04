using Fonoteca.Domain.Acquisition;

namespace Fonoteca.Domain.Tests;

/// <summary>
/// The rule that turns a provider's metadata into a path.
/// </summary>
/// <remarks>
/// Every input here is a real album or track title. The point of the suite is
/// that none of them may become a path outside the staging root or a name a
/// filesystem refuses, because all of them arrive from a service this project
/// does not control.
/// </remarks>
public sealed class StagedFileNameTests
{
    [Fact]
    public void TheLayoutIsArtistThenAlbumThenNumberedTitle()
    {
        // Two deep, which is the depth ALBUM_FOLDER_DEPTH cuts at on the
        // matching screen — so a staged folder later moved into the library
        // arrives as one album question rather than as loose files.
        Assert.Equal(
            "Fleetwood Mac/Rumours/03 Never Going Back Again.flac",
            StagedFileName.For("Fleetwood Mac", "Rumours", 1, 3, "Never Going Back Again", "flac"));
    }

    [Fact]
    public void ASingleDiscReleaseCarriesNoDiscPrefix()
    {
        // "1-03" on a single-disc album reads as a box set that lost its other
        // discs, which is a claim nothing in the metadata made.
        Assert.StartsWith(
            "03 ",
            Leaf(StagedFileName.For("A", "B", 1, 3, "T", "flac", discCount: 1)),
            StringComparison.Ordinal);

        Assert.StartsWith(
            "2-03 ",
            Leaf(StagedFileName.For("A", "B", 2, 3, "T", "flac", discCount: 3)),
            StringComparison.Ordinal);
    }

    [Theory]
    // The whole reason this rule is not string interpolation at the call site.
    [InlineData("../../etc")]
    [InlineData("..")]
    [InlineData(".")]
    [InlineData("/etc/passwd")]
    [InlineData(@"..\..\windows")]
    public void NoSegmentCanEscapeItsDirectory(string hostile)
    {
        var segment = StagedFileName.Segment(hostile);

        Assert.DoesNotContain('/', segment);
        Assert.DoesNotContain('\\', segment);
        Assert.NotEqual(".", segment);
        Assert.NotEqual("..", segment);
    }

    [Fact]
    public void APathBuiltFromHostileMetadataStillHasExactlyThreeSegments()
    {
        var path = StagedFileName.For("../..", "/etc", 1, 1, "../../passwd", "flac");

        Assert.Equal(3, path.Split('/').Length);

        // No segment may begin with a dot either. Separators are gone by this
        // point so it is not a traversal any more — it is a dotfile, and a
        // staging folder whose contents do not show up in `ls` is its own bug.
        Assert.All(path.Split('/'), segment =>
            Assert.False(segment.StartsWith('.'), $"'{segment}' is hidden"));
    }

    [Theory]
    // Legal to create on ext4, impossible to open on Windows, silently stripped
    // by SMB — so a staging directory shared over the network loses its own file.
    [InlineData("Space Oddity.", "Space Oddity")]
    [InlineData("Trailing space ", "Trailing space")]
    [InlineData("Where Are We Now?", "Where Are We Now")]
    [InlineData("AC/DC", "ACDC")]
    [InlineData("Sgt. Pepper: Deluxe", "Sgt. Pepper Deluxe")]
    public void CharactersNoFilesystemAcceptsAreRemoved(string title, string expected)
    {
        Assert.Equal(expected, StagedFileName.Segment(title));
    }

    [Fact]
    public void WhitespaceIsCollapsedRatherThanKept()
    {
        // Tabs and newlines in a title are legal in an ext4 name and a menace in
        // every log and shell that later prints one.
        Assert.Equal("Ella Fitzgerald", StagedFileName.Segment("  Ella\t\nFitzgerald  "));
    }

    [Fact]
    public void ASegmentThatSanitisesToNothingBecomesAName()
    {
        // "???" is a real track title. An empty segment would make the path
        // "Artist//01 x.flac", which is a different directory on every platform.
        Assert.Equal(StagedFileName.Unnamed, StagedFileName.Segment("???"));
        Assert.Equal(StagedFileName.Unnamed, StagedFileName.Segment("   "));
        Assert.Equal(StagedFileName.Unnamed, StagedFileName.Segment(null));
    }

    [Fact]
    public void LongTitlesAreCutToSomethingExt4CanHold()
    {
        // ext4's limit is 255 bytes, not characters, and CJK is three bytes a
        // character — a character cap of 96 produces a 288-byte name that will
        // not create. The cap has to be counted in bytes.
        var leaf = Leaf(StagedFileName.For("A", "B", 1, 1, new string('壱', 300), "flac"));

        // And it must still be a valid string: a surrogate pair cut in half does
        // not round-trip through UTF-8.
        Assert.DoesNotContain('\uFFFD', System.Text.Encoding.UTF8.GetString(
            System.Text.Encoding.UTF8.GetBytes(leaf)));

        Assert.True(
            System.Text.Encoding.UTF8.GetByteCount(leaf) < 255,
            $"a {leaf.Length}-character leaf came to {System.Text.Encoding.UTF8.GetByteCount(leaf)} bytes");
    }

    [Fact]
    public void TheExtensionIsNotDoubled()
    {
        Assert.EndsWith(".flac", StagedFileName.For("A", "B", 1, 1, "T", ".flac"), StringComparison.Ordinal);
        Assert.DoesNotContain("..flac", StagedFileName.For("A", "B", 1, 1, "T", ".flac"), StringComparison.Ordinal);
    }

    private static string Leaf(string path) => path[(path.LastIndexOf('/') + 1)..];
}
