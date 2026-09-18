using Fonoteca.Domain.Abstractions;
using Fonoteca.Domain.Catalogue;

namespace Fonoteca.Domain.Identification;

/// <summary>
/// Decides which release each file in a set actually came from.
/// </summary>
/// <remarks>
/// The third rule in this namespace and the first that cannot answer one file at
/// a time. <see cref="AcoustIdSelection"/> and <see cref="RecordingCandidates"/>
/// both take evidence about a single file and return a single answer; a lone
/// file simply does not contain the information. A recording of <i>Sloe Gin</i>
/// appears on the album, on two compilations, on a remaster and on six regional
/// pressings, and nothing about that one file prefers any of them. Eleven files
/// filling eleven of eleven tracks prefer exactly one.
///
/// So the unit here is a <b>set</b>, and the answer is an assignment across it.
///
/// <b>Folders are not consulted, and this is the point of the whole rule.</b>
/// It would be far easier to take the directory as the grouping and ask only
/// which release it names — but the directory is a claim made by whatever wrote
/// the files, and it is wrong in ways that matter: it dates <i>Off the Wall</i>
/// to 1979, the year of the release group, when the audio present is the 2015
/// remaster. The folder is worth checking the answer against afterwards. It is
/// not worth deriving the answer from.
///
/// <b>Why gates come before size.</b> The obvious greedy — repeatedly take the
/// release explaining the most files — is wrong, and measurably so. Run over one
/// artist's 389 files it let a thirty-one-track bootleg <i>Greatest Hits</i>
/// (coverage 0.55, mean drift 1.99s) claim seventeen files out of four different
/// albums before any of those albums was considered, leaving one of them holding
/// six of its own eleven tracks. Requiring a fit to be <i>good</i> before asking
/// whether it is <i>big</i> removes the bootleg entirely and returns every album
/// to full coverage. Set cover is not the problem being solved here; the problem
/// is that most candidate releases are wrong and only a few are worth ranking.
///
/// <b>Why the gates then relax.</b> One ladder rung would either refuse every
/// incomplete rip or admit every compilation. Running strictest-first and letting
/// each pass claim what it is sure of means a confident album takes its tracks
/// before a weaker candidate is ever allowed to bid for them, which is what
/// protects the albums from the compilations rather than any rule about
/// compilations.
/// </remarks>
public static class ReleaseAttribution
{
    /// <summary>
    /// How close two fits must be to count as indistinguishable.
    /// </summary>
    /// <remarks>
    /// Pressings of one album usually share a track list exactly, so their drifts
    /// are identical to the tick and a plain equality would do. The tolerance is
    /// for the case that is not quite that — a reissue whose printed lengths were
    /// re-entered by hand and land a frame apart. Fifty milliseconds is below
    /// anything that distinguishes two masterings and above anything that
    /// distinguishes two spreadsheets.
    /// </remarks>
    private static readonly TimeSpan TieTolerance = TimeSpan.FromMilliseconds(50);

    /// <summary>
    /// Which release each file came from, or why the question was refused.
    /// </summary>
    /// <remarks>
    /// Every file in <paramref name="files"/> appears in the result exactly once.
    /// Refusing is a first-class answer: a file no release explains well comes
    /// back <see cref="ReleaseAttributionOutcome.NoConfidentFit"/> rather than
    /// attached to whichever anthology scored least badly, because a wrong album
    /// is worse than a missing one and is far harder to notice.
    /// </remarks>
    public static IReadOnlyList<ReleaseAssignment> Assign(
        IReadOnlyCollection<AttributionFile> files,
        IReadOnlyCollection<MusicBrainzRelease> candidates,
        AttributionThresholds? thresholds = null)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(candidates);

        var gates = Ladder(thresholds ?? AttributionThresholds.Default);
        var assignments = new Dictionary<MediaFileId, ReleaseAssignment>();
        var unassigned = files.ToDictionary(file => file.Id);

