using Fonoteca.Domain.Acquisition;
using Fonoteca.Domain.Catalogue;

namespace Fonoteca.Domain.Tests;

/// <summary>
/// The rule that stands between a click and somebody's music being taken away.
/// </summary>
public sealed class UpgradeReplacementTests
{
    [Fact]
    public void AHiResDownloadOfAWholeCdAlbumMayReplaceIt()
    {
        Assert.Equal(
            ReplacementVerdict.Replace,
            UpgradeReplacement.Check([Cd(), Cd(), Cd()], 3, [HiRes(), HiRes(), HiRes()]));
    }

    [Fact]
    public void AlossyAlbumIsReplacedByAnyLosslessOne()
    {
        // Tier dominates in AudioQuality.Compare, so a 700kbps FLAC beats a
        // 320kbps MP3 despite the smaller number.
        Assert.Equal(
            ReplacementVerdict.Replace,
            UpgradeReplacement.Check([Mp3(), Mp3()], 2, [Cd(), Cd()]));
    }

    [Fact]
    public void AShortDownloadNeverReplacesAWholeAlbum()
    {
        // Licensing gaps are ordinary on compilations and come back as Skipped
        // rather than as failures. Eleven of twelve is worth having and is not
        // worth replacing twelve with.
        Assert.Equal(
            ReplacementVerdict.Incomplete,
            UpgradeReplacement.Check([Mp3(), Mp3(), Mp3()], 3, [HiRes(), HiRes()]));
    }

    [Fact]
    public void NothingArrivingIsItsOwnAnswer()
    {
        // Distinct from Incomplete because it means the download failed
        // entirely, which is a different thing to tell somebody.
        Assert.Equal(
            ReplacementVerdict.NothingArrived,
            UpgradeReplacement.Check([Mp3()], 1, []));
    }

    [Fact]
    public void AnEqualCopyIsNotAnUpgrade()
    {
        // Re-downloading the CD master already held and deleting the original is
        // a pure loss of provenance for no gain.
        Assert.Equal(
            ReplacementVerdict.NotBetter,
            UpgradeReplacement.Check([Cd(), Cd()], 2, [Cd(), Cd()]));
    }

    [Fact]
    public void TheBestFileHeldIsWhatMustBeBeaten()
    {
        // An album of eleven FLACs and one MP3 is on the upgrade list because of
        // the MP3. Judged against the MP3, a CD-quality download would retire
        // eleven hi-res files — so it is judged against the best of them.
        var mixed = new AudioQuality?[] { HiRes(), HiRes(), Mp3() };

        Assert.Equal(ReplacementVerdict.NotBetter, UpgradeReplacement.Check(mixed, 3, [Cd(), Cd(), Cd()]));
        Assert.Equal(ReplacementVerdict.Replace, UpgradeReplacement.Check(mixed, 3, [Better(), Better(), Better()]));
    }

    [Fact]
    public void TheWorstFileArrivingIsWhatHasToBeatIt()
    {
        // A hi-res album with one CD-quality bonus track does not get to retire a
        // CD-quality album on the strength of its other tracks.
        Assert.Equal(
            ReplacementVerdict.NotBetter,
            UpgradeReplacement.Check([Cd(), Cd(), Cd()], 3, [HiRes(), HiRes(), Cd()]));
    }

    [Fact]
    public void AnUnmeasuredAlbumIsRefusedRatherThanAssumed()
    {
        // A gap in the catalogue is not permission, and it is answerable: the
        // measure pass fills it in. Refusing sends somebody to run that pass.
        Assert.Equal(
            ReplacementVerdict.NotMeasured,
            UpgradeReplacement.Check([null, null], 2, [HiRes(), HiRes()]));

        // One measured file among unmeasured ones is enough to decide, because
        // the comparison is against the best that can be established.
        Assert.Equal(
            ReplacementVerdict.Replace,
            UpgradeReplacement.Check([null, Mp3()], 2, [HiRes(), HiRes()]));
    }

    private static AudioQuality Mp3() => new()
    {
        Codec = "mp3",
        SampleRateHz = 44_100,
        Channels = 2,
        BitrateBps = 320_000,
        IsLossless = false,
    };

    private static AudioQuality Cd() => Lossless(16, 44_100);

    private static AudioQuality HiRes() => Lossless(24, 96_000);

    private static AudioQuality Better() => Lossless(24, 192_000);

    private static AudioQuality Lossless(int depth, int rate) => new()
    {
        Codec = "flac",
        SampleRateHz = rate,
        Channels = 2,
        BitDepth = depth,
        BitrateBps = 900_000,
        IsLossless = true,
    };
}
