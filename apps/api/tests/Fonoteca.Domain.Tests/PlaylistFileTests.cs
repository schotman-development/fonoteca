using Fonoteca.Domain.Catalogue;

namespace Fonoteca.Domain.Tests;

public sealed class PlaylistFileTests
{
    private static readonly AudioQuality CdFlac = new()
    {
        Codec = "flac",
        SampleRateHz = 44_100,
        Channels = 2,
        BitDepth = 16,
        BitrateBps = 900_000,
        IsLossless = true,
    };

    private static readonly AudioQuality HiResFlac = CdFlac with { BitDepth = 24, SampleRateHz = 96_000, BitrateBps = 3_000_000 };

    private static readonly AudioQuality Mp3 = new()
    {
        Codec = "mp3",
        SampleRateHz = 44_100,
        Channels = 2,
        BitrateBps = 320_000,
        IsLossless = false,
    };

    [Fact]
    public void TheFileIsExtendedM3uWithPathsRelativeToThePlaylistsFolder()
    {
        var text = PlaylistFile.Render(
            "Road trip",
            [
                new PlaylistLine("Fleetwood Mac/Rumours (1977)/07 - The Chain.flac", TimeSpan.FromSeconds(270.6), "Fleetwood Mac", "The Chain"),
                new PlaylistLine("Unknown/01.flac", null, null, "Untitled"),
            ]);

        Assert.Equal(
            "#EXTM3U\n"
            + "#PLAYLIST:Road trip\n"
            + "#EXTINF:271,Fleetwood Mac - The Chain\n"
            + "../Fleetwood Mac/Rumours (1977)/07 - The Chain.flac\n"
            + "#EXTINF:-1,Untitled\n"
            + "../Unknown/01.flac\n",
            text);
    }

    [Fact]
    public void ALineBreakInATitleCannotStartARecordAndOneInAPathLeavesTheSongOut()
    {
        var text = PlaylistFile.Render(
            "Two\nlines",
            [
                new PlaylistLine("A/01.flac", null, "Some\nbody", "Title\r\nhere"),
                new PlaylistLine("B/broken\nname.flac", null, null, "Gone"),
            ]);

        Assert.Equal(
            "#EXTM3U\n#PLAYLIST:Two lines\n#EXTINF:-1,Some body - Title  here\n../A/01.flac\n",
            text);
    }

    [Fact]
    public void TheNameBecomesOneSafeSegmentUnderThePlaylistsFolder()
    {
        Assert.Equal("Playlists/Road trip.m3u8", PlaylistFile.PathFor("Road trip"));
        Assert.Equal("Playlists/ACDC best.m3u8", PlaylistFile.PathFor("AC/DC best"));
        Assert.Equal("Playlists/passwd.m3u8", PlaylistFile.PathFor("../passwd"));
    }

    [Fact]
    public void TheBestCopyIsTheBestAudioThatDecodes()
    {
        var mp3 = Copy("A/01.mp3", Mp3);
        var cd = Copy("B/01.flac", CdFlac);
        var hiRes = Copy("C/01.flac", HiResFlac);
        var damaged = Copy("D/01.flac", HiResFlac, IntegrityState.Corrupt);

        Assert.Equal(hiRes, PlaylistFile.Best([mp3, cd, damaged, hiRes]));
        Assert.Equal(cd, PlaylistFile.Best([mp3, damaged, cd]));
        Assert.Equal(damaged, PlaylistFile.Best([damaged]));
        Assert.Null(PlaylistFile.Best([]));
    }

    /// <summary>16-bit audio padded into a 24-bit FLAC is the CD it is, the duplicate rule's own reading.</summary>
    [Fact]
    public void APaddedFileRanksAsTheAudioItHolds()
    {
        var padded = Copy("A/01.flac", CdFlac with { BitDepth = 24 }, measurement: new AudioMeasurement { EffectiveBitDepth = 16 });
        var real = Copy("B/01.flac", CdFlac with { BitDepth = 24, BitrateBps = 1_400_000 });

        Assert.Equal(real, PlaylistFile.Best([padded, real]));
    }

