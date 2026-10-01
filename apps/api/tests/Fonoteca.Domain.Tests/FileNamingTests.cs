using Fonoteca.Domain.Catalogue;

namespace Fonoteca.Domain.Tests;

public sealed class FileNamingTests
{
    private static readonly NamingFacts Rumours = new(
        "Fleetwood Mac", "Rumours", 1977, 1, 1, 3, "Never Going Back Again", "Fleetwood Mac");

    [Fact]
    public void TheDefaultIsArtistThenAlbumAndYearThenNumberedTitle()
    {
        Assert.Equal("Fleetwood Mac/Rumours (1977)", FileNaming.Folder(FileNaming.DefaultPattern, Rumours));
        Assert.Equal("03 - Never Going Back Again.flac", FileNaming.File(FileNaming.DefaultPattern, Rumours, "flac"));
    }

    [Fact]
    public void AnOptionalGroupGoesWholeWhenItsTokenIsEmpty()
    {
        var undated = Rumours with { Year = null };

        Assert.Equal("Fleetwood Mac/Rumours", FileNaming.Folder(FileNaming.DefaultPattern, undated));

        // The disc prefix only on a release with more than one disc.
        var boxed = Rumours with { Disc = 2, DiscCount = 2 };
        Assert.Equal("2-03 - Never Going Back Again.mp3", FileNaming.File(FileNaming.DefaultPattern, boxed, "mp3"));
    }

    [Fact]
    public void AMissingFactOutsideAGroupNamesNothing()
    {
        // No track number: the file keeps its name rather than being called
        // " - Title", and no album artist leaves the folder alone.
        Assert.Null(FileNaming.File(FileNaming.DefaultPattern, Rumours with { Track = null }, "flac"));
        Assert.Null(FileNaming.Folder(FileNaming.DefaultPattern, Rumours with { AlbumArtist = null }));
    }

    [Fact]
    public void ValuesAreSegmentsAndCannotAddAFolder()
    {
        var awkward = Rumours with { Album = "AC/DC: Live?", Title = "What / Now." };

        Assert.Equal("Fleetwood Mac/ACDC Live (1977)", FileNaming.Folder(FileNaming.DefaultPattern, awkward));
        Assert.Equal("03 - What Now.flac", FileNaming.File(FileNaming.DefaultPattern, awkward, "flac"));
    }

    [Fact]
    public void AnotherPatternIsFollowed()
    {
        const string pattern = "{albumartist}/{year} - {album}/Disc [{disc}]/{track} {artist} - {title}";

        Assert.Equal("Fleetwood Mac/1977 - Rumours", FileNaming.Folder(pattern, Rumours));
        Assert.Equal(
            "Disc/03 Fleetwood Mac - Never Going Back Again.flac",
            FileNaming.File(pattern, Rumours, "flac"));
    }

    [Theory]
    [InlineData("{albumartist}/{album}/{track} - {title}", null)]
    [InlineData("{album}/{track}", "two '/'")]
    [InlineData("{albumartist}/{album}/{genre}", "not a token")]
    [InlineData("{albumartist}/{album}[/{year}]/{title}", "inside '[…]'")]
    [InlineData("{albumartist}/{album}/[[{disc}]]{title}", "nested")]
    [InlineData("{albumartist}/{album}/[{disc}{title}", "never closed")]
    [InlineData("", "empty")]
    [InlineData("{albumartist}//{album}/{title}", "no name")]
    [InlineData("/{albumartist}/{album}/{title}", "no name")]
    [InlineData("{albumartist}/{album}/{title}/", "no name")]
    public void APatternIsCheckedBeforeItIsUsed(string pattern, string? problem)
    {
        var found = FileNaming.Problem(pattern);

        if (problem is null) Assert.Null(found);
        else Assert.Contains(problem, found, StringComparison.Ordinal);
    }
}
