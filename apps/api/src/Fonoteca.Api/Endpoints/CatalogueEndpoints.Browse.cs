using Fonoteca.Data;
using Fonoteca.Domain.Catalogue;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace Fonoteca.Api.Endpoints;

/*
 * The library by track, by composer and by composition, beside the artist and
 * album lists.
 *
 * A composition is the piece: a movement's parent work (`Works.ParentId`), or
 * the work itself where it has none. Its composers are the `composer` links on
 * the works its held recordings perform — the parent is minted from a stub and
 * carries no writers of its own.
 */
public static partial class CatalogueEndpoints
{
    /// <summary>Rows per page of tracks and of compositions.</summary>
    private const int BrowsePage = 100;

    private const string ComposerLink = "composer";

    private static void MapBrowseEndpoints(IEndpointRouteBuilder group)
    {
        group.MapGet("/tracks", GetTracks)
            .WithName("GetTracks")
            .WithSummary("Every recording the library holds a file of, a page at a time.")
            .WithDescription(
                "One row per recording, so two encodings of a song are one row and `files` says "
                + "how many. Ordered by title, or by `sort=artist` (the first billed name, "
                + "uncredited last), `sort=album` (the album's title, then the file's place in its "
                + "folder) or `sort=added` (newest file first). `query` matches the title, a billed "
                + "name or the album title, case-insensitively, anywhere in the string. A file no "
                + "recording is known for is not listed: it is a question on Identify.");

        group.MapGet("/composers", GetComposers)
            .WithName("GetComposers")
            .WithSummary("Everyone who wrote a piece the library holds a recording of.")
            .WithDescription(
                "Ordered by sort name, or by `sort=compositions` for the most pieces held first. "
                + "`compositionCount` counts pieces, a work's movements once; `songCount` is the "
                + "artist list's. `query` filters on the name as the artist list does. Every "
                + "songwriter is here, not only the classical composers.");

        group.MapGet("/compositions", GetCompositions)
            .WithName("GetCompositions")
            .WithSummary("Every piece the library holds a recording of, with each performance of it.")
            .WithDescription(
                "A piece is a movement's parent work, or the work itself. A performance is one "
                + "album and one set of performers, and a piece's movements on one album are one "
                + "whoever each is credited to (`Performances`). Ordered by title, by `sort=recorded` for the most performances "
                + "first, or by `sort=composer` (the first composer by name, none last). `query` "
                + "matches the title or a composer's name, case-insensitively.");
    }

