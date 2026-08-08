using Fonoteca.Domain.Abstractions;
using Fonoteca.Domain.Catalogue;

namespace Fonoteca.Domain.Identification;

/// <summary>
/// Turns a raw AcoustID answer into a ranked list of recordings it could be.
/// </summary>
/// <remarks>
/// A lookup does not return one answer per file. It returns AcoustID
/// <i>clusters</i>, and the mapping between clusters and MusicBrainz recordings
/// is many-to-many in both directions: one cluster can be linked to several
/// recordings (a track and its remaster, submitted by people who disagreed
/// about which it was), and one recording can appear under several clusters
/// (a lossless rip and a 128kbps rip that never got merged).
///
/// So the question "which recording is this file" is not answered by
/// <c>results[0].recordings[0]</c>, which is what a naive client reads and what
/// makes it tag a remaster as the original. It is answered by collapsing every
/// cluster onto the recordings behind them, which is what this does.
///
/// Pure, and here rather than in the adapter, for the reason the whole domain
/// is: this is the rule that decides what a file becomes, and it needs to be
/// arguable and testable without a network.
/// </remarks>
public static class RecordingCandidates
{
    /// <summary>
    /// Every recording the matches point at, strongest first.
    /// </summary>
    /// <remarks>
    /// Ranked by fingerprint score first and submission count second. Score
    /// leads because it is evidence about <i>this audio</i>, while sources are
    /// evidence about what other people believed; a poor acoustic match backed
    /// by a thousand submissions is still a poor acoustic match.
    ///
    /// Sources are summed across clusters rather than maximised, because two
    /// clusters linked to the same recording were submitted by two separate
    /// populations — that really is more agreement, not the same agreement
    /// counted twice. Score is maximised, not averaged, for the mirror-image
    /// reason: a second, weaker cluster is not evidence against the strong one.
    ///
    /// No threshold is applied. Where to stop believing a score is policy that
    /// depends on what happens next — proposing a tag edit and writing one
    /// deserve different answers — so it belongs to the caller.
    /// </remarks>
    public static IReadOnlyList<RecordingCandidate> From(IEnumerable<AcoustIdMatch> matches)
    {
        ArgumentNullException.ThrowIfNull(matches);

        var byRecording = new Dictionary<Mbid, (double Score, int Sources)>();

        foreach (var match in matches)
        {
            foreach (var recording in match.Recordings)
            {
                if (byRecording.TryGetValue(recording.Id, out var running))
                {
                    byRecording[recording.Id] = (
                        Math.Max(running.Score, match.Score),
                        running.Sources + recording.Sources);
                }
                else
                {
                    byRecording[recording.Id] = (match.Score, recording.Sources);
                }
            }
        }

        return byRecording
            .Select(entry => new RecordingCandidate(entry.Key, entry.Value.Score, entry.Value.Sources))
            .OrderByDescending(candidate => candidate.Score)
            .ThenByDescending(candidate => candidate.Sources)
            // Ties broken by id so the ranking is a function of its input alone.
            // Without it the order depends on dictionary iteration, and a rerun
            // of the same identification can pick a different recording.
            .ThenBy(candidate => candidate.Id.Value)
            .ToList();
    }
}

/// <summary>A recording this audio might be, with the evidence for it.</summary>
public sealed record RecordingCandidate(
    Mbid Id,

    /// <summary>Best acoustic match across the clusters pointing at this recording, 0 to 1.</summary>
    double Score,

    /// <summary>Total submissions linking this audio to this recording.</summary>
    int Sources);
