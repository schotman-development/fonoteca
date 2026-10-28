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

    /// <summary>Whether a piece is classical by the albums it is on, where those albums have no genres of their own.</summary>
    /// <remarks>
    /// <b>An album nobody voted on shows its tracks' commonest genres</b>
    /// (<see cref="FileGenres.Voted"/>), and a symphony there whose own
    /// movements nobody tagged is still on a classical album: 23 tracks of a
    /// Shostakovich cycle, a Tchaikovsky concerto on a ballet album, Mahler's
    /// First. <b>But a neighbour's tag is weaker than the piece's own</b>, and
    /// taken alone it let a film-music concert in whole — Zimmer's
    /// <i>Live in Prague</i>, a John Williams best-of — and with it Pharrell
    /// Williams and Johnny Marr among the composers, the crossover the owner
    /// kept off (2026-10-14). So it counts only for a piece MusicBrainz types
    /// as a form, never <c>Song</c> or <c>Soundtrack</c>: measured
    /// 2026-10-23, that admits the 43 tracks and none of the 22.
    /// </remarks>
    /// <param name="form">The piece's MusicBrainz type, asked of the whole piece even where only a movement is held.</param>
    /// <param name="borrowed">Its albums' tracks' votes, one per album that has none of its own.</param>
    public static bool ByItsAlbums(string? form, IEnumerable<string?> borrowed)
    {
        ArgumentNullException.ThrowIfNull(borrowed);

        return form is not (null or "Song" or "Soundtrack") && borrowed.Select(FileGenres.Split).Any(Classical);
    }

    /// <summary>The era a piece is of: by the year it was begun, else its earliest-born composer at thirty.</summary>
    /// <remarks>
    /// Derived, since MusicBrainz states no period; the bounds and the thirty
    /// years are the ones the owner's client proposed for its Period column
    /// (2026-10-19). Null where neither year is known.
    /// </remarks>
    public static string? Period(int? begun, IEnumerable<int?> born)
    {
        ArgumentNullException.ThrowIfNull(born);

        return (begun ?? born.Min() + 30) switch
        {
            null => null,
            < 1400 => "Medieval",
            < 1600 => "Renaissance",
            < 1750 => "Baroque",
            < 1820 => "Classical",
            < 1910 => "Romantic",
            < 1975 => "Modern",
            _ => "Contemporary",
        };
    }

    private static bool Classical(IReadOnlyList<string> genres) =>
        genres.Any(genre => genre == "classical" || genre.EndsWith(" classical", StringComparison.Ordinal))
        && !genres.Any(genre => genre is "classical crossover" or "pop");
}