    internal static async Task<Ok<TrackListResponse>> GetTracks(
        FonotecaDbContext db,
        CancellationToken cancellationToken,
        string? query = null,
        string? sort = null,
        int skip = 0,
        int take = BrowsePage)
    {
        take = Math.Clamp(take, 1, MaxTake);
        skip = Math.Max(skip, 0);

        var recordings = db.Recordings.AsNoTracking().Where(recording => recording.Files.Any());

        if (!string.IsNullOrWhiteSpace(query))
        {
            var pattern = $"%{Escape(query.Trim())}%";

            recordings = recordings.Where(recording =>
                EF.Functions.ILike(recording.Title, pattern, "\\")
                || recording.Credits.Any(credit => EF.Functions.ILike(
                    credit.CreditedAs ?? credit.Artist!.LatinName ?? credit.Artist!.Name, pattern, "\\"))
                || recording.Files.Any(file => file.ReleaseGroup != null
                    && EF.Functions.ILike(file.ReleaseGroup.Title, pattern, "\\")));
        }

        var total = await recordings.CountAsync(cancellationToken).ConfigureAwait(false);

        // In SQL, because the list is paged there. "Added" is the album list's
        // reading: a file id is a UUIDv7 minted when the scan first saw it.
        var sorted = sort switch
        {
            "artist" => recordings
                .OrderBy(recording => recording.Credits
                    .OrderBy(credit => credit.Position)
                    .Select(credit => credit.CreditedAs ?? credit.Artist!.LatinName ?? credit.Artist!.Name)
                    .FirstOrDefault())
                .ThenBy(recording => recording.Title),
            "album" => recordings
                .OrderBy(recording => recording.Files
                    .OrderBy(file => file.Path)
                    .Select(file => file.ReleaseGroup!.Title)
                    .FirstOrDefault())
                .ThenBy(recording => recording.Files.Min(file => file.Path)),
            "added" => recordings
                .OrderByDescending(recording => recording.Files
                    .OrderByDescending(file => file.Id)
                    .Select(file => file.Id)
                    .FirstOrDefault()),
            _ => recordings.OrderBy(recording => recording.Title),
        };

        var rows = await sorted
            .ThenBy(recording => recording.Id)
            .Skip(skip)
            .Take(take)
            .Select(recording => new
            {
                recording.Id,
                recording.Title,
                recording.Duration,
                WorkTitle = recording.Work == null ? null : recording.Work.Title,
                Credits = recording.Credits
                    .OrderBy(credit => credit.Position)
                    .Select(credit => new CreditName(
                        credit.ArtistId.Value,
                        credit.CreditedAs ?? credit.Artist!.LatinName ?? credit.Artist!.Name,
                        credit.JoinPhrase))
                    .ToList(),
                Files = recording.Files
                    .OrderBy(file => file.Path)
                    .Select(file => new
                    {
                        file.Path,
                        file.SizeBytes,
                        file.Quality,
                        file.Integrity,
                        file.Measurement,
                        file.ReleaseId,
                        file.ReleaseGroupId,
                        AlbumTitle = file.ReleaseGroup == null ? null : file.ReleaseGroup.Title,
                        AlbumYear = file.ReleaseGroup == null ? null : file.ReleaseGroup.FirstReleaseYear,
                        AlbumMbid = file.ReleaseGroup == null ? null : file.ReleaseGroup.Mbid,
                    })
                    .ToList(),
            })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        // The artist page's album rule: a claimed pressing's file first, and
        // where none is claimed the display edition's sleeve.
        var albums = rows.ToDictionary(
            row => row.Id,
            row => row.Files
                .Where(file => file.ReleaseGroupId != null)
                .OrderBy(file => file.ReleaseId == null)
                .FirstOrDefault());

        var unclaimed = albums.Values
            .Where(file => file is { ReleaseId: null })
            .Select(file => file!.ReleaseGroupId!.Value)
            .Distinct()
            .ToList();

        var displays = (await EditionFactsAsync(db, unclaimed, cancellationToken).ConfigureAwait(false))
            .ToDictionary(entry => entry.Key, entry => DisplayEdition(entry.Value)?.Id);

        var items = rows
            .Select(row =>
            {
                var first = row.Files[0];
                var album = albums[row.Id];

                return new TrackListRow(
                    row.Id.Value,
                    row.Title,
                    row.WorkTitle,
                    Format(row.Duration ?? first.Quality?.Duration),
                    row.Credits,
                    album is null
                        ? null
                        : new TrackListAlbum(
                            album.ReleaseGroupId!.Value.Value,
                            album.AlbumTitle!,
                            album.AlbumYear,
                            (album.ReleaseId ?? displays.GetValueOrDefault(album.ReleaseGroupId!.Value))?.Value,
                            album.AlbumMbid?.Value),
                    FolderOf(first.Path),
                    FileRowOf(first.Path, first.SizeBytes, first.Quality, first.Integrity, first.Measurement),
                    row.Files.Count);
            })
            .ToList();

        return TypedResults.Ok(new TrackListResponse(total, items));
    }

