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

    private static PlaylistCopy Copy(
        string path,
        AudioQuality? quality,
        IntegrityState integrity = IntegrityState.Intact,
        AudioMeasurement? measurement = null) =>
        new(MediaFileId.New(), path, 1, quality, measurement, integrity);
}
