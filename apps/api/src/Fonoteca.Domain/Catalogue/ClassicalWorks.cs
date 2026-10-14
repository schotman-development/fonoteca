namespace Fonoteca.Domain.Catalogue;

/// <summary>Whether a held work is classical, which is who the Composers page lists.</summary>
/// <remarks>
/// <b>Not a song, and a performance of it tagged classical and not
/// crossover.</b> Neither half is enough alone. MusicBrainz types almost every
/// popular song <c>Song</c> and leaves a symphony's movements untyped, but so
/// is many a jazz standard. And a crossover album is tagged classical whole,
/// which on its own put Slash, Taylor Swift and Bono among 275 "classical
/// composers" here. MusicBrainz states no genres on works themselves.
///
/// Genres are a performance's, so any one recording or album answers for the
/// work: Beethoven has 8 of 64 recordings tagged. One also tagged
/// <c>classical crossover</c> or <c>pop</c> answers nothing (the owner's
/// choice): that kept Morricone and a Neapolitan song's writers off, and costs
/// Bizet and Rachmaninoff, held here only as a crossover singer's Habanera and
/// Vocalise. An untyped piece on an album tagged plain classical still counts,
/// so the Shermans' overture on <i>Disney Goes Classical</i> does.
/// </remarks>
public static class ClassicalWorks
{
    /// <param name="type">The work's MusicBrainz type, or null where it states none.</param>
    /// <param name="genres">Stored genre columns of its recordings and their albums, one per performance or album, comma-separated.</param>
    public static bool Is(string? type, IEnumerable<string?> genres)
    {
        ArgumentNullException.ThrowIfNull(genres);

        return type is not ("Song" or "Soundtrack") && genres.Select(FileGenres.Split).Any(Classical);
    }

    private static bool Classical(IReadOnlyList<string> genres) =>
        genres.Any(genre => genre == "classical" || genre.EndsWith(" classical", StringComparison.Ordinal))
        && !genres.Any(genre => genre is "classical crossover" or "pop");
}
