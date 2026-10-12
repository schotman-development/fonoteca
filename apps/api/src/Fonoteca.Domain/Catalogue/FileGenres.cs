namespace Fonoteca.Domain.Catalogue;

/// <summary>Which genres a file carries, and an album shows.</summary>
/// <remarks>
/// <b>The album's, and the recording's only where the album has none.</b> One
/// album should sit under one set of genres in any player that browses by
/// them; a track's own votes differ from its album's often enough — one song
/// on a rock album tagged "pop" — that preferring them would scatter an album
/// across a genre list. Spelled as MusicBrainz spells them, most-voted first,
/// and cut at <see cref="Max"/>, which is Picard's default.
///
/// One rule for the tag write and for Subsonic, so what a file says and what a
/// client is told cannot drift apart.
/// </remarks>
public static class FileGenres
{
    public const int Max = 5;

    /// <param name="album">The album's stored genres, comma-separated, or null.</param>
    /// <param name="recording">The recording's, likewise.</param>
    public static IReadOnlyList<string> Of(string? album, string? recording)
    {
        var own = Split(album);
        return [.. (own.Count > 0 ? own : Split(recording)).Take(Max)];
    }

    /// <summary>
    /// An album's: its own, else the genres most of its recordings carry.
    /// </summary>
    /// <remarks>
    /// What a list of albums is filtered by, so an album nobody voted on still
    /// appears under the genres its files carry. Ties keep the order the
    /// recordings first gave them, so a rerun answers the same.
    /// </remarks>
    public static IReadOnlyList<string> OfAlbum(string? album, IEnumerable<string?> recordings)
    {
        ArgumentNullException.ThrowIfNull(recordings);

        var own = Split(album);
        if (own.Count > 0) return [.. own.Take(Max)];

        return [.. recordings
            .SelectMany(Split)
            .GroupBy(genre => genre, StringComparer.Ordinal)
            .Select((group, first) => (Genre: group.Key, Count: group.Count(), First: first))
            .OrderByDescending(entry => entry.Count)
            .ThenBy(entry => entry.First)
            .Take(Max)
            .Select(entry => entry.Genre)];
    }

    /// <summary>A stored genre column, as the list it was joined from.</summary>
    public static IReadOnlyList<string> Split(string? joined) =>
        string.IsNullOrWhiteSpace(joined)
            ? []
            : joined.Split(", ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
