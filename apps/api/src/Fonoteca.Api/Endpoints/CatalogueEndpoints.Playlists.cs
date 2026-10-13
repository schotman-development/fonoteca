using System.Globalization;
using Fonoteca.Api.Configuration;
using Fonoteca.Api.Library;
using Fonoteca.Api.Logging;
using Fonoteca.Data;
using Fonoteca.Domain.Abstractions;
using Fonoteca.Domain.Catalogue;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Fonoteca.Api.Endpoints;

/*
 * Playlists: a person's lists of songs, each written out as an m3u under the
 * library's Playlists folder. `PlaylistEntry` says why an entry is a song and
 * not a file; `PlaylistFiles` keeps the files current. Subsonic's playlist
 * methods call these handlers.
 */
public static partial class CatalogueEndpoints
{
    private static void MapPlaylistEndpoints(IEndpointRouteBuilder group)
    {
        group.MapGet("/playlists", GetPlaylists)
            .WithName("GetPlaylists")
            .WithSummary("Every playlist, by name, with its first albums for a cover.");

        group.MapPost("/playlists", CreatePlaylist)
            .WithName("CreatePlaylist")
            .WithSummary("A new playlist, optionally with songs, written to the library as an m3u.")
            .WithDescription(
                "`recordingIds` are songs the library holds, in order; the same song may appear "
                + "twice. The file is `Playlists/<name>.m3u8` with paths relative to it, naming the "
                + "best copy of each song, and Fonoteca rewrites it after every tag write and every "
                + "edit to the playlist — an edit made to the file elsewhere is overwritten.")
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status409Conflict);

