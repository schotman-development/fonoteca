using Fonoteca.Data;
using Fonoteca.Domain.Catalogue;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace Fonoteca.Api.Endpoints;

/*
 * The home page's totals. The counts of artists and albums stay on
 * `/api/system/info`, so the two pages that show them cannot disagree.
 */
public static partial class CatalogueEndpoints
{
    private const int OverviewGenres = 12;

    private static void MapOverviewEndpoints(IEndpointRouteBuilder group)
    {
        group.MapGet("/overview", GetLibraryOverview)
            .WithName("GetLibraryOverview")
            .WithSummary("The library in totals: songs, composers, compositions, quality, formats, decades and genres.");
    }

    internal static async Task<Ok<LibraryOverviewResponse>> GetLibraryOverview(
        FonotecaDbContext db,
        CancellationToken cancellationToken)
    {
        // ponytail: one row per file, because the tier is a Domain rule rather
        // than a column. Group in SQL on the quality columns if this shows at 100k.
        var files = await db.MediaFiles
            .Select(file => new OverviewFile(file.Quality, file.Measurement))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        // Albums held, as `/api/system/info` counts them, less the folders
        // answered as no release: those have no year and no genres to give.
        // Their genres as the Genres screen counts them: their own, else their tracks'.
        var albums = (await db.ReleaseGroups
            .Where(group => group.Files.Any())
            .Select(group => new
            {
                group.FirstReleaseYear,
                group.Genres,
                RecordingGenres = group.Files
                    .Select(file => file.Recording != null ? file.Recording.Genres : null)
                    .ToList(),
            })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false))
            .Select(group => new OverviewAlbum(group.FirstReleaseYear, FileGenres.OfAlbum(group.Genres, group.RecordingGenres)))
            .ToList();

        var held = await db.Recordings
            .AsNoTracking()
            .Where(recording => recording.Files.Any())
            .Select(recording => recording.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var pieces = await PiecesAsync(db, cancellationToken).ConfigureAwait(false);

        // The Composers and Compositions pages' own reading, so the tiles and
        // the totals on those pages are one number.
        var works = await HeldWorksAsync(db, cancellationToken).ConfigureAwait(false);
        var composers = await ClassicalComposersAsync(db, cancellationToken).ConfigureAwait(false);

        var fileTotals = LibraryOverview.OfFiles(files);
        var albumTotals = LibraryOverview.OfAlbums(albums, OverviewGenres);

        return TypedResults.Ok(new LibraryOverviewResponse(
            Songs: Songs.Count(held, pieces),
            Composers: works.SelectMany(work => composers[work.Work]).Distinct().Count(),
            Compositions: works.Select(work => work.Piece ?? work.Work).Distinct().Count(),
            Quality: [.. fileTotals.Tiers.Select(entry => new OverviewCount(entry.Tier.ToString(), entry.Files))],
            Formats: [.. fileTotals.Codecs.Select(entry => new OverviewCount(entry.Codec, entry.Files))],
            Decades: [.. albumTotals.Decades.Select(entry => new DecadeCount(entry.Decade, entry.Albums))],
            UndatedAlbums: albumTotals.Undated,
            Genres: [.. albumTotals.Genres.Select(entry => new OverviewCount(entry.Genre, entry.Albums))]));
    }
}

/// <param name="Songs">Held songs, a work's movements counting once (<see cref="Fonoteca.Domain.Catalogue.Songs"/>).</param>
/// <param name="Composers">Everyone who composed a classical piece held, as <c>GET /composers</c> lists them.</param>
/// <param name="Compositions">Pieces held, a work's movements counting once, as <c>GET /compositions</c> lists them.</param>
/// <param name="Quality">Files per <see cref="QualityTier"/> name, best first; <c>Unknown</c> is not yet probed or unreadable.</param>
/// <param name="Formats">Probed files per codec, commonest first.</param>
/// <param name="Decades">Held albums per decade of first release, oldest first.</param>
/// <param name="Genres">The commonest genres, each counting the held albums that carry it.</param>
public sealed record LibraryOverviewResponse(
    int Songs,
    int Composers,
    int Compositions,
    IReadOnlyList<OverviewCount> Quality,
    IReadOnlyList<OverviewCount> Formats,
    IReadOnlyList<DecadeCount> Decades,
    int UndatedAlbums,
    IReadOnlyList<OverviewCount> Genres);

public sealed record OverviewCount(string Name, int Count);

public sealed record DecadeCount(int Decade, int Count);
