namespace Fonoteca.Domain.Catalogue;

/// <summary>How many songs a set of recordings is.</summary>
/// <remarks>
/// A recording of a movement — its work is part of a larger one,
/// <c>Works.ParentId</c>, one level — counts as that piece, so the four
/// movements of a symphony are one song; so are two performances of it. Every
/// other recording counts as itself. The owner's choice: an orchestra's 228
/// tracks were mostly movements, and every count of music a screen shows is in
/// songs.
/// </remarks>
public static class Songs
{
    public static int Count(
        IEnumerable<RecordingId> recordings,
        IReadOnlyDictionary<RecordingId, WorkId> pieceOf)
    {
        ArgumentNullException.ThrowIfNull(recordings);
        ArgumentNullException.ThrowIfNull(pieceOf);

        var pieces = new HashSet<WorkId>();
        var singles = new HashSet<RecordingId>();

        foreach (var recording in recordings)
        {
            if (pieceOf.TryGetValue(recording, out var piece)) pieces.Add(piece);
            else singles.Add(recording);
        }

        return pieces.Count + singles.Count;
    }
}
