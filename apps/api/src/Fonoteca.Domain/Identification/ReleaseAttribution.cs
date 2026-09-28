using Fonoteca.Domain.Abstractions;
using Fonoteca.Domain.Catalogue;

namespace Fonoteca.Domain.Identification;

/// <summary>
/// Decides which album an album folder is, and whether any one pressing of it is
/// proven.
/// </summary>
/// <remarks>
/// <b>The folder is the album.</b> A folder is the one grouping a library is
/// nearly always right about, and splitting it between releases is how a single
/// song ended up filed under a compilation while the album around it lost track
/// 11. So the unit is the folder (see <see cref="AlbumFolder"/>), the answer is
/// one album for all of it, and files the album's editions do not print stay with
/// it as <see cref="ReleaseAttributionOutcome.OnNoEdition"/> rather than being
/// sent anywhere else.
///
/// <b>The album is claimed on a majority; the pressing only on proof.</b> The
/// album is the release group of the candidate that explains the folder best —
/// weighted, so a box set reprinting the album does not win on size — and it is
/// claimed only if its recordings account for more than half the folder. A
/// folder that is mostly something else is a compilation or a mix, and is a
/// question for a person. The pressing is a far stronger claim and needs
/// <see cref="EditionProof"/>; two pressings proving alike leave only the album,
/// because naming one would claim its barcode, label and country on no evidence.
///
/// <b>Order, not positions.</b> Without a proven pressing the catalogue claims no
/// track numbers; <see cref="FolderOrder"/> checks the files' own order instead,
/// and an order every edition contradicts is a question rather than a filing.
///
/// A pure rule: files and candidate releases in, one decision out. Nothing here
/// reads a folder's <i>name</i>.
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
    /// How far a substituted file may sit from the length the album prints for it.
    /// </summary>
    /// <remarks>
    /// The cluster says the audio is that recording; this refuses a different
    /// performance sharing the cluster. Measured on the target library,
    /// <i>L-O-V-E</i>'s tracks sit 1.4 to 2.8s off their printed lengths and its
    /// left-out one likewise, while <i>Guess Who</i>'s leftover is 6.2s out
    /// against siblings at 0.05s.
    /// </remarks>
    private static readonly TimeSpan SubstitutionDrift = TimeSpan.FromSeconds(3);

    /// <summary>The album this folder is, what each identified file is on it, and the order they run in.</summary>
    /// <param name="files">Every file in the folder; unidentified ones count towards its size and get no decision.</param>
    /// <param name="candidates">The releases the gather fetched, from every group the folder's recordings reach.</param>
    /// <param name="albumByHand">The album a person or agent named for this folder, which the rule keeps.</param>
    /// <param name="gatherComplete">False when the gather stopped at its cap.</param>
    /// <param name="minimumCoverage">How much of itself the best candidate must explain before its album is considered.</param>
    public static FolderDecision Decide(
        IReadOnlyList<FolderFile> files,
        IReadOnlyCollection<MusicBrainzRelease> candidates,
        Mbid? albumByHand,
        bool gatherComplete,
        double minimumCoverage)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(candidates);

        var identified = files.Where(file => file.Recording is not null).ToList();

        var fits = candidates
            .Select(release => ReleaseFit.For(
                release,
                [.. identified.Select(file => new AttributionFile(file.Id, file.Recording!.Value, file.Duration))]))
            .OfType<ReleaseFit>()
            .ToList();

        var best = fits
            .OrderByDescending(Weight)
            .ThenByDescending(fit => fit.Coverage)
            .ThenBy(fit => fit.MeanDrift ?? TimeSpan.MaxValue)
            .ThenByDescending(fit => fit.IsOfficial)
            .ThenBy(fit => fit.ReleaseId.Value)
            .FirstOrDefault();

        Mbid album;

        if (albumByHand is { } named)
        {
            album = named;
        }
        else
        {
            // Two different albums explaining the folder equally well is two
            // answers, and choosing between them would be a coin flip.
            if (best is not { Release.ReleaseGroupId: { } group }
                || best.Coverage < minimumCoverage
                || fits.Exists(fit => fit.Release.ReleaseGroupId != group && Indistinguishable(fit, best)))
            {
                return Refused(identified, best?.Release.ReleaseGroupId, best?.Release);
            }

            album = group;
        }

        var editions = candidates.Where(release => release.ReleaseGroupId == album).ToList();
        var printed = editions
            .SelectMany(edition => edition.Tracks)
            .Where(track => track.RecordingId is not null)
            .GroupBy(track => track.RecordingId!.Value)
            .ToDictionary(group => group.Key, group => group.Select(track => track.Length).ToList());

        var effective = Substituted(files, album, printed);

        bool OnAlbum(FolderFile file) =>
            file.Recording is { } recording && (file.Groups.Contains(album) || printed.ContainsKey(recording));

        var explained = effective.Count(OnAlbum);

        // More than half, counting every file in the folder: a folder that is
        // mostly something else is not this album with a few strays in it. A
        // person's answer is theirs and is not held to it.
        if (albumByHand is null && explained * 2 <= files.Count)
        {
            return Refused(identified, album, best?.Release);
        }

        // A pseudo-release is a transliteration of another release, usually
        // printing its lengths: not a pressing, and a tie with every one it copies.
        var proven = editions
            .Where(edition => !string.Equals(edition.Status, "Pseudo-Release", StringComparison.OrdinalIgnoreCase))
            .Select(edition => (Edition: edition, Seats: EditionProof.Seat(edition, effective)))
            .Where(proof => proof.Seats is not null)
            .ToList();

        var claimed = proven.Count == 1 ? proven[0].Edition : null;
        var seats = proven.Count == 1
            ? proven[0].Seats!.ToDictionary(match => match.File, match => match.Slot)
            : [];

        var official = editions
            .Where(edition => string.Equals(edition.Status, "Official", StringComparison.OrdinalIgnoreCase))
            .ToList();

        var (order, running) = FolderOrder.Check(effective, official, gatherComplete);
        var place = running.Select((id, index) => (id, index)).ToDictionary(entry => entry.id, entry => entry.index + 1);

        // A contradicted order with a proven pressing is the tags being wrong,
        // not the album, and the pressing stands. Without one it is a question.
        if (order == FolderOrderOutcome.Contradicted && claimed is null && albumByHand is null)
        {
            return new FolderDecision(
                null,
                album,
                null,
                0,
                order,
                [.. effective
                    .Where(file => file.Recording is not null)
                    .Select(file => new FileDecision(
                        file.Id,
                        ReleaseAttributionOutcome.OrderContradicted,
                        file.Recording,
                        null,
                        place.TryGetValue(file.Id, out var at) ? at : null))],
                best?.Release);
        }

        return new FolderDecision(
            album,
            album,
            claimed,
            proven.Count,
            order,
            [.. effective
                .Where(file => file.Recording is not null)
                .Select(file => new FileDecision(
                    file.Id,
                    seats.ContainsKey(file.Id) ? ReleaseAttributionOutcome.Attributed
                        : OnAlbum(file) ? ReleaseAttributionOutcome.GroupOnly
                        : ReleaseAttributionOutcome.OnNoEdition,
                    file.Recording,
                    seats.TryGetValue(file.Id, out var slot) ? slot : null,
                    place.TryGetValue(file.Id, out var at) ? at : null))],
            best?.Release);
    }

    /// <summary>
    /// The folder's files, with a recording the album does not print swapped for
    /// the one of its own AcoustID cluster that the album does.
    /// </summary>
    /// <remarks>
    /// <b>The commonest way one song goes missing from an album.</b> An AcoustID
    /// cluster is routinely linked to a dozen recordings — the master, the
    /// alternate take, the remaster, each compilation's duplicate entry — and
    /// enrichment has to name one, so it names the most-submitted. <i>Kind of
    /// Blue</i>'s "Blue in Green" came out as "Blue in Green (Take 1)": 324
    /// submissions against the album's own recording's 4, one cluster, the same
    /// audio.
    ///
    /// The cluster is the evidence that the audio is that recording, and the
    /// album is what says which of the cluster's recordings is meant — neither
    /// alone would do. So only one: a file whose cluster names two of the album's
    /// recordings, or a recording another file already holds or another file
    /// also wants, is left as it is.
    /// </remarks>
    private static List<FolderFile> Substituted(
        IReadOnlyList<FolderFile> files,
        Mbid album,
        Dictionary<Mbid, List<TimeSpan?>> printed)
    {
        var held = files
            .Where(file => file.Recording is { } recording && printed.ContainsKey(recording))
            .Select(file => file.Recording!.Value)
            .ToHashSet();

        var wanted = files
            .Where(file => file.Recording is { } recording
                && !file.Groups.Contains(album)
                && !printed.ContainsKey(recording))
            .Select(file => (File: file, Targets: file.Linked
                .Where(linked => printed.TryGetValue(linked, out var lengths)
                    && lengths.Exists(length => Close(file.Duration, length)))
                .ToList()))
            .Where(want => want.Targets.Count == 1 && !held.Contains(want.Targets[0]))
            .ToList();

        var single = wanted
            .GroupBy(want => want.Targets[0])
            .Where(claim => claim.Count() == 1)
            .ToDictionary(claim => claim.Single().File.Id, claim => claim.Key);

        return [.. files.Select(file => single.TryGetValue(file.Id, out var recording) ? file with { Recording = recording } : file)];
    }

    private static bool Close(TimeSpan? measured, TimeSpan? printed) =>
        measured is not { } left || printed is not { } right || (left - right).Duration() <= SubstitutionDrift;

    /// <summary>A folder no album can be claimed for: every identified file is a question.</summary>
    /// <remarks>
    /// The two refusals are worth telling apart. A recording MusicBrainz lists on
    /// no release at all is a fact about the database; one it lists on releases
    /// none of which is this folder is a fact about this library.
    /// </remarks>
    private static FolderDecision Refused(
        IReadOnlyList<FolderFile> identified,
        Mbid? proposed,
        MusicBrainzRelease? best) =>
        new(
            null,
            proposed,
            null,
            0,
            FolderOrderOutcome.NotChecked,
            [.. identified.Select(file => new FileDecision(
                file.Id,
                file.Groups.Count == 0
                    ? ReleaseAttributionOutcome.NoCandidate
                    : ReleaseAttributionOutcome.NoConfidentFit,
                file.Recording,
                null,
                null))],
            best);

    /// <summary>
    /// How much a fit is worth: how much of itself it explains, times how much of
    /// ours it explains.
    /// </summary>
    /// <remarks>
    /// Ranking on files alone was wrong and a box set is what proved it.
    /// <i>The Collection</i> is five discs and 76 tracks, of which this library
    /// holds two whole albums — 20 files, coverage 0.26. <i>Off the Wall</i> is
    /// ten tracks and the library holds all ten, coverage 1.00. On a files-first
    /// ranking the box set takes them, and two albums vanish into a compilation
    /// nobody owns. Ranking on coverage alone is wrong in the mirror image: a
    /// two-track single holding tracks 3 and 4 of an album covers itself
    /// perfectly. The product answers both, because it asks both questions at
    /// once.
    /// </remarks>
    private static double Weight(ReleaseFit fit) => fit.Coverage * fit.FilesExplained;

    /// <summary>
    /// Is this the same answer as the winner, or a different one that scores alike?
    /// </summary>
    /// <remarks>
    /// The file set has to match: two albums of five tracks, each held whole,
    /// score identically on every number here while explaining completely
    /// different music. "Tied" means one answer arriving twice — the same files,
    /// placed the same way, by two releases.
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
}

