namespace Fonoteca.Domain.Catalogue;

/// <summary>
/// The measurable audio properties of a file, and the rules for ranking them.
/// </summary>
/// <remarks>
/// Owned by the domain because "which of these files is better" is the decision
/// behind both dedupe (which copy to keep) and upgrade monitoring (is this
/// candidate actually an improvement). It is pure: no I/O, fully unit-testable.
/// </remarks>
public sealed record AudioQuality
{
    public required string Codec { get; init; }
    public required int SampleRateHz { get; init; }
    public required int Channels { get; init; }

    /// <summary>Bits per sample. Null for lossy formats, where it is meaningless.</summary>
    public int? BitDepth { get; init; }

    /// <summary>Average bitrate in bits per second.</summary>
    public required long BitrateBps { get; init; }

    public required bool IsLossless { get; init; }

    public TimeSpan? Duration { get; init; }

    public QualityTier Tier => ClassifyTier();

    private QualityTier ClassifyTier()
    {
        if (!IsLossless)
        {
            return BitrateBps switch
            {
                >= 256_000 => QualityTier.LossyHigh,
                >= 128_000 => QualityTier.LossyStandard,
                _ => QualityTier.LossyLow,
            };
        }

        // "Hi-res" in the sense the industry uses it: better than CD in either
        // depth or rate. Both axes count — 24/44.1 and 16/96 are each above CD.
        var aboveCdDepth = BitDepth is > 16;
        var aboveCdRate = SampleRateHz > 48_000;
        return aboveCdDepth || aboveCdRate ? QualityTier.LosslessHiRes : QualityTier.LosslessCd;
    }

    /// <summary>
    /// Orders two files by desirability. Positive means <paramref name="left"/> is better.
    /// </summary>
    /// <remarks>
    /// Tier dominates, and deliberately so: lossless always beats lossy, whatever
    /// the bitrate. A 320kbps MP3 has a higher number than a 700kbps FLAC and is
    /// still the worse file, which is exactly the trap a naive bitrate sort falls
    /// into.
    ///
    /// Note what is NOT considered. Dynamic range would be a genuinely better
    /// signal of listening quality than sample rate — a loudness-war remaster can
    /// be 24/96 and sound worse than the CD — but measuring it requires decoding
    /// the whole file, so it is deferred until the ingest pipeline is real. Until
    /// then this ranks provenance, not sound.
    /// </remarks>
    public static int Compare(AudioQuality? left, AudioQuality? right)
    {
        if (ReferenceEquals(left, right)) return 0;
        if (left is null) return -1;
        if (right is null) return 1;

        var byTier = ((int)left.Tier).CompareTo((int)right.Tier);
        if (byTier != 0) return byTier;

        if (left.IsLossless && right.IsLossless)
        {
            var byDepth = (left.BitDepth ?? 0).CompareTo(right.BitDepth ?? 0);
            if (byDepth != 0) return byDepth;

            var byRate = left.SampleRateHz.CompareTo(right.SampleRateHz);
            if (byRate != 0) return byRate;

            // Same depth and rate: prefer the smaller file, since for lossless
            // a larger one is just a less efficient encode of identical audio.
            return right.BitrateBps.CompareTo(left.BitrateBps);
        }

        var byBitrate = left.BitrateBps.CompareTo(right.BitrateBps);
        if (byBitrate != 0) return byBitrate;

        return left.SampleRateHz.CompareTo(right.SampleRateHz);
    }

    /// <summary>True when <paramref name="candidate"/> is a genuine improvement on <paramref name="current"/>.</summary>
    public static bool IsUpgrade(AudioQuality current, AudioQuality candidate) =>
        Compare(candidate, current) > 0;
}

/// <summary>
/// Ordered worst to best. The numeric values are the ranking, so a new tier must
/// be inserted at the right position rather than appended.
/// </summary>
public enum QualityTier
{
    Unknown = 0,
    LossyLow = 1,
    LossyStandard = 2,
    LossyHigh = 3,
    LosslessCd = 4,
    LosslessHiRes = 5,
}