        foreach (var gate in gates)
        {
            // Each rung is worked to exhaustion before the next relaxes it, so a
            // release that is certain claims its files while the standard is
            // still high enough to keep the pretenders out.
            while (unassigned.Count > 0)
            {
                var ranked = candidates
                    .Select(release => ReleaseFit.For(release, unassigned.Values))
                    .OfType<ReleaseFit>()
                    .Where(fit => Passes(fit, gate))
                    .ToList();

                if (ranked.Count == 0) break;

                var winner = ranked.OrderByDescending(Weight)
                    .ThenByDescending(fit => fit.Coverage)
                    .ThenBy(fit => fit.MeanDrift ?? TimeSpan.MaxValue)
                    .ThenByDescending(fit => fit.IsOfficial)
                    .ThenBy(fit => fit.ReleaseId.Value)
                    .First();

                var tied = ranked.Where(fit => Indistinguishable(fit, winner)).ToList();
                var chosen = Preferred(tied);

                foreach (var assignment in Resolve(chosen, tied))
                {
                    assignments[assignment.File] = assignment;
                    unassigned.Remove(assignment.File);
                }
            }
        }

        // Whatever survives every rung is refused, and the two reasons are worth
        // telling apart: MusicBrainz holding no release for this recording is a
        // fact about the database, while a poor fit is a fact about this library.
        var reachable = candidates
            .SelectMany(release => release.Tracks)
            .Select(track => track.RecordingId)
            .OfType<Mbid>()
            .ToHashSet();

        foreach (var file in unassigned.Values)
        {
            assignments[file.Id] = ReleaseAssignment.Refused(
                file.Id,
                reachable.Contains(file.Recording)
                    ? ReleaseAttributionOutcome.NoConfidentFit
                    : ReleaseAttributionOutcome.NoCandidate);
        }

