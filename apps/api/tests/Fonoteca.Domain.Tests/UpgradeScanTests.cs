using Fonoteca.Domain.Acquisition;
using Fonoteca.Domain.Catalogue;

namespace Fonoteca.Domain.Tests;

/// <summary>
/// The rule behind the upgrade list on the acquire screen.
/// </summary>
public sealed class UpgradeScanTests
{
    [Theory]
    [InlineData("Artist/Album/01 Track.flac", true)]
    [InlineData("Artist/Album/01 Track.FLAC", true)]
    [InlineData("Artist/Album/01 Track.mp3", false)]
    [InlineData("Artist/Album/01 Track.ogg", false)]
    public void AnUnprobedFileIsJudgedByItsExtension(string path, bool lossless)
    {
        Assert.Equal(lossless, UpgradeScan.IsLossless(path, measured: null));
    }

    [Fact]
    public void AMeasurementOutranksTheName()
    {
        // The forty ID3-prefixed FLACs and every mis-extended container: a
        // decoder looked at these bytes and a filename did not.
        Assert.True(UpgradeScan.IsLossless("Artist/Album/01 Track.mp3", Measured("flac", true)));
        Assert.False(UpgradeScan.IsLossless("Artist/Album/01 Track.flac", Measured("mp3", false)));
    }

    [Fact]
    public void AnUnprobedM4aReadsAsLossy()
    {
        // ALAC about as often as AAC, and the extension cannot say which. An
        // ALAC album on the list costs a click; an AAC album missing from it is
        // an upgrade nobody is offered.
        Assert.False(UpgradeScan.IsLossless("Artist/Album/01 Track.m4a", measured: null));
    }

    [Theory]
    [InlineData("Artist/Album/01 Track.mp3", "MP3")]
    [InlineData("Artist/Album.2011/01 Track", "")]
    [InlineData("Artist/Album/no extension", "")]
    public void TheFormatOfAnUnmeasuredFileIsJustItsExtension(string path, string expected)
    {
        // The middle case is the one worth pinning: a dot in a directory name is
        // not this file's extension, and slicing on it prints half a path.
        Assert.Equal(expected, UpgradeScan.Format(path, measured: null));
        Assert.False(UpgradeScan.IsLossless(path, measured: null));
    }

    [Fact]
    public void AMeasuredLosslessFileCarriesItsDepthAndRate()
    {
        // The whole reason a FLAC row is on the list, so it has to be on the row.
        Assert.Equal(
            "FLAC 16/44.1",
            UpgradeScan.Format("Artist/Album/01.flac", Lossless(16, 44_100)));

        Assert.Equal(
            "FLAC 24/96",
            UpgradeScan.Format("Artist/Album/01.flac", Lossless(24, 96_000)));
    }

    [Fact]
    public void AMeasuredLossyFileDoesNotPretendToADepth()
    {
        // BitDepth is null on lossy formats where it is meaningless, and a rate
        // beside MP3 reads as a claim about quality the number cannot make.
        Assert.Equal("MP3", UpgradeScan.Format("Artist/Album/01.mp3", Measured("mp3", false)));
    }

    [Fact]
    public void AnUnprobedLosslessFileIsNoAnswerRatherThanAMaybe()
    {
        // The distinction the whole list rests on. A library nobody has probed
        // shows its lossy half and says nothing about the rest, instead of
        // reporting every FLAC as a question.
        Assert.Equal(
            UpgradeReason.None,
            UpgradeScan.Assess("Artist/Album/01.flac", measured: null));

        Assert.Equal(
            UpgradeReason.Lossy,
            UpgradeScan.Assess("Artist/Album/01.mp3", measured: null));
    }

    [Theory]
    [InlineData(16, 44_100, UpgradeReason.BelowHiRes)]
    [InlineData(16, 48_000, UpgradeReason.BelowHiRes)]
    [InlineData(24, 44_100, UpgradeReason.None)]
    [InlineData(16, 96_000, UpgradeReason.None)]
    [InlineData(24, 192_000, UpgradeReason.None)]
    public void HiResIsBetterThanCdOnEitherAxis(int depth, int rate, UpgradeReason expected)
    {
        // Not a second copy of the rule — AudioQuality.Tier already counts depth
        // and rate separately, so 24/44.1 and 16/96 are each above CD.
        Assert.Equal(expected, UpgradeScan.Assess("Artist/Album/01.flac", Lossless(depth, rate)));
    }

