using Fonoteca.Data;
using Fonoteca.Domain.Catalogue;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Fonoteca.Api.Subsonic;

/// <summary>
/// A listener's own tags on songs: Fonoteca's <c>fonotecaTags</c> extension,
/// kept in <see cref="Tag"/> and <see cref="SongTag"/>.
/// </summary>
/// <remarks>
/// <b>Every tag is the signed-in account's</b> and nobody else sees it, an
/// admin neither (the owner's choice): the context's query filter scopes each
/// read to <c>db.AccountId</c>, so another account's tag is not found.
///
/// <b>A tag is a label and a parent at once</b>, nested at any depth. Its
/// songs are its own and every tag's below it, each once; a song's
/// <c>tagIds</c> are only the tags placed on it. Sibling names are unique
/// ignoring case, and tags are listed by name.
///
/// <b>A song is tagged on its recording</b>, as it is loved (the owner's
/// choice): every copy shows the tag, a copy tagged before it was identified
/// carries its song's tag once it is, and taking a tag off a song takes it
/// off every copy. A tag lists each song once, as the copy a playlist plays
/// (<see cref="PlaylistFile.Best"/>), as <c>getStarred2</c> lists hearts.
/// Albums and artists are never tagged.
///
/// <b>Every id is checked before anything is written</b>, so a refusal
/// changes nothing; adding a tag a song has, or taking off one it has not,
/// is no error.
/// </remarks>
public static partial class SubsonicEndpoints
{
    private static async Task<IResult> GetTags(FonotecaDbContext db, CancellationToken cancellationToken)
    {
        var tags = await db.Tags.AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false);
        var tagged = await TaggedAsync(db, cancellationToken).ConfigureAwait(false);

