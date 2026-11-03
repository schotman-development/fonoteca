using Fonoteca.Domain.Catalogue;

namespace Fonoteca.Domain.Tests;

/// <summary>
/// Which stream is re-encoded, to what, and when the file goes out as it is.
/// </summary>
public sealed class TranscodeTests
{
    private static readonly AudioQuality Flac = Quality("flac", 900_000);

    [Theory]
    [InlineData(null, 0)]
    [InlineData("", 0)]
    [InlineData("raw", 0)]
    [InlineData("RAW", 128)]
    [InlineData("flv", 0)]
    public void NothingSmallerAskedForIsTheFile(string? format, int maxBitRate) =>
        Assert.Null(Transcode.For(format, maxBitRate, Flac, seeking: false));

    [Theory]
    [InlineData("opus", 0, StreamCodec.Opus, 160)]
    [InlineData("MP3", 0, StreamCodec.Mp3, 320)]
    [InlineData("aac", 0, StreamCodec.Aac, 256)]
    [InlineData("opus", 96, StreamCodec.Opus, 96)]
    [InlineData(null, 128, StreamCodec.Mp3, 128)]
    [InlineData("flv", 192, StreamCodec.Mp3, 192)]
    [InlineData("mp3", 1000, StreamCodec.Mp3, 320)]
    [InlineData("mp3", 8, StreamCodec.Mp3, 32)]
    public void AFormatOrABitrateIsWhatItIsSentAs(string? format, int maxBitRate, StreamCodec codec, int kbps) =>
        Assert.Equal(new Transcode(codec, kbps), Transcode.For(format, maxBitRate, Flac, seeking: false));

    /// <summary>Re-encoding a file already within what was asked would only lose quality.</summary>
    [Theory]
    [InlineData("mp3", 320, "mp3", 320_000)]
    [InlineData("opus", 128, "opus", 96_000)]
    [InlineData("aac", 500, "aac", 400_000)]
    [InlineData(null, 192, "mp3", 192_000)]
    public void AFileAlreadyWithinItIsSentAsItIs(string? format, int maxBitRate, string codec, long bitrate) =>
        Assert.Null(Transcode.For(format, maxBitRate, Quality(codec, bitrate), seeking: false));

    [Theory]
    [InlineData("mp3", 128, "mp3", 320_000)]
    [InlineData("opus", 320, "mp3", 128_000)]
    [InlineData(null, 128, "aac", 256_000)]
    [InlineData(null, 320, "aac", 256_000)]
    public void AFileOverItOrInAnotherFormatIsReencoded(string? format, int maxBitRate, string codec, long bitrate) =>
        Assert.NotNull(Transcode.For(format, maxBitRate, Quality(codec, bitrate), seeking: false));

    /// <summary>An unprobed file has no rate to be within.</summary>
    [Fact]
    public void AFileNothingProbedIsReencoded() =>
        Assert.Equal(new Transcode(StreamCodec.Mp3, 128), Transcode.For(null, 128, source: null, seeking: false));

    /// <summary>The original cannot start at an offset, so a seek is always a transcode.</summary>
    [Fact]
    public void ASeekIsATranscodeEvenWhereTheFileFits() =>
        Assert.Equal(
            new Transcode(StreamCodec.Mp3, 320),
            Transcode.For("mp3", 320, Quality("mp3", 320_000), seeking: true));

    [Fact]
    public void ASeekOnTheFileIsTheFile() =>
        Assert.Null(Transcode.For("raw", 128, Flac, seeking: true));

    [Theory]
    [InlineData(StreamCodec.Mp3, "audio/mpeg")]
    [InlineData(StreamCodec.Opus, "audio/ogg")]
    [InlineData(StreamCodec.Aac, "audio/aac")]
    public void EachFormatNamesItsMediaType(StreamCodec codec, string mediaType) =>
        Assert.Equal(mediaType, new Transcode(codec, 128).MediaType);

    /// <summary>Over the rate by the measured overrun, and never negative.</summary>
    [Fact]
    public void TheEstimateRunsOverTheRate()
    {
        var estimate = new Transcode(StreamCodec.Opus, 64).EstimatedLength(TimeSpan.FromMinutes(5));

        Assert.InRange(estimate, 300 * 64_000 / 8 * 1.06, 300 * 64_000 / 8 * 1.10);
        Assert.Equal(4096, new Transcode(StreamCodec.Opus, 64).EstimatedLength(TimeSpan.FromSeconds(-3)));
    }

    /// <summary>The most ffmpeg's AAC encoder ran over its rate, at each rate, across sixteen of the library's files.</summary>
    [Theory]
    [InlineData(64, 1.051)]
    [InlineData(128, 1.074)]
    [InlineData(192, 1.063)]
    [InlineData(320, 1.043)]
    public void AnAacEstimateHoldsWhatAacWasMeasuredAt(int kbps, double measured) =>
        Assert.True(
            new Transcode(StreamCodec.Aac, kbps).EstimatedLength(TimeSpan.FromMinutes(5))
            > (300 * kbps * 1000 / 8 * measured) + 4096);

    private static AudioQuality Quality(string codec, long bitrate) => new()
    {
        Codec = codec,
        SampleRateHz = 44_100,
        Channels = 2,
        BitrateBps = bitrate,
        IsLossless = codec == "flac",
        Duration = TimeSpan.FromMinutes(4),
    };
}
