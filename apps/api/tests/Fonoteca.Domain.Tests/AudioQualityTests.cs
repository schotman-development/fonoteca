using Fonoteca.Domain.Catalogue;

namespace Fonoteca.Domain.Tests;

/// <summary>
/// No database, no filesystem, no fixtures. If a rule cannot be tested this
/// plainly, it is on the wrong side of the domain boundary.
/// </summary>
public sealed class AudioQualityTests
{
    private static AudioQuality Flac(int bitDepth, int sampleRate, long bitrate = 900_000) =>
        new()
        {
            Codec = "flac",
            SampleRateHz = sampleRate,
            Channels = 2,
            BitDepth = bitDepth,
            BitrateBps = bitrate,
            IsLossless = true,
        };

    private static AudioQuality Mp3(long bitrate) =>
        new()
        {
            Codec = "mp3",
            SampleRateHz = 44_100,
            Channels = 2,
            BitDepth = null,
            BitrateBps = bitrate,
            IsLossless = false,
        };

    [Theory]
    [InlineData(320_000, QualityTier.LossyHigh)]
    [InlineData(256_000, QualityTier.LossyHigh)]
    [InlineData(192_000, QualityTier.LossyStandard)]
    [InlineData(128_000, QualityTier.LossyStandard)]
    [InlineData(96_000, QualityTier.LossyLow)]
    public void LossyIsTieredByBitrate(long bitrate, QualityTier expected) =>
        Assert.Equal(expected, Mp3(bitrate).Tier);

    [Fact]
    public void CdQualityLosslessIsItsOwnTier() =>
        Assert.Equal(QualityTier.LosslessCd, Flac(16, 44_100).Tier);

    [Theory]
    [InlineData(24, 44_100)] // greater depth alone
    [InlineData(16, 96_000)] // greater rate alone
    [InlineData(24, 192_000)]
    public void AnythingAboveCdOnEitherAxisIsHiRes(int bitDepth, int sampleRate) =>
        Assert.Equal(QualityTier.LosslessHiRes, Flac(bitDepth, sampleRate).Tier);

    /// <summary>
    /// The trap a naive bitrate comparison falls into: 320,000 is a bigger
    /// number than a low-bitrate FLAC's, and the FLAC is still the better file.
    /// </summary>
    [Fact]
    public void LosslessAlwaysBeatsLossyRegardlessOfBitrate()
    {
        var lowBitrateFlac = Flac(16, 44_100, bitrate: 400_000);
        var maximumMp3 = Mp3(320_000);

        Assert.True(AudioQuality.Compare(lowBitrateFlac, maximumMp3) > 0);
        Assert.True(AudioQuality.IsUpgrade(current: maximumMp3, candidate: lowBitrateFlac));
        Assert.False(AudioQuality.IsUpgrade(current: lowBitrateFlac, candidate: maximumMp3));
    }

    [Fact]
    public void HigherBitDepthWinsWithinTheSameTier() =>
        Assert.True(AudioQuality.Compare(Flac(24, 96_000), Flac(16, 96_000)) > 0);

    [Fact]
    public void BitDepthOutranksSampleRate()
    {
        // 24/48 beats 16/192: depth is compared first.
        Assert.True(AudioQuality.Compare(Flac(24, 48_000), Flac(16, 192_000)) > 0);
    }

    /// <summary>
    /// For lossless, a bigger file at identical depth and rate is just a less
    /// efficient encode of the same audio — so the smaller one wins.
    /// </summary>
    [Fact]
    public void IdenticalLosslessPrefersTheSmallerFile()
    {
        var efficient = Flac(16, 44_100, bitrate: 700_000);
        var bloated = Flac(16, 44_100, bitrate: 1_100_000);

        Assert.True(AudioQuality.Compare(efficient, bloated) > 0);
    }

    [Fact]
    public void ComparisonIsAntisymmetric()
    {
        AudioQuality[] all =
        [
            Mp3(128_000), Mp3(320_000), Flac(16, 44_100), Flac(24, 96_000), Flac(24, 192_000),
        ];

        foreach (var a in all)
        {
            foreach (var b in all)
            {
                Assert.Equal(
                    Math.Sign(AudioQuality.Compare(a, b)),
                    -Math.Sign(AudioQuality.Compare(b, a)));
            }
        }
    }

    [Fact]
    public void MissingQualitySortsBelowAnythingKnown()
    {
        Assert.True(AudioQuality.Compare(Mp3(96_000), null) > 0);
        Assert.True(AudioQuality.Compare(null, Mp3(96_000)) < 0);
        Assert.Equal(0, AudioQuality.Compare(null, null));
    }

    [Fact]
    public void UpgradeRequiresStrictImprovement()
    {
        var same = Flac(16, 44_100);
        Assert.False(AudioQuality.IsUpgrade(current: same, candidate: Flac(16, 44_100)));
    }
}
