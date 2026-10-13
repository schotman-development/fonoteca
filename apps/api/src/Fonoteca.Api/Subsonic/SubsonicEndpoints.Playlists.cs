using System.Globalization;
using Fonoteca.Api.Configuration;
using Fonoteca.Api.Endpoints;
using Fonoteca.Api.Library;
using Fonoteca.Data;
using Fonoteca.Domain.Abstractions;
using Fonoteca.Domain.Catalogue;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Fonoteca.Api.Subsonic;

/// <summary>
/// The playlists, read and written from somebody else's client.
/// </summary>
/// <remarks>
/// <b>Every write is the playlist endpoint's own handler</b>, so the rules —
/// an entry is a song, a name is a file, the file is rewritten — are the web
/// client's too, and a refusal comes back as the protocol's error.
///
/// <b>A client sees the entries a copy plays, and only those.</b> One the
/// library holds no copy of has nothing to stream, so it is left out of
/// <c>getPlaylist</c> — and kept on the playlist all the same: an index from
/// <c>updatePlaylist</c> counts the songs the client was shown, and
/// <c>createPlaylist</c> over an existing playlist puts the hidden entries back
/// after the songs it was sent rather than dropping what it never saw.
///
/// <b>A song id is a file and an entry is a song.</b> A file nothing has
/// identified is not one yet and is refused by name.
/// </remarks>
public static partial class SubsonicEndpoints
{
    private static async Task<IResult> GetPlaylists(
        HttpContext http,
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

        // The cover is the first held song's album, else that song itself.
        List<MediaFileId> firsts = [.. playlists
            .Select(playlist => playlist.Entries
                .OrderBy(entry => entry.Position)
                .Select(entry => songs[entry.Id].Copy?.Id)
                .FirstOrDefault(copy => copy is not null))
            .OfType<MediaFileId>()];

        var albums = await db.MediaFiles
            .AsNoTracking()
            .Where(file => firsts.Contains(file.Id))
            .Select(file => new { file.Id, file.ReleaseGroupId })
            .ToDictionaryAsync(file => file.Id, file => file.ReleaseGroupId, cancellationToken)
            .ConfigureAwait(false);

        var owner = Owner(http);

        return SubsonicResult.Ok(
            "playlists",
            list => list.Children("playlist", playlists, (writer, playlist) =>
            {
                var held = playlist.Entries
                    .OrderBy(entry => entry.Position)
                    .Select(entry => songs[entry.Id])
                    .Where(song => song.Copy is not null)
                    .ToList();

                var cover = held.Count == 0
                    ? null
                    : albums.GetValueOrDefault(held[0].Copy!.Id) is { } album
                        ? SubsonicIds.Album(album)
                        : SubsonicIds.Song(held[0].Copy!.Id);

                WritePlaylist(
                    writer,
                    playlist.Id,
                    playlist.Name,
                    owner,
                    held.Count,
                    (int)Math.Round(held.Sum(song => song.Duration?.TotalSeconds ?? 0)),
                    playlist.CreatedUtc,
                    playlist.ChangedUtc,
                    cover);
            }));
    }