        return files.Select(file => assignments[file.Id]).ToList();
    }

    /// <summary>
    /// Seats a refused file on a gap in an album its folder was filed under, when
    /// the file's own AcoustID cluster names the recording that gap prints.
    /// </summary>
    /// <remarks>
    /// <b>The commonest way one song goes missing from an album that matched.</b>
    /// An AcoustID cluster is routinely linked to a dozen recordings — the master,
    /// the alternate take, the remaster, each compilation's duplicate entry — and
    /// enrichment has to name one, so it names the most-submitted. <i>Kind of
    /// Blue</i>'s "Blue in Green" came out as "Blue in Green (Take 1)": 324
    /// submissions against the album's own recording's 4, one cluster, the same
    /// audio. <see cref="Assign"/> matches on the recording MBID, so eight files
    /// fill eight tracks and the ninth is refused beside the one empty slot it
    /// belongs on.
    ///
    /// Nothing here reopens the choice of album. It runs after <see cref="Assign"/>
    /// and reaches only releases that rule already chose on the strength of the
    /// other files, and only slots none of them took. The cluster is the evidence
    /// that this audio is that recording, and the album is what says which of the
    /// cluster's recordings is meant — neither alone would do.
    ///
    /// Refused unless the answer is single in both directions: one gap for the
    /// file, one file for the gap. An album printing a studio take and a live one,
    /// both linked to one cluster, is a real disagreement; two files wanting one
    /// gap is a duplicate encoding, which the gap cannot tell apart.
    ///
    /// The drift gate is the caller's, the loosest the album itself could have been
    /// admitted under, and not the strictest rung. Measured on the target library,
    /// <i>L-O-V-E</i>'s filed tracks sit 1.39 to 1.44s off their printed lengths
    /// and its left-out ones 1.4 to 2.8s: a strict gate refuses a track for
    /// drifting exactly as its siblings do. What it still refuses is a different
    /// performance — <i>Guess Who</i>'s leftover is 6.2s out against siblings at
    /// 0.05s.
    /// </remarks>
    /// <param name="linked">
    /// For each file with evidence, every recording its own cluster is linked to.
    /// A file absent from the map is left as it was.
    /// </param>
    public static IReadOnlyList<ReleaseAssignment> Reseat(
        IReadOnlyList<ReleaseAssignment> assignments,
        IReadOnlyCollection<AttributionFile> files,
        IReadOnlyCollection<MusicBrainzRelease> candidates,
        IReadOnlyDictionary<MediaFileId, IReadOnlySet<Mbid>> linked,
        AttributionThresholds? thresholds = null)
    {
        ArgumentNullException.ThrowIfNull(assignments);
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(linked);

        var loosest = (thresholds ?? AttributionThresholds.Default).MaximumDrift;
        var durations = files.ToDictionary(file => file.Id, file => file.Duration);

        // Only an album filed without a tie. An edition chosen from several was
        // checked to agree about the other files' positions, not this one's.
        var filed = assignments
            .Where(a => a.Release is not null && a.Outcome == ReleaseAttributionOutcome.Attributed)
            .ToList();

        var gaps = candidates
            .Where(release => filed.Exists(a => a.Release == release.Id))
            .SelectMany(release => release.Tracks
                .Where(track => track.RecordingId is not null
                    && !filed.Exists(a => a.Release == release.Id
                        && a.DiscNumber == track.DiscNumber
                        && a.Position == track.Position))
                .Select(track => (Release: release, Track: track)))
            .ToList();

        var wanted = assignments
            .Where(a => a.Release is null
                && a.Outcome is ReleaseAttributionOutcome.NoConfidentFit or ReleaseAttributionOutcome.NoCandidate
                && linked.ContainsKey(a.File))
            .Select(a => (a.File, Gaps: gaps
                .Where(gap => linked[a.File].Contains(gap.Track.RecordingId!.Value)
                    && (DriftOf(durations.GetValueOrDefault(a.File), gap.Track.Length) is not { } drift
                        || drift <= loosest))
                .ToList()))
            .Where(want => want.Gaps.Count == 1)
            .Select(want => (want.File, Gap: want.Gaps[0]))
            .ToList();

        var seated = wanted
            .GroupBy(want => (want.Gap.Release.Id, want.Gap.Track.DiscNumber, want.Gap.Track.Position))
            .Where(claim => claim.Count() == 1)
            .Select(claim => claim.Single())
            .ToDictionary(
                want => want.File,
                want =>
                    new ReleaseAssignment(
                        want.File,
                        want.Gap.Release.Id,
                        want.Gap.Release.ReleaseGroupId,
                        want.Gap.Track.DiscNumber,
                        want.Gap.Track.Position,
                        ReleaseAttributionOutcome.Attributed,
                        0));

        return [.. assignments.Select(a => seated.GetValueOrDefault(a.File, a))];
    }

    private static TimeSpan? DriftOf(TimeSpan? measured, TimeSpan? printed) =>
        measured is { } left && printed is { } right ? (left - right).Duration() : null;

    /// <summary>
    /// Turns a winning fit — and the editions that matched it just as well —
    /// into one answer per file.
    /// </summary>
    /// <remarks>
    /// Where all three of the possible answers live, and the question that
    /// separates them is not "how many editions tied" but <b>what the tie
    /// actually costs</b>.
    ///
    /// Three pressings of one album normally carry the same track list. Choosing
    /// between them by a stated tie-break loses nothing that anyone could later
    /// observe, because every fact this catalogue stores about the file — its
    /// disc, its position, its title — is the same whichever is picked. That is
    /// <see cref="ReleaseAttributionOutcome.AttributedAmbiguously"/>: the choice
    /// is recorded along with how many editions it was chosen from, so it can be
    /// reviewed, but the file is filed.
    ///
    /// A two-disc original tying with a nineteen-track single-disc reissue is a
    /// different matter. Picking one asserts a disc and a track number that the
    /// other flatly contradicts, and there is no evidence between them. Then the
    /// release is dropped and only the release group kept
    /// (<see cref="ReleaseAttributionOutcome.GroupOnly"/>) — the album is known,
    /// the pressing is not, and the catalogue says exactly that rather than
    /// inventing a position.
    ///
    /// And if the tied editions do not even share a release group, nothing
    /// survives the tie and the file is refused. That is rare and it should be:
    /// it means two different albums fit identically.
    /// </remarks>
    private static IEnumerable<ReleaseAssignment> Resolve(
        ReleaseFit chosen,
        List<ReleaseFit> tied)
    {
        var alternatives = tied.Count - 1;

        if (alternatives == 0)
        {
            foreach (var match in chosen.Matches)
            {
                yield return ReleaseAssignment.On(match, chosen.Release, ReleaseAttributionOutcome.Attributed, 0);
            }

            yield break;
        }

        // Asked of the whole set, not of each file. A two-disc original and a
        // single-disc reissue agree about their first two tracks and diverge
        // after, and answering per file would split one rip between a release
        // and a release group — leaving a release page claiming two tracks of a
        // four-track rip, and an incomplete-rip report firing on a rip that is
        // complete. An attribution is a claim about a set; it degrades as one.
        if (!AgreeOnEverySlot(chosen, tied))
        {
            foreach (var match in chosen.Matches)
            {
                yield return SharedGroup(tied) is { } shared
                    ? ReleaseAssignment.InGroup(match.File, shared, alternatives)
                    : ReleaseAssignment.Refused(match.File, ReleaseAttributionOutcome.NoConfidentFit);
            }

            yield break;
        }

        foreach (var match in chosen.Matches)
        {
            yield return ReleaseAssignment.On(
                match,
                chosen.Release,
                ReleaseAttributionOutcome.AttributedAmbiguously,
                alternatives);
        }
    }

    /// <summary>
    /// How much a fit is worth: how much of itself it explains, times how much of
    /// ours it explains.
    /// </summary>
    /// <remarks>
    /// Ranking on files alone was wrong and a box set is what proved it.
    /// <i>The Collection</i> is five discs and 76 tracks, of which this library
    /// holds two whole albums — 20 files, coverage 0.26. <i>Off the Wall</i> is
    /// ten tracks and the library holds all ten, coverage 1.00. Both clear the
    /// loosest rung; the box set explains twice as many files, so on a
    /// files-first ranking it takes them, and two albums vanish into a
    /// compilation nobody owns.
    ///
    /// Ranking on coverage alone is wrong in the mirror image: a two-track single
    /// holding tracks 3 and 4 of an album covers itself perfectly and would
    /// outrank an album missing one song, taking two files out of it.
    ///
    /// The product answers both, because it asks both questions at once. The box
    /// set scores 5.2 against the album's 10.0; the single scores 2.0 against the
    /// album's 10.1; and the bootleg that started all this scores 9.4 against
    /// <i>Sloe Gin</i>'s 11.0. Every one of the measured failures comes out the
    /// right way round, which is more than any single term manages.
    /// </remarks>
    private static double Weight(ReleaseFit fit) => fit.Coverage * fit.FilesExplained;

    /// <summary>Do all the tied editions put all of this music in the same places?</summary>
    private static bool AgreeOnEverySlot(ReleaseFit chosen, List<ReleaseFit> tied) =>
        chosen.Matches.All(match => tied.All(fit => fit.Matches
            .Where(other => other.Recording == match.Recording)
            .Select(other => other.Slot)
            .Contains(match.Slot)));

    /// <summary>The one release group behind every tied edition, if there is one.</summary>
    private static Mbid? SharedGroup(List<ReleaseFit> tied)
    {
        var groups = tied.Select(fit => fit.Release.ReleaseGroupId).Distinct().ToList();
        return groups.Count == 1 ? groups[0] : null;
    }

    /// <summary>
    /// Which edition to name when the evidence cannot choose.
    /// </summary>
    /// <remarks>
    /// Stated rather than incidental, because an unstated tie-break is a
    /// dictionary iteration order and reruns disagree with each other. Official
    /// pressings first — a promo or a bootleg is the less likely source of a
    /// file that fits both. Then the earliest release, which is the original
    /// where a tie is between an album and its own reissue. Then the id, which
    /// decides nothing and settles everything.
    /// </remarks>
    private static ReleaseFit Preferred(List<ReleaseFit> tied) =>
        tied.OrderByDescending(fit => fit.IsOfficial)
            .ThenBy(fit => Sortable(fit.Release.ReleasedOn))
            .ThenBy(fit => fit.ReleaseId.Value)
            .First();

    /// <summary>
    /// A partial date flattened for ordering only.
    /// </summary>
    /// <remarks>
    /// The widening the provider layer refuses to do — a year-only date becomes
    /// its 1 January — is safe here and nowhere else, because the value is
    /// compared and discarded rather than stored. An undated release sorts last:
    /// "no date" is not evidence of being early.
    /// </remarks>
    private static (int Year, int Month, int Day) Sortable(ReleaseDate? date) =>
        date is { } known
            ? (known.Year, known.Month ?? 1, known.Day ?? 1)
            : (int.MaxValue, 12, 31);

    private static bool Passes(ReleaseFit fit, Gate gate) =>
        fit.FilesExplained >= gate.MinimumFiles
        && fit.Coverage >= gate.MinimumCoverage
        // Null drift is unmeasurable rather than infinite, so it cannot fail a
        // test that was never run. Coverage still has to carry it.
        && (fit.MeanDrift is not { } drift || drift <= gate.MaximumDrift);

    /// <summary>
    /// Is this the same answer as the winner, or a different one that scores alike?
    /// </summary>
    /// <remarks>
    /// The file set has to match, and leaving that out was a real bug. Two albums
    /// of five tracks, each held whole, score identically on every number here —
    /// same file count, same coverage, same drift — while explaining completely
    /// different music. Treated as tied editions they are then asked whether they
    /// agree about where each track sits, which of course they do not, and both
    /// albums are refused for disagreeing about songs neither of them contains.
    ///
    /// "Tied" means one answer arriving twice: the same files, placed the same
    /// way, by two pressings of one record.
    /// </remarks>
    private static bool Indistinguishable(ReleaseFit fit, ReleaseFit winner) =>
        fit.FilesExplained == winner.FilesExplained
        && fit.Matches.Select(match => match.File)
            .ToHashSet()
            .SetEquals(winner.Matches.Select(match => match.File))
        && Math.Abs(fit.Coverage - winner.Coverage) < 1e-9
        && (fit.MeanDrift, winner.MeanDrift) switch
        {
            ({ } left, { } right) => (left - right).Duration() <= TieTolerance,
            (null, null) => true,
            _ => false,
        };

    /// <summary>
    /// The rungs, strictest first.
    /// </summary>
    /// <remarks>
    /// The first two are fixed because they are what the measurements support: a
    /// correctly attributed album in the author's library sits at coverage 1.00
    /// and drift under 0.1s, and the compilations that cause trouble sit at 0.55
    /// and 2.0s. The last rung is the caller's, so the floor can be tightened for
    /// a library of CD rips or loosened for one of live recordings without
    /// anybody editing this ladder.
    ///
    /// The fourth rung exists only for singles. A one-track release cannot reach
    /// two files, and refusing every standalone single would be a rule about
    /// arithmetic rather than about music — so it is admitted, at a strictness
    /// the earlier rungs never relax to.
    /// </remarks>
    private static Gate[] Ladder(AttributionThresholds thresholds) =>
    [
        new Gate(0.75, TimeSpan.FromMilliseconds(750), MinimumFiles: 2),
        new Gate(0.50, TimeSpan.FromMilliseconds(1_500), MinimumFiles: 2),
        new Gate(thresholds.MinimumCoverage, thresholds.MaximumDrift, MinimumFiles: 2),
        new Gate(0.50, TimeSpan.FromMilliseconds(750), MinimumFiles: 1),
    ];

    private readonly record struct Gate(double MinimumCoverage, TimeSpan MaximumDrift, int MinimumFiles);
}