        return SubsonicResult.Ok("tags", list => list.Children(
            "tag",
            ByName(tags),
            (writer, tag) => WriteTag(writer, tag, SongsUnder(tags, tagged, tag.Id).Count)));
    }

    /// <summary>A tag, with its songs and every tag's below it.</summary>
    private static async Task<IResult> GetTag(HttpContext http, FonotecaDbContext db, CancellationToken cancellationToken)
    {
        if (Missing(http, "id") is { } missing) return missing;

        var tags = await db.Tags.AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false);

        if (Find(tags, Text(http, "id")) is not { } tag) return NoSuchTag();

        var songs = SongsUnder(tags, await TaggedAsync(db, cancellationToken).ConfigureAwait(false), tag.Id);

        List<MediaFileId> copies =
        [
            .. await BestCopiesAsync(db, [.. songs.Select(song => song.Recording).OfType<RecordingId>()], cancellationToken).ConfigureAwait(false),
            .. songs.Select(song => song.File).OfType<MediaFileId>(),
        ];

        var listed = await SongsAsync(db, db.MediaFiles.AsNoTracking().Where(file => copies.Contains(file.Id)), cancellationToken)
            .ConfigureAwait(false);

        return SubsonicResult.Ok("tag", root =>
        {
            WriteTag(root, tag, songs.Count);
            root.Children("song", listed, WriteSong);
        });
    }

    private static async Task<IResult> CreateTag(HttpContext http, FonotecaDbContext db, CancellationToken cancellationToken)
    {
        if (NameSent(http) is not { } name) return SubsonicResult.Error(10, "Required parameter is missing: name.");
        if (name.Length > Tag.NameLength) return TagNameTooLong();

        var tags = await db.Tags.AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false);
        Guid? parent = null;

        if (Text(http, "parentId") is { } parentId)
        {
            if (Find(tags, parentId) is not { } found) return NoSuchTag();

            parent = found.Id;
        }

        if (Taken(tags, parent, name, except: null)) return TagNameTaken(name);

        var tag = new Tag { Id = Guid.CreateVersion7(), AccountId = db.AccountId!.Value, Name = name, ParentId = parent };
        db.Tags.Add(tag);

        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException cause) when (cause.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // The same name made at once, by a double tap.
            return TagNameTaken(name);
        }

        return SubsonicResult.Ok("tag", writer => WriteTag(writer, tag, 0));
    }

    /// <summary>A rename, a move, and songs taken off and put on: removes first, as <c>updatePlaylist</c> has it.</summary>
    private static async Task<IResult> UpdateTag(HttpContext http, FonotecaDbContext db, CancellationToken cancellationToken)
    {
        if (Missing(http, "tagId") is { } missing) return missing;

        var name = NameSent(http);
        var moving = http.Request.Query.ContainsKey("parentId");
        List<string> adding = [.. http.Request.Query["songIdToAdd"].Where(id => !string.IsNullOrWhiteSpace(id)).Select(id => id!.Trim())];
        List<string> removing = [.. http.Request.Query["songIdToRemove"].Where(id => !string.IsNullOrWhiteSpace(id)).Select(id => id!.Trim())];

        if (name is null && !moving && adding.Count == 0 && removing.Count == 0)
        {
            return SubsonicResult.Error(10, "Nothing to change: send name, parentId, songIdToAdd or songIdToRemove.");
        }

        if (name is { Length: > Tag.NameLength }) return TagNameTooLong();

        // A double tap adds the same song twice at once: the one that loses
        // is asked again once, and finds the song tagged.
        for (var attempt = 0; ; attempt++)
        {
            var tags = await db.Tags.ToListAsync(cancellationToken).ConfigureAwait(false);

            if (Find(tags, Text(http, "tagId")) is not { } tag) return NoSuchTag();

            var parent = tag.ParentId;

            if (moving)
            {
                parent = null;

                if (Text(http, "parentId") is { } parentId)
                {
                    if (Find(tags, parentId) is not { } found) return NoSuchTag();
                    if (Below(tags, row => row.Id, row => row.ParentId, tag.Id).Contains(found.Id))
                    {
                        return SubsonicResult.Error(0, "A tag cannot go below itself.");
                    }

                    parent = found.Id;
                }
            }

            if ((name is not null || moving) && Taken(tags, parent, name ?? tag.Name, except: tag.Id)) return TagNameTaken(name ?? tag.Name);

            if (await SongsOfAsync(db, adding, cancellationToken).ConfigureAwait(false) is not { } added
                || await SongsOfAsync(db, removing, cancellationToken).ConfigureAwait(false) is not { } removed)
            {
                return SubsonicResult.Error(70, "No such song.");
            }

            tag.Name = name ?? tag.Name;
            tag.ParentId = parent;

            // A song both taken off and put on ends with the tag.
            var on = added.Select(song => song.Key).ToHashSet();
            var off = removed.Where(song => !on.Contains(song.Key)).ToList();
            List<RecordingId> offRecordings = [.. off.Select(song => song.Recording).OfType<RecordingId>()];
            List<MediaFileId> offFiles = [.. off.Where(song => song.Recording is null).Select(song => song.File)];

            db.SongTags.RemoveRange(await db.SongTags
                .Where(placed => placed.TagId == tag.Id
                    && ((placed.RecordingId != null && offRecordings.Contains(placed.RecordingId.Value))
                        || (placed.MediaFileId != null && offFiles.Contains(placed.MediaFileId.Value))
                        || db.MediaFiles.Any(file => file.Id == placed.MediaFileId
                            && file.RecordingId != null
                            && offRecordings.Contains(file.RecordingId.Value))))
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false));

            List<RecordingId> onRecordings = [.. on.Select(song => song.Recording).OfType<RecordingId>()];
            List<MediaFileId> onFiles = [.. on.Select(song => song.File).OfType<MediaFileId>()];

            var already = (await db.SongTags
                    .Where(placed => placed.TagId == tag.Id
                        && ((placed.RecordingId != null && onRecordings.Contains(placed.RecordingId.Value))
                            || (placed.MediaFileId != null && onFiles.Contains(placed.MediaFileId.Value))))
                    .Select(placed => new { placed.RecordingId, placed.MediaFileId })
                    .ToListAsync(cancellationToken)
                    .ConfigureAwait(false))
                .Select(placed => new TaggedSong(placed.RecordingId, placed.RecordingId is null ? placed.MediaFileId : null))
                .ToHashSet();

            foreach (var song in on.Where(song => !already.Contains(song)))
            {
                db.SongTags.Add(new SongTag { Id = Guid.CreateVersion7(), TagId = tag.Id, RecordingId = song.Recording, MediaFileId = song.File });
            }

            try
            {
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

                return SubsonicResult.Ok();
            }
            catch (DbUpdateException cause) when (cause.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } unique)
            {
                if (unique.TableName == "Tags") return TagNameTaken(name ?? tag.Name);
                if (attempt > 0) throw;

                db.ChangeTracker.Clear();
            }
        }
    }

    /// <summary>The tag and every tag below it; the songs stay.</summary>
    private static async Task<IResult> DeleteTag(HttpContext http, FonotecaDbContext db, CancellationToken cancellationToken)
    {
        if (Missing(http, "tagId") is { } missing) return missing;

        // The tags below go by the database's cascade, and every song's place on them.
        var deleted = SubsonicIds.AsTag(Text(http, "tagId")) is { } id
            ? await db.Tags.Where(tag => tag.Id == id).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false)
            : 0;

        return deleted == 0 ? NoSuchTag() : SubsonicResult.Ok();
    }

    /// <summary>A song as a tag holds it: its recording, or the file while it has none.</summary>
    private readonly record struct TaggedSong(RecordingId? Recording, MediaFileId? File);

    /// <summary>A song a request names, by its file and its recording.</summary>
    private sealed record TagTarget(MediaFileId File, RecordingId? Recording)
    {
        public TaggedSong Key => Recording is { } recording ? new TaggedSong(recording, null) : new TaggedSong(null, File);
    }

    /// <summary>Each song id's file; null where any names no file the catalogue holds.</summary>
    private static async Task<List<TagTarget>?> SongsOfAsync(FonotecaDbContext db, List<string> ids, CancellationToken cancellationToken)
    {
        var files = new List<MediaFileId>();

        foreach (var id in ids)
        {
            if (SubsonicIds.AsSong(id) is not { } file) return null;

            files.Add(file);
        }

        var held = await db.MediaFiles
            .AsNoTracking()
            .Where(file => files.Contains(file.Id))
            .Select(file => new TagTarget(file.Id, file.RecordingId))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return held.Count == files.Distinct().Count() ? held : null;
    }

    /// <summary>Each of the account's tags, by id, with the songs on it the library holds.</summary>
    private static async Task<ILookup<Guid, TaggedSong>> TaggedAsync(FonotecaDbContext db, CancellationToken cancellationToken)
    {
        var onRecordings = await db.SongTags
            .AsNoTracking()
            .Where(placed => placed.RecordingId != null && db.MediaFiles.Any(file => file.RecordingId == placed.RecordingId))
            .Select(placed => new { placed.TagId, placed.RecordingId })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var onFiles = await db.SongTags
            .AsNoTracking()
            .Where(placed => placed.MediaFileId != null)
            .SelectMany(placed => db.MediaFiles.Where(file => file.Id == placed.MediaFileId), (placed, file) => new { placed.TagId, file.RecordingId, file.Id })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return onRecordings
            .Select(placed => (placed.TagId, Song: new TaggedSong(placed.RecordingId, null)))
            .Concat(onFiles.Select(placed => (placed.TagId, Song: new TagTarget(placed.Id, placed.RecordingId).Key)))
            .ToLookup(placed => placed.TagId, placed => placed.Song);
    }

    /// <summary>The songs on a tag and on every tag below it, each once.</summary>
    private static HashSet<TaggedSong> SongsUnder(List<Tag> tags, ILookup<Guid, TaggedSong> tagged, Guid id) =>
        [.. Below(tags, tag => tag.Id, tag => tag.ParentId, id).SelectMany(below => tagged[below])];

    /// <summary>
    /// The account's tags on each of these songs: on its recording or on any
    /// copy tagged before it was identified, so every copy agrees; on the file
    /// alone while it has no recording.
    /// </summary>
    private static async Task<(ILookup<RecordingId, Guid> Songs, ILookup<MediaFileId, Guid> Files)> SongTagsAsync(
        FonotecaDbContext db,
        List<RecordingId> recordings,
        List<MediaFileId> files,
        CancellationToken cancellationToken)
    {
        var onRecordings = await db.SongTags
            .AsNoTracking()
            .Where(placed => placed.RecordingId != null && recordings.Contains(placed.RecordingId.Value))
            .Select(placed => new { Recording = placed.RecordingId!.Value, placed.TagId })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var onCopies = await db.MediaFiles
            .AsNoTracking()
            .Where(file => file.RecordingId != null && recordings.Contains(file.RecordingId.Value))
            .SelectMany(file => db.SongTags.Where(placed => placed.MediaFileId == file.Id), (file, placed) => new { Recording = file.RecordingId!.Value, placed.TagId })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var onFiles = await db.SongTags
            .AsNoTracking()
            .Where(placed => placed.MediaFileId != null && files.Contains(placed.MediaFileId.Value))
            .Select(placed => new { File = placed.MediaFileId!.Value, placed.TagId })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return (
            onRecordings.Concat(onCopies).Distinct().ToLookup(placed => placed.Recording, placed => placed.TagId),
            onFiles.ToLookup(placed => placed.File, placed => placed.TagId));
    }

    /// <summary><paramref name="root"/> and everything below it, in a tree kept as parent ids.</summary>
    private static HashSet<Guid> Below<T>(IEnumerable<T> items, Func<T, Guid> id, Func<T, Guid?> parent, Guid root)
    {
        var children = items.ToLookup(parent, id);
        var below = new HashSet<Guid>();
        var open = new Stack<Guid>([root]);

        while (open.TryPop(out var next))
        {
            if (!below.Add(next)) continue;

            foreach (var child in children[next]) open.Push(child);
        }

        return below;
    }

    private static void WriteTag(ISubsonicWriter writer, Tag tag, int songCount)
    {
        writer.Attr("id", SubsonicIds.Tag(tag.Id));
        writer.Attr("name", tag.Name);
        writer.Attr("parentId", tag.ParentId is { } parent ? SubsonicIds.Tag(parent) : null);
        writer.Attr("songCount", songCount);
    }

    private static IEnumerable<Tag> ByName(List<Tag> tags) =>
        tags.OrderBy(tag => tag.Name, StringComparer.OrdinalIgnoreCase).ThenBy(tag => tag.Name, StringComparer.Ordinal);

    private static Tag? Find(List<Tag> tags, string? id) =>
        SubsonicIds.AsTag(id) is { } wanted ? tags.FirstOrDefault(tag => tag.Id == wanted) : null;

    private static bool Taken(List<Tag> tags, Guid? parent, string name, Guid? except) =>
        tags.Any(tag => tag.ParentId == parent && tag.Id != except && string.Equals(tag.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>The name sent, trimmed; null where none was, or only blanks.</summary>
    private static string? NameSent(HttpContext http) => Text(http, "name")?.Trim() is { Length: > 0 } name ? name : null;

    private static SubsonicResult NoSuchTag() => SubsonicResult.Error(70, "No such tag.");

    private static SubsonicResult TagNameTaken(string name) =>
        SubsonicResult.Error(0, $"A tag beside it is already called {name}.");

    private static SubsonicResult TagNameTooLong() =>
        SubsonicResult.Error(0, $"A tag's name is at most {Tag.NameLength} characters.");
}