    [Fact]
    public void EqualCopiesAreDecidedByPathSoTheFileIsNotRewrittenForNothing()
    {
        var first = Copy("A/01.flac", CdFlac);
        var second = Copy("B/01.flac", CdFlac);

        Assert.Equal(first, PlaylistFile.Best([second, first]));
    }

    private const string Root = "/srv/music";

    /// <summary>A file another program wrote: its comments, a stream, a missing path and a Windows one all read as they are.</summary>
    private const string Foreign =
        "#EXTM3U\r\n"
        + "#PLAYLIST:Mine\r\n"
        + "# made in foobar2000\r\n"
        + "#EXTINF:271,Fleetwood Mac - The Chain\r\n"
        + "../Fleetwood Mac/Rumours (1977)/07 - The Chain.flac\r\n"
        + "/srv/music/Fleetwood Mac/Rumours (1977)/02 - Dreams.flac\r\n"
        + "#EXTINF:-1,Radio\r\n"
        + "http://radio.example/stream\r\n"
        + "..\\Somebody\\01 Nameless.flac\r\n";

    [Fact]
    public void AFileIsReadEntryByEntryAsItIsWritten()
    {
        var items = PlaylistFile.Items(Foreign);

        Assert.Equal("Mine", PlaylistFile.NameIn(Foreign));
        Assert.Equal(
            [
                "../Fleetwood Mac/Rumours (1977)/07 - The Chain.flac",
                "/srv/music/Fleetwood Mac/Rumours (1977)/02 - Dreams.flac",
                "http://radio.example/stream",
                "..\\Somebody\\01 Nameless.flac",
            ],
            items.Select(item => item.Location));
        Assert.Equal("Fleetwood Mac - The Chain", items[0].Title);
        Assert.Equal(TimeSpan.FromSeconds(271), items[0].Duration);
        Assert.Null(items[1].Title);
        Assert.Null(items[2].Duration);
        Assert.Null(PlaylistFile.NameIn("#EXTM3U\n../a.flac\n"));
    }

    [Theory]
    [InlineData("../A/01.flac", "A/01.flac")]
    [InlineData("Sub/01.flac", "Playlists/Sub/01.flac")]
    [InlineData("../A/./B/../01.flac", "A/01.flac")]
    [InlineData("..\\A\\01.flac", "A/01.flac")]
    [InlineData("/srv/music/A/01.flac", "A/01.flac")]
    [InlineData("file:///srv/music/A/01%20one.flac", "A/01 one.flac")]
    [InlineData("/srv/other/A/01.flac", null)]
    [InlineData("/srv/musicals/A/01.flac", null)]
    [InlineData("../../etc/passwd", null)]
    [InlineData("http://radio.example/stream", null)]
    [InlineData("C:\\Music\\01.flac", null)]
    public void AnEntryNamesALibraryPathOnlyWhereItIsUnderTheRoot(string location, string? path)
    {
        Assert.Equal(path, PlaylistFile.Resolve(location, "Playlists/Mix.m3u8", Root));
    }

    [Fact]
    public void AMoveRepointsTheEntriesItNamesInTheirOwnStyleAndNothingElse()
    {
        var moved = PlaylistFile.Repoint(
            Foreign,
            "Playlists/Mine.m3u8",
            Root,
            [new PlaylistMove("Fleetwood Mac/Rumours (1977)", "Fleetwood Mac/Rumours (2004)")]);

        Assert.Equal(
            Foreign
                .Replace("../Fleetwood Mac/Rumours (1977)/", "../Fleetwood Mac/Rumours (2004)/", StringComparison.Ordinal)
                .Replace("/srv/music/Fleetwood Mac/Rumours (1977)/", "/srv/music/Fleetwood Mac/Rumours (2004)/", StringComparison.Ordinal),
            moved);
    }

