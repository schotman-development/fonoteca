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
/// a static playlist is its file, a name is a file, a smart playlist is the
/// library's — are the web client's too, and a refusal comes back as the
/// protocol's error.
///
/// <b>A client sees the entries naming a file the library holds, and only
/// those.</b> Anything else in the file has nothing to stream, so it is left out
/// of <c>getPlaylist</c> — and kept in the file all the same: an index from
/// <c>updatePlaylist</c> counts the songs the client was shown, and
/// <c>createPlaylist</c> over an existing playlist puts the hidden entries back
/// after the songs it was sent rather than dropping what it never saw.
///
/// <b>A song id is a file, and a static playlist's entry is the file it
/// names.</b> A smart playlist is read-only here: its rules are the library's
/// to keep, so a client asking to change or delete one is refused, and each
/// playlist says which it is in OpenSubsonic's <c>readonly</c>.
/// </remarks>
public static partial class SubsonicEndpoints
{
    private static async Task<IResult> GetPlaylists(
        HttpContext http,
        FonotecaDbContext db,
        PlaylistFiles files,
        CancellationToken cancellationToken)
    {
        var playlists = (await CatalogueEndpoints.GetPlaylists(db, files, cancellationToken).ConfigureAwait(false)).Value!.Items;

        var owner = Owner(http);

        return SubsonicResult.Ok(
            "playlists",
            list => list.Children("playlist", playlists, (writer, playlist) =>
                WritePlaylist(
                    writer,
                    Id(playlist.Kind, playlist.Id),
                    playlist.Name,
                    owner,
                    playlist.Held,
                    playlist.Seconds,
                    playlist.ChangedUtc,
                    playlist.ChangedUtc,
                    playlist.Albums.Count == 0 ? null : SubsonicIds.Album(new ReleaseGroupId(playlist.Albums[0].AlbumId)),
                    playlist.Kind == PlaylistKind.Smart)));
    }

    private static async Task<IResult> GetPlaylist(
        HttpContext http,
        FonotecaDbContext db,
        PlaylistFiles files,
        CancellationToken cancellationToken)
    {
        if (await ReadAsync(Text(http, "id"), db, files, cancellationToken).ConfigureAwait(false) is not { } result)
        {
            return SubsonicResult.Error(70, "No such playlist.");
        }

        return await PlaylistAsync(http, db, result, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// A new static playlist, or an existing one's songs replaced.
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
        CancellationToken cancellationToken)
    {
        var (songs, problem) = FilesOf(http.Request.Query["songId"]);

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
                        .CreatePlaylist(new CreatePlaylistRequest(name, null, songs), db, files, cancellationToken)
                        .ConfigureAwait(false),
                    cancellationToken)
                .ConfigureAwait(false);
        }

        if (SubsonicIds.AsPlaylist(existing) is not null) return ReadOnly();

        if (SubsonicIds.AsPlaylistFile(existing) is not { } file
            || (await CatalogueEndpoints.GetPlaylist(file, db, files, cancellationToken).ConfigureAwait(false)).Result
                is not Ok<PlaylistResponse> { Value: { } before })
        {
            return SubsonicResult.Error(70, "No such playlist.");
        }

        var appended = await CatalogueEndpoints
            .AddPlaylistSongs(file, new AddPlaylistSongsRequest(null, songs), db, files, cancellationToken)
            .ConfigureAwait(false);

        if (appended.Result is not Ok<PlaylistResponse> { Value: { } added })
        {
            return Refusal(appended.Result) ?? SubsonicResult.Error(0, "The songs could not be added.");
        }

        List<int> order =
        [
            .. added.Entries.Skip(before.Entries.Count).Select(entry => entry.Index),
            .. before.Entries.Where(entry => entry.FileId is null).Select(entry => entry.Index),
        ];