    internal static async Task<Ok<ArtistListResponse>> GetComposers(
        FonotecaDbContext db,
        CancellationToken cancellationToken,
        string? query = null,
        string? sort = null)
    {
        var held = await HeldWorksAsync(db, cancellationToken).ConfigureAwait(false);
        var composers = await ComposersAsync(db, cancellationToken).ConfigureAwait(false);

        var pieces = held
            .Where(recording => recording.ParentId != null)
            .ToDictionary(recording => recording.Id, recording => recording.ParentId!.Value);

        var counts = held
            .SelectMany(recording => composers[recording.Work].Select(composer => (composer.Artist, Recording: recording)))
            .GroupBy(pair => pair.Artist)
            .ToDictionary(
                theirs => theirs.Key,
                theirs => (
                    Compositions: theirs.Select(pair => pair.Recording.ParentId ?? pair.Recording.Work).Distinct().Count(),
                    Songs: Songs.Count(theirs.Select(pair => pair.Recording.Id), pieces)));

        var ids = counts.Keys.ToList();
        var matching = db.Artists.AsNoTracking().Where(artist => ids.Contains(artist.Id));

        if (!string.IsNullOrWhiteSpace(query))
        {
            var pattern = $"%{Escape(query.Trim())}%";

            matching = matching.Where(a =>
                EF.Functions.ILike(a.Name, pattern, "\\")
                || (a.LatinName != null && EF.Functions.ILike(a.LatinName, pattern, "\\")));
        }

        var named = await matching
            .OrderBy(artist => artist.SortName ?? artist.Name)
            .ThenBy(artist => artist.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        // Stable, so a tie keeps the collation's alphabetical order.
        var ordered = sort == "compositions"
            ? [.. named.OrderByDescending(artist => counts[artist.Id].Compositions)]
            : named;

        var items = ordered
            .Select(artist => Describe(
                artist,
                counts[artist.Id].Songs,
                compositionCount: counts[artist.Id].Compositions))
            .ToList();

        return TypedResults.Ok(new ArtistListResponse(items.Count, items.Count, items));
    }

    internal static async Task<Ok<CompositionListResponse>> GetCompositions(
        FonotecaDbContext db,
        CancellationToken cancellationToken,
        string? query = null,
        string? sort = null,
        int skip = 0,
        int take = BrowsePage)
    {
        take = Math.Clamp(take, 1, MaxTake);
        skip = Math.Max(skip, 0);

        // ponytail: every held recording of a work, read whole on each page, since
        // the performance count a page is sorted by needs every recording's
        // performers. Store the piece and its count if this shows at 100k.
        var held = await db.Recordings
            .AsNoTracking()
            .Where(recording => recording.WorkId != null && recording.Files.Any())
            .Select(recording => new
            {
                recording.Id,
                Work = recording.WorkId!.Value,
                recording.Work!.ParentId,
                Billed = recording.Credits
                    .OrderBy(credit => credit.Position)
                    .Select(credit => new
                    {
                        credit.ArtistId,
                        Name = credit.CreditedAs ?? credit.Artist!.LatinName ?? credit.Artist!.Name,
                    })
                    .ToList(),
                Players = recording.Relationships
                    .Select(link => new
                    {
                        link.Type,
                        Name = db.Artists
                            .Where(artist => artist.Id == link.ArtistId)
                            .Select(artist => artist.LatinName ?? artist.Name)
                            .FirstOrDefault(),
                    })
                    .ToList(),
                Album = recording.Files
                    .Where(file => file.ReleaseGroupId != null)
                    .OrderBy(file => file.ReleaseId == null)
                    .ThenBy(file => file.Path)
                    .Select(file => new
                    {
                        Id = file.ReleaseGroupId!.Value,
                        file.ReleaseGroup!.Title,
                        file.ReleaseGroup.FirstReleaseYear,
                    })
                    .FirstOrDefault(),
                Path = recording.Files.Min(file => file.Path)!,
            })
            .AsSplitQuery()
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var composers = await ComposersAsync(db, cancellationToken).ConfigureAwait(false);

        var works = await db.Works
            .AsNoTracking()
            .Select(work => new { work.Id, work.Title, work.Type })
            .ToDictionaryAsync(work => work.Id, cancellationToken)
            .ConfigureAwait(false);

        var pieces = held
            .GroupBy(recording => recording.ParentId ?? recording.Work)
            .Select(piece =>
            {
                var writers = piece
                    .SelectMany(recording => composers[recording.Work])
                    .DistinctBy(composer => composer.Artist)
                    .OrderBy(composer => composer.Name, StringComparer.CurrentCulture)
                    .ToList();

                var wrote = writers.Select(composer => composer.Artist).ToHashSet();

                // Who plays it rather than who wrote it. MusicBrainz bills a
                // Karajan reading to Beethoven, so where an orchestra or a
                // conductor is linked the composers come off the billing; a
                // songwriter singing their own song keeps theirs.
                string? PlayedBy(
                    IReadOnlyList<(ArtistId ArtistId, string Name)> billed,
                    IReadOnlyList<(string Type, string? Name)> players)
                {
                    var played = players.Any(player => player.Type is "ensemble" or "conductor" && player.Name is not null);

                    return Performers(
                        billed.Where(credit => !played || !wrote.Contains(credit.ArtistId)).Select(credit => credit.Name),
                        players);
                }

                // A piece in movements, even where one track links the whole
                // work rather than its movement.
                var inMovements = piece.Any(recording => recording.ParentId != null);

                var recordings = piece
                    .Select(recording => (
                        recording.Album,
                        recording.Path,
                        Movement: inMovements,
                        Performers: PlayedBy(
                            [.. recording.Billed.Select(credit => (credit.ArtistId, credit.Name))],
                            [.. recording.Players.Select(player => (player.Type, player.Name))])))
                    .ToList();

                var performances = Performances
                    .Of(
                        recordings,
                        recording => recording.Album?.Id.Value.ToString() ?? FolderOf(recording.Path),
                        recording => recording.Performers,
                        recording => recording.Movement)
                    .Select(performance =>
                    {
                        var first = performance[0];

                        // A cast album's songs each name their singers; the
                        // names most of its movements share stand for it.
                        var performers = performance
                            .GroupBy(recording => recording.Performers)
                            .MaxBy(named => named.Count())!
                            .Key;

                        return new CompositionPerformance(
                            performers,
                            first.Album?.Id.Value,
                            first.Album?.Title ?? Leaf(FolderOf(first.Path)),
                            first.Album?.FirstReleaseYear,
                            performance.Count,
                            first.Movement);
                    })
                    .OrderBy(performance => performance.Year ?? int.MaxValue)
                    .ThenBy(performance => performance.Album, StringComparer.CurrentCulture)
                    .ToList();

                var work = works[piece.Key];

                return new CompositionRow(
                    piece.Key.Value,
                    work.Title,
                    work.Type,
                    [.. writers.Select(composer => new CompositionComposer(composer.Artist.Value, composer.Name))],
                    performances);
            });

        if (!string.IsNullOrWhiteSpace(query))
        {
            var text = query.Trim();

            pieces = pieces.Where(piece =>
                piece.Title.Contains(text, StringComparison.OrdinalIgnoreCase)
                || piece.Composers.Any(composer => composer.Name.Contains(text, StringComparison.OrdinalIgnoreCase)));
        }

        var titled = pieces
            .OrderBy(piece => piece.Title, StringComparer.CurrentCulture)
            .ThenBy(piece => piece.Id)
            .ToList();

        // Stable, so a tie stays alphabetical by title.
        IEnumerable<CompositionRow> ordered = sort switch
        {
            "recorded" => titled.OrderByDescending(piece => piece.Performances.Count),
            "composer" => titled
                .OrderBy(piece => piece.Composers.Count == 0)
                .ThenBy(piece => piece.Composers.Count == 0 ? null : piece.Composers[0].Name, StringComparer.CurrentCulture),
            _ => titled.AsEnumerable(),
        };

        return TypedResults.Ok(new CompositionListResponse(
            titled.Count,
            [.. ordered.Skip(skip).Take(take)]));
    }

    /// <summary>Every held recording of a work, with the piece it is a movement of.</summary>
    private static async Task<List<HeldWork>> HeldWorksAsync(
        FonotecaDbContext db,
        CancellationToken cancellationToken) =>
        await db.Recordings
            .AsNoTracking()
            .Where(recording => recording.WorkId != null && recording.Files.Any())
            .Select(recording => new HeldWork(recording.Id, recording.WorkId!.Value, recording.Work!.ParentId))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

    /// <summary>Who wrote each work, by work.</summary>
    private static async Task<ILookup<WorkId, (ArtistId Artist, string Name)>> ComposersAsync(
        FonotecaDbContext db,
        CancellationToken cancellationToken) =>
        (await db.Relationships
            .AsNoTracking()
            .Where(link => link.Type == ComposerLink && link.WorkId != null && link.ArtistId != null)
            .Select(link => new
            {
                Work = link.WorkId!.Value,
                Artist = link.ArtistId!.Value,
                Name = db.Artists
                    .Where(artist => artist.Id == link.ArtistId)
                    .Select(artist => artist.LatinName ?? artist.Name)
                    .FirstOrDefault(),
            })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false))
            .Where(link => link.Name is not null)
            .ToLookup(link => link.Work, link => (link.Artist, link.Name!));

