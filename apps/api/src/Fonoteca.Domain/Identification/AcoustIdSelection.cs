using Fonoteca.Domain.Abstractions;
using Fonoteca.Domain.Catalogue;

namespace Fonoteca.Domain.Identification;

/// <summary>
/// Decides which AcoustID, if any, is safe to write into a file.
/// </summary>
/// <remarks>
/// The sibling of <see cref="RecordingCandidates"/>, and reaching for that one
/// instead is the obvious mistake. It collapses clusters onto MusicBrainz
/// <i>recordings</i>, which is the right rule for enrichment and the wrong rule
/// here: this decides the <b>cluster identifier itself</b>, touches MusicBrainz
/// not at all, and its answer is written into somebody's file.
///
/// That last part is why there is a threshold at all, where
/// <see cref="RecordingCandidates"/> deliberately has none. A ranking hands the
/// caller evidence and lets it choose; this <i>is</i> the choosing, and the
/// consequence of choosing wrong is a wrong identifier in a file, which will
/// then be believed by every later pass and by every other tool that reads it.
/// Leaving a file untagged costs a rerun. Tagging it wrongly costs the truth.
///
/// Two conditions, and the second matters more than the first:
///
/// - The best cluster must clear <see cref="DefaultMinimumScore"/>. A weak match
///   is a guess about audio nobody has confidently heard before.
/// - It must also beat every <i>competing</i> cluster by
///   <see cref="DefaultMinimumMargin"/>. Two clusters at 0.95 and 0.94 that mean
///   different audio are AcoustID reporting two candidates and no opinion — a
///   live take against a studio one. Picking the higher is a coin flip dressed
///   up as a decision.
///
/// The word <i>competing</i> carries the whole rule, and reading it as "any
/// other cluster" was worth 925 wrongly-withheld files in the author's library.
/// See <see cref="Competes"/>.
/// </remarks>
public static class AcoustIdSelection
{
    /// <summary>
    /// Below this, nothing is written.
    /// </summary>
    /// <remarks>
    /// Measured rather than picked: a confident match against the real corpus
    /// scored 0.9667, and AcoustID's own client tooling treats the low 0.9s as
    /// the confident band. Configurable, because a library of live bootlegs and
    /// a library of CD rips deserve different answers.
    /// </remarks>
    public const double DefaultMinimumScore = 0.90;

    /// <summary>How far clear of the runner-up the winner has to be.</summary>
    public const double DefaultMinimumMargin = 0.05;

    /// <summary>Which cluster to write, or why none.</summary>
    public static AcoustIdChoice Choose(
        IReadOnlyList<AcoustIdMatch> matches,
        double minimumScore = DefaultMinimumScore,
        double minimumMargin = DefaultMinimumMargin)
    {
        ArgumentNullException.ThrowIfNull(matches);

        if (matches.Count == 0)
        {
            // The ordinary answer for audio nobody has ever submitted, and not an
            // error. It is also worth recording as distinct from a weak match:
            // this file can be re-asked about usefully in a year, when somebody
            // else may have submitted it.
            return new AcoustIdChoice(null, AcoustIdChoiceReason.NoMatch, null);
        }

        // Sorted here rather than trusted from the adapter. The client does
        // return best-first, but a rule that silently depends on its caller
        // having sorted is a rule that breaks the day someone passes a set.
        var ranked = matches
            .OrderByDescending(match => match.Score)
            .ThenBy(match => match.AcoustId)
            .ToList();

        var best = ranked[0];

        if (best.Score < minimumScore)
        {
            return new AcoustIdChoice(null, AcoustIdChoiceReason.BelowThreshold, best.Score);
        }

        var bestRecording = DominantRecording(best);

        foreach (var rival in ranked.Skip(1))
        {
            // Ranked descending, so the first rival outside the margin ends it.
            if (best.Score - rival.Score >= minimumMargin) break;

            if (Competes(bestRecording, rival))
            {
                return new AcoustIdChoice(null, AcoustIdChoiceReason.Ambiguous, best.Score);
            }
        }

        return new AcoustIdChoice(new AcoustId(best.AcoustId), AcoustIdChoiceReason.Confident, best.Score);
    }

