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
/// - It must also beat the runner-up by <see cref="DefaultMinimumMargin"/>. Two
///   clusters at 0.95 and 0.94 are not one good answer and one bad one; they are
///   AcoustID saying it has two candidates and no opinion — usually a track that
///   appears on both an album and a compilation. Picking the higher one is a coin
///   flip dressed up as a decision.
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

        if (ranked.Count > 1 && best.Score - ranked[1].Score < minimumMargin)
        {
            return new AcoustIdChoice(null, AcoustIdChoiceReason.Ambiguous, best.Score);
        }

        return new AcoustIdChoice(new AcoustId(best.AcoustId), AcoustIdChoiceReason.Confident, best.Score);
    }
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
