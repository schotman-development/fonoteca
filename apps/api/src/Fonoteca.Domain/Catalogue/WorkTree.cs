namespace Fonoteca.Domain.Catalogue;

/// <summary>The whole piece a work belongs to: the top of its <c>parts</c> chain, below any mere collection.</summary>
/// <remarks>
/// MusicBrainz nests a work as deep as its structure goes: a Swan Lake
/// variation is part of a pas de six, which is part of Act III, which is part
/// of Swan Lake. The piece is the top — what counts as one song, what the
/// Composers and Compositions pages list, and what an album's track list heads
/// a run with (the owner's choice, 2026-10-14, over stopping one level up) —
/// <b>except a top that only gathers pieces</b> (<see cref="Cut"/>), the
/// piece a listener names being the owner's measure since 2026-10-23 ("whatever
/// gives the truest result").
///
/// A cycle in somebody's data ends the climb at the last work before it
/// repeats, rather than looping.
/// </remarks>
public static class WorkTree
{
    /// <param name="work">Any work, a movement or a whole piece.</param>
    /// <param name="parents">Each work's parent, for the works that have one.</param>
    /// <returns>The work itself when it is part of nothing.</returns>
    public static WorkId Top(WorkId work, IReadOnlyDictionary<WorkId, WorkId> parents)
    {
        ArgumentNullException.ThrowIfNull(parents);

        var seen = new HashSet<WorkId> { work };

        while (parents.TryGetValue(work, out var parent) && seen.Add(parent)) work = parent;

        return work;
    }

    /// <summary>The <c>parts</c> chain with every link up into a collection cut, so <see cref="Top"/> stops below it.</summary>
    /// <remarks>
    /// <b>A collection is a top MusicBrainz gives no type and whose every part
    /// it types</b>: <i>Der Ring des Nibelungen</i> over four operas,
    /// <i>6 Flute Concertos, op. 10</i> over six concertos. A typed top stays
    /// whole however its parts are typed — Swan Lake a ballet, Giustino an
    /// opera with a sinfonia typed Symphony — and so does an untyped one with
    /// any part untyped, so an untyped opera with one aria typed is still the
    /// opera, and a set MusicBrainz half-typed is not split down the middle.
    /// "Every part" is MusicBrainz's list, not the parts held here.
    ///
    /// Cut from the top down only: a collection below a typed work is inside
    /// a piece, and one below an untyped work with an untyped part is too.
    /// Measured 2026-10-23, four tops here are collections: the Ring, op. 8,
    /// op. 10 and BWV 1001–1006.
    /// </remarks>
    /// <param name="parents">Each work's parent, for the works that have one.</param>
    /// <param name="collections">The untyped works whose every part is typed.</param>
    public static Dictionary<WorkId, WorkId> Cut(
        IReadOnlyDictionary<WorkId, WorkId> parents,
        IReadOnlySet<WorkId> collections)
    {
        ArgumentNullException.ThrowIfNull(parents);
        ArgumentNullException.ThrowIfNull(collections);

        var tops = new Dictionary<WorkId, bool>();

        // Whether the work heads its chain once cut: it has no parent, or its
        // link up is cut. A work already on the stack is in a cycle, which no
        // cut can start from.
        bool HeadsItsChain(WorkId work, HashSet<WorkId> climbing)
        {
            if (!parents.TryGetValue(work, out var parent)) return true;
            if (tops.TryGetValue(work, out var known)) return known;
            if (!climbing.Add(work)) return false;

            return tops[work] = collections.Contains(parent) && HeadsItsChain(parent, climbing);
        }

        return parents
            .Where(link => !HeadsItsChain(link.Key, []))
            .ToDictionary(link => link.Key, link => link.Value);
    }
}
