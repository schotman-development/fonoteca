using Fonoteca.Data;
using Fonoteca.Domain.Catalogue;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace Fonoteca.Api.Endpoints;

/*
 * The library by track, by composer and by composition, beside the artist and
 * album lists.
 *
 * A composition is the piece: the top of a movement's parent works
 * (`Works.ParentId`, climbed by `WorkTree`), or the work itself where it has
 * none. Its composers are the `composer` links on
 * the works its held recordings perform — the parent is minted from a stub and
 * carries no writers of its own.
 *
 * The Composers page is narrower: whoever is credited as composer, not
 * lyricist, of a classical work (`ClassicalWorks`). The Compositions page
 * still lists every piece and every writer of it.
 */
public static partial class CatalogueEndpoints
{
    /// <summary>Rows per page of tracks and of compositions.</summary>
    private const int BrowsePage = 100;

    internal const string ComposerLink = "composer";

    /// <summary>
    /// MusicBrainz's <c>[traditional]</c>, <c>[anonymous]</c> and <c>[unknown]</c>,
    /// credited where nobody is known to have written the piece.
    /// </summary>
    internal static readonly HashSet<Mbid> Nobody =
    [
        new(Guid.Parse("9be7f096-97ec-4615-8957-8d40b5dcbc41")),
        new(Guid.Parse("f731ccc4-e22a-43af-a747-64213329e088")),
        new(Guid.Parse("125ec42a-7229-4250-afc5-e057484327fe")),
    ];

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
            .WithSummary("Everyone who composed a classical piece the library holds a recording of.")
            .WithDescription(
                "Ordered by sort name, or by `sort=compositions` for the most pieces held first. "
                + "`compositionCount` counts pieces, a work's movements once; `songCount` is the "
                + "artist list's. `query` filters on the name as the artist list does. A classical "
                + "piece is a work MusicBrainz does not type as a song, with a recording or album of "
                + "it tagged classical and not `classical crossover` or `pop`; its lyricist is not "
                + "its composer, and `[traditional]`, "
                + "`[anonymous]` and `[unknown]` are nobody. Both counts are of the composer's "
                + "classical pieces alone.");

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

        var items = await TrackRowsAsync(
                db,
                sorted.ThenBy(recording => recording.Id).Skip(skip).Take(take),
                cancellationToken)
            .ConfigureAwait(false);