    private static async Task<IResult> GetPlaylist(
        HttpContext http,
        FonotecaDbContext db,
        CancellationToken cancellationToken)
    {
        if (SubsonicIds.AsPlaylist(Text(http, "id")) is not { } id)
        {
            return SubsonicResult.Error(70, "No such playlist.");
        }

        return await PlaylistAsync(
                http,
                db,
                await CatalogueEndpoints.GetPlaylist(id.Value, db, cancellationToken).ConfigureAwait(false),
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// A new playlist, or an existing one's songs replaced.
    /// </summary>
    /// <remarks>
    /// The protocol's one method for both, told apart by <c>playlistId</c>.
    /// Replacing appends the songs sent and then orders the playlist as they
    /// came, followed by the entries the client could not see.
    /// </remarks>
    private static async Task<IResult> CreatePlaylist(
        HttpContext http,
        FonotecaDbContext db,
        PlaylistFiles files,
        IClock clock,
        CancellationToken cancellationToken)
    {
        var (recordings, problem) = await RecordingsOfAsync(db, http.Request.Query["songId"], cancellationToken)
            .ConfigureAwait(false);

        if (problem is not null) return SubsonicResult.Error(70, problem);

        if (Text(http, "playlistId") is not { } existing)
        {
            if (Text(http, "name") is not { } name)
            {
                return SubsonicResult.Error(10, "Required parameter is missing: name.");
            }

            return await PlaylistAsync(
                    http,
                    db,
                    await CatalogueEndpoints
                        .CreatePlaylist(new CreatePlaylistRequest(name, recordings), db, files, clock, cancellationToken)
                        .ConfigureAwait(false),
                    cancellationToken)
                .ConfigureAwait(false);
        }

        if (SubsonicIds.AsPlaylist(existing) is not { } id)
        {
            return SubsonicResult.Error(70, "No such playlist.");
        }

        if ((await CatalogueEndpoints.GetPlaylist(id.Value, db, cancellationToken).ConfigureAwait(false)).Result
            is not Ok<PlaylistResponse> { Value: { } before })
        {
            return SubsonicResult.Error(70, "No such playlist.");
        }

        var appended = await CatalogueEndpoints
            .AddPlaylistSongs(id.Value, new AddPlaylistSongsRequest(recordings), db, files, clock, cancellationToken)
            .ConfigureAwait(false);

        if (appended.Result is not Ok<PlaylistResponse> { Value: { } added })
        {
            return Refusal(appended.Result) ?? SubsonicResult.Error(0, "The songs could not be added.");
        }

        List<Guid> order =
        [
            .. added.Entries.Skip(before.Entries.Count).Select(entry => entry.Id),
            .. before.Entries.Where(entry => entry.FileId is null).Select(entry => entry.Id),
        ];

        return await PlaylistAsync(
                http,
                db,
                await CatalogueEndpoints
                    .UpdatePlaylist(id.Value, new UpdatePlaylistRequest(Text(http, "name"), order), db, files, clock, cancellationToken)
                    .ConfigureAwait(false),
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// A rename, songs taken out by their place in the list the client was
    /// shown, and songs added at the end — in that order, as the protocol has
    /// it. <c>comment</c> and <c>public</c> are accepted and kept nowhere.
    /// </summary>
    private static async Task<IResult> UpdatePlaylist(
        HttpContext http,
        FonotecaDbContext db,
        PlaylistFiles files,
        IClock clock,
        CancellationToken cancellationToken)
    {
        if (SubsonicIds.AsPlaylist(Text(http, "playlistId")) is not { } id)
        {
            return SubsonicResult.Error(70, "No such playlist.");
        }

        if ((await CatalogueEndpoints.GetPlaylist(id.Value, db, cancellationToken).ConfigureAwait(false)).Result
            is not Ok<PlaylistResponse> { Value: { } before })
        {
            return SubsonicResult.Error(70, "No such playlist.");
        }

        var (recordings, problem) = await RecordingsOfAsync(db, http.Request.Query["songIdToAdd"], cancellationToken)
            .ConfigureAwait(false);

        if (problem is not null) return SubsonicResult.Error(70, problem);

        var shown = before.Entries.Where(entry => entry.FileId is not null).Select(entry => entry.Id).ToList();

        var removed = http.Request.Query["songIndexToRemove"]
            .Select(index => int.TryParse(index, NumberStyles.Integer, CultureInfo.InvariantCulture, out var at) ? at : -1)
            .Where(at => at >= 0 && at < shown.Count)
            .Select(at => shown[at])
            .ToHashSet();

        var updated = await CatalogueEndpoints
            .UpdatePlaylist(
                id.Value,
                new UpdatePlaylistRequest(
                    Text(http, "name"),
                    [.. before.Entries.Select(entry => entry.Id).Where(entry => !removed.Contains(entry))]),
                db,
                files,
                clock,
                cancellationToken)
            .ConfigureAwait(false);

        if (Refusal(updated.Result) is { } refused) return refused;

        if (recordings.Count > 0
            && Refusal((await CatalogueEndpoints
                .AddPlaylistSongs(id.Value, new AddPlaylistSongsRequest(recordings), db, files, clock, cancellationToken)
                .ConfigureAwait(false)).Result) is { } stopped)
        {
            return stopped;
        }

        return SubsonicResult.Ok();
    }

    private static async Task<IResult> DeletePlaylist(
        HttpContext http,
        FonotecaDbContext db,
        PlaylistFiles files,
        IOptions<FonotecaOptions> options,
        IClock clock,
        ILogger<PlaylistFiles> logger,
        CancellationToken cancellationToken)
    {
        if (SubsonicIds.AsPlaylist(Text(http, "id")) is not { } id)
        {
            return SubsonicResult.Error(70, "No such playlist.");
        }

        var result = await CatalogueEndpoints
            .DeletePlaylist(id.Value, db, files, options, clock, logger, cancellationToken)
            .ConfigureAwait(false);

        return Refusal(result.Result) ?? SubsonicResult.Ok();
    }

    /// <summary>A handler's playlist as the protocol's, with the songs a copy plays.</summary>
    private static async Task<IResult> PlaylistAsync(
        HttpContext http,
        FonotecaDbContext db,
        IResult result,
        CancellationToken cancellationToken)
    {
        if (result is INestedHttpResult nested) result = nested.Result;

        if (Refusal(result) is { } refused) return refused;

        if (result is not IValueHttpResult { Value: PlaylistResponse playlist })
        {
            return SubsonicResult.Error(0, "The playlist could not be read back.");
        }

        List<MediaFileId> copies = [.. playlist.Entries
            .Select(entry => entry.FileId)
            .OfType<Guid>()
            .Distinct()
            .Select(file => new MediaFileId(file))];

        var songs = (await SongsAsync(db, db.MediaFiles.AsNoTracking().Where(file => copies.Contains(file.Id)), cancellationToken)
                .ConfigureAwait(false))
            .ToDictionary(song => song.Id);

        var ordered = playlist.Entries
            .Select(entry => entry.FileId is { } file ? songs.GetValueOrDefault(SubsonicIds.Song(new MediaFileId(file))) : null)
            .OfType<Song>()
            .ToList();

        var owner = Owner(http);

        return SubsonicResult.Ok("playlist", root =>
        {
            WritePlaylist(
                root,
                new PlaylistId(playlist.Id),
                playlist.Name,
                owner,
                ordered.Count,
                ordered.Sum(song => song.DurationSeconds ?? 0),
                playlist.CreatedUtc,
                playlist.ChangedUtc,
                ordered.Count == 0 ? null : ordered[0].AlbumId is { } album ? SubsonicIds.Album(album) : ordered[0].Id);

            root.Children("entry", ordered, WriteSong);
        });
    }

    private static void WritePlaylist(
        ISubsonicWriter writer,
        PlaylistId id,
        string name,
        string owner,
        int songCount,
        int duration,
        DateTimeOffset created,
        DateTimeOffset changed,
        string? coverArt)
    {
        writer.Attr("id", SubsonicIds.Playlist(id));
        writer.Attr("name", name);
        writer.Attr("owner", owner);
        writer.Attr("public", true);
        writer.Attr("songCount", songCount);
        writer.Attr("duration", duration);
        writer.Attr("created", created);
        writer.Attr("changed", changed);
        writer.Attr("coverArt", coverArt);
    }

    /// <summary>
    /// Each song id's recording, in order; or which id is not a song.
    /// </summary>
    private static async Task<(List<Guid> Recordings, string? Problem)> RecordingsOfAsync(
        FonotecaDbContext db,
        IEnumerable<string?> songIds,
        CancellationToken cancellationToken)
    {
        var asked = new List<(string Raw, MediaFileId File)>();

        foreach (var raw in songIds)
        {
            if (string.IsNullOrEmpty(raw)) continue;
            if (SubsonicIds.AsSong(raw) is not { } file) return ([], $"No such song: {raw}.");

            asked.Add((raw, file));
        }

        List<MediaFileId> files = [.. asked.Select(song => song.File).Distinct()];

        var known = await db.MediaFiles
            .AsNoTracking()
            .Where(file => files.Contains(file.Id))
            .Select(file => new { file.Id, file.RecordingId })
            .ToDictionaryAsync(file => file.Id, file => file.RecordingId, cancellationToken)
            .ConfigureAwait(false);

        var recordings = new List<Guid>(asked.Count);

        foreach (var (raw, file) in asked)
        {
            if (!known.TryGetValue(file, out var recording)) return ([], $"No such song: {raw}.");

            if (recording is not { } song)
            {
                return ([], $"{raw} is a file nothing has identified yet, so it is not a song a playlist can hold.");
            }

            recordings.Add(song.Value);
        }

        return (recordings, null);
    }

    /// <summary>A handler's problem document as the protocol's error; null for anything else.</summary>
    private static SubsonicResult? Refusal(IResult result) => result switch
    {
        INestedHttpResult nested => Refusal(nested.Result),
        ProblemHttpResult problem => SubsonicResult.Error(
            problem.StatusCode == StatusCodes.Status404NotFound ? 70 : 0,
            $"{problem.ProblemDetails.Title}. {problem.ProblemDetails.Detail}"),
        _ => null,
    };

    /// <summary>The user a client logged in as, since there are no others to own anything.</summary>
    private static string Owner(HttpContext http) => Text(http, "u") ?? "fonoteca";
}
