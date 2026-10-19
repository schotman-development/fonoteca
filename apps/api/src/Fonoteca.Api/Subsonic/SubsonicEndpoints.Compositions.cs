using System.Globalization;
using Fonoteca.Api.Endpoints;
using Fonoteca.Data;
using Fonoteca.Domain.Catalogue;
using Microsoft.EntityFrameworkCore;

namespace Fonoteca.Api.Subsonic;

/// <summary>
/// Composers and their compositions, Fonoteca's <c>fonotecaCompositions</c>.
/// </summary>
/// <remarks>
/// OpenSubsonic has no way to list either, so this is an extension a client
/// may use and need not: unprefixed methods under a prefixed extension name,
/// as <c>getAlbumMotion</c> is.
///
/// <b>A composition is what a song's <c>works</c> names</b>: the top of a
/// work's parts chain, where <see cref="ClassicalWorks"/> asked with the
/// piece's type over its held performances says classical, as
/// <c>SongWorksAsync</c> asks it. Never a performance: its recordings are the
/// songs under it.
///
/// <b>Its composers are the <c>composer</c> contributions</b> on the piece
/// and on each held part, as <c>contributors</c> names them and with the
/// same artist ids. One the catalogue has no artist row for has no id to
/// browse by, and is left out — which a describe cannot produce: it writes a
/// work's contributions in the same save as <see cref="PrimaryCredits"/>
/// mints a row for each of its writers, and no artist row is ever deleted
/// (1,060 of 1,060 composers had one on 2026-10-17).
/// </remarks>
public static partial class SubsonicEndpoints
{
    private static async Task<IResult> GetComposers(
        FonotecaDbContext db,
        CancellationToken cancellationToken)
    {
        var pieces = await CompositionsAsync(db, cancellationToken).ConfigureAwait(false);

        var counts = pieces
            .SelectMany(piece => piece.Composers)
            .CountBy(composer => composer.Id)
            .ToDictionary();

        var indexed = pieces
            .SelectMany(piece => piece.Composers)
            .DistinctBy(composer => composer.Id)
            .OrderBy(composer => composer.SortName ?? composer.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(composer => composer.Name, StringComparer.OrdinalIgnoreCase)
            .GroupBy(composer => Initial(composer.SortName ?? composer.Name))
            .OrderBy(bucket => bucket.Key, StringComparer.Ordinal)
            .ToList();

        return SubsonicResult.Ok("composers", root => root.Children("index", indexed, (index, bucket) =>
        {
            index.Attr("name", bucket.Key);
            index.Children("composer", bucket, (writer, composer) =>
            {
                writer.Attr("id", SubsonicIds.Artist(composer.Id));
                writer.Attr("name", composer.Name);
                writer.Attr("sortName", composer.SortName);
                writer.Attr("musicBrainzId", composer.Mbid.Value.ToString());
                writer.Attr("coverArt", composer.Pictured ? SubsonicIds.Artist(composer.Id) : null);
                writer.Attr("compositionCount", counts[composer.Id]);
            });
        }));
    }

    /// <summary>
    /// Every composition by name, one composer's, or one with its movements and
    /// every song of it on every album.
    /// </summary>
    private static async Task<IResult> GetCompositions(
        HttpContext http,
        FonotecaDbContext db,
        CancellationToken cancellationToken)
    {
        var pieces = await CompositionsAsync(db, cancellationToken).ConfigureAwait(false);

        if (Text(http, "id") is { } id)
        {
            if (SubsonicIds.AsWork(id) is not { } work || pieces.FirstOrDefault(piece => piece.Id == work) is not { } one)
            {
                return SubsonicResult.Error(70, "No such composition.");
            }

            var performed = one.Performed.ToList();
            var songs = await SongsAsync(
                    db,
                    db.MediaFiles.AsNoTracking().Where(file =>
                        file.Recording != null && file.Recording.WorkId != null && performed.Contains(file.Recording.WorkId.Value)),
                    cancellationToken)
                .ConfigureAwait(false);

            return SubsonicResult.Ok("compositions", root => root.Children("composition", [one], (writer, piece) =>
            {
                WriteComposition(writer, piece);
                writer.Children("movement", piece.Movements, static (movement, part) =>
                {
                    movement.Attr("name", part.Name);
                    movement.Attr("number", part.Number);
                });
                writer.Children("song", songs, WriteSong);
            }));
        }

        if (Text(http, "composerId") is { } composerId)
        {
            if (SubsonicIds.AsArtist(composerId) is not { } composer
                || !pieces.Any(piece => piece.Composers.Any(writer => writer.Id == composer)))
            {
                return SubsonicResult.Error(70, "No such composer.");
            }

            pieces = [.. pieces.Where(piece => piece.Composers.Any(writer => writer.Id == composer))];
        }

        return SubsonicResult.Ok("compositions", root => root.Children("composition", pieces, WriteComposition));
    }

    private static void WriteComposition(ISubsonicWriter writer, Composition piece)
    {
        writer.Attr("id", SubsonicIds.Work(piece.Id));
        writer.Attr("name", piece.Piece.Name);
        writer.Attr("musicBrainzId", piece.Piece.Mbid?.Value.ToString());
        writer.Attr("albumCount", piece.AlbumCount);
        writer.Attr("coverArt", piece.Cover is { } album ? SubsonicIds.Album(album) : null);
        writer.Children("composers", piece.Composers, static (composers, composer) =>
        {
            composers.Attr("id", SubsonicIds.Artist(composer.Id));
            composers.Attr("name", composer.Name);
        });

        if (piece.Piece.Composed is { } composed) writer.Child("composed", span => WriteSpan(span, composed));
    }

    /// <summary>Every classical piece the library holds a recording of, by name.</summary>
    /// <remarks>
    /// ponytail: every held recording of a work and every work are read whole
    /// on each call, as the web Compositions list does; store the piece per
    /// recording if this shows at 100k.
    /// </remarks>
    private static async Task<List<Composition>> CompositionsAsync(
        FonotecaDbContext db,
        CancellationToken cancellationToken)
    {
        var parents = await CatalogueEndpoints.WorkParentsAsync(db, cancellationToken).ConfigureAwait(false);

        var held = await db.Recordings
            .AsNoTracking()
            .Where(recording => recording.WorkId != null && recording.Files.Any())
            .Select(recording => new
            {
                Work = recording.WorkId!.Value,
                recording.Genres,
                Files = recording.Files
                    .Select(file => new
                    {
                        file.ReleaseGroupId,
                        Genres = file.ReleaseGroup == null ? null : file.ReleaseGroup.Genres,
                    })
                    .ToList(),
            })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var works = await db.Works
            .AsNoTracking()
            .Select(work => new WorkFacts(work.Id, work.Title, work.Mbid, work.Type, work.Position, work.PartCount, work.ComposedBegin, work.ComposedEnd))
            .ToDictionaryAsync(work => work.Id, cancellationToken)
            .ConfigureAwait(false);

        // SongWorksAsync's piece, so a cycle in the chain ends where it does there.
        var classical = held
            .GroupBy(recording => parents.TryGetValue(recording.Work, out var parent) ? WorkTree.Top(parent, parents) : recording.Work)
            .Where(piece => ClassicalWorks.Is(
                works[piece.Key].Type,
                piece.SelectMany(recording => recording.Files.Select(file => file.Genres).Prepend(recording.Genres))))
            .ToList();

        var wanted = classical
            .SelectMany(piece => piece.Select(recording => recording.Work).Append(piece.Key))
            .Distinct()
            .ToList();

        var credited = (await db.Contributions
                .AsNoTracking()
                .Where(credit => credit.Type == CatalogueEndpoints.ComposerLink
                    && credit.WorkId != null
                    && credit.ArtistMbid != null
                    && wanted.Contains(credit.WorkId.Value))
                .OrderBy(credit => credit.Position)
                .Select(credit => new { Work = credit.WorkId!.Value, Artist = credit.ArtistMbid!.Value })
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false))
            .Where(credit => !CatalogueEndpoints.Nobody.Contains(credit.Artist))
            .ToList();

        var mbids = credited.Select(credit => credit.Artist).Distinct().ToList();

        var artists = (await db.Artists
                .AsNoTracking()
                .Where(artist => artist.Mbid != null && mbids.Contains(artist.Mbid.Value))
                .Select(artist => new
                {
                    artist.Id,
                    Mbid = artist.Mbid!.Value,
                    Name = artist.LatinName ?? artist.Name,
                    artist.SortName,
                    artist.PortraitUrl,
                    artist.EditsJson,
                    Uploaded = db.ArtistImages.Any(image =>
                        image.ArtistId == artist.Id && image.Kind == ArtistImageKind.Portrait.Name),
                })
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false))
            .ToDictionary(
                artist => artist.Mbid,
                artist => new Composer(artist.Id, artist.Name, artist.SortName, artist.Mbid, Pictured(artist.Uploaded, artist.EditsJson, artist.PortraitUrl)));

