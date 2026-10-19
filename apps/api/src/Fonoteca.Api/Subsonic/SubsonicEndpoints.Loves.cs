using Fonoteca.Data;
using Fonoteca.Domain.Abstractions;
using Fonoteca.Domain.Catalogue;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Fonoteca.Api.Subsonic;

/// <summary>
/// Hearts: <c>star</c>, <c>unstar</c> and <c>getStarred2</c>, kept in <see cref="Love"/>.
/// </summary>
/// <remarks>
/// <b>Every id is read by its prefix</b>, whichever of <c>id</c>,
/// <c>albumId</c> and <c>artistId</c> carries it, and each may repeat: a song,
/// an album, an artist, or a composition, which is a whole piece: a work no
/// other is part of. One that names none of those refuses the whole
/// request with error 70, so nothing is half done. Starring twice keeps the
/// first time; unstarring what is not loved is no error.
/// </remarks>
public static partial class SubsonicEndpoints
{
    private static readonly string[] LoveParameters = ["id", "albumId", "artistId"];

    private static Task<IResult> Star(HttpContext http, FonotecaDbContext db, IClock clock, CancellationToken cancellationToken) =>
        LoveAsync(http, db, clock, loved: true, cancellationToken);

    private static Task<IResult> Unstar(HttpContext http, FonotecaDbContext db, IClock clock, CancellationToken cancellationToken) =>
        LoveAsync(http, db, clock, loved: false, cancellationToken);

    private static async Task<IResult> LoveAsync(
        HttpContext http,
        FonotecaDbContext db,
        IClock clock,
        bool loved,
        CancellationToken cancellationToken)
    {
        var ids = LoveParameters
            .SelectMany(name => http.Request.Query[name])
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id!.Trim())
            .ToList();

        if (ids.Count == 0) return SubsonicResult.Error(10, "Required parameter is missing: id.");

        var targets = new List<Love>();

        foreach (var id in ids)
        {
            var path = SubsonicIds.AsPath(id);

            if (SubsonicIds.AsSong(id) is not null || path is not null)
            {
                var files = db.MediaFiles.AsNoTracking();
                files = SubsonicIds.AsSong(id) is { } song ? files.Where(row => row.Id == song) : files.Where(row => row.Path == path);

                var file = await files
                    .Select(row => new { row.Id, row.RecordingId })
                    .FirstOrDefaultAsync(cancellationToken)
                    .ConfigureAwait(false);

                if (file is null) return NoSuchLove();

                // Loved on its recording, or on itself until it has one.
                targets.Add(file.RecordingId is { } recording ? Target(recording: recording) : Target(file: file.Id));
            }
            else if (SubsonicIds.AsAlbum(id) is { } album
                && await db.ReleaseGroups.AnyAsync(row => row.Id == album && row.Files.Any(), cancellationToken).ConfigureAwait(false))
            {
                targets.Add(Target(album: album));
            }
            else if (SubsonicIds.AsArtist(id) is { } artist
                && await db.Artists.AnyAsync(row => row.Id == artist, cancellationToken).ConfigureAwait(false))
            {
                targets.Add(Target(artist: artist));
            }
            else if (SubsonicIds.AsWork(id) is { } work
                && await db.Works.AnyAsync(row => row.Id == work && row.ParentId == null, cancellationToken).ConfigureAwait(false))
            {
                targets.Add(Target(work: work));
            }
            else
            {
                return NoSuchLove();
            }
        }

        var now = StoreTime.ToStorePrecision(clock.UtcNow);
        var distinct = targets.DistinctBy(target => (target.RecordingId, target.MediaFileId, target.ReleaseGroupId, target.ArtistId, target.WorkId)).ToList();

        // A double tap sends the same request twice at once: deleted where it
        // stands, an unstar finds nothing left rather than failing, and a star
        // the other inserted between the check and the save is asked again
        // once, which finds it and keeps its time.
        if (!loved)
        {
            foreach (var target in distinct)
            {
                await Matching(db, target).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);

                // A song's heart is also any copy's, loved as itself before it was identified.
                if (target.RecordingId is { } song)
                {
                    await db.Loves
                        .Where(love => db.MediaFiles.Any(file => file.Id == love.MediaFileId && file.RecordingId == song))
                        .ExecuteDeleteAsync(cancellationToken)
                        .ConfigureAwait(false);
                }
            }

