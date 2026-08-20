using Fonoteca.Api.Endpoints;
using Fonoteca.Domain.Abstractions;
using Fonoteca.Domain.Catalogue;
using Fonoteca.Domain.Identification;

namespace Fonoteca.Api.Matching;

/// <summary>
/// The candidate albums for one refused component, assembled once and shared by
/// the pass that gathers them and the endpoint that serves them.
/// </summary>
/// <remarks>
/// <b>The gather is not the expensive part — asking twice is.</b> The attribution
/// pass browses every recording in a component and fetches the track lists of
/// everything worth fetching, ranks them, and then throws the whole set away; the
/// endpoint that puts the question to a person did the same work again, from
/// scratch, on a click. Measured against the live mirror that is 30 browses plus
/// 8 lookups, up to two minutes and 38 turns at the rate limit, for an answer the
/// application had already computed and discarded — the same mistake
/// <c>AcoustIdMatchesJson</c> exists because of, one pass along.
///
/// So this is the one place a candidate document is built, and it is built from
/// whatever the caller already has. The pass hands it every release it confirmed
/// — hundreds, with their track lists, all free — and the cut to
/// <see cref="Shown"/> happens here, on the real <see cref="ReleaseFit"/> rather
/// than on an estimate. The endpoint's live path hands it the few it could afford
/// to look up, having used <see cref="Support"/> to choose them.
///
/// <b>Only the shape a screen reads is stored.</b> Not the raw candidate set:
/// one component's confirmed releases run to hundreds and a document of all of
/// them would be megabytes of MusicBrainz per refused component. Eight, scored,
/// with their whole track lists, is the thing a person is going to be shown.
/// </remarks>
internal static class ComponentCandidates
{
    /// <summary>
    /// How many candidate releases are offered.
    /// </summary>
    /// <remarks>
    /// Eight rather than the six a recording gets, because a component's
    /// candidates are near-duplicates far more often — the pressings of one
    /// album — and a cut that lands in the middle of a run of editions offers a
    /// person a choice between three of the five copies of the same record.
    /// </remarks>
    public const int Shown = 8;

    /// <summary>
    /// How many of a component's recordings the live path puts to MusicBrainz.
    /// </summary>
    /// <remarks>
    /// Bounded because a component may hold up to 600 files, and each recording
    /// is a gated request. It applies only to the endpoint gathering from
    /// nothing; the pass has already browsed them all by the time it calls here.
    /// </remarks>
    public const int MaxBrowses = 30;

    /// <summary>
    /// How well a release and a component account for each other, before either is fetched.
    /// </summary>
    /// <remarks>
    /// The harmonic mean of two shares that pull in opposite directions: how much
    /// of the <i>release</i> the component holds, and how much of the
    /// <i>component</i> the release explains. Either alone has a degenerate
    /// maximum — a one-track single is 100% held, a thousand-track anthology
    /// explains the most files — and both were measured producing exactly that
    /// list. The mean is near zero unless both are decent, which is the shape of
    /// "this looks like the album these came from".
    ///
    /// Both denominators are estimates rather than facts. The track count is the
    /// browse's, which is the only size available before a lookup, and the
    /// component's is the recordings actually <i>asked about</i> rather than all
    /// of them — a release cannot be credited with holding a recording nobody
    /// browsed. A release MusicBrainz gives no media for scores zero and sorts
    /// last: an unknown size is not a small one, and the other way round would
    /// rank every undocumented release above every documented one.
    ///
    /// Only the live path needs this. The pass ranks on the real fit, because it
    /// already holds every track list this is standing in for.
    /// </remarks>
    public static double Support(int trackCount, int asked, int held)
    {
        if (trackCount <= 0 || asked <= 0 || held <= 0) return 0;

        var ofRelease = (double)held / trackCount;
        var ofComponent = (double)held / asked;

        return 2 * ofRelease * ofComponent / (ofRelease + ofComponent);
    }

    /// <summary>
    /// Score every release against the component, and keep the best few.
    /// </summary>
    /// <param name="stamp">The component's identity, as the string it travels as.</param>
    /// <param name="recordings">Distinct recordings among the files waiting on an answer.</param>
    /// <param name="browsed">How many of those were actually put to MusicBrainz.</param>
    /// <param name="total">Distinct releases seen before the list was cut.</param>
    public static ComponentCandidatesResponse Build(
        string stamp,
        DateTimeOffset asOfUtc,
        IReadOnlyList<ComponentMember> files,
        IReadOnlyList<MusicBrainzRelease> releases,
        IReadOnlyDictionary<Mbid, string?> formats,
        int recordings,
        int browsed,
        int total,
        double minimumCoverage,
        int maximumDriftMs)
    {
        var available = files
            .Select(file => new AttributionFile(file.Id, file.Recording, file.Duration))
            .ToList();

        var byId = files.ToDictionary(file => file.Id);
        var scored = new List<ComponentCandidateRow>(releases.Count);

        foreach (var release in releases)
        {
            var fit = ReleaseFit.For(release, available);
            if (fit is null) continue;

            scored.Add(Row(release, fit, formats.GetValueOrDefault(release.Id), byId));
        }

        return new ComponentCandidatesResponse(
            stamp,
            asOfUtc,
            false,
            files.Count,
            recordings,
            browsed,
            total,
            minimumCoverage,
            maximumDriftMs,
            [.. Rank(scored).Take(Shown)],
            [.. files.Select(file => new ComponentFileRow(
                file.Id.Value,
                file.Path,
                file.Title,
                CatalogueEndpoints.Format(file.Duration)))]);
    }

