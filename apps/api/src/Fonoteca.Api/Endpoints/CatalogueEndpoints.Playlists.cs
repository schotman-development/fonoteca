using System.Globalization;
using System.Text.Json.Serialization;
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
 * Playlists: m3u files in the library's Playlists folder. A static one is its
 * file and is addressed by the file's name; a smart one is rules the library
 * keeps a file of, addressed by its id. Both are described by reading the file,
 * because the file is what every player sees. `PlaylistFiles` says why and owns
 * every write; Subsonic's playlist methods call these handlers.
 */
public static partial class CatalogueEndpoints
{
    /// <summary>How many songs a preview shows; its counts are of every song.</summary>
    private const int PreviewRows = 200;

    private static void MapPlaylistEndpoints(IEndpointRouteBuilder group)
    {
        group.MapGet("/playlists", GetPlaylists)
            .WithName("GetPlaylists")
            .WithSummary("Every playlist, static and smart, by name, with its first albums for a cover.")
            .WithDescription(
                "A static playlist is any `.m3u8` or `.m3u` at the top of the library's `Playlists` folder "
                + "that no smart playlist owns, whoever wrote it; its `id` is the file's name. A smart "
                + "playlist's `id` is its own. Counts are of the file's entries: `held` are those naming a "
                + "file the catalogue holds.");

        group.MapPost("/playlists", CreatePlaylist)
            .WithName("CreatePlaylist")
            .WithSummary("A new static playlist, optionally with songs, written to the library as an m3u.")
            .WithDescription(
                "`recordingIds` are songs, each written as the best copy the library holds; `fileIds` "
                + "are files, written as themselves, after the songs. The file is "
                + "`Playlists/<name>.m3u8` with paths relative to it. From then on the file is the "
                + "playlist: an edit made to it elsewhere is what this application reads.")
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status409Conflict);

