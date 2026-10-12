using System.Globalization;

namespace Fonoteca.Domain.Catalogue;

/// <summary>
/// What an <see cref="AudioMeasurement"/> means: the depth to trust, whether it
/// clips, its dynamic range, and its ReplayGain.
/// </summary>
/// <remarks>
/// Rules over stored readings, so changing one needs no second decode.
/// </remarks>
public static class AudioAnalysis
{
    /// <summary>ReplayGain 2.0's reference loudness, LUFS.</summary>
    public const double ReferenceLufs = -18;

    /// <summary>
    /// The quality to decide by: the header's, with the measured bit depth where
    /// a lossless file uses fewer bits than it declares — the owner's choice.
    /// </summary>
    /// <remarks>
    /// A padded file is the audio it holds, not the container it came in: 16-bit
    /// audio in a 24-bit FLAC is a CD rip for the upgrade list and for choosing
    /// which copy to keep. A lossy file's decoded depth is the decoder's choice
    /// and says nothing, so it never applies there.
    /// </remarks>
    public static AudioQuality? Decided(AudioQuality? quality, AudioMeasurement? measurement) =>
        RealBitDepth(quality, measurement) is { } real ? quality! with { BitDepth = real } : quality;

    /// <summary>The bits a lossless file really uses, where fewer than it declares; otherwise null.</summary>
    public static int? RealBitDepth(AudioQuality? quality, AudioMeasurement? measurement) =>
        quality is { IsLossless: true, BitDepth: { } declared }
        && measurement?.EffectiveBitDepth is > 0 and var used
        && used < declared
            ? used
            : null;

    /// <summary>
    /// Whether the audio clips: a lossy decode over full scale, or a run of
    /// samples sitting at it.
    /// </summary>
    /// <remarks>
    /// ponytail: eight samples at full scale is a threshold, not a measurement of
    /// clipped runs — a mastered peak touches full scale once or twice. A
    /// run-length reading of the waveform is the upgrade if it misjudges.
    /// </remarks>
    public static bool Clipped(AudioMeasurement? measurement) =>
        measurement is { SamplePeakDbfs: { } peak }
        && (peak > 0.01 || (peak >= -0.01 && measurement.PeakCount >= 8));

    /// <summary>The DR figure as it is quoted: a whole number, "DR13".</summary>
    public static int? DynamicRange(AudioMeasurement? measurement) =>
        measurement?.DynamicRange is { } dr ? (int)Math.Round(dr, MidpointRounding.AwayFromZero) : null;

    /// <summary>An album's DR, as the DR database gives one: the mean of its tracks', rounded.</summary>
    /// <remarks>
    /// Null unless every track has a figure, as an album gain is: it goes into
    /// every file of the album, and a mean over part of it is a wrong number there.
    /// </remarks>
    public static int? AlbumDynamicRange(IEnumerable<AudioMeasurement?> tracks)
    {
        ArgumentNullException.ThrowIfNull(tracks);

        var figures = tracks.Select(track => track?.DynamicRange).ToList();

        return figures.Count == 0 || figures.Any(figure => figure is null)
            ? null
            : (int)Math.Round(figures.Average()!.Value, MidpointRounding.AwayFromZero);
    }

    /// <summary>ReplayGain 2.0 track gain, dB: what brings the track to −18 LUFS.</summary>
    public static double? TrackGain(AudioMeasurement? measurement) =>
        measurement?.IntegratedLufs is { } lufs ? Math.Round(ReferenceLufs - lufs, 2) : null;

    /// <summary>The track's peak as ReplayGain states it: the highest sample, as a linear amplitude.</summary>
    public static double? TrackPeak(AudioMeasurement? measurement) =>
        measurement?.SamplePeakDbfs is { } db ? Math.Round(Math.Pow(10, db / 20), 6) : null;

    /// <summary>
    /// ReplayGain for an album: its tracks' loudness as one programme, and the highest peak.
    /// </summary>
    /// <remarks>
    /// Null unless every track is measured — an album gain over part of an album
    /// is a wrong number written into every file of it.
    ///
    /// ponytail: the loudness is the duration-weighted energy mean of the
    /// tracks' integrated loudness, where EBU R128 would gate the whole album as
    /// one signal. They differ by a few tenths of a dB on ordinary albums;
    /// measuring the folder in one decode is the upgrade.
    /// </remarks>
    public static (double Gain, double Peak)? Album(IReadOnlyList<AudioMeasurement?> tracks)
    {
        ArgumentNullException.ThrowIfNull(tracks);

        if (tracks.Count == 0
            || tracks.Any(track => track is not { IntegratedLufs: not null, DurationSeconds: > 0, SamplePeakDbfs: not null }))
        {
            return null;
        }

        var seconds = tracks.Sum(track => track!.DurationSeconds!.Value);
        var energy = tracks.Sum(track => track!.DurationSeconds!.Value * Math.Pow(10, track.IntegratedLufs!.Value / 10));
        var lufs = 10 * Math.Log10(energy / seconds);
        var peak = tracks.Max(track => track!.SamplePeakDbfs!.Value);

        return (Math.Round(ReferenceLufs - lufs, 2), Math.Round(Math.Pow(10, peak / 20), 6));
    }

    /// <summary>A gain as the tags carry it: "-6.23 dB".</summary>
    public static string Gain(double decibels) =>
        decibels.ToString("0.00", CultureInfo.InvariantCulture) + " dB";

    /// <summary>A peak as the tags carry it: "0.988553".</summary>
    public static string Peak(double amplitude) =>
        amplitude.ToString("0.000000", CultureInfo.InvariantCulture);
}
