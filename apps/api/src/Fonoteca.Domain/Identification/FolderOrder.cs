using Fonoteca.Domain.Abstractions;
using Fonoteca.Domain.Catalogue;

namespace Fonoteca.Domain.Identification;

/// <summary>
/// The order an album folder's files run in, checked against the album's editions.
/// </summary>
/// <remarks>
/// <b>Order, not track numbers.</b> An album told as a whole is heard in sequence,
/// and the sequence is what survives between editions: a bonus track, a second
/// disc or a vinyl side renumbers everything after it and changes nothing about
/// what comes before what. So what the catalogue claims about a folder with no
/// proven pressing is the order, and the numbers stay the file's own — a file
/// with none is given its place in this order when tags are written.
///
/// <b>Evidence against, and only from one side.</b> The files' order is
/// <see cref="FolderOrderOutcome.Corroborated"/> when some official edition
/// prints their shared recordings in it; it is
/// <see cref="FolderOrderOutcome.Contradicted"/> only when a pair of files the
/// tags put one way round is printed the other way round by <i>every</i> official
/// edition holding both — US and UK editions that disagree with each other leave
/// nothing to contradict. And only on the strength of tags: a numbered file name
/// is a weaker claim, and a <c>Bonus</c> folder sorting before <c>CD 1</c> is a
/// fact about spelling. A gather cut off at its cap has not seen every edition,
/// so it cannot say every edition disagrees.
/// </remarks>
public static class FolderOrder
{
    /// <summary>What the files' own order says, and that order, first to last.</summary>
    /// <param name="files">The folder's files; those without a recording still take a place.</param>
    /// <param name="officialEditions">The album's official editions the gather fetched.</param>
    /// <param name="gatherComplete">False when the gather stopped at its cap, and some editions were never seen.</param>
    /// <returns>
    /// The outcome, and the file ids in running order — empty where the folder
    /// has no order anybody can vouch for.
    /// </returns>
    public static (FolderOrderOutcome Outcome, IReadOnlyList<MediaFileId> Order) Check(
        IReadOnlyList<FolderFile> files,
        IReadOnlyList<MusicBrainzRelease> officialEditions,
        bool gatherComplete)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(officialEditions);

        var (own, fromTags) = OwnOrder(files);

        var sequences = officialEditions
            .Select(edition => Positions(edition.Tracks.Select(track => track.RecordingId)))
            .ToList();

        if (own is null)
        {
            var taken = TakenFromEditions(files, officialEditions, sequences);

            return taken is null
                ? (FolderOrderOutcome.Uncorroborated, [])
                : (FolderOrderOutcome.TakenFromEditions, taken);
        }

        var ids = own.Select(file => file.Id).ToList();
        var recordings = Distinct(own.Select(file => file.Recording));

        if (sequences.Exists(edition => Agrees(recordings, edition))) return (FolderOrderOutcome.Corroborated, ids);

        if (gatherComplete && fromTags && Reversed(recordings, sequences)) return (FolderOrderOutcome.Contradicted, ids);

        return (FolderOrderOutcome.Uncorroborated, ids);
    }

    /// <summary>
    /// The order the files themselves claim, and whether it came from their tags.
    /// </summary>
    /// <remarks>
    /// Tags first, but only when every file carries a track number and no two
    /// land on the same place — a set where two files both say "1" is two rips or
    /// a tagger's mistake, and neither is an order. Then numbered file names, in
    /// number order across the whole path so disc folders count. Then nothing:
    /// alphabetical is not an order anybody chose.
    /// </remarks>
    private static (IReadOnlyList<FolderFile>? Order, bool FromTags) OwnOrder(IReadOnlyList<FolderFile> files)
    {
        if (files.All(file => file.TaggedTrack is not null)
            && files.Select(file => (file.TaggedDisc ?? 1, file.TaggedTrack)).Distinct().Count() == files.Count)
        {
            return ([.. files.OrderBy(file => file.TaggedDisc ?? 1).ThenBy(file => file.TaggedTrack)], true);
        }

        if (files.All(file => NameOf(file.Path) is [var first, ..] && char.IsAsciiDigit(first)))
        {
            return (
                [.. files
                    .OrderBy(file => AlbumFolder.SortKey(file.Path), StringComparer.Ordinal)
                    .ThenBy(file => file.Path, StringComparer.Ordinal)],
                false);
        }

        return (null, false);
    }

    /// <summary>
    /// Where the files carry no order: the one every edition agrees on, laid out
    /// as the edition holding most of the folder prints it — or null if two
    /// editions disagree about any pair the folder holds.
    /// </summary>
    private static List<MediaFileId>? TakenFromEditions(
        IReadOnlyList<FolderFile> files,
        IReadOnlyList<MusicBrainzRelease> editions,
        List<Dictionary<Mbid, int>> sequences)
    {
        var held = Distinct(files.Select(file => file.Recording));
        if (held.Count < 2 || editions.Count == 0) return null;

        for (var a = 0; a < held.Count; a++)
        {
            for (var b = a + 1; b < held.Count; b++)
            {
                var ways = sequences
                    .Where(edition => edition.ContainsKey(held[a]) && edition.ContainsKey(held[b]))
                    .Select(edition => edition[held[a]] < edition[held[b]])
                    .Distinct()
                    .Count();

                if (ways > 1) return null;
            }
        }

        var fullest = sequences
            .OrderByDescending(edition => held.Count(edition.ContainsKey))
            .First();

        if (held.Count(fullest.ContainsKey) < 2) return null;

        return
        [
            .. files
                .OrderBy(file => file.Recording is { } recording && fullest.TryGetValue(recording, out var at) ? at : int.MaxValue)
                .ThenBy(file => AlbumFolder.SortKey(file.Path), StringComparer.Ordinal)
                .Select(file => file.Id),
        ];
    }

    /// <summary>Does this edition print the recordings it shares with the files in the files' order? Two shared are needed to say.</summary>
    private static bool Agrees(List<Mbid> recordings, Dictionary<Mbid, int> edition)
    {
        var shared = recordings.Where(edition.ContainsKey).Select(recording => edition[recording]).ToList();

        return shared.Count >= 2 && shared.Zip(shared.Skip(1)).All(pair => pair.First < pair.Second);
    }

    /// <summary>
    /// Is some pair the files order one way printed the other way by every
    /// edition that holds both — and held by at least one?
    /// </summary>
    private static bool Reversed(List<Mbid> recordings, List<Dictionary<Mbid, int>> sequences)
    {
        for (var a = 0; a < recordings.Count; a++)
        {
            for (var b = a + 1; b < recordings.Count; b++)
            {
                var holding = sequences
                    .Where(edition => edition.ContainsKey(recordings[a]) && edition.ContainsKey(recordings[b]))
                    .ToList();

                if (holding.Count > 0 && holding.TrueForAll(edition => edition[recordings[b]] < edition[recordings[a]]))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>Each recording's first place in an edition's running order.</summary>
    private static Dictionary<Mbid, int> Positions(IEnumerable<Mbid?> running)
    {
        var at = new Dictionary<Mbid, int>();
        var place = 0;

        foreach (var recording in running)
        {
            if (recording is { } known) at.TryAdd(known, place);
            place++;
        }

        return at;
    }

    /// <summary>The recordings in order, each once, the unidentified left out.</summary>
    private static List<Mbid> Distinct(IEnumerable<Mbid?> recordings) =>
        [.. recordings.OfType<Mbid>().Distinct()];

    private static string NameOf(string path) => path[(path.LastIndexOf('/') + 1)..];
}