    /// <summary>
    /// The order a person reads the candidates in.
    /// </summary>
    /// <remarks>
    /// <b>Files explained first, then coverage, and the order of those two is
    /// measured rather than argued.</b> Coverage alone reads well and puts a
    /// one-track single at the top of every list: a release with one track the
    /// library holds scores 1.00 by arithmetic, and on a real twelve-file
    /// component that pushed two singles above the album explaining all twelve.
    /// Files alone would put a box set above the album it reprints, where both
    /// explain the same files and only the denominator differs. Both, in that
    /// order, get both right — which is what <see cref="ReleaseFit"/>'s own
    /// remarks say about the same pair.
    ///
    /// Unmeasured drift ranks last rather than best: no evidence is not perfect
    /// evidence. Ties end on the identifier so a rerun cannot change its mind.
    /// </remarks>
    private static IEnumerable<ComponentCandidateRow> Rank(
        IEnumerable<ComponentCandidateRow> scored) =>
        scored
            .OrderByDescending(candidate => candidate.FilesExplained)
            .ThenByDescending(candidate => candidate.Coverage)
            .ThenBy(candidate => candidate.MeanDriftMs ?? int.MaxValue)
            .ThenByDescending(candidate => candidate.Official)
            .ThenBy(candidate => candidate.Mbid, StringComparer.Ordinal);

    /// <summary>One candidate release, scored and laid out slot by slot.</summary>
    /// <remarks>
    /// Every slot is a row, filled or not — the same discipline the release page
    /// keeps, and for the same reason: "you are missing track 7" is exactly the
    /// question a person deciding between an album and the compilation that
    /// reprints half of it has to answer, and a list of only the matched tracks
    /// cannot answer it.
    /// </remarks>
    private static ComponentCandidateRow Row(
        MusicBrainzRelease release,
        ReleaseFit fit,
        string? formats,
        Dictionary<MediaFileId, ComponentMember> files)
    {
        // An indexer rather than ToDictionary. `ReleaseFit` emits one match per
        // track, so a release printing two tracks at one (disc, position) — which
        // nothing on our side controls — would throw on a duplicate key.
        // `ReleaseWriter` already tolerates the same shape.
        var placed = new Dictionary<TrackSlot, SlotMatch>();

        foreach (var match in fit.Matches)
        {
            placed[match.Slot] = match;
        }

        var slots = release.Tracks
            .Select(track =>
            {
                var slot = new TrackSlot(track.DiscNumber, track.Position);

                if (!placed.TryGetValue(slot, out var match)
                    || !files.TryGetValue(match.File, out var file))
                {
                    return new ComponentSlotRow(
                        track.DiscNumber, track.Position, track.Number, track.Title,
                        CatalogueEndpoints.Format(track.Length), null, null, null, null);
                }

                return new ComponentSlotRow(
                    track.DiscNumber,
                    track.Position,
                    track.Number,
                    track.Title,
                    CatalogueEndpoints.Format(track.Length),
                    CatalogueEndpoints.Format(file.Duration),
                    match.Drift is { } drift ? (int)Math.Round(drift.TotalMilliseconds) : null,
                    file.Path,
                    file.SizeBytes);
            })
            .ToList();

        return new ComponentCandidateRow(
            release.Id.Value.ToString(),
            release.Title,
            CatalogueEndpoints.CreditLine(release.Credits),
            release.ReleasedOn?.Year,
            release.Country,
            release.Status,
            release.PrimaryType,
            release.SecondaryTypes,
            formats,
            release.Tracks.Count,
            release.Tracks.Select(track => track.DiscNumber).Distinct().Count(),
            fit.FilesExplained,
            fit.Coverage,
            fit.MeanDrift is { } mean ? (int)Math.Round(mean.TotalMilliseconds) : null,
            fit.IsOfficial,
            slots);
    }

    /// <summary>
    /// A stored document that can still be believed.
    /// </summary>
    /// <remarks>
    /// Three questions, and the middle one is the interesting one. Age is the
    /// ordinary cache check. Shape is the check <c>RecordingCandidates</c> pays
    /// for too: a document written before a field existed deserialises happily
    /// and leaves the new collections null, which serialise back as <c>null</c>
    /// where the generated schema promises an array — so the cache that makes the
    /// screen fast would be the thing that broke it.
    ///
    /// And the size, which is this document's own: the component is a set of
    /// files, and the set changes. A scan clears <c>ReleaseLookupUtc</c> on a
    /// file whose bytes moved, and a person answering part of a component takes
    /// files out of it. A stored document naming files that are no longer waiting
    /// would show somebody a coverage figure computed against a component that no
    /// longer exists — so the count is compared, and a mismatch is a miss.
    /// </remarks>
    public static bool Usable(ComponentCandidatesResponse document, int files, DateTimeOffset now) =>
        document.Files == files
        && now - document.AsOfUtc < CatalogueEndpoints.CacheDuration
        && document.Candidates is not null
        && document.FileList is not null
        && document.Candidates.All(row => row.SecondaryTypes is not null && row.Slots is not null);
}

/// <summary>One file of a component, as the candidate document needs it.</summary>
/// <remarks>
/// <see cref="Duration"/> is <c>FingerprintDuration ?? Quality.Duration</c> and
/// must stay that on both paths: a third of this worklist has no fingerprint
/// duration at all, and measuring one thing on the screen and another on the
/// commit is how a person is shown drift figures the write did not use.
/// </remarks>
internal sealed record ComponentMember(
    MediaFileId Id,
    string Path,
    string? Title,
    Mbid Recording,
    TimeSpan? Duration,
    long SizeBytes);