            return SubsonicResult.Ok();
        }

        for (var attempt = 0; ; attempt++)
        {
            foreach (var target in distinct)
            {
                if (await Matching(db, target).AnyAsync(cancellationToken).ConfigureAwait(false)) continue;

                db.Loves.Add(new Love
                {
                    Id = Guid.CreateVersion7(),
                    RecordingId = target.RecordingId,
                    MediaFileId = target.MediaFileId,
                    ReleaseGroupId = target.ReleaseGroupId,
                    ArtistId = target.ArtistId,
                    WorkId = target.WorkId,
                    LovedUtc = now,
                });
            }

            try
            {
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

                return SubsonicResult.Ok();
            }
            catch (DbUpdateException cause) when (attempt == 0 && cause.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
            {
                db.ChangeTracker.Clear();
            }
        }

        static Love Target(
            RecordingId? recording = null,
            MediaFileId? file = null,
            ReleaseGroupId? album = null,
            ArtistId? artist = null,
            WorkId? work = null) =>
            new()
            {
                Id = Guid.Empty,
                RecordingId = recording,
                MediaFileId = file,
                ReleaseGroupId = album,
                ArtistId = artist,
                WorkId = work,
                LovedUtc = default,
            };
    }

    private static SubsonicResult NoSuchLove() => SubsonicResult.Error(70, "No such song, album, artist or composition.");

    private static IQueryable<Love> Matching(FonotecaDbContext db, Love target) => target switch
    {
        { RecordingId: { } recording } => db.Loves.Where(love => love.RecordingId == recording),
        { MediaFileId: { } file } => db.Loves.Where(love => love.MediaFileId == file),
        { ReleaseGroupId: { } album } => db.Loves.Where(love => love.ReleaseGroupId == album),
        { ArtistId: { } artist } => db.Loves.Where(love => love.ArtistId == artist),
        _ => db.Loves.Where(love => love.WorkId == target.WorkId),
    };

    /// <summary>Everything loved: its artists and albums, and each loved song once, as its best copy.</summary>
    /// <remarks>
    /// A loved recording held on an album and a best-of is one song here, the
    /// copy a playlist would play (<see cref="PlaylistFile.Best"/>). A loved
    /// artist no album is billed to — a composer from <c>getComposers</c> — is
    /// listed with no albums; compositions are not, the spec having no place
    /// for them.
    /// </remarks>
    private static async Task<IResult> GetStarred2(
        FonotecaDbContext db,
        CancellationToken cancellationToken)
    {
        var albums = await AlbumsAsync(db, artist: null, cancellationToken).ConfigureAwait(false);

        var lovedArtists = await db.Loves
            .Where(love => love.ArtistId != null)
            .Select(love => love.ArtistId!.Value)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var artists = (await ArtistsOfAsync(db, albums, cancellationToken, lovedArtists).ConfigureAwait(false))
            .Where(artist => artist.Starred is not null)
            .ToList();

        // A file loved as itself is its song's heart once it is identified.
        var lovedFiles = await db.MediaFiles
            .Where(file => db.Loves.Any(love => love.MediaFileId == file.Id))
            .Select(file => new { file.Id, file.RecordingId })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var recordings = (await db.Loves
                .Where(love => love.RecordingId != null)
                .Select(love => love.RecordingId!.Value)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false))
            .Concat(lovedFiles.Where(file => file.RecordingId is not null).Select(file => file.RecordingId!.Value))
            .Distinct()
            .ToList();

        var files = lovedFiles.Where(file => file.RecordingId is null).Select(file => file.Id).ToList();

        var copies = await db.MediaFiles
            .AsNoTracking()
            .Where(file => file.RecordingId != null && recordings.Contains(file.RecordingId.Value))
            .Select(file => new
            {
                Recording = file.RecordingId!.Value,
                Copy = new PlaylistCopy(file.Id, file.Path, file.SizeBytes, file.Quality, file.Measurement, file.Integrity),
            })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var best = copies
            .GroupBy(copy => copy.Recording)
            .Select(song => PlaylistFile.Best(song.Select(copy => copy.Copy))!.Id)
            .Concat(files)
            .Distinct()
            .ToList();

        var songs = await SongsAsync(db, db.MediaFiles.AsNoTracking().Where(file => best.Contains(file.Id)), cancellationToken)
            .ConfigureAwait(false);

        return SubsonicResult.Ok("starred2", root =>
        {
            root.Children("artist", artists, WriteArtist);
            root.Children("album", albums.Where(album => album.Starred is not null).OrderByDescending(album => album.Starred), WriteAlbum);
            root.Children("song", songs, WriteSong);
        });
    }
}