        var composers = credited
            .Where(credit => artists.ContainsKey(credit.Artist))
            .ToLookup(credit => credit.Work, credit => artists[credit.Artist]);

        return classical
            .Select(piece =>
            {
                var whole = works[piece.Key];
                var performed = piece.Select(recording => recording.Work).Distinct().ToList();

                // Held most of on an album first, which is the fullest
                // performance's sleeve.
                var albums = piece
                    .SelectMany(recording => recording.Files)
                    .Where(file => file.ReleaseGroupId is not null)
                    .GroupBy(file => file.ReleaseGroupId!.Value)
                    .OrderByDescending(album => album.Count())
                    .ThenBy(album => album.Key.Value)
                    .Select(album => album.Key)
                    .ToList();

                // In the piece's order: each part by its place in its parent,
                // from the top down, so an opera's numbers stay in their acts.
                string Place(WorkId part)
                {
                    var places = new List<string>();
                    var seen = new HashSet<WorkId>();

                    while (part != piece.Key && seen.Add(part) && parents.TryGetValue(part, out var parent))
                    {
                        places.Add((works[part].Position ?? int.MaxValue).ToString("D10", CultureInfo.InvariantCulture));
                        part = parent;
                    }

                    places.Reverse();
                    return string.Join('.', places);
                }

                var movements = performed
                    .Where(part => part != piece.Key)
                    .OrderBy(Place, StringComparer.Ordinal)
                    .ThenBy(part => works[part].Title, StringComparer.Ordinal)
                    .Select(part => MovementOf(works[part], works[parents[part]], whole))
                    .ToList();

                return new Composition(
                    piece.Key,
                    PieceOf(whole),
                    [.. performed.Prepend(piece.Key).SelectMany(work => composers[work]).Distinct()],
                    albums.Count,
                    albums.Count > 0 ? albums[0] : null,
                    performed,
                    movements);
            })
            .OrderBy(piece => piece.Piece.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(piece => piece.Id.Value)
            .ToList();
    }

    /// <param name="Performed">The piece and each part of it a held recording performs.</param>
    /// <param name="Cover">The album holding the most of its recordings.</param>
    private sealed record Composition(
        WorkId Id,
        SongPiece Piece,
        IReadOnlyList<Composer> Composers,
        int AlbumCount,
        ReleaseGroupId? Cover,
        IReadOnlyList<WorkId> Performed,
        IReadOnlyList<SongMovement> Movements);

    private sealed record Composer(ArtistId Id, string Name, string? SortName, Mbid Mbid, bool Pictured);
}