        group.MapGet("/playlists/{id:guid}", GetPlaylist)
            .WithName("GetPlaylist")
            .WithSummary("One playlist's songs, in order, each with the copy that plays it.")
            .WithDescription(
                "An entry with no `track` is a song the library holds no copy of any more — an "
                + "album being replaced, or every copy trashed. It stays on the playlist and out of "
                + "the file until a copy returns.")
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPost("/playlists/{id:guid}", UpdatePlaylist)
            .WithName("UpdatePlaylist")
            .WithSummary("Rename a playlist, or set the order of its entries.")
            .WithDescription(
                "`entries` is the new order as entry ids; an entry left out is removed. Either "
                + "field may be omitted. A rename renames the file.")
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        group.MapPost("/playlists/{id:guid}/songs", AddPlaylistSongs)
            .WithName("AddPlaylistSongs")
            .WithSummary("Add songs to the end of a playlist.")
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapDelete("/playlists/{id:guid}", DeletePlaylist)
            .WithName("DeletePlaylist")
            .WithSummary("Delete a playlist; its file moves to the trash.")
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);
    }

    internal static async Task<Ok<PlaylistListResponse>> GetPlaylists(
        FonotecaDbContext db,
        CancellationToken cancellationToken)
    {
        var playlists = await db.Playlists
            .AsNoTracking()
            .Include(playlist => playlist.Entries)
            .OrderBy(playlist => playlist.Name)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var songs = await PlaylistFiles
            .ResolveAsync(db, [.. playlists.SelectMany(playlist => playlist.Entries)], cancellationToken)
            .ConfigureAwait(false);

        // Enough songs from the top of each to find four albums for its cover.
        var tops = playlists.ToDictionary(
            playlist => playlist.Id,
            playlist => playlist.Entries
                .OrderBy(entry => entry.Position)
                .Select(entry => songs[entry.Id])
                .Where(song => song.Linked)
                .Select(song => song.RecordingId!.Value)
                .Distinct()
                .Take(16)
                .ToList());

        List<RecordingId> wanted = [.. tops.Values.SelectMany(ids => ids).Distinct()];

        var rows = (await TrackRowsAsync(
                    db,
                    db.Recordings.AsNoTracking().Where(recording => wanted.Contains(recording.Id)),
                    cancellationToken)
                .ConfigureAwait(false))
            .ToDictionary(row => row.RecordingId);

        var items = playlists
            .Select(playlist =>
            {
                var resolved = playlist.Entries.Select(entry => songs[entry.Id]).ToList();

                return new PlaylistSummary(
                    playlist.Id.Value,
                    playlist.Name,
                    playlist.Path,
                    resolved.Count,
                    resolved.Count(song => song.Copy is not null),
                    Format(Total(resolved)),
                    playlist.ChangedUtc,
                    [.. tops[playlist.Id]
                        .Select(id => rows.GetValueOrDefault(id.Value)?.Album)
                        .OfType<TrackListAlbum>()
                        .DistinctBy(album => album.AlbumId)
                        .Take(4)]);
            })
            .ToList();

        return TypedResults.Ok(new PlaylistListResponse(items));
    }

    internal static async Task<Results<Ok<PlaylistResponse>, ProblemHttpResult>> GetPlaylist(
        Guid id,
        FonotecaDbContext db,
        CancellationToken cancellationToken)
    {
        var playlistId = new PlaylistId(id);

        var playlist = await db.Playlists
            .AsNoTracking()
            .Include(row => row.Entries)
            .FirstOrDefaultAsync(row => row.Id == playlistId, cancellationToken)
            .ConfigureAwait(false);

        return playlist is null
            ? NoSuchPlaylist(id)
            : TypedResults.Ok(await DescribeAsync(db, playlist, cancellationToken).ConfigureAwait(false));
    }

    internal static async Task<Results<Ok<PlaylistResponse>, ProblemHttpResult>> CreatePlaylist(
        CreatePlaylistRequest request,
        FonotecaDbContext db,
        PlaylistFiles files,
        IClock clock,
        CancellationToken cancellationToken)
    {
        if (NameProblem(request?.Name) is { } unnamed) return unnamed;

        var name = request!.Name!.Trim();
        var path = PlaylistFile.PathFor(name);

        if (await TakenAsync(db, files, path, null, cancellationToken).ConfigureAwait(false) is { } taken)
        {
            return taken;
        }

        var now = clock.UtcNow;

        var playlist = new Playlist
        {
            Id = PlaylistId.New(),
            Name = name,
            Path = path,
            CreatedUtc = now,
            ChangedUtc = now,
        };

        if (await AppendAsync(db, playlist, request.RecordingIds ?? [], cancellationToken).ConfigureAwait(false) is { } refused)
        {
            return refused;
        }

        db.Playlists.Add(playlist);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await files.SyncAsync(cancellationToken).ConfigureAwait(false);

        return TypedResults.Ok(await DescribeAsync(db, playlist, cancellationToken).ConfigureAwait(false));
    }

    internal static async Task<Results<Ok<PlaylistResponse>, ProblemHttpResult>> UpdatePlaylist(
        Guid id,
        UpdatePlaylistRequest request,
        FonotecaDbContext db,
        PlaylistFiles files,
        IClock clock,
        CancellationToken cancellationToken)
    {
        var playlistId = new PlaylistId(id);

        var playlist = await db.Playlists
            .Include(row => row.Entries)
            .FirstOrDefaultAsync(row => row.Id == playlistId, cancellationToken)
            .ConfigureAwait(false);

        if (playlist is null) return NoSuchPlaylist(id);

        if (request?.Entries is { } order)
        {
            var current = playlist.Entries.ToDictionary(entry => entry.Id);

            if (order.Distinct().Count() != order.Count || order.Any(entry => !current.ContainsKey(entry)))
            {
                return TypedResults.Problem(
                    title: "Not this playlist's entries",
                    detail: "`entries` lists each of the playlist's own entry ids at most once.",
                    statusCode: StatusCodes.Status400BadRequest);
            }

            var kept = order.ToHashSet();
            db.PlaylistEntries.RemoveRange(playlist.Entries.Where(entry => !kept.Contains(entry.Id)));
            playlist.Entries.RemoveAll(entry => !kept.Contains(entry.Id));

            for (var position = 0; position < order.Count; position++) current[order[position]].Position = position;
        }

        if (request?.Name is not null)
        {
            if (NameProblem(request.Name) is { } unnamed) return unnamed;

            var name = request.Name.Trim();
            var path = PlaylistFile.PathFor(name);

            if (!string.Equals(path, playlist.Path, StringComparison.Ordinal))
            {
                if (await TakenAsync(db, files, path, playlist, cancellationToken).ConfigureAwait(false) is { } taken)
                {
                    return taken;
                }

                if (Move(files, playlist.Path, path) is { } stuck) return stuck;

                playlist.Path = path;
            }

            playlist.Name = name;
        }

        playlist.ChangedUtc = clock.UtcNow;

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await files.SyncAsync(cancellationToken).ConfigureAwait(false);

        return TypedResults.Ok(await DescribeAsync(db, playlist, cancellationToken).ConfigureAwait(false));
    }

    internal static async Task<Results<Ok<PlaylistResponse>, ProblemHttpResult>> AddPlaylistSongs(
        Guid id,
        AddPlaylistSongsRequest request,
        FonotecaDbContext db,
        PlaylistFiles files,
        IClock clock,
        CancellationToken cancellationToken)
    {
        var playlistId = new PlaylistId(id);

        var playlist = await db.Playlists
            .Include(row => row.Entries)
            .FirstOrDefaultAsync(row => row.Id == playlistId, cancellationToken)
            .ConfigureAwait(false);

        if (playlist is null) return NoSuchPlaylist(id);

        if (await AppendAsync(db, playlist, request?.RecordingIds ?? [], cancellationToken).ConfigureAwait(false) is { } refused)
        {
            return refused;
        }

        playlist.ChangedUtc = clock.UtcNow;

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await files.SyncAsync(cancellationToken).ConfigureAwait(false);

        return TypedResults.Ok(await DescribeAsync(db, playlist, cancellationToken).ConfigureAwait(false));
    }

    /// <remarks>
    /// The file goes to the trash before the row goes, as the file manager's
    /// trash does: nothing here is deleted, and a file that would not move
    /// keeps its playlist, or the next player scan would import a playlist
    /// Fonoteca no longer knows about.
    /// </remarks>
    internal static async Task<Results<NoContent, ProblemHttpResult>> DeletePlaylist(
        Guid id,
        FonotecaDbContext db,
        PlaylistFiles files,
        IOptions<FonotecaOptions> options,
        IClock clock,
        ILogger<PlaylistFiles> logger,
        CancellationToken cancellationToken)
    {
        var playlistId = new PlaylistId(id);

        var playlist = await db.Playlists
            .FirstOrDefaultAsync(row => row.Id == playlistId, cancellationToken)
            .ConfigureAwait(false);

        if (playlist is null) return NoSuchPlaylist(id);

        try
        {
            var absolute = files.AbsolutePathFor(playlist.Path);

            if (File.Exists(absolute))
            {
                var destination = Path.Combine(
                    FileManagerService.TrashRoot(options.Value),
                    clock.UtcNow.ToString("yyyy-MM-dd HHmmss.fff", CultureInfo.InvariantCulture),
                    playlist.Path.Replace('/', Path.DirectorySeparatorChar));

                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Move(absolute, destination);

                Log.PlaylistTrashed(logger, playlist.Path, destination);
            }
        }
        catch (Exception cause) when (cause is IOException or UnauthorizedAccessException)
        {
            return TypedResults.Problem(
                title: "The playlist's file would not move",
                detail: $"'{playlist.Path}' could not be moved to the trash, so the playlist was kept: {cause.Message}",
                statusCode: StatusCodes.Status409Conflict);
        }

        db.Playlists.Remove(playlist);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return TypedResults.NoContent();
    }

    /// <summary>A playlist as its page shows it: each entry with the copy the m3u names.</summary>
    private static async Task<PlaylistResponse> DescribeAsync(
        FonotecaDbContext db,
        Playlist playlist,
        CancellationToken cancellationToken)
    {
        var entries = playlist.Entries.OrderBy(entry => entry.Position).ToList();

        var songs = await PlaylistFiles.ResolveAsync(db, entries, cancellationToken).ConfigureAwait(false);

        List<RecordingId> held = [.. entries
            .Select(entry => songs[entry.Id])
            .Where(song => song.Linked)
            .Select(song => song.RecordingId!.Value)
            .Distinct()];

        var rows = (await TrackRowsAsync(
                    db,
                    db.Recordings.AsNoTracking().Where(recording => held.Contains(recording.Id)),
                    cancellationToken)
                .ConfigureAwait(false))
            .ToDictionary(row => row.RecordingId);

        var items = entries
            .Select(entry =>
            {
                var song = songs[entry.Id];

                // The Tracks page's row, playing the copy the m3u names
                // rather than the first by path. A last copy linked to nothing
                // has no such row, and goes out as itself: no album, no credits.
                TrackListRow? track = null;

                if (song.Copy is { } copy)
                {
                    var file = FileRowOf(copy.Path, copy.SizeBytes, copy.Quality, copy.Integrity, copy.Measurement);

                    track = song.Linked && rows.TryGetValue(song.RecordingId!.Value.Value, out var row)
                        ? row with { Folder = FolderOf(copy.Path), File = file }
                        : new TrackListRow(
                            song.RecordingId?.Value ?? entry.Id,
                            song.Title,
                            null,
                            Format(song.Duration),
                            [],
                            null,
                            FolderOf(copy.Path),
                            file,
                            1);
                }

                return new PlaylistEntryRow(entry.Id, song.Title, song.Artist, song.Copy?.Id.Value, track);
            })
            .ToList();

        return new PlaylistResponse(
            playlist.Id.Value,
            playlist.Name,
            playlist.Path,
            Format(Total([.. songs.Values])),
            playlist.CreatedUtc,
            playlist.ChangedUtc,
            items);
    }

    /// <summary>Songs onto the end, in the order asked; each must be a recording the library holds a file of.</summary>
    private static async Task<ProblemHttpResult?> AppendAsync(
        FonotecaDbContext db,
        Playlist playlist,
        IReadOnlyList<Guid> recordingIds,
        CancellationToken cancellationToken)
    {
        if (recordingIds.Count == 0) return null;

        List<RecordingId> asked = [.. recordingIds.Distinct().Select(id => new RecordingId(id))];

        var found = await db.Recordings
            .AsNoTracking()
            .Where(recording => asked.Contains(recording.Id) && recording.Files.Any())
            .Select(recording => new
            {
                recording.Id,
                recording.Title,
                Credits = recording.Credits
                    .OrderBy(credit => credit.Position)
                    .Select(credit => new
                    {
                        Name = credit.CreditedAs ?? credit.Artist!.LatinName ?? credit.Artist!.Name,
                        credit.JoinPhrase,
                    })
                    .ToList(),
            })
            .ToDictionaryAsync(recording => recording.Id, cancellationToken)
            .ConfigureAwait(false);

        var missing = asked.Where(id => !found.ContainsKey(id)).ToList();

        if (missing.Count > 0)
        {
            return TypedResults.Problem(
                title: "Not a song the library holds",
                detail: $"{missing.Count} of the recordings asked for are not in the catalogue with a file: "
                    + string.Join(", ", missing.Take(5)) + ".",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var next = playlist.Entries.Count == 0 ? 0 : playlist.Entries.Max(entry => entry.Position) + 1;

        foreach (var id in recordingIds)
        {
            var recording = found[new RecordingId(id)];
            var artist = CreditLine(recording.Credits.Select(credit => (credit.Name, credit.JoinPhrase)));

            var entry = new PlaylistEntry
            {
                Id = Guid.CreateVersion7(),
                PlaylistId = playlist.Id,
                Position = next++,
                RecordingId = recording.Id,
                Title = recording.Title,
                Artist = artist is null || artist.Length <= PlaylistEntry.ArtistLength ? artist : artist[..PlaylistEntry.ArtistLength],
            };

            playlist.Entries.Add(entry);

            // Added to the context as well as to the list, so an append to a
            // tracked playlist is an insert rather than an update of a row
            // that does not exist.
            if (db.Entry(playlist).State != EntityState.Detached) db.PlaylistEntries.Add(entry);
        }

        return null;
    }

    /// <summary>
    /// A path another playlist has, or a file already there that is not this
    /// playlist's — a person's own m3u, say, which is never written over.
    /// </summary>
    /// <remarks>
    /// Compared without case, because the production share is read by a Mac,
    /// where two names differing only in case are one file.
    /// </remarks>
    private static async Task<ProblemHttpResult?> TakenAsync(
        FonotecaDbContext db,
        PlaylistFiles files,
        string path,
        Playlist? self,
        CancellationToken cancellationToken)
    {
        var paths = await db.Playlists
            .AsNoTracking()
            .Select(playlist => new { playlist.Id, playlist.Path })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        if (paths.Any(other => other.Id != self?.Id && string.Equals(other.Path, path, StringComparison.OrdinalIgnoreCase)))
        {
            return TypedResults.Problem(
                title: "A playlist has that name",
                detail: $"Another playlist is already saved as '{path}'.",
                statusCode: StatusCodes.Status409Conflict);
        }

        var ours = self is not null && string.Equals(self.Path, path, StringComparison.OrdinalIgnoreCase);

        if (!ours && File.Exists(files.AbsolutePathFor(path)))
        {
            return TypedResults.Problem(
                title: "A file has that name",
                detail: $"'{path}' is already in the library and is not a Fonoteca playlist, so it is left alone.",
                statusCode: StatusCodes.Status409Conflict);
        }

        return null;
    }

    /// <summary>A renamed playlist's file, renamed with it, so the old name does not linger as a second playlist in a player.</summary>
    private static ProblemHttpResult? Move(PlaylistFiles files, string from, string to)
    {
        try
        {
            var source = files.AbsolutePathFor(from);
            var target = files.AbsolutePathFor(to);

            if (!File.Exists(source)) return null;

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Move(source, target);

            return null;
        }
        catch (Exception cause) when (cause is IOException or UnauthorizedAccessException)
        {
            return TypedResults.Problem(
                title: "The playlist's file would not move",
                detail: $"'{from}' could not be renamed to '{to}', so the playlist keeps its name: {cause.Message}",
                statusCode: StatusCodes.Status409Conflict);
        }
    }

    private static ProblemHttpResult? NameProblem(string? name) =>
        string.IsNullOrWhiteSpace(name)
            ? TypedResults.Problem(
                title: "A playlist needs a name",
                detail: "`name` is empty.",
                statusCode: StatusCodes.Status400BadRequest)
            : name.Trim().Length > Playlist.NameLength
                ? TypedResults.Problem(
                    title: "That name is too long",
                    detail: $"A playlist's name is at most {Playlist.NameLength} characters.",
                    statusCode: StatusCodes.Status400BadRequest)
                : null;

    private static ProblemHttpResult NoSuchPlaylist(Guid id) =>
        TypedResults.Problem(
            title: "No such playlist",
            detail: $"There is no playlist with id {id}.",
            statusCode: StatusCodes.Status404NotFound);

    /// <summary>How long the songs the library holds play for; null where none has a length.</summary>
    private static TimeSpan? Total(IReadOnlyCollection<PlaylistSong> songs)
    {
        var lengths = songs.Where(song => song.Copy is not null).Select(song => song.Duration).OfType<TimeSpan>().ToList();
        return lengths.Count == 0 ? null : lengths.Aggregate(TimeSpan.Zero, (sum, length) => sum + length);
    }
}

public sealed record PlaylistListResponse(IReadOnlyList<PlaylistSummary> Items);

/// <param name="File">The m3u, library-relative.</param>
/// <param name="Songs">Every entry, held or not.</param>
/// <param name="Held">The entries a copy plays: what the file lists.</param>
/// <param name="Duration">Of the held entries.</param>
/// <param name="Albums">Up to four albums from the top of the list, for its cover.</param>
public sealed record PlaylistSummary(
    Guid Id,
    string Name,
    string File,
    int Songs,
    int Held,
    string? Duration,
    DateTimeOffset ChangedUtc,
    IReadOnlyList<TrackListAlbum> Albums);

/// <param name="File">The m3u, library-relative.</param>
/// <param name="Duration">Of the held entries.</param>
public sealed record PlaylistResponse(
    Guid Id,
    string Name,
    string File,
    string? Duration,
    DateTimeOffset CreatedUtc,
    DateTimeOffset ChangedUtc,
    IReadOnlyList<PlaylistEntryRow> Entries);

/// <param name="Id">The entry's own id: what reordering and removing name.</param>
/// <param name="Title">The song's title, or what it was last known as.</param>
/// <param name="FileId">The media file the m3u names; null where the library holds no copy.</param>
/// <param name="Track">The song as the Tracks page shows it, with that copy as its file; null where the library holds no copy.</param>
public sealed record PlaylistEntryRow(Guid Id, string Title, string? Artist, Guid? FileId, TrackListRow? Track);

/// <param name="RecordingIds">Songs to start with, in order.</param>
public sealed record CreatePlaylistRequest(string? Name, IReadOnlyList<Guid>? RecordingIds);

/// <param name="Name">A new name; omitted keeps the old one.</param>
/// <param name="Entries">The new order as entry ids, leaving out those to remove; omitted keeps the order.</param>
public sealed record UpdatePlaylistRequest(string? Name, IReadOnlyList<Guid>? Entries);

public sealed record AddPlaylistSongsRequest(IReadOnlyList<Guid>? RecordingIds);
