using System.Globalization;

namespace Fonoteca.Domain.Catalogue;

/// <summary>One position on one edition, as MusicBrainz prints it.</summary>
public sealed record EditionSlot(
    int Disc,
    int Position,
    string? Number,
    string? Title,
    TimeSpan? Length,
    RecordingId Recording);

/// <summary>An edition's whole track list, disc-major.</summary>
public sealed record EditionTracks(ReleaseId Id, IReadOnlyList<EditionSlot> Slots);

/// <param name="Slot">Where the first edition carrying the recording prints it.</param>
/// <param name="Held">Whether the album's files hold the recording.</param>
/// <param name="On">Every edition that carries it, in the order the editions were given.</param>
public sealed record CombinedTrack(EditionSlot Slot, bool Held, IReadOnlyList<ReleaseId> On);

/// <summary>
/// What an album's editions say together, led by the claimed pressing where there is one.
/// </summary>
/// <remarks>
/// An album is a release group, and the files are held to it rather than to one
/// pressing of it unless a pressing is proven. Its track list is then every
/// edition's at once: the deluxe's bonus disc, the Japanese edition's extra song
/// and the US edition's different closer are all things a person may be missing,
/// and each is shown with the editions that carry it rather than being ruled in
/// or out by a choice of edition nobody made.
///
/// Keyed on the recording, not the position. A bonus track inserted mid-album
/// moves every number after it and changes nothing about which songs the album
/// has.
/// </remarks>
public static class Editions
{
    /// <summary>The union of the editions' track lists.</summary>
    /// <param name="editions">
    /// The first sets the running order; each later one adds only the recordings
    /// not seen yet, in its own order. A later edition sharing no recording with
    /// the first is another performance of the album — a tour's other night, a
    /// re-recording, or MusicBrainz holding the same audio twice — so a song the
    /// list already has, by title, is not a track the library is missing; only a
    /// song new to the list is added from it. Callers pass the claimed edition
    /// first when there is one, else the one the files are nearest to.
    /// </param>
    /// <param name="held">The recordings the album's files hold.</param>
    /// <param name="firstIsClaimed">
    /// Whether the first edition is the pressing the files are proven to be,
    /// which is kept whole — a recording it prints twice keeps both places,
    /// because each of its files sits on its own slot and its track count is
    /// what the album is measured against. Otherwise every recording is one row.
    /// </param>
    public static IReadOnlyList<CombinedTrack> Combine(
        IReadOnlyList<EditionTracks> editions,
        IReadOnlySet<RecordingId> held,
        bool firstIsClaimed = false)
    {
        ArgumentNullException.ThrowIfNull(editions);
        ArgumentNullException.ThrowIfNull(held);

        var rows = new List<(EditionSlot Slot, List<ReleaseId> On)>();
        var seen = new Dictionary<RecordingId, int>();

        var lead = editions.Take(1).SelectMany(edition => edition.Slots).Select(slot => slot.Recording).ToHashSet();
        var songs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var e = 0; e < editions.Count; e++)
        {
            var edition = editions[e];
            var another = e > 0 && !edition.Slots.Any(slot => lead.Contains(slot.Recording));

            foreach (var slot in edition.Slots)
            {
                // One row per recording, at its first place. An unclaimed
                // edition that prints one twice is usually a box carrying the
                // album on two media — vinyl and the CD inside it — and a second
                // row would read as a track the library is missing.
                if (seen.TryGetValue(slot.Recording, out var row))
                {
                    if (!rows[row].On.Contains(edition.Id)) rows[row].On.Add(edition.Id);
                    if (!(firstIsClaimed && e == 0)) continue;
                }

                if (another && slot.Title is { } title && songs.Contains(title.Trim())) continue;

                seen.TryAdd(slot.Recording, rows.Count);
                rows.Add((slot, [edition.Id]));

                if (slot.Title is { } listed) songs.Add(listed.Trim());
            }
        }

        return [.. rows.Select(row => new CombinedTrack(row.Slot, held.Contains(row.Slot.Recording), row.On))];
    }

    /// <summary>
    /// The edition a set of files is most plausibly a rip of, for measuring what
    /// the rip is missing.
    /// </summary>
    /// <remarks>
    /// Fewest held recordings left off it first, then the shortest, so a folder
    /// holding the ten-track album is measured against the ten-track album and
    /// not told it lacks the deluxe's five bonus tracks. Among editions equally
    /// near, one whose lengths are printed and whose tracks are numbered rather
    /// than sided: the LP of a download is as near as its CD, and reads "A1" with
    /// no running times. Not a claim that the files <i>are</i> that edition —
    /// only the attribution pass makes that, and only on proof.
    /// </remarks>
    public static EditionTracks? Nearest(
        IReadOnlyList<EditionTracks> editions,
        IReadOnlySet<RecordingId> held)
    {
        ArgumentNullException.ThrowIfNull(editions);
        ArgumentNullException.ThrowIfNull(held);

        return editions
            .OrderBy(edition => held.Count(recording => !edition.Slots.Any(slot => slot.Recording == recording)))
            .ThenBy(edition => edition.Slots.Count)
            .ThenByDescending(edition => edition.Slots.All(slot => slot.Length is not null))
            .ThenByDescending(edition => edition.Slots.All(slot =>
                int.TryParse(slot.Number, NumberStyles.None, CultureInfo.InvariantCulture, out _)))
            .ThenBy(edition => edition.Id.Value)
            .FirstOrDefault();
    }
}
