using Fonoteca.Domain.Abstractions;
using Fonoteca.Domain.Catalogue;

namespace Fonoteca.Domain.Identification;

/// <summary>
/// How well one release explains a set of files.
/// </summary>
/// <remarks>
/// The measurement <see cref="ReleaseAttribution"/> decides on, kept separate
/// from the deciding so that "how good is this fit" can be asserted on its own.
///
/// Two numbers, and they answer different questions. <see cref="Coverage"/> asks
/// whether the <i>release</i> is accounted for — a twelve-track album with all
/// twelve present is a rip of that album, while a forty-track compilation with
/// three present is three songs that happen to also appear on it.
/// <see cref="MeanDrift"/> asks whether the <i>audio</i> matches — the same
/// tracklist mastered twice gives two releases identical coverage, and only the
/// running times tell them apart.
///
/// Both are needed and neither is sufficient. Coverage alone lets a bootleg
/// compilation outrank the album it plundered, because it explains more files.
/// Drift alone cannot see that thirty-seven tracks are missing.
/// </remarks>
public sealed record ReleaseFit
{
    private ReleaseFit(
        MusicBrainzRelease release,
        IReadOnlyList<SlotMatch> matches,
        int slotCount,
        TimeSpan? meanDrift)
    {
        Release = release;
        Matches = matches;
        SlotCount = slotCount;
        MeanDrift = meanDrift;
    }

    /// <summary>The release being measured, with the track list it was measured against.</summary>
    public MusicBrainzRelease Release { get; }

    /// <summary>Which file landed on which track, in track order.</summary>
    public IReadOnlyList<SlotMatch> Matches { get; }

    /// <summary>Tracks on the release, filled or not.</summary>
    public int SlotCount { get; }

    /// <summary>
    /// Mean distance between a file's measured length and the track's printed
    /// one, or null when nothing could be compared.
    /// </summary>
    /// <remarks>
    /// Null rather than zero, and the difference decides a gate: an unmeasurable
    /// fit has produced no evidence, which is not the same as having produced
    /// perfect evidence. Callers let null through the drift gate — there is
    /// nothing to test it against — and rank it below anything measured.
    /// </remarks>
    public TimeSpan? MeanDrift { get; }

    public Mbid ReleaseId => Release.Id;

    public int FilesExplained => Matches.Count;

    public double Coverage => SlotCount == 0 ? 0 : (double)Matches.Count / SlotCount;

    /// <summary>
    /// Official, as opposed to a promo, a bootleg or a pseudo-release.
    /// </summary>
    /// <remarks>
    /// Only ever a tie-break. It is tempting to make it a gate — bootlegs are
    /// where most bad attributions come from — but a legitimately unofficial
    /// release is still the release a file came from, and coverage already
    /// removes the compilations that do the damage.
    /// </remarks>
    public bool IsOfficial =>
        string.Equals(Release.Status, "Official", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Score this release against the files still looking for one.
    /// </summary>
    /// <remarks>
    /// Walks the track list in order and gives each track the closest-lasting
    /// unclaimed file holding its recording. A track takes at most one file, so
    /// five encodings of the same song cannot fill one slot five times and
    /// pretend to be an album — the other four stay available for the release
    /// they actually came from.
    ///
    /// Greedy per track rather than a global optimum. A release that lists one
    /// recording twice could in principle assign its two files the wrong way
    /// round; both are the same audio, both land on the same release, and the
    /// only visible consequence is which of two identical tracks a duplicate
    /// encoding sits on.
    /// </remarks>
    /// <returns>Null when the release explains nothing at all.</returns>
    public static ReleaseFit? For(
        MusicBrainzRelease release,
        IReadOnlyCollection<AttributionFile> available)
    {
        ArgumentNullException.ThrowIfNull(release);
        ArgumentNullException.ThrowIfNull(available);

        var byRecording = new Dictionary<Mbid, List<AttributionFile>>();

        foreach (var file in available)
        {
            if (!byRecording.TryGetValue(file.Recording, out var pool))
            {
                pool = [];
                byRecording[file.Recording] = pool;
            }

            pool.Add(file);
        }

        var claimed = new HashSet<MediaFileId>();
        var matches = new List<SlotMatch>();

        foreach (var track in release.Tracks)
        {
            if (track.RecordingId is not { } recording) continue;
            if (!byRecording.TryGetValue(recording, out var pool)) continue;

            AttributionFile? best = null;
            TimeSpan? bestDrift = null;

            foreach (var file in pool)
            {
                if (claimed.Contains(file.Id)) continue;

                var drift = DriftBetween(file.Duration, track.Length);

                if (best is null || Closer(drift, file, bestDrift, best))
                {
                    best = file;
                    bestDrift = drift;
                }
            }

            if (best is null) continue;

            claimed.Add(best.Id);
            matches.Add(new SlotMatch(
                new TrackSlot(track.DiscNumber, track.Position),
                best.Id,
                recording,
                bestDrift));
        }

        if (matches.Count == 0) return null;

        var measured = matches.Where(match => match.Drift is not null).ToList();

        var meanDrift = measured.Count == 0
            ? (TimeSpan?)null
            : TimeSpan.FromTicks(measured.Sum(match => match.Drift!.Value.Ticks) / measured.Count);

        return new ReleaseFit(release, matches, release.Tracks.Count, meanDrift);
    }

    /// <summary>
    /// The distance between what a file lasts and what the track claims to.
    /// </summary>
    /// <remarks>
    /// Null when either side is unknown, so that a missing length reads as
    /// "no evidence" rather than as a perfect match. MusicBrainz omits track
    /// lengths on plenty of older releases, and treating those as zero drift
    /// would make the least documented pressing win every tie.
    /// </remarks>
    private static TimeSpan? DriftBetween(TimeSpan? measured, TimeSpan? printed) =>
        measured is { } left && printed is { } right
            ? TimeSpan.FromTicks(Math.Abs((left - right).Ticks))
            : null;

    /// <summary>
    /// Is this file a better tenant for the slot than the one held so far?
    /// </summary>
    /// <remarks>
    /// A measured drift always beats an unmeasured one — evidence beats none —
    /// and identical drifts are broken by file id so the same inputs always
    /// produce the same assignment, the discipline every rule here follows.
    /// </remarks>
    private static bool Closer(
        TimeSpan? drift,
        AttributionFile file,
        TimeSpan? bestDrift,
        AttributionFile best) => (drift, bestDrift) switch
    {
        ({ } left, { } right) when left != right => left < right,
        (not null, null) => true,
        (null, not null) => false,
        _ => file.Id.Value.CompareTo(best.Id.Value) < 0,
    };
}

/// <summary>A file waiting to be told which album it came from.</summary>
/// <remarks>
/// <see cref="Duration"/> is what <c>fpcalc</c> measured, not what any tag
/// claims — this library's files carry no tags at all — and it is stored to
/// ten milliseconds, which is what makes it able to separate one mastering
/// from another rather than merely one song from another.
/// </remarks>
public sealed record AttributionFile(MediaFileId Id, Mbid Recording, TimeSpan? Duration);

/// <summary>A position on a release: which disc, and where on it.</summary>
public readonly record struct TrackSlot(int DiscNumber, int Position);

/// <summary>One file placed on one track, with the evidence for the placement.</summary>
public sealed record SlotMatch(TrackSlot Slot, MediaFileId File, Mbid Recording, TimeSpan? Drift);