    [Fact]
    public void TheReasonsAreOrderedByHowMuchBetterTheReplacementIs()
    {
        // ListUpgrades sorts on this and Max() picks an album's reason with it.
        // The list read correctly for a while by accident, because "Lossy" also
        // happens to sort after "BelowHiRes" alphabetically.
        Assert.True(UpgradeReason.Lossy > UpgradeReason.BelowHiRes);
        Assert.True(UpgradeReason.BelowHiRes > UpgradeReason.None);
    }

    [Fact]
    public void AMeasurementCanPutALosslessNameOnTheListAndTakeALossyOneOff()
    {
        // Both directions, because the extension is the thing being overruled.
        Assert.Equal(
            UpgradeReason.BelowHiRes,
            UpgradeScan.Assess("Artist/Album/01.mp3", Lossless(16, 44_100)));

        Assert.Equal(
            UpgradeReason.Lossy,
            UpgradeScan.Assess("Artist/Album/01.flac", Measured("mp3", false)));
    }

    [Theory]
    [InlineData("Artist/Album/CD1/01.flac", "Artist/Album")]
    [InlineData("Artist/Album/01.flac", "Artist/Album")]
    [InlineData("Artist/01.flac", "Artist")]
    [InlineData("01.flac", "01.flac")]
    public void TheAlbumFolderIsTwoSegmentsDeep(string path, string expected)
    {
        // The depth the by-hand matching screen cuts at, so CD1 and CD2 of one
        // set collapse onto one row rather than becoming two albums to buy.
        Assert.Equal(expected, UpgradeScan.AlbumFolder(path));
    }

    [Theory]
    [InlineData("Quitter (2023)", "Quitter")]
    [InlineData("Beethoven: Symphonies [FLAC 24-44.1]", "Beethoven: Symphonies")]
    [InlineData("Live (2008) [remaster]", "Live")]
    [InlineData("Rumours", "Rumours")]
    [InlineData("(2019)", "")]
    public void ASearchTermDropsWhatRippersPutInBrackets(string folder, string expected)
    {
        // The heading keeps the folder's own name; only the query is cleaned.
        Assert.Equal(expected, UpgradeScan.Searchable(folder));
    }

    [Fact]
    public void EveryExtensionItCallsLosslessIsOneTheScanWouldHaveCatalogued()
    {
        // The testable half of a two-list problem. Whether a *new* extension in
        // AudioFormats is lossless is a fact only a person knows, so nothing can
        // assert it — but an extension this rule calls lossless that the scan
        // does not catalogue is dead weight, and one spelled differently in the
        // two files is a rule that never fires. Comparing against a third
        // hand-copy of the same list, which is what this used to do, asserts
        // nothing at all: add "shn" to AudioFormats and both sides answer false.
        Assert.Empty(UpgradeScan.LosslessExtensions.Except(AudioFormats.Extensions));

        // And the families that matter are named, so deleting one is a failure
        // rather than a silently shorter list.
        Assert.Subset(
            UpgradeScan.LosslessExtensions.ToHashSet(StringComparer.OrdinalIgnoreCase),
            new HashSet<string>(
                ["flac", "wav", "alac", "ape", "dsf"], StringComparer.OrdinalIgnoreCase));

        // m4a is on neither side of the split, deliberately.
        Assert.DoesNotContain("m4a", UpgradeScan.LosslessExtensions);
    }

    private static AudioQuality Lossless(int depth, int rate) => new()
    {
        Codec = "flac",
        SampleRateHz = rate,
        Channels = 2,
        BitDepth = depth,
        BitrateBps = 900_000,
        IsLossless = true,
    };

    private static AudioQuality Measured(string codec, bool lossless) => new()
    {
        Codec = codec,
        SampleRateHz = 44_100,
        Channels = 2,
        BitrateBps = 320_000,
        IsLossless = lossless,
    };
}