        return TypedResults.Ok(new TrackListResponse(total, items));
    }

    /// <summary>
    /// The Tracks page's row for each recording, in the order given.
    /// </summary>
    /// <remarks>
    /// Every recording must hold a file: a row's <c>File</c> is its first. The
    /// playlist page shows its songs through this too, with its own copy laid
    /// over that one.
    /// </remarks>
    internal static async Task<List<TrackListRow>> TrackRowsAsync(
        FonotecaDbContext db,
        IQueryable<Recording> recordings,
        CancellationToken cancellationToken)
    {
        var rows = await recordings
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

        return rows
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
    }

    internal static async Task<Ok<ArtistListResponse>> GetComposers(
        FonotecaDbContext db,
        CancellationToken cancellationToken,
        string? query = null,
        string? sort = null)
    {
        var held = await HeldWorksAsync(db, cancellationToken).ConfigureAwait(false);
        var composers = await ClassicalComposersAsync(db, cancellationToken).ConfigureAwait(false);

        var pieces = held
            .Where(recording => recording.Piece != null)
            .ToDictionary(recording => recording.Id, recording => recording.Piece!.Value);

        var counts = held
            .SelectMany(recording => composers[recording.Work].Select(artist => (Artist: artist, Recording: recording)))
            .GroupBy(pair => pair.Artist)
            .ToDictionary(
                theirs => theirs.Key,
                theirs => (
                    Compositions: theirs.Select(pair => pair.Recording.Piece ?? pair.Recording.Work).Distinct().Count(),
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

        var parents = await WorkParentsAsync(db, cancellationToken).ConfigureAwait(false);

        var pieces = held
            .GroupBy(recording => WorkTree.Top(recording.Work, parents))
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
                var inMovements = piece.Any(recording => parents.ContainsKey(recording.Work));

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

    /// <summary>Every held recording of a work, with the whole piece it is a movement of.</summary>
    private static async Task<List<HeldWork>> HeldWorksAsync(
        FonotecaDbContext db,
        CancellationToken cancellationToken)
    {
        var parents = await WorkParentsAsync(db, cancellationToken).ConfigureAwait(false);

        return [.. (await db.Recordings
                .AsNoTracking()
                .Where(recording => recording.WorkId != null && recording.Files.Any())
                .Select(recording => new { recording.Id, Work = recording.WorkId!.Value })
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false))
            .Select(row => new HeldWork(
                row.Id,
                row.Work,
                parents.ContainsKey(row.Work) ? WorkTree.Top(row.Work, parents) : null))];
    }

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

    /// <summary>Who is credited as composer of each held classical work (<see cref="ClassicalWorks"/>).</summary>
    /// <remarks>
    /// Read off <c>Contributions</c>, which keep MusicBrainz's own relation
    /// name, because the <c>composer</c> link folds the lyricist and the
    /// librettist in: Rilke and Apollinaire are writers of a Shostakovich
    /// symphony. A work not yet described again has no contributions, so its
    /// composer waits for enrichment. A contribution mints no artist row, so
    /// only a composer the catalogue has a row for is listed. <c>[traditional]</c>
    /// and its kin are nobody, and are not listed.
    /// </remarks>
    private static async Task<ILookup<WorkId, ArtistId>> ClassicalComposersAsync(
        FonotecaDbContext db,
        CancellationToken cancellationToken)
    {
        var performances = await db.Recordings
            .AsNoTracking()
            .Where(recording => recording.WorkId != null && recording.Files.Any())
            .Select(recording => new
            {
                Work = recording.WorkId!.Value,
                recording.Work!.Type,
                recording.Genres,
                Albums = recording.Files
                    .Select(file => file.ReleaseGroup == null ? null : file.ReleaseGroup.Genres)
                    .ToList(),
                On = recording.Files.Select(file => file.ReleaseGroupId).ToList(),
            })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var borrowed = await BorrowedVotesAsync(db, [.. performances.SelectMany(performance => performance.On)], cancellationToken)
            .ConfigureAwait(false);
        var parents = await WorkParentsAsync(db, cancellationToken).ConfigureAwait(false);
        var forms = await db.Works
            .AsNoTracking()
            .Where(work => work.Type != null)
            .Select(work => new { work.Id, work.Type })
            .ToDictionaryAsync(work => work.Id, work => work.Type, cancellationToken)
            .ConfigureAwait(false);

        var works = performances
            .GroupBy(performance => performance.Work)
            .Where(work => ClassicalWorks.Is(
                    work.First().Type,
                    work.SelectMany(performance => performance.Albums.Prepend(performance.Genres)))
                || ClassicalWorks.ByItsAlbums(
                    forms.GetValueOrDefault(WorkTree.Top(work.Key, parents)),
                    borrowed.Of(work.SelectMany(performance => performance.On))))
            .Select(work => work.Key)
            .ToHashSet();

        var credited = (await db.Contributions
            .AsNoTracking()
            .Where(credit => credit.Type == ComposerLink && credit.WorkId != null && credit.ArtistMbid != null)
            .Select(credit => new { Work = credit.WorkId!.Value, Artist = credit.ArtistMbid!.Value })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false))
            .Where(credit => works.Contains(credit.Work) && !Nobody.Contains(credit.Artist))
            .ToList();

        var mbids = credited.Select(credit => credit.Artist).Distinct().ToList();

        var rows = (await db.Artists
            .AsNoTracking()
            .Where(artist => artist.Mbid != null && mbids.Contains(artist.Mbid.Value))
            .Select(artist => new { artist.Id, Mbid = artist.Mbid!.Value })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false))
            .ToLookup(artist => artist.Mbid, artist => artist.Id);

        return credited
            .SelectMany(credit => rows[credit.Artist].Select(artist => (credit.Work, Artist: artist)))
            .Distinct()
            .ToLookup(pair => pair.Work, pair => pair.Artist);
    }

    /// <summary>The last segment of a folder, which is all a row has room for.</summary>
    private static string Leaf(string folder) => folder[(folder.LastIndexOf('/') + 1)..];

    private readonly record struct HeldWork(RecordingId Id, WorkId Work, WorkId? Piece);
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
