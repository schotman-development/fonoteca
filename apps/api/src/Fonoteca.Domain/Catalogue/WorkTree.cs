namespace Fonoteca.Domain.Catalogue;

/// <summary>The whole piece a work belongs to: the top of its <c>parts</c> chain.</summary>
/// <remarks>
/// MusicBrainz nests a work as deep as its structure goes: a Swan Lake
/// variation is part of a pas de six, which is part of Act III, which is part
/// of Swan Lake. The piece is the top — what counts as one song, what the
/// Composers and Compositions pages list, and what an album's track list heads
/// a run with (the owner's choice, 2026-10-14, over stopping one level up).
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
}
