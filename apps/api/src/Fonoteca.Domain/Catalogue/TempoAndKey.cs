using System.Globalization;

namespace Fonoteca.Domain.Catalogue;

/// <summary>
/// What Essentia heard in a file: its tempo and its key, each with how sure it was.
/// </summary>
/// <remarks>
/// <b>Answers, never rankings</b>, as <see cref="AudioMeasurement"/>: every
/// reading is kept whatever Essentia's certainty, and which reach a tag is
/// <see cref="TagBpm"/>'s and <see cref="TagKey"/>'s rule over them, so moving
/// the line needs no second decode. Measured from the file's own audio by the
/// analyse pass, and cleared with the rest by a scan that sees the audio change.
/// </remarks>
public sealed record TempoAndKey
{
    /// <summary>
    /// The certainty below which a tempo is a guess: Essentia's own "very low
    /// confidence, the input signal is hard for the employed candidate beat
    /// trackers" band, [0, 1).
    /// </summary>
    /// <remarks>
    /// The owner's choice. Of 90 songs here, 35 fell below it — nearly all
    /// classical, slow jazz ballads and solo improvisations — and 55 had a tempo
    /// above it that looked right.
    /// </remarks>
    public const double MinimumBpmConfidence = 1;

    /// <summary>Beats per minute, as RhythmExtractor2013 estimates them.</summary>
    public double Bpm { get; init; }

    /// <summary>The multifeature beat tracker's confidence, 0 to 5.32.</summary>
    public double BpmConfidence { get; init; }

    /// <summary>The key's tonic as Essentia spells it: "C", "F#", "Bb".</summary>
    public string? Key { get; init; }

    /// <summary>"major" or "minor".</summary>
    public string? Scale { get; init; }

    /// <summary>How strongly the audio correlates with that key's profile, 0 to 1.</summary>
    public double KeyStrength { get; init; }

    /// <summary>The tempo a tag carries, whole as ID3's <c>TBPM</c> wants it, where Essentia was sure enough.</summary>
    public static int? TagBpm(TempoAndKey? reading) =>
        reading is { Bpm: > 0 and var bpm, BpmConfidence: >= MinimumBpmConfidence }
            ? (int)Math.Round(bpm, MidpointRounding.AwayFromZero)
            : null;

    /// <summary>
    /// The key in ID3's <c>TKEY</c> notation — "C", "F#m", "Bbm" — which Picard
    /// writes under every container's name for it.
    /// </summary>
    /// <remarks>
    /// "The ground keys are represented with "A","B","C","D","E", "F" and "G"
    /// and halfkeys represented with "b" and "#". Minor is represented as "m"."
    /// Anything Essentia might answer outside that — silence correlates with no
    /// key at all — is no key.
    /// </remarks>
    public static string? TagKey(TempoAndKey? reading) =>
        reading is { Key: { Length: 1 or 2 } key, Scale: "major" or "minor", KeyStrength: > 0 }
        && key[0] is >= 'A' and <= 'G'
        && (key.Length == 1 || key[1] is '#' or 'b')
            ? string.Create(CultureInfo.InvariantCulture, $"{key}{(reading.Scale == "minor" ? "m" : "")}")
            : null;
}