/// <summary>How weak a fit may be before it is refused rather than recorded.</summary>
/// <remarks>
/// Only the last rung of the ladder; the strict rungs above it are not
/// negotiable, because loosening those is what lets compilations win.
/// </remarks>
public sealed record AttributionThresholds(double MinimumCoverage, TimeSpan MaximumDrift)
{
    public static AttributionThresholds Default { get; } = new(0.25, TimeSpan.FromSeconds(3));
}

/// <summary>Where one file ended up, and how sure the catalogue is about it.</summary>
public sealed record ReleaseAssignment(
    MediaFileId File,
    Mbid? Release,
    Mbid? ReleaseGroup,
    int? DiscNumber,
    int? Position,
    ReleaseAttributionOutcome Outcome,

    /// <summary>
    /// How many other editions fitted exactly as well. Zero for a clean answer.
    /// </summary>
    int EditionAlternatives)
{
    internal static ReleaseAssignment On(
        SlotMatch match,
        MusicBrainzRelease release,
        ReleaseAttributionOutcome outcome,
        int alternatives) =>
        new(
            match.File,
            release.Id,
            release.ReleaseGroupId,
            match.Slot.DiscNumber,
            match.Slot.Position,
            outcome,
            alternatives);

    internal static ReleaseAssignment InGroup(MediaFileId file, Mbid group, int alternatives) =>
        new(file, null, group, null, null, ReleaseAttributionOutcome.GroupOnly, alternatives);

    internal static ReleaseAssignment Refused(MediaFileId file, ReleaseAttributionOutcome outcome) =>
        new(file, null, null, null, null, outcome, 0);
}