    /// <summary>The last segment of a folder, which is all a row has room for.</summary>
    private static string Leaf(string folder) => folder[(folder.LastIndexOf('/') + 1)..];

    private readonly record struct HeldWork(RecordingId Id, WorkId Work, WorkId? ParentId);
}

/// <summary>A page of tracks, with the total the filter matched.</summary>
public sealed record TrackListResponse(int Total, IReadOnlyList<TrackListRow> Items);

/// <param name="WorkTitle">The work it performs, where there is one.</param>
/// <param name="Duration">MusicBrainz's length, else the first file's measured one.</param>
/// <param name="Artists">The recording's billing line, a name per credit with the phrase joining it to the next.</param>
/// <param name="Album">The album a file of it is held to, a claimed pressing's first; null where none is.</param>
/// <param name="Folder">The first file's folder, which stands in for an album where there is none.</param>
/// <param name="File">The first file by path: what plays, and its format.</param>
/// <param name="Files">Every file holding it; more than one is the same recording kept twice.</param>
public sealed record TrackListRow(
    Guid RecordingId,
    string Title,
    string? WorkTitle,
    string? Duration,
    IReadOnlyList<CreditName> Artists,
    TrackListAlbum? Album,
    string Folder,
    FileRow File,
    int Files);

/// <summary>One name on a billing line.</summary>
public sealed record CreditName(Guid ArtistId, string Name, string? JoinPhrase);