        group.MapGet("/playlists/{file}", GetPlaylist)
            .WithName("GetPlaylist")
            .WithSummary("One static playlist's entries, in the file's order.")
            .WithDescription(
                "An entry with no `track` names nothing the catalogue holds: a file since removed or "
                + "upgraded away, a stream, a path outside the library. It stays in the file as written.")
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPost("/playlists/{file}", UpdatePlaylist)
            .WithName("UpdatePlaylist")
            .WithSummary("Rename a static playlist, or set the order of its entries.")
            .WithDescription(
                "`entries` is the new order as indexes into the entries `GetPlaylist` returned; one left "
                + "out is removed. It needs the `version` that came with them, and is refused when the "
                + "file has changed since. A rename renames the file and its `#PLAYLIST:` line. Every "
                + "other line of the file is kept as it is.")
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        group.MapPost("/playlists/{file}/songs", AddPlaylistSongs)
            .WithName("AddPlaylistSongs")
            .WithSummary("Add songs or files to the end of a static playlist.")
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        group.MapDelete("/playlists/{file}", DeletePlaylist)
            .WithName("DeletePlaylist")
            .WithSummary("Delete a static playlist: its file moves to the trash.")
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        group.MapPost("/smart-playlists", CreateSmartPlaylist)
            .WithName("CreateSmartPlaylist")
            .WithSummary("A new smart playlist: rules the library keeps an m3u of.")
            .WithDescription(
                "The file is `Playlists/<name>.m3u8`, written now and again after every pass, undo, tag "
                + "edit, download, answer on Identify and scan. Only the file holds the songs; an edit "
                + "made to it elsewhere is overwritten.")
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status409Conflict);

        group.MapPost("/smart-playlists/preview", PreviewSmartPlaylist)
            .WithName("PreviewSmartPlaylist")
            .WithSummary("What a set of rules would hold, without saving anything.")
            .WithDescription($"Counts are of every song; `entries` are the first {PreviewRows}.")
            .ProducesProblem(StatusCodes.Status400BadRequest);

        group.MapGet("/smart-playlists/{id:guid}", GetSmartPlaylist)
            .WithName("GetSmartPlaylist")
            .WithSummary("One smart playlist's rules, and the songs its file holds.")
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPost("/smart-playlists/{id:guid}", UpdateSmartPlaylist)
            .WithName("UpdateSmartPlaylist")
            .WithSummary("Rename a smart playlist or change its rules; its file is written again.")
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        group.MapDelete("/smart-playlists/{id:guid}", DeleteSmartPlaylist)
            .WithName("DeleteSmartPlaylist")
            .WithSummary("Delete a smart playlist; its file moves to the trash.")
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);
    }

    internal static async Task<Ok<PlaylistListResponse>> GetPlaylists(
        FonotecaDbContext db,
        PlaylistFiles files,
        CancellationToken cancellationToken)
    {
        var smart = await db.SmartPlaylists
            .AsNoTracking()
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var owned = smart.Select(playlist => playlist.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var listed = new List<(string Id, PlaylistKind Kind, string Name, string Path, DateTimeOffset Changed, string Text)>();

        foreach (var path in files.AllPaths().Where(path => !owned.Contains(path)))
        {
            if (await files.ReadAsync(path, cancellationToken).ConfigureAwait(false) is not { } text) continue;

            listed.Add((FileName(path), PlaylistKind.Static, NameOf(text), path, new DateTimeOffset(text.ChangedUtc, TimeSpan.Zero), text.Text));
        }

        foreach (var playlist in smart)
        {
            var text = await files.ReadAsync(playlist.Path, cancellationToken).ConfigureAwait(false);
            listed.Add((playlist.Id.Value.ToString(), PlaylistKind.Smart, playlist.Name, playlist.Path, playlist.ChangedUtc, text?.Text ?? string.Empty));
        }

        var resolved = await ResolveAsync(db, files.Root, [.. listed.Select(playlist => (playlist.Path, playlist.Text))], cancellationToken)
            .ConfigureAwait(false);

        // Enough songs from the top of each to find four albums for its cover.
        List<RecordingId> wanted = [.. resolved.Values
            .SelectMany(entries => entries.Select(entry => entry.RecordingId).OfType<RecordingId>().Distinct().Take(16))
            .Distinct()];

        var rows = (await TrackRowsAsync(
                    db,
                    db.Recordings.AsNoTracking().Where(recording => wanted.Contains(recording.Id)),
                    cancellationToken)
                .ConfigureAwait(false))
            .ToDictionary(row => row.RecordingId);

        var items = listed
            .OrderBy(playlist => playlist.Name, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(playlist => playlist.Path, StringComparer.Ordinal)
            .Select(playlist =>
            {
                var entries = resolved[playlist.Path];

                var total = Total(entries);

                return new PlaylistSummary(
                    playlist.Id,
                    playlist.Kind,
                    playlist.Name,
                    playlist.Path,
                    entries.Count,
                    entries.Count(entry => entry.Copy is not null),
                    Format(total),
                    (int)Math.Round(total?.TotalSeconds ?? 0),
                    playlist.Changed,
                    [.. entries
                        .Select(entry => entry.RecordingId)
                        .OfType<RecordingId>()
                        .Distinct()
                        .Take(16)
                        .Select(id => rows.GetValueOrDefault(id.Value)?.Album)
                        .OfType<TrackListAlbum>()
                        .DistinctBy(album => album.AlbumId)
                        .Take(4)]);
            })
            .ToList();

        return TypedResults.Ok(new PlaylistListResponse(items));
    }

    internal static async Task<Results<Ok<PlaylistResponse>, ProblemHttpResult>> GetPlaylist(
        string file,
        FonotecaDbContext db,
        PlaylistFiles files,
        CancellationToken cancellationToken)
    {
        if (await StaticPathAsync(db, file, cancellationToken).ConfigureAwait(false) is not { } path
            || await files.ReadAsync(path, cancellationToken).ConfigureAwait(false) is not { } text)
        {
            return NoSuchPlaylist(file);
        }

        return TypedResults.Ok(await DescribeAsync(db, files, text, cancellationToken).ConfigureAwait(false));
    }

    internal static async Task<Results<Ok<PlaylistResponse>, ProblemHttpResult>> CreatePlaylist(
        CreatePlaylistRequest request,
        FonotecaDbContext db,
        PlaylistFiles files,
        CancellationToken cancellationToken)
    {
        if (NameProblem(request?.Name) is { } unnamed) return unnamed;

        var name = request!.Name!.Trim();
        var path = PlaylistFile.PathFor(name);

        var (lines, refused) = await LinesOfAsync(db, request.RecordingIds, request.FileIds, cancellationToken).ConfigureAwait(false);
        if (refused is not null) return refused;

        if (!files.Mounted()) return Unmounted();

        var problem = await files.EditAsync<ProblemHttpResult?>(
                async () =>
                {
                    if (await TakenAsync(db, files, path, null, cancellationToken).ConfigureAwait(false) is { } taken) return taken;

                    return await files.WriteLockedAsync(path, PlaylistFile.Render(name, lines), bom: false, cancellationToken).ConfigureAwait(false)
                        ? null
                        : NotWritten(path);
                },
                cancellationToken)
            .ConfigureAwait(false);

        if (problem is not null) return problem;

        return await GetPlaylist(FileName(path), db, files, cancellationToken).ConfigureAwait(false);
    }

    internal static async Task<Results<Ok<PlaylistResponse>, ProblemHttpResult>> UpdatePlaylist(
        string file,
        UpdatePlaylistRequest request,
        FonotecaDbContext db,
        PlaylistFiles files,
        CancellationToken cancellationToken)
    {
        if (await StaticPathAsync(db, file, cancellationToken).ConfigureAwait(false) is not { } path) return NoSuchPlaylist(file);

        if (request?.Name is not null && NameProblem(request.Name) is { } unnamed) return unnamed;

        if (request?.Entries is not null && request.Version is null)
        {
            return TypedResults.Problem(
                title: "Which version of the file?",
                detail: "Reordering or removing names entries by their place, so it needs the `version` they came with.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var target = path;

        var problem = await files.EditAsync<ProblemHttpResult?>(
                async () =>
                {
                    if (await files.ReadLockedAsync(path, cancellationToken).ConfigureAwait(false) is not { } current)
                    {
                        return NoSuchPlaylist(file);
                    }

                    if (!current.Editable) return NotUtf8(path);

                    if (request?.Version is { } version && version != current.Version) return Changed(path);

                    var text = current.Text;

                    if (request?.Entries is { } order)
                    {
                        var count = PlaylistFile.Items(text).Count;

                        if (order.Distinct().Count() != order.Count || order.Any(index => index < 0 || index >= count))
                        {
                            return TypedResults.Problem(
                                title: "Not this playlist's entries",
                                detail: "`entries` lists each of the playlist's entry indexes at most once.",
                                statusCode: StatusCodes.Status400BadRequest);
                        }

                        text = PlaylistFile.Arrange(text, order);
                    }

                    if (request?.Name is { } renamed)
                    {
                        var name = renamed.Trim();
                        var moved = PlaylistFile.PathFor(name);

                        if (!string.Equals(moved, path, StringComparison.Ordinal))
                        {
                            if (await TakenAsync(db, files, moved, path, cancellationToken).ConfigureAwait(false) is { } taken) return taken;
                            target = moved;
                        }

                        text = PlaylistFile.Renamed(text, name);
                    }

                    if (target != path && Move(files, path, target) is { } stuck)
                    {
                        target = path;
                        return stuck;
                    }

                    await files.WriteLockedAsync(target, text, current.Bom, cancellationToken).ConfigureAwait(false);
                    return null;
                },
                cancellationToken)
            .ConfigureAwait(false);

        if (problem is not null) return problem;

        return await GetPlaylist(FileName(target), db, files, cancellationToken).ConfigureAwait(false);
    }

    internal static async Task<Results<Ok<PlaylistResponse>, ProblemHttpResult>> AddPlaylistSongs(
        string file,
        AddPlaylistSongsRequest request,
        FonotecaDbContext db,
        PlaylistFiles files,
        CancellationToken cancellationToken)
    {
        if (await StaticPathAsync(db, file, cancellationToken).ConfigureAwait(false) is not { } path) return NoSuchPlaylist(file);

        var (lines, refused) = await LinesOfAsync(db, request?.RecordingIds, request?.FileIds, cancellationToken).ConfigureAwait(false);
        if (refused is not null) return refused;

        var problem = await files.EditAsync<ProblemHttpResult?>(
                async () =>
                {
                    if (await files.ReadLockedAsync(path, cancellationToken).ConfigureAwait(false) is not { } current)
                    {
                        return NoSuchPlaylist(file);
                    }

                    if (!current.Editable) return NotUtf8(path);

                    var text = PlaylistFile.Append(current.Text, NameOf(current), lines);

                    return ReferenceEquals(text, current.Text)
                        || await files.WriteLockedAsync(path, text, current.Bom, cancellationToken).ConfigureAwait(false)
                        ? null
                        : NotWritten(path);
                },
                cancellationToken)
            .ConfigureAwait(false);

        if (problem is not null) return problem;

        return await GetPlaylist(file, db, files, cancellationToken).ConfigureAwait(false);
    }

    /// <remarks>
    /// To the trash, as the file manager's trash does: nothing here is deleted,
    /// and a file that would not move keeps its playlist.
    /// </remarks>
    internal static async Task<Results<NoContent, ProblemHttpResult>> DeletePlaylist(
        string file,
        FonotecaDbContext db,
        PlaylistFiles files,
        IOptions<FonotecaOptions> options,
        IClock clock,
        ILogger<PlaylistFiles> logger,
        CancellationToken cancellationToken)
    {
        if (await StaticPathAsync(db, file, cancellationToken).ConfigureAwait(false) is not { } path) return NoSuchPlaylist(file);

        var problem = await files.EditAsync<ProblemHttpResult?>(
                () => Task.FromResult(File.Exists(files.AbsolutePathFor(path))
                    ? Trash(files, options.Value, clock, logger, path)
                    : NoSuchPlaylist(file)),
                cancellationToken)
            .ConfigureAwait(false);

        return problem is null ? TypedResults.NoContent() : problem;
    }

    internal static async Task<Results<Ok<PlaylistResponse>, ProblemHttpResult>> CreateSmartPlaylist(
        SmartPlaylistRequest request,
        FonotecaDbContext db,
        PlaylistFiles files,
        IClock clock,
        CancellationToken cancellationToken)
    {
        if (NameProblem(request?.Name) is { } unnamed) return unnamed;
        if (RulesProblem(request!.Criteria) is { } wrong) return wrong;

        var name = request.Name!.Trim();
        var path = PlaylistFile.PathFor(name);
        var now = clock.UtcNow;

        var playlist = new SmartPlaylist
        {
            Id = PlaylistId.New(),
            Name = name,
            Path = path,
            CriteriaJson = SmartRules.Serialize(request.Criteria!),
            CreatedUtc = now,
            ChangedUtc = now,
        };

        var problem = await files.EditAsync<ProblemHttpResult?>(
                async () =>
                {
                    if (await TakenAsync(db, files, path, null, cancellationToken).ConfigureAwait(false) is { } taken) return taken;

                    db.SmartPlaylists.Add(playlist);
                    await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                    return null;
                },
                cancellationToken)
            .ConfigureAwait(false);

        if (problem is not null) return problem;

        await files.RefreshAsync(playlist.Id, cancellationToken).ConfigureAwait(false);

        return await GetSmartPlaylist(playlist.Id.Value, db, files, cancellationToken).ConfigureAwait(false);
    }

    internal static async Task<Results<Ok<SmartPreviewResponse>, ProblemHttpResult>> PreviewSmartPlaylist(
        SmartPreviewRequest request,
        FonotecaDbContext db,
        IClock clock,
        CancellationToken cancellationToken)
    {
        if (RulesProblem(request?.Criteria) is { } wrong) return wrong;

        var songs = await SmartQuery
            .SongsAsync(db, request!.Criteria!, "preview", clock.UtcNow, cancellationToken)
            .ConfigureAwait(false);

        var shown = songs
            .Take(PreviewRows)
            .Select((song, index) => new ResolvedEntry(
                index,
                new PlaylistItem(song.Copy.Path, song.Title, song.Duration),
                song.Copy,
                song.RecordingId))
            .ToList();

        var lengths = songs.Select(song => song.Duration).OfType<TimeSpan>().ToList();

        return TypedResults.Ok(new SmartPreviewResponse(
            songs.Count,
            Format(lengths.Count == 0 ? null : lengths.Aggregate(TimeSpan.Zero, (sum, length) => sum + length)),
            await RowsAsync(db, shown, cancellationToken).ConfigureAwait(false)));
    }

    internal static async Task<Results<Ok<PlaylistResponse>, ProblemHttpResult>> GetSmartPlaylist(
        Guid id,
        FonotecaDbContext db,
        PlaylistFiles files,
        CancellationToken cancellationToken)
    {
        var playlistId = new PlaylistId(id);

        var playlist = await db.SmartPlaylists
            .AsNoTracking()
            .FirstOrDefaultAsync(row => row.Id == playlistId, cancellationToken)
            .ConfigureAwait(false);

        if (playlist is null) return NoSuchPlaylist(id.ToString());

        var text = await files.ReadAsync(playlist.Path, cancellationToken).ConfigureAwait(false)
            ?? new PlaylistText(playlist.Path, string.Empty, false, false, playlist.ChangedUtc.UtcDateTime, PlaylistFiles.Version(string.Empty));

        var described = await DescribeAsync(db, files, text, cancellationToken).ConfigureAwait(false);

        return TypedResults.Ok(described with
        {
            Id = playlist.Id.Value.ToString(),
            Kind = PlaylistKind.Smart,
            Name = playlist.Name,
            Editable = false,
            ChangedUtc = playlist.ChangedUtc,
            Criteria = SmartRules.Parse(playlist.CriteriaJson),
        });
    }

    internal static async Task<Results<Ok<PlaylistResponse>, ProblemHttpResult>> UpdateSmartPlaylist(
        Guid id,
        SmartPlaylistRequest request,
        FonotecaDbContext db,
        PlaylistFiles files,
        IClock clock,
        CancellationToken cancellationToken)
    {
        var playlistId = new PlaylistId(id);

        if (request?.Name is not null && NameProblem(request.Name) is { } unnamed) return unnamed;
        if (request?.Criteria is not null && RulesProblem(request.Criteria) is { } wrong) return wrong;

        var problem = await files.EditAsync<ProblemHttpResult?>(
                async () =>
                {
                    var playlist = await db.SmartPlaylists
                        .FirstOrDefaultAsync(row => row.Id == playlistId, cancellationToken)
                        .ConfigureAwait(false);

                    if (playlist is null) return NoSuchPlaylist(id.ToString());

                    if (request?.Name is { } renamed)
                    {
                        var name = renamed.Trim();
                        var path = PlaylistFile.PathFor(name);

                        if (!string.Equals(path, playlist.Path, StringComparison.Ordinal))
                        {
                            if (await TakenAsync(db, files, path, playlist.Path, cancellationToken).ConfigureAwait(false) is { } taken)
                            {
                                return taken;
                            }

                            if (Move(files, playlist.Path, path) is { } stuck) return stuck;

                            playlist.Path = path;
                        }

                        playlist.Name = name;
                    }

                    if (request?.Criteria is { } criteria) playlist.CriteriaJson = SmartRules.Serialize(criteria);

                    playlist.ChangedUtc = clock.UtcNow;
                    await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                    return null;
                },
                cancellationToken)
            .ConfigureAwait(false);

        if (problem is not null) return problem;

        await files.RefreshAsync(playlistId, cancellationToken).ConfigureAwait(false);

        return await GetSmartPlaylist(id, db, files, cancellationToken).ConfigureAwait(false);
    }

    internal static async Task<Results<NoContent, ProblemHttpResult>> DeleteSmartPlaylist(
        Guid id,
        FonotecaDbContext db,
        PlaylistFiles files,
        IOptions<FonotecaOptions> options,
        IClock clock,
        ILogger<PlaylistFiles> logger,
        CancellationToken cancellationToken)
    {
        var playlistId = new PlaylistId(id);

        var problem = await files.EditAsync<ProblemHttpResult?>(
                async () =>
                {
                    var playlist = await db.SmartPlaylists
                        .FirstOrDefaultAsync(row => row.Id == playlistId, cancellationToken)
                        .ConfigureAwait(false);

                    if (playlist is null) return NoSuchPlaylist(id.ToString());

                    // The file first: left behind, the next player scan would
                    // import it as a playlist of its own.
                    if (File.Exists(files.AbsolutePathFor(playlist.Path))
                        && Trash(files, options.Value, clock, logger, playlist.Path) is { } stuck)
                    {
                        return stuck;
                    }

                    db.SmartPlaylists.Remove(playlist);
                    await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                    return null;
                },
                cancellationToken)
            .ConfigureAwait(false);

        return problem is null ? TypedResults.NoContent() : problem;
    }

    /// <summary>One entry of a playlist's file, and what in the library it names.</summary>
    /// <param name="Copy">The catalogued file the entry names; null where it names none.</param>
    /// <param name="RecordingId">That file's recording, where it has one.</param>
    private sealed record ResolvedEntry(int Index, PlaylistItem Item, PlaylistCopy? Copy, RecordingId? RecordingId);

    /// <summary>Each playlist's entries, by its path, each with the catalogued file it names.</summary>
    private static async Task<Dictionary<string, List<ResolvedEntry>>> ResolveAsync(
        FonotecaDbContext db,
        string root,
        IReadOnlyList<(string Path, string Text)> playlists,
        CancellationToken cancellationToken)
    {
        var items = playlists
            .DistinctBy(playlist => playlist.Path)
            .ToDictionary(
                playlist => playlist.Path,
                playlist => PlaylistFile.Items(playlist.Text)
                    .Select(item => (Item: item, Path: PlaylistFile.Resolve(item.Location, playlist.Path, root)))
                    .ToList());

        List<string> paths = [.. items.Values.SelectMany(entries => entries).Select(entry => entry.Path).OfType<string>().Distinct()];

        var catalogued = await db.MediaFiles
            .AsNoTracking()
            .Where(file => paths.Contains(file.Path))
            .Select(file => new { file.Id, file.RecordingId, file.Path, file.SizeBytes, file.Quality, file.Measurement, file.Integrity })
            .ToDictionaryAsync(file => file.Path, StringComparer.Ordinal, cancellationToken)
            .ConfigureAwait(false);

        return items.ToDictionary(
            playlist => playlist.Key,
            playlist => playlist.Value
                .Select((entry, index) =>
                {
                    var file = entry.Path is { } path ? catalogued.GetValueOrDefault(path) : null;

                    return new ResolvedEntry(
                        index,
                        entry.Item,
                        file is null ? null : new PlaylistCopy(file.Id, file.Path, file.SizeBytes, file.Quality, file.Measurement, file.Integrity),
                        file?.RecordingId);
                })
                .ToList(),
            StringComparer.Ordinal);
    }

    /// <summary>A playlist's file as its page shows it.</summary>
    private static async Task<PlaylistResponse> DescribeAsync(
        FonotecaDbContext db,
        PlaylistFiles files,
        PlaylistText text,
        CancellationToken cancellationToken)
    {
        var entries = (await ResolveAsync(db, files.Root, [(text.Path, text.Text)], cancellationToken).ConfigureAwait(false))[text.Path];

        return new PlaylistResponse(
            FileName(text.Path),
            PlaylistKind.Static,
            NameOf(text),
            text.Path,
            text.Version,
            text.Editable,
            Format(Total(entries)),
            new DateTimeOffset(text.ChangedUtc, TimeSpan.Zero),
            null,
            await RowsAsync(db, entries, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>Each entry as the Tracks page shows a song, playing the file the entry names.</summary>
    private static async Task<List<PlaylistEntryRow>> RowsAsync(
        FonotecaDbContext db,
        IReadOnlyList<ResolvedEntry> entries,
        CancellationToken cancellationToken)
    {
        List<RecordingId> held = [.. entries.Select(entry => entry.RecordingId).OfType<RecordingId>().Distinct()];

        var rows = (await TrackRowsAsync(
                    db,
                    db.Recordings.AsNoTracking().Where(recording => held.Contains(recording.Id)),
                    cancellationToken)
                .ConfigureAwait(false))
            .ToDictionary(row => row.RecordingId);

        return [.. entries.Select(entry =>
        {
            var named = entry.Item.Title ?? Path.GetFileNameWithoutExtension(entry.Item.Location.Replace('\\', '/'));

            if (entry.Copy is not { } copy) return new PlaylistEntryRow(entry.Index, named, null, entry.Item.Location, null, null);

            var file = FileRowOf(copy.Path, copy.SizeBytes, copy.Quality, copy.Integrity, copy.Measurement);

            // A file linked to no recording has no Tracks row, and goes out as
            // itself: no album, no credits.
            var track = entry.RecordingId is { } recording && rows.TryGetValue(recording.Value, out var row)
                ? row with { Folder = FolderOf(copy.Path), File = file }
                : new TrackListRow(
                    copy.Id.Value,
                    Path.GetFileNameWithoutExtension(copy.Path),
                    null,
                    Format(copy.Quality?.Duration ?? entry.Item.Duration),
                    [],
                    null,
                    FolderOf(copy.Path),
                    file,
                    1);

            var byline = string.Concat(track.Artists.Select(credit => credit.Name + credit.JoinPhrase));

            return new PlaylistEntryRow(entry.Index, track.Title, byline.Length == 0 ? null : byline, entry.Item.Location, copy.Id.Value, track);
        })];
    }

    /// <summary>
    /// What to write for these songs and files, in order: each song as the best
    /// copy the library holds, each file as itself; or which was not found.
    /// </summary>
    private static async Task<(List<PlaylistLine> Lines, ProblemHttpResult? Problem)> LinesOfAsync(
        FonotecaDbContext db,
        IReadOnlyList<Guid>? recordingIds,
        IReadOnlyList<Guid>? fileIds,
        CancellationToken cancellationToken)
    {
        recordingIds ??= [];
        fileIds ??= [];

        List<RecordingId> asked = [.. recordingIds.Distinct().Select(id => new RecordingId(id))];
        List<MediaFileId> named = [.. fileIds.Distinct().Select(id => new MediaFileId(id))];

        // Nullable for the files' column: EF translates no member access on a typed id.
        List<RecordingId?> askedOfFiles = [.. asked.Select(id => (RecordingId?)id)];

        var files = await db.MediaFiles
            .AsNoTracking()
            .Where(file => askedOfFiles.Contains(file.RecordingId) || named.Contains(file.Id))
            .Select(file => new { file.Id, file.RecordingId, file.Path, file.SizeBytes, file.Quality, file.Measurement, file.Integrity })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var best = files
            .Where(file => file.RecordingId is { } recording && asked.Contains(recording))
            .GroupBy(file => file.RecordingId!.Value)
            .ToDictionary(
                group => group.Key,
                group => PlaylistFile.Best(group.Select(file =>
                    new PlaylistCopy(file.Id, file.Path, file.SizeBytes, file.Quality, file.Measurement, file.Integrity)))!);

        var byId = files.ToDictionary(file => file.Id);

        var missingSongs = asked.Where(id => !best.ContainsKey(id)).ToList();
        var missingFiles = named.Where(id => !byId.ContainsKey(id)).ToList();

        if (missingSongs.Count > 0 || missingFiles.Count > 0)
        {
            return ([], TypedResults.Problem(
                title: "Not in the library",
                detail: $"{missingSongs.Count + missingFiles.Count} of those are not a song or a file the library holds: "
                    + string.Join(", ", missingSongs.Select(id => id.Value).Concat(missingFiles.Select(id => id.Value)).Take(5)) + ".",
                statusCode: StatusCodes.Status400BadRequest));
        }

        List<RecordingId> recordings = [.. asked.Concat(named.Select(id => byId[id].RecordingId).OfType<RecordingId>()).Distinct()];

        var facts = await db.Recordings
            .AsNoTracking()
            .Where(recording => recordings.Contains(recording.Id))
            .Select(recording => new { recording.Id, recording.Title, recording.Duration })
            .ToDictionaryAsync(recording => recording.Id, cancellationToken)
            .ConfigureAwait(false);

        var billing = await PlaylistFiles.BillingAsync(db, recordings, cancellationToken).ConfigureAwait(false);

        PlaylistLine Line(PlaylistCopy copy, RecordingId? recording) =>
            recording is { } known && facts.TryGetValue(known, out var song)
                ? new PlaylistLine(copy.Path, copy.Quality?.Duration ?? song.Duration, billing.GetValueOrDefault(known), song.Title)
                : new PlaylistLine(copy.Path, copy.Quality?.Duration, null, Path.GetFileNameWithoutExtension(copy.Path));

        List<PlaylistLine> lines =
        [
            .. recordingIds.Select(id => new RecordingId(id)).Select(id => Line(best[id], id)),
            .. fileIds.Select(id => byId[new MediaFileId(id)]).Select(file => Line(
                new PlaylistCopy(file.Id, file.Path, file.SizeBytes, file.Quality, file.Measurement, file.Integrity),
                file.RecordingId)),
        ];

        return (lines, null);
    }

    /// <summary>
    /// The static playlist a file name in the route names, or null: not a
    /// plain name of an m3u, or the file of a smart playlist, which has a
    /// route of its own.
    /// </summary>
    private static async Task<string?> StaticPathAsync(FonotecaDbContext db, string? file, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(file)
            || file.StartsWith('.')
            || file.AsSpan().IndexOfAny('/', '\\') >= 0
            || !PlaylistFile.Extensions.Contains(Path.GetExtension(file)))
        {
            return null;
        }

        var path = $"{PlaylistFile.Folder}/{file}";

        var smart = await db.SmartPlaylists
            .AsNoTracking()
            .Select(playlist => playlist.Path)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return smart.Contains(path, StringComparer.OrdinalIgnoreCase) ? null : path;
    }

    /// <summary>
    /// A path another playlist has: any m3u in the folder, or a smart
    /// playlist's file not written yet.
    /// </summary>
    /// <remarks>
    /// Compared without case, because the production share is read by a Mac,
    /// where two names differing only in case are one file.
    /// </remarks>
    /// <param name="self">The playlist being renamed, whose own path is no clash.</param>
    private static async Task<ProblemHttpResult?> TakenAsync(
        FonotecaDbContext db,
        PlaylistFiles files,
        string path,
        string? self,
        CancellationToken cancellationToken)
    {
        var smart = await db.SmartPlaylists
            .AsNoTracking()
            .Select(playlist => playlist.Path)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var taken = smart.Concat(files.AllPaths())
            .Any(other => string.Equals(other, path, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(other, self, StringComparison.OrdinalIgnoreCase));

        return taken
            ? TypedResults.Problem(
                title: "A playlist has that name",
                detail: $"'{path}' is already a playlist.",
                statusCode: StatusCodes.Status409Conflict)
            : null;
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

    /// <summary>A playlist's file into a stamped folder under the trash; a problem where it would not go.</summary>
    private static ProblemHttpResult? Trash(PlaylistFiles files, FonotecaOptions options, IClock clock, ILogger logger, string path)
    {
        try
        {
            var destination = Path.Combine(
                FileManagerService.TrashRoot(options),
                clock.UtcNow.ToString("yyyy-MM-dd HHmmss.fff", CultureInfo.InvariantCulture),
                path.Replace('/', Path.DirectorySeparatorChar));

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Move(files.AbsolutePathFor(path), destination);

            Log.PlaylistTrashed(logger, path, destination);
            return null;
        }
        catch (Exception cause) when (cause is IOException or UnauthorizedAccessException)
        {
            return TypedResults.Problem(
                title: "The playlist's file would not move",
                detail: $"'{path}' could not be moved to the trash, so the playlist was kept: {cause.Message}",
                statusCode: StatusCodes.Status409Conflict);
        }
    }

    /// <summary>The name a file gives itself, else its file name.</summary>
    private static string NameOf(PlaylistText text) =>
        PlaylistFile.NameIn(text.Text) ?? Path.GetFileNameWithoutExtension(text.Path);

    private static string FileName(string path) => path[(path.LastIndexOf('/') + 1)..];

    private static ProblemHttpResult? NameProblem(string? name) =>
        string.IsNullOrWhiteSpace(name)
            ? TypedResults.Problem(
                title: "A playlist needs a name",
                detail: "`name` is empty.",
                statusCode: StatusCodes.Status400BadRequest)
            : name.Trim().Length > SmartPlaylist.NameLength
                ? TypedResults.Problem(
                    title: "That name is too long",
                    detail: $"A playlist's name is at most {SmartPlaylist.NameLength} characters.",
                    statusCode: StatusCodes.Status400BadRequest)
                : null;

    private static ProblemHttpResult? RulesProblem(SmartCriteria? criteria) =>
        SmartRules.Problems(criteria) is { Count: > 0 } problems
            ? TypedResults.Problem(
                title: "Those rules cannot be asked",
                detail: string.Join(" ", problems),
                statusCode: StatusCodes.Status400BadRequest)
            : null;

    private static ProblemHttpResult NoSuchPlaylist(string id) =>
        TypedResults.Problem(
            title: "No such playlist",
            detail: $"There is no playlist '{id}'.",
            statusCode: StatusCodes.Status404NotFound);

    private static ProblemHttpResult Unmounted() =>
        TypedResults.Problem(
            title: "The library is not mounted",
            detail: "Its root holds nothing but the Playlists folder, so nothing is written there.",
            statusCode: StatusCodes.Status409Conflict);

    private static ProblemHttpResult NotWritten(string path) =>
        TypedResults.Problem(
            title: "The playlist's file could not be written",
            detail: $"'{path}' could not be written; the log says why.",
            statusCode: StatusCodes.Status409Conflict);

    private static ProblemHttpResult NotUtf8(string path) =>
        TypedResults.Problem(
            title: "That file is not UTF-8",
            detail: $"'{path}' is read as Latin-1 and never written, so a path in it is not garbled for another player.",
            statusCode: StatusCodes.Status409Conflict);

    private static ProblemHttpResult Changed(string path) =>
        TypedResults.Problem(
            title: "The file changed",
            detail: $"'{path}' has changed since it was read, so nothing was changed. Read it again and retry.",
            statusCode: StatusCodes.Status409Conflict);

    /// <summary>How long the entries the library holds play for; null where none has a length.</summary>
    private static TimeSpan? Total(IReadOnlyCollection<ResolvedEntry> entries)
    {
        var lengths = entries
            .Where(entry => entry.Copy is not null)
            .Select(entry => entry.Copy!.Quality?.Duration ?? entry.Item.Duration)
            .OfType<TimeSpan>()
            .ToList();

        return lengths.Count == 0 ? null : lengths.Aggregate(TimeSpan.Zero, (sum, length) => sum + length);
    }
}

[JsonConverter(typeof(JsonStringEnumConverter<PlaylistKind>))]
public enum PlaylistKind
{
    /// <summary>An m3u that is the whole of the playlist.</summary>
    Static = 0,

    /// <summary>Rules the library keeps an m3u of.</summary>
    Smart = 1,
}

public sealed record PlaylistListResponse(IReadOnlyList<PlaylistSummary> Items);

/// <param name="Id">A static playlist's file name, or a smart playlist's id.</param>
/// <param name="File">The m3u, library-relative.</param>
/// <param name="Songs">Every entry in the file.</param>
/// <param name="Held">The entries naming a file the catalogue holds.</param>
/// <param name="Duration">Of the held entries.</param>
/// <param name="Seconds">The same, in whole seconds, as Subsonic counts it.</param>
/// <param name="Albums">Up to four albums from the top of the list, for its cover.</param>
public sealed record PlaylistSummary(
    string Id,
    PlaylistKind Kind,
    string Name,
    string File,
    int Songs,
    int Held,
    string? Duration,
    int Seconds,
    DateTimeOffset ChangedUtc,
    IReadOnlyList<TrackListAlbum> Albums);

/// <param name="Id">A static playlist's file name, or a smart playlist's id.</param>
/// <param name="File">The m3u, library-relative.</param>
/// <param name="Version">What an edit to a static playlist's entries names, to say which text it was made against.</param>
/// <param name="Editable">Whether entries can be changed here: a static playlist whose file is UTF-8.</param>
/// <param name="Duration">Of the held entries.</param>
/// <param name="Criteria">A smart playlist's rules.</param>
public sealed record PlaylistResponse(
    string Id,
    PlaylistKind Kind,
    string Name,
    string File,
    string Version,
    bool Editable,
    string? Duration,
    DateTimeOffset ChangedUtc,
    SmartCriteria? Criteria,
    IReadOnlyList<PlaylistEntryRow> Entries);

/// <param name="Index">The entry's place in the file, from 0: what reordering and removing name.</param>
/// <param name="Title">The song's title, or what the file calls the entry.</param>
/// <param name="Location">The entry's path line as the file writes it.</param>
/// <param name="FileId">The media file the entry names; null where it names none the catalogue holds.</param>
/// <param name="Track">That file as the Tracks page shows its song.</param>
public sealed record PlaylistEntryRow(int Index, string Title, string? Artist, string Location, Guid? FileId, TrackListRow? Track);

/// <param name="RecordingIds">Songs to start with, in order, each as the best copy held.</param>
/// <param name="FileIds">Files to write as themselves, after the songs.</param>
public sealed record CreatePlaylistRequest(string? Name, IReadOnlyList<Guid>? RecordingIds, IReadOnlyList<Guid>? FileIds);

/// <param name="Name">A new name; omitted keeps the old one.</param>
/// <param name="Entries">The new order as entry indexes, leaving out those to remove; omitted keeps the order.</param>
/// <param name="Version">The <c>version</c> the entries came with; required with <paramref name="Entries"/>.</param>
public sealed record UpdatePlaylistRequest(string? Name, IReadOnlyList<int>? Entries, string? Version);

/// <param name="RecordingIds">Songs, each as the best copy held.</param>
/// <param name="FileIds">Files, as themselves, after the songs.</param>
public sealed record AddPlaylistSongsRequest(IReadOnlyList<Guid>? RecordingIds, IReadOnlyList<Guid>? FileIds);

/// <param name="Name">Required to create; omitted keeps the old one.</param>
/// <param name="Criteria">Required to create; omitted keeps the old rules.</param>
public sealed record SmartPlaylistRequest(string? Name, SmartCriteria? Criteria);

public sealed record SmartPreviewRequest(SmartCriteria? Criteria);

/// <param name="Songs">Every song the rules hold.</param>
/// <param name="Duration">Of every song.</param>
/// <param name="Entries">The first of them, in the playlist's order.</param>
public sealed record SmartPreviewResponse(int Songs, string? Duration, IReadOnlyList<PlaylistEntryRow> Entries);
