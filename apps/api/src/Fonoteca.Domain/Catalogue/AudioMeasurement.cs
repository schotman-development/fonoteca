namespace Fonoteca.Domain.Catalogue;

/// <summary>
/// What decoding a file found about the audio itself: how loud, how compressed,
/// how many bits it really uses, and where it is silent or clipped.
/// </summary>
/// <remarks>
/// <b>Answers, never rankings.</b> The raw readings are stored; what they mean —
/// a ReplayGain, "clipped", "padded to 24 bits" — is a rule over them, so a
/// changed rule needs no second decode.
///
/// Apart from <see cref="AudioQuality"/>, which is what the container declares
/// and the probe pass fills: these come from a second decode through ffmpeg's
/// analysis filters, by the measure pass, and a scan that sees the bytes change
/// clears both.
/// </remarks>
public sealed record AudioMeasurement
{
    /// <summary>EBU R128 integrated loudness, LUFS. Null for audio below the gate — silence.</summary>
    public double? IntegratedLufs { get; init; }

    /// <summary>EBU R128 loudness range, LU.</summary>
    public double? LoudnessRangeLu { get; init; }

    /// <summary>The highest sample, dBFS.</summary>
    public double? SamplePeakDbfs { get; init; }

    /// <summary>The highest inter-sample peak, dBTP — above zero where playback clips.</summary>
    public double? TruePeakDbtp { get; init; }

    /// <summary>How many samples sit at <see cref="SamplePeakDbfs"/>, per channel, averaged over the channels.</summary>
    public long? PeakCount { get; init; }

    /// <summary>Dynamic range as the DR meter reports it, before rounding to "DR13".</summary>
    public double? DynamicRange { get; init; }

    /// <summary>
    /// The bits the samples actually use, which a padded file states as more than it has.
    /// </summary>
    /// <remarks>
    /// Meaningful for lossless audio only: a lossy decode produces whatever depth
    /// the decoder chose.
    /// </remarks>
    public int? EffectiveBitDepth { get; init; }

    /// <summary>Silence before the first sound, seconds; zero where there is none.</summary>
    public double LeadingSilenceSeconds { get; init; }

    /// <summary>Silence after the last sound, seconds; zero where there is none.</summary>
    public double TrailingSilenceSeconds { get; init; }

    /// <summary>The decoded length, seconds, which the silences are measured against.</summary>
    public double? DurationSeconds { get; init; }
}