/// <param name="CoverReleaseId">Whose stored sleeve to draw, as on the album list; null where no edition is stored.</param>
public sealed record TrackListAlbum(Guid AlbumId, string Title, int? Year, Guid? CoverReleaseId, Guid? Mbid);

/// <summary>A page of compositions, with the total the filter matched.</summary>
public sealed record CompositionListResponse(int Total, IReadOnlyList<CompositionRow> Items);

/// <param name="Id">The piece's work.</param>
/// <param name="Type">MusicBrainz's work type: Song, Symphony, Aria. Null where it states none.</param>
/// <param name="Composers">The <c>composer</c> links on the works its recordings perform, by name.</param>
/// <param name="Performances">Each album and set of performers holding it, oldest first.</param>
public sealed record CompositionRow(
    Guid Id,
    string Title,
    string? Type,
    IReadOnlyList<CompositionComposer> Composers,
    IReadOnlyList<CompositionPerformance> Performances);

public sealed record CompositionComposer(Guid Id, string Name);

/// <param name="Performers">
/// The billing line, then the ensembles and conductors — the composers off the
/// billing where either is linked — as most of its recordings name them.
/// </param>
/// <param name="AlbumId">The album; null where the files are on none, and <paramref name="Album"/> is their folder.</param>
/// <param name="Tracks">Its recordings held on the album: the movements, or the tracks one piece spans.</param>
/// <param name="Movements">Whether those recordings are movements of the piece rather than the piece itself.</param>
public sealed record CompositionPerformance(
    string? Performers,
    Guid? AlbumId,
    string Album,
    int? Year,
    int Tracks,
    bool Movements);