    /// <summary>
    /// Is this near-tied cluster a rival answer, or the same answer again?
    /// </summary>
    /// <remarks>
    /// The question a margin is really asking is "might this file be some other
    /// audio", and cluster scores answer a different one. AcoustID clusters
    /// fingerprints, and one recording routinely spans several clusters — a
    /// lossless rip and a 128kbps rip that were never merged. Two clusters
    /// naming the same recording are therefore one answer arriving twice, and
    /// treating that as a disagreement withholds a file over nothing.
    ///
    /// Measured before it was believed: of 925 files this rule had refused to
    /// tag, the near-tied rival named the same recording in 794, and shared some
    /// recording with the winner in 852. What remains once those are set aside
    /// is the real thing — a live take against a studio one, a specific session
    /// against a generic entry — and stays ambiguous, which is the point.
    ///
    /// Two cases deserve their own sentence:
    ///
    /// - <b>A cluster with no MusicBrainz link does not compete.</b> It asserts
    ///   nothing about what the audio is, so it cannot contradict a winner that
    ///   does. 46 further files turned on this alone, and every one of them
    ///   resolved to the recording its filename already claimed.
    /// - <b>Unless the winner is unlinked too</b>, in which case there is
    ///   nothing to reason with and the plain margin applies. Nothing in the
    ///   author's library depends on this; it is here so the rule degrades to
    ///   the conservative answer instead of to a coin flip.
    ///
    /// Deliberately <i>not</i> <c>RecordingCandidates</c>, which collapses the
    /// other way — onto recordings — and so turns one cluster legitimately
    /// linked to several recordings into a zero margin. Measured too: it breaks
    /// 64 of 200 files that identify confidently today.
    ///
    /// <see cref="AcoustIdRecordingRef.Sources"/> is deliberately not consulted.
    /// Letting a better-supported cluster outvote a near-tied one recovers a
    /// further 36 files and decides live-against-studio by popularity — it tags
    /// a track from an album called <i>Live</i> with the studio recording
    /// because that one has more submissions. Whatever sources are good for,
    /// they are not good for this.
    /// </remarks>
    private static bool Competes(Mbid? bestRecording, AcoustIdMatch rival)
    {
        if (bestRecording is null) return true;

        var rivalRecording = DominantRecording(rival);

        return rivalRecording is not null && rivalRecording != bestRecording;
    }

    /// <summary>
    /// The recording this cluster is most agreed to be, or null if nobody has said.
    /// </summary>
    /// <remarks>
    /// Most submissions wins, because a cluster is often linked to several
    /// recordings by people who disagreed, and the long tail of those is
    /// somebody's one-off mistagging. Ties broken by id so a rerun cannot change
    /// its mind, the same discipline as the ranking above.
    /// </remarks>
    private static Mbid? DominantRecording(AcoustIdMatch match) =>
        match.Recordings.Count == 0
            ? null
            : match.Recordings
                .OrderByDescending(recording => recording.Sources)
                .ThenBy(recording => recording.Id.Value)
                .First()
                .Id;
}

/// <summary>The decision, and the evidence for it.</summary>
/// <remarks>
/// <see cref="Score"/> is populated even when nothing was chosen, because "we
/// looked and the best was 0.62" and "we looked and found nothing" are different
/// facts about a file and lead to different follow-ups.
/// </remarks>
public readonly record struct AcoustIdChoice(
    AcoustId? Value,
    AcoustIdChoiceReason Reason,
    double? Score)
{
    public bool IsConfident => Reason == AcoustIdChoiceReason.Confident;
}

/// <summary>Why a choice was or was not made.</summary>
public enum AcoustIdChoiceReason
{
    /// <summary>One cluster, clear of the threshold and clear of the field.</summary>
    Confident = 0,

    /// <summary>AcoustID has never heard this audio.</summary>
    NoMatch = 1,

    /// <summary>Something matched, but not well enough to write down.</summary>
    BelowThreshold = 2,

    /// <summary>Two clusters too close together to choose between.</summary>
    Ambiguous = 3,
}
