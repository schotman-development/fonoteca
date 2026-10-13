namespace Fonoteca.Domain.Catalogue;

/// <summary>Which held recordings of a piece are one performance of it.</summary>
/// <remarks>
/// One album and one set of performers, as the artist page's <c>worksOf</c>
/// (<c>artistWorks.ts</c>) groups them: a symphony in seven tracks of one disc
/// is one performance, two singers' versions on one compilation are two.
///
/// <b>A piece in movements is one performance per album, whoever each track is
/// credited to</b> — a cast album bills every song to its singers, and by
/// performers one musical read as thirty performances. <c>worksOf</c> cannot
/// apply this half, since its rows do not say which work is a movement.
/// </remarks>
public static class Performances
{
    /// <returns>The groups, in the order each first appears.</returns>
    public static IReadOnlyList<IReadOnlyList<T>> Of<T>(
        IEnumerable<T> recordings,
        Func<T, string> album,
        Func<T, string?> performers,
        Func<T, bool> inMovements)
    {
        ArgumentNullException.ThrowIfNull(recordings);
        ArgumentNullException.ThrowIfNull(album);
        ArgumentNullException.ThrowIfNull(performers);
        ArgumentNullException.ThrowIfNull(inMovements);

        return [.. recordings
            .GroupBy(row => (album(row), inMovements(row), inMovements(row) ? null : performers(row)))
            .Select(performance => (IReadOnlyList<T>)[.. performance])];
    }
}