    [Fact]
    public void MovesApplyInOrderSoAChainLandsAtItsEnd()
    {
        const string text = "#EXTM3U\n../A/rip/one.flac\n../A/rip/two.flac\n";

        var moved = PlaylistFile.Repoint(
            text,
            "Playlists/Mix.m3u8",
            Root,
            [new PlaylistMove("A/rip", "A/Album (1999)"), new PlaylistMove("A/Album (1999)/one.flac", "A/Album (1999)/01 - One.flac")]);

        Assert.Equal("#EXTM3U\n../A/Album (1999)/01 - One.flac\n../A/Album (1999)/two.flac\n", moved);
    }

    [Fact]
    public void AMoveThatNamesNothingInTheFileReturnsTheSameText()
    {
        Assert.Same(Foreign, PlaylistFile.Repoint(Foreign, "Playlists/Mine.m3u8", Root, [new PlaylistMove("Other", "Elsewhere")]));

        // A folder whose name only starts the same is another folder.
        Assert.Same(
            Foreign,
            PlaylistFile.Repoint(Foreign, "Playlists/Mine.m3u8", Root, [new PlaylistMove("Fleetwood Mac/Rumours", "X")]));
    }

    /// <summary>What comes before the first entry is the file's own, and stays at the top.</summary>
    [Fact]
    public void ReorderingTakesEachEntrysOwnLinesAlongAndDropsWhatIsLeftOut()
    {
        var arranged = PlaylistFile.Arrange(Foreign, [2, 0]);

        Assert.Equal(
            "#EXTM3U\r\n#PLAYLIST:Mine\r\n# made in foobar2000\r\n"
            + "#EXTINF:-1,Radio\r\nhttp://radio.example/stream\r\n"
            + "#EXTINF:271,Fleetwood Mac - The Chain\r\n../Fleetwood Mac/Rumours (1977)/07 - The Chain.flac\r\n",
            arranged);

        Assert.Throws<ArgumentOutOfRangeException>(() => PlaylistFile.Arrange(Foreign, [0, 0]));
        Assert.Throws<ArgumentOutOfRangeException>(() => PlaylistFile.Arrange(Foreign, [4]));
    }

    [Fact]
    public void SongsAreAppendedAfterTheLastEntryInTheFilesOwnNewline()
    {
        var appended = PlaylistFile.Append(
            "#EXTM3U\r\n../A/01.flac",
            "Mix",
            [new PlaylistLine("B/02.flac", TimeSpan.FromSeconds(60), "B", "Two")]);

        Assert.Equal("#EXTM3U\r\n../A/01.flac\r\n#EXTINF:60,B - Two\r\n../B/02.flac\r\n", appended);
        Assert.Equal(
            PlaylistFile.Render("Mix", [new PlaylistLine("B/02.flac", null, null, "Two")]),
            PlaylistFile.Append("", "Mix", [new PlaylistLine("B/02.flac", null, null, "Two")]));
    }

    [Fact]
    public void ARenameRewritesTheNameLineOrAddsOne()
    {
        Assert.Equal("#EXTM3U\n#PLAYLIST:New\n../A/01.flac\n", PlaylistFile.Renamed("#EXTM3U\n#PLAYLIST:Old\n../A/01.flac\n", "New"));
        Assert.Equal("#EXTM3U\n#PLAYLIST:New\n../A/01.flac\n", PlaylistFile.Renamed("#EXTM3U\n../A/01.flac\n", "New"));
        Assert.Equal("#EXTM3U\n#PLAYLIST:New\n../A/01.flac\n", PlaylistFile.Renamed("../A/01.flac\n", "New"));
        Assert.Equal("#EXTM3U\n#PLAYLIST:New\n", PlaylistFile.Renamed("", "New"));
    }

    private static PlaylistCopy Copy(
        string path,
        AudioQuality? quality,
        IntegrityState integrity = IntegrityState.Intact,
        AudioMeasurement? measurement = null) =>
        new(MediaFileId.New(), path, 1, quality, measurement, integrity);
}