/// <summary>What was decided about one album folder.</summary>
/// <param name="Album">The album claimed; null when the folder is a question.</param>
/// <param name="Proposed">The album that was considered, claimed or not — what a second look is taken against.</param>
/// <param name="Edition">The one pressing proven; null when none is, or more than one.</param>
/// <param name="EditionsProven">How many pressings proved; above one means none is claimed.</param>
/// <param name="Order">What the files' own order says against the album's editions.</param>
/// <param name="Files">One decision per identified file.</param>
/// <param name="Best">The candidate that explained the folder best, whose album it was.</param>
public sealed record FolderDecision(
    Mbid? Album,
    Mbid? Proposed,
    MusicBrainzRelease? Edition,
    int EditionsProven,
    FolderOrderOutcome Order,
    IReadOnlyList<FileDecision> Files,
    MusicBrainzRelease? Best);

/// <summary>What one file is on the folder's album.</summary>
/// <param name="Recording">The recording to hold, which a substitution may have changed.</param>
/// <param name="Slot">Where the proven pressing prints it; null without one.</param>
/// <param name="Position">Its place in the folder's order, from 1; null where the folder has none.</param>
public sealed record FileDecision(
    MediaFileId File,
    ReleaseAttributionOutcome Outcome,
    Mbid? Recording,
    TrackSlot? Slot,
    int? Position);
