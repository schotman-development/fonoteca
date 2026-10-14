using Fonoteca.Data;
using Fonoteca.Domain.Catalogue;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace Fonoteca.Api.Endpoints;

/*
 * Fonoteca's own genres (`Genres`), as the Genres screen browses them. An
 * album's are what its files carry (`FileGenres.OfAlbum`), so the screen,
 * the tags and Subsonic list an album under the same names.
 */
public static partial class CatalogueEndpoints
{
    private const int GenreCovers = 4;

    private static void MapGenreEndpoints(IEndpointRouteBuilder group)
    {
        group.MapGet("/genres", GetGenres)
            .WithName("GetGenres")
            .WithSummary("Every genre a held album carries: its tops, how many albums, and sleeves to show it by.")
            .WithDescription(
                "Most albums first, a tie in the tree's order; a genre no held album carries is "
                + "left out. `tops` is empty for a top and names two for a fusion, "
                + "which sits under both. An album counts under each genre it carries and under "
                + "their tops. `covers` are up to four releases whose stored sleeves stand for "
                + "the genre, albums with one first. The albums and artists of a genre are "
                + "`/albums?genre=` and `/artists?genre=`.");
    }

    internal static async Task<Ok<GenreListResponse>> GetGenres(
        FonotecaDbContext db,
        CancellationToken cancellationToken)
    {
        var albums = await AlbumGenresAsync(db, cancellationToken).ConfigureAwait(false);

        var counted = Genres.All
            .Select(genre => (Name: genre, Albums: albums.Where(album => album.Genres.Contains(genre, StringComparer.Ordinal)).ToList()))
            .Where(entry => entry.Albums.Count > 0)
            .OrderByDescending(entry => entry.Albums.Count)
            .ToList();

        var faces = counted.ToDictionary(
            entry => entry.Name,
            entry => entry.Albums
                .OrderByDescending(album => album.HasCover)
                .ThenByDescending(album => album.Files)
                .ThenBy(album => album.Id.Value)
                .Take(GenreCovers)
                .Select(album => album.Id)
                .ToList());

        var editions = await EditionFactsAsync(db, [.. faces.Values.SelectMany(ids => ids).Distinct()], cancellationToken)
            .ConfigureAwait(false);

        return TypedResults.Ok(new GenreListResponse(
        [
            .. counted.Select(entry => new GenreSummary(
                entry.Name,
                Genres.TopsOf(entry.Name),
                entry.Albums.Count,
                [.. faces[entry.Name]
                    .Select(id => DisplayEdition(editions.GetValueOrDefault(id) ?? [])?.Id.Value)
                    .OfType<Guid>()])),
        ]));
    }

    /// <summary>The held albums carrying a genre, or every one carrying a top it sits under.</summary>
    internal static async Task<HashSet<ReleaseGroupId>> AlbumsOfGenreAsync(
        FonotecaDbContext db,
        string genre,
        CancellationToken cancellationToken) =>
    [
        .. (await AlbumGenresAsync(db, cancellationToken).ConfigureAwait(false))
            .Where(album => album.Genres.Contains(genre.Trim(), StringComparer.OrdinalIgnoreCase))
            .Select(album => album.Id),
    ];

    /// <summary>
    /// Every genre name the held files' albums and recordings state, as stored:
    /// what a question about Fonoteca's genres is asked of in SQL (<see cref="Genres.Stating"/>).
    /// </summary>
    internal static async Task<List<string>> StatedGenresAsync(
        FonotecaDbContext db,
        CancellationToken cancellationToken)
    {
        var albums = await db.ReleaseGroups
            .AsNoTracking()
            .Where(album => album.Genres != null && album.Files.Any())
            .Select(album => album.Genres!)
            .Distinct()
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var recordings = await db.Recordings
            .AsNoTracking()
            .Where(recording => recording.Genres != null && recording.Files.Any())
            .Select(recording => recording.Genres!)
            .Distinct()
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return [.. albums.Concat(recordings).SelectMany(FileGenres.Split).Distinct(StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>
    /// Each held album's genres, its own else its tracks' commonest. ponytail:
    /// one row per file for the recordings' genres; a genre column per album is
    /// the upgrade if this shows at 100k files.
    /// </summary>
    private static async Task<List<AlbumGenres>> AlbumGenresAsync(
        FonotecaDbContext db,
        CancellationToken cancellationToken)
    {
        var rows = await db.ReleaseGroups
            .AsNoTracking()
            .Where(album => album.Files.Any())
            .Select(album => new
            {
                album.Id,
                album.Genres,
                Files = album.Files.Count,
                HasCover = album.Releases.Any(release => db.ReleaseCovers.Any(cover => cover.ReleaseId == release.Id && cover.Bytes != null)),
                RecordingGenres = album.Files
                    .Select(file => file.Recording != null ? file.Recording.Genres : null)
                    .ToList(),
            })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return [.. rows.Select(row => new AlbumGenres(row.Id, row.Files, row.HasCover, FileGenres.OfAlbum(row.Genres, row.RecordingGenres)))];
    }

    private sealed record AlbumGenres(ReleaseGroupId Id, int Files, bool HasCover, IReadOnlyList<string> Genres);
}

/// <summary>The genres the held albums carry, most albums first.</summary>
public sealed record GenreListResponse(IReadOnlyList<GenreSummary> Genres);

/// <summary>One genre of Fonoteca's own, and how much of the library it holds.</summary>
/// <param name="Name">As files carry it, e.g. "Blues Rock".</param>
/// <param name="Tops">The tops it sits under: empty for a top, two for a fusion.</param>
/// <param name="Albums">Held albums carrying it, or carrying a subgenre of it.</param>
/// <param name="Covers">Up to four release ids whose stored sleeves stand for it.</param>
public sealed record GenreSummary(string Name, IReadOnlyList<string> Tops, int Albums, IReadOnlyList<Guid> Covers);
