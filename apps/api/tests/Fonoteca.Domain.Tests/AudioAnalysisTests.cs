using Fonoteca.Domain.Catalogue;

namespace Fonoteca.Domain.Tests;

public sealed class AudioAnalysisTests
{
    private static readonly AudioQuality TwentyFourBitFlac = new()
    {
        Codec = "flac",
        SampleRateHz = 44_100,
        Channels = 2,
        BitDepth = 24,
        BitrateBps = 1_200_000,
        IsLossless = true,
    };

    /// <summary>16-bit audio in a 24-bit FLAC is decided as the CD rip it is — the owner's choice.</summary>
    [Fact]
    public void APaddedLosslessFileIsDecidedByTheBitsItUses()
    {
        var decided = AudioAnalysis.Decided(TwentyFourBitFlac, new AudioMeasurement { EffectiveBitDepth = 16 });

        Assert.Equal(16, decided!.BitDepth);
        Assert.Equal(QualityTier.LosslessCd, decided.Tier);
        Assert.Equal(16, AudioAnalysis.RealBitDepth(TwentyFourBitFlac, new AudioMeasurement { EffectiveBitDepth = 16 }));
    }

    [Fact]
    public void RealTwentyFourBitAudioAndLossyFilesAreLeftAsTheyAre()
    {
        Assert.Same(TwentyFourBitFlac, AudioAnalysis.Decided(TwentyFourBitFlac, new AudioMeasurement { EffectiveBitDepth = 24 }));
        Assert.Same(TwentyFourBitFlac, AudioAnalysis.Decided(TwentyFourBitFlac, null));

        // Silence uses no bits at all, which says nothing about the recording.
        Assert.Same(TwentyFourBitFlac, AudioAnalysis.Decided(TwentyFourBitFlac, new AudioMeasurement { EffectiveBitDepth = 0 }));

        var mp3 = TwentyFourBitFlac with { Codec = "mp3", IsLossless = false, BitDepth = null };
        Assert.Same(mp3, AudioAnalysis.Decided(mp3, new AudioMeasurement { EffectiveBitDepth = 64 }));
    }

    [Theory]
    [InlineData(0.0, 40L, true)]
    [InlineData(0.0, 2L, false)]
    [InlineData(-0.3, 400L, false)]
    [InlineData(0.8, 2L, true)]
    public void ClippingIsARunAtFullScaleOrALossyDecodeOverIt(double peak, long count, bool clipped) =>
        Assert.Equal(clipped, AudioAnalysis.Clipped(new AudioMeasurement { SamplePeakDbfs = peak, PeakCount = count }));

    [Fact]
    public void TheDynamicRangeIsQuotedRoundedAndAnAlbumsIsEveryTracksMean()
    {
        Assert.Equal(14, AudioAnalysis.DynamicRange(new AudioMeasurement { DynamicRange = 13.78 }));
        Assert.Equal(
            11,
            AudioAnalysis.AlbumDynamicRange([new AudioMeasurement { DynamicRange = 9.6 }, new AudioMeasurement { DynamicRange = 12.1 }]));
        Assert.Null(AudioAnalysis.AlbumDynamicRange([new AudioMeasurement { DynamicRange = 9.6 }, null]));
        Assert.Null(AudioAnalysis.AlbumDynamicRange([new AudioMeasurement { DynamicRange = 9.6 }, new AudioMeasurement()]));
        Assert.Null(AudioAnalysis.AlbumDynamicRange([]));
    }

    [Fact]
    public void ATracksGainBringsItToMinusEighteenLufs()
    {
        var track = new AudioMeasurement { IntegratedLufs = -9.3, SamplePeakDbfs = -0.1 };

        Assert.Equal(-8.7, AudioAnalysis.TrackGain(track));
        Assert.Equal(0.988553, AudioAnalysis.TrackPeak(track));
        Assert.Equal("-8.70 dB", AudioAnalysis.Gain(-8.7));
        Assert.Equal("0.988553", AudioAnalysis.Peak(0.988553));
        Assert.Null(AudioAnalysis.TrackGain(new AudioMeasurement()));
    }

    /// <summary>An album's loudness is its tracks' as one programme; a part-measured album has none.</summary>
    [Fact]
    public void AnAlbumsGainIsItsTracksTogetherAndNeedsEveryTrack()
    {
        AudioMeasurement Track(double lufs, double seconds, double peak) =>
            new() { IntegratedLufs = lufs, DurationSeconds = seconds, SamplePeakDbfs = peak };

        var album = AudioAnalysis.Album([Track(-10, 200, -0.5), Track(-10, 100, -0.1)]);
        Assert.Equal((-8.0, 0.988553), album);

        // A loud long track outweighs a quiet short one, and the peak is the highest.
        var weighted = AudioAnalysis.Album([Track(-8, 300, -1), Track(-20, 60, -6)])!.Value;
        Assert.InRange(weighted.Gain, -10.0, -9.0);

        Assert.Null(AudioAnalysis.Album([Track(-10, 200, -0.5), new AudioMeasurement()]));
        Assert.Null(AudioAnalysis.Album([Track(-10, 200, -0.5), null]));
    }
}
