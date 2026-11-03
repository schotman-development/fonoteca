using Fonoteca.Api.Library;
using Fonoteca.Data;
using Fonoteca.Domain.Abstractions;
using Fonoteca.Domain.Catalogue;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace Fonoteca.Api.Endpoints;

/*
 * Fonoteca's own genres (`Genres`), as the Genres screen browses them. An
 * album's are what its files carry (`FileGenres.OfAlbum`), so the screen,
 * the tags and Subsonic list an album under the same names.
 *
 * And a person's answer to which genres an album is, which goes in the
 * album's own genres column, named as theirs (`GenreSources`, ADR 0020), so
 * every reader takes it — the SQL ones too — and no rule replaces it.
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

        group.MapPut("/albums/{id:guid}/genres", SetAlbumGenres)
            .RequireRole(AccountRoles.CoverArt)
            .WithName("SetAlbumGenres")
            .WithSummary("Say which genres an album is, over MusicBrainz's and the shops'.")
            .WithDescription(
                "Each a MusicBrainz genre or one of Fonoteca's tree, most important first, at most "
                + "twelve. They replace the album's genres and no enrichment run replaces them; the "
                + "files get them at the next tag write. An empty list takes a person's genres back, "
                + "and MusicBrainz and the shops are asked again on the next enrichment run.")
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound);
    }

    internal static async Task<Results<Ok<AlbumGenresResponse>, ProblemHttpResult>> SetAlbumGenres(
        Guid id,
        AlbumGenresRequest request,
        FonotecaDbContext db,
        ICallerContext caller,
        CancellationToken cancellationToken)
    {
        if (request?.Genres is not { } typed || typed.Count > EnrichmentService.MaxGenres)
        {
            return TypedResults.Problem(
                title: "Not a list of genres",
                detail: $"The body names the album's genres, at most {EnrichmentService.MaxGenres}.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        if (GenreSources.Stated(typed, out var unknown) is not { } stated)
        {
            return TypedResults.Problem(
                title: "Not a genre",
                detail: $"'{unknown}' is neither a MusicBrainz genre nor one of Fonoteca's.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var groupId = new ReleaseGroupId(id);

        var album = await db.ReleaseGroups
            .FirstOrDefaultAsync(row => row.Id == groupId, cancellationToken)
            .ConfigureAwait(false);

        if (album is null)
        {
            return TypedResults.Problem(
                title: "No such album",
                detail: $"The catalogue has no album with id {id}.",
                statusCode: StatusCodes.Status404NotFound);
        }

        if (stated.Count > 0)
        {
            album.Genres = EnrichmentService.Joined(stated);
            album.GenresSource = caller is AgentCallerContext ? GenreSources.Agent : GenreSources.Person;
        }
        else if (GenreSources.IsDecided(album.GenresSource))
        {
            album.Genres = null;
            album.GenresSource = null;
            album.GenresLookupUtc = null;
            album.ShopGenresLookupUtc = null;
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return TypedResults.Ok(new AlbumGenresResponse(FileGenres.Split(album.Genres), album.GenresSource));
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

/// <param name="Genres">MusicBrainz's spellings or the tree's, most important first; empty takes a person's back.</param>
public sealed record AlbumGenresRequest(IReadOnlyList<string?>? Genres);

/// <param name="Genres">The album's genres as stored, MusicBrainz's spellings.</param>
/// <param name="Source">Who stated them: "person", "agent", a shop, or null for MusicBrainz.</param>
public sealed record AlbumGenresResponse(IReadOnlyList<string> Genres, string? Source);

/// <summary>The genres the held albums carry, most albums first.</summary>
public sealed record GenreListResponse(IReadOnlyList<GenreSummary> Genres);

/// <summary>One genre of Fonoteca's own, and how much of the library it holds.</summary>
/// <param name="Name">As files carry it, e.g. "Blues Rock".</param>
/// <param name="Tops">The tops it sits under: empty for a top, two for a fusion.</param>
/// <param name="Albums">Held albums carrying it, or carrying a subgenre of it.</param>
/// <param name="Covers">Up to four release ids whose stored sleeves stand for it.</param>
public sealed record GenreSummary(string Name, IReadOnlyList<string> Tops, int Albums, IReadOnlyList<Guid> Covers);