        return await PlaylistAsync(
                http,
                db,
                await CatalogueEndpoints
                    .UpdatePlaylist(file, new UpdatePlaylistRequest(Text(http, "name"), order, added.Version), db, files, cancellationToken)
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
        CancellationToken cancellationToken)
    {
        var id = Text(http, "playlistId");

        if (SubsonicIds.AsPlaylist(id) is not null) return ReadOnly();

        if (SubsonicIds.AsPlaylistFile(id) is not { } file
            || (await CatalogueEndpoints.GetPlaylist(file, db, files, cancellationToken).ConfigureAwait(false)).Result
                is not Ok<PlaylistResponse> { Value: { } before })
        {
            return SubsonicResult.Error(70, "No such playlist.");
        }

        var (songs, problem) = FilesOf(http.Request.Query["songIdToAdd"]);

        if (problem is not null) return SubsonicResult.Error(70, problem);

        var shown = before.Entries.Where(entry => entry.FileId is not null).Select(entry => entry.Index).ToList();

        var removed = http.Request.Query["songIndexToRemove"]
            .Select(index => int.TryParse(index, NumberStyles.Integer, CultureInfo.InvariantCulture, out var at) ? at : -1)
            .Where(at => at >= 0 && at < shown.Count)
            .Select(at => shown[at])
            .ToHashSet();

        var updated = await CatalogueEndpoints
            .UpdatePlaylist(
                file,
                new UpdatePlaylistRequest(
                    Text(http, "name"),
                    [.. before.Entries.Select(entry => entry.Index).Where(entry => !removed.Contains(entry))],
                    before.Version),
                db,
                files,
                cancellationToken)
            .ConfigureAwait(false);

        if (Refusal(updated.Result) is { } refused) return refused;

        if (songs.Count > 0
            && updated.Result is Ok<PlaylistResponse> { Value: { } renamed }
            && Refusal((await CatalogueEndpoints
                .AddPlaylistSongs(renamed.Id, new AddPlaylistSongsRequest(null, songs), db, files, cancellationToken)
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
        var id = Text(http, "id");

        if (SubsonicIds.AsPlaylist(id) is not null) return ReadOnly();

        if (SubsonicIds.AsPlaylistFile(id) is not { } file) return SubsonicResult.Error(70, "No such playlist.");

        var result = await CatalogueEndpoints
            .DeletePlaylist(file, db, files, options, clock, logger, cancellationToken)
            .ConfigureAwait(false);

        return Refusal(result.Result) ?? SubsonicResult.Ok();
    }

    /// <summary>The playlist an id names, by its own handler; null where the id names none.</summary>
    private static async Task<IResult?> ReadAsync(string? id, FonotecaDbContext db, PlaylistFiles files, CancellationToken cancellationToken)
    {
        if (SubsonicIds.AsPlaylist(id) is { } smart)
        {
            return await CatalogueEndpoints.GetSmartPlaylist(smart.Value, db, files, cancellationToken).ConfigureAwait(false);
        }

        if (SubsonicIds.AsPlaylistFile(id) is { } file)
        {
            return await CatalogueEndpoints.GetPlaylist(file, db, files, cancellationToken).ConfigureAwait(false);
        }

        return null;
    }

    /// <summary>A handler's playlist as the protocol's, with the entries naming a file the library holds.</summary>
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
                Id(playlist.Kind, playlist.Id),
                playlist.Name,
                owner,
                ordered.Count,
                ordered.Sum(song => song.DurationSeconds ?? 0),
                playlist.ChangedUtc,
                playlist.ChangedUtc,
                ordered.Count == 0 ? null : ordered[0].AlbumId is { } album ? SubsonicIds.Album(album) : ordered[0].Id,
                playlist.Kind == PlaylistKind.Smart);

            root.Children("entry", ordered, WriteSong);
        });
    }

    private static string Id(PlaylistKind kind, string id) =>
        kind == PlaylistKind.Smart ? SubsonicIds.Playlist(new PlaylistId(Guid.Parse(id))) : SubsonicIds.PlaylistFile(id);

    private static void WritePlaylist(
        ISubsonicWriter writer,
        string id,
        string name,
        string owner,
        int songCount,
        int duration,
        DateTimeOffset created,
        DateTimeOffset changed,
        string? coverArt,
        bool readOnly)
    {
        writer.Attr("id", id);
        writer.Attr("name", name);
        writer.Attr("owner", owner);
        writer.Attr("public", true);
        writer.Attr("songCount", songCount);
        writer.Attr("duration", duration);
        writer.Attr("created", created);
        writer.Attr("changed", changed);
        writer.Attr("coverArt", coverArt);
        writer.Attr("readonly", readOnly);
    }

    /// <summary>Each song id's file, in order; or which id is not a song.</summary>
    /// <remarks>Whether the catalogue holds each is the handler's to say.</remarks>
    private static (List<Guid> Files, string? Problem) FilesOf(IEnumerable<string?> songIds)
    {
        var files = new List<Guid>();

        foreach (var raw in songIds)
        {
            if (string.IsNullOrEmpty(raw)) continue;
            if (SubsonicIds.AsSong(raw) is not { } file) return ([], $"No such song: {raw}.");

            files.Add(file.Value);
        }

        return (files, null);
    }

    private static SubsonicResult ReadOnly() =>
        SubsonicResult.Error(50, "A smart playlist is kept by the library from its rules; change it in Fonoteca.");

    /// <summary>A handler's problem document as the protocol's error; null for anything else.</summary>
    private static SubsonicResult? Refusal(IResult result) => result switch
    {
        INestedHttpResult nested => Refusal(nested.Result),
        ProblemHttpResult problem => SubsonicResult.Error(
            problem.StatusCode == StatusCodes.Status404NotFound ? 70 : 0,
            $"{problem.ProblemDetails.Title}. {problem.ProblemDetails.Detail}"),
        _ => null,
    };

    /// <summary>
    /// The account signed in, so a client lets it edit every playlist: they are
    /// everyone's (ADR 0018). Not <c>u</c>, which an API key comes without.
    /// </summary>
    private static string Owner(HttpContext http) => http.User.Identity!.Name!;
}
