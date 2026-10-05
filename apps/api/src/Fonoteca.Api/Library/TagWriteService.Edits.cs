using System.Text.Json;
using System.Text.Json.Serialization;
using Fonoteca.Data;
using Fonoteca.Domain.Abstractions;
using Fonoteca.Domain.Catalogue;
using Fonoteca.Domain.Events;
using Fonoteca.Tagging;
using Microsoft.EntityFrameworkCore;

namespace Fonoteca.Api.Library;

/// <summary>
/// A person's tags: what one album folder's files carry and would carry, and saving a change to them.
/// </summary>
/// <remarks>
/// <b>Saving writes the folder at once</b>, through the tag write itself
/// narrowed to the folder — so a person's values meet the catalogue's in the
/// one place that already decides which wins, the folder is renamed by the
/// same rule, and one journal correlation holds the save, which is what one
/// press of Undo reverses. The corrections are stored first: a write that
/// fails leaves them to be written by the next one.
///
/// <b>Where the folder is one album</b>, its title and its artist are
/// corrections to the album (<see cref="PersonTags.AlbumFields"/>), kept on the
/// album like the album page's own, and its year is set on every file of the
/// folder — kept with them whatever pressing they are filed under later, and
/// leaving the album's other folders theirs (the owner's choice). Elsewhere
/// every field is the file's.
/// </remarks>
public sealed partial class TagWriteService
{
    /// <summary>The kind saving a folder's tags takes the gate as.</summary>
    public const string EditKind = "library.tags.edit";

    /// <summary>One per file of a saved folder: its tags as a person set them, before and after.</summary>
    private const string PersonSavedEvent = "tagging.person.saved";

    /// <summary>A save's corrections to the album, before and after.</summary>
    private const string AlbumSavedEvent = "tagging.person.album";

    /// <summary>What an album folder's files carry, what the catalogue would write, and what a person set.</summary>
    public async Task<FolderTags?> FolderTagsAsync(string folder, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(folder);

        var dbScope = scopeFactory.CreateAsyncScope();
        await using (dbScope.ConfigureAwait(false))
        {
            var provider = dbScope.ServiceProvider;
            var db = provider.GetRequiredService<FonotecaDbContext>();
            var reader = provider.GetRequiredService<TagReader>();

            if (await AlbumFolderRowsAsync(db, folder, cancellationToken).ConfigureAwait(false) is not { } rows) return null;

            var prefix = folder + "/";

            var placed = await db.MediaFiles
                .AsNoTracking()
                .Where(file => file.Path.StartsWith(prefix))
                .Select(file => new { file.Id, file.ReleaseGroupId, file.TagEditsJson })
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            var catalogue = (await ProjectAsync(db, Writable(db.MediaFiles.AsNoTracking()).Where(file => file.Path.StartsWith(prefix)), cancellationToken)
                    .ConfigureAwait(false))
                .ToDictionary(file => file.Id);

            var albumWide = placed.Select(file => file.ReleaseGroupId).Distinct().ToList() is [{ }];
            var readings = new Dictionary<MediaFileId, TagSnapshot?>();

            foreach (var row in rows)
            {
                try
                {
                    readings[row.Id] = await reader.ReadAsync(new LibraryPath(row.Path), null, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception cause) when (cause is TagReadFailedException or IOException or UnauthorizedAccessException)
                {
                    // Shown as unreadable: the file says nothing the editor can show.
                    readings[row.Id] = null;
                }
            }

            // The editor's own fields, then every field a person named here or a
            // file of the folder already carries — by its plain name, an MP4
            // freeform atom's prefix taken off as the readers take it off.
            var fields = PersonTags.Fields
                .Concat(placed
                    .SelectMany(file => PersonEdits.Read(file.TagEditsJson).Keys)
                    .Concat(readings.Values.OfType<TagSnapshot>()
                        .SelectMany(reading => reading.Fields.Keys)
                        .Select(key => key[(key.LastIndexOf(':') + 1)..])
                        .Where(CatalogueTagFields.IsCustom))
                    .Where(PersonTags.IsCustom)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Order(StringComparer.OrdinalIgnoreCase))
                .ToList();

            var files = new List<FileTags>();

            foreach (var row in rows.OrderBy(row => row.Path, StringComparer.Ordinal))
            {
                var path = new LibraryPath(row.Path);
                var person = PersonEdits.Read(placed.First(file => file.Id == row.Id).TagEditsJson);
                var wanted = catalogue.TryGetValue(row.Id, out var pending)
                    ? CatalogueTags.For((pending with { TagEditsJson = null }).Describe())
                    : new Dictionary<string, string>();

                var reading = readings[row.Id];

                files.Add(new FileTags(
                    row.Id.Value,
                    row.Path,
                    reading is not null,
                    [.. fields.Select(field =>
                    {
                        var spelled = CatalogueTagFields.Spell(path, field) ?? CatalogueTagFields.Custom(path, field);
                        var now = spelled is null ? null : reading?.Find(spelled);
                        var stated = wanted.GetValueOrDefault(field);
                        var mine = person.TryGetValue(field, out var set);

                        return new TagCell(field, mine ? set : stated ?? now, stated, now, mine);
                    })]));
            }

            List<TagCell> album = [];

            if (albumWide && catalogue.Values.FirstOrDefault() is { } first)
            {
                var edited = CatalogueTags.For((first with { TagEditsJson = null }).Describe());
                var stated = CatalogueTags.For((first with { TagEditsJson = null, AlbumEditsJson = null, PressingEditsJson = null }).Describe());
                var corrected = PersonEdits.Combine(first.AlbumEditsJson, first.PressingEditsJson);

                // The folder's typed year is on its files, the same on each.
                var years = placed.Select(file => PersonEdits.Read(file.TagEditsJson)).ToList();
                var typed = years.All(edits => edits.ContainsKey(CatalogueTags.Year))
                    && years.Select(edits => edits[CatalogueTags.Year]).Distinct().ToList() is [{ } year]
                        ? year
                        : null;

                album = [.. PersonTags.AlbumFields.Select(field => field == CatalogueTags.Year && typed is not null
                    ? new TagCell(field, typed, stated.GetValueOrDefault(field), null, true)
                    : new TagCell(
                        field,
                        edited.GetValueOrDefault(field),
                        stated.GetValueOrDefault(field),
                        null,
                        corrected.ContainsKey(AlbumKey(field, first.ReleaseId is not null))))];
            }

            return new FolderTags(
                folder,
                writerOptions.AllowFileMutation,
                albumWide ? placed[0].ReleaseGroupId!.Value.Value : null,
                albumWide,
                fields,
                album,
                files);
        }
    }

    /// <summary>Stores a person's changes to an album folder's tags and writes the folder.</summary>
    public async Task<TagEditResult> SaveTagsAsync(
        string folder,
        IReadOnlyList<TagEdit> changes,
        ICallerContext caller,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(folder);
        ArgumentNullException.ThrowIfNull(changes);
        ArgumentNullException.ThrowIfNull(caller);

        if (!writerOptions.AllowFileMutation) return TagEditResult.Refused(TagEditStatus.MutationOff, folder);

        if (!gate.TryEnter(EditKind, out var lease)) return TagEditResult.Refused(TagEditStatus.Busy, folder);

        using var held = lease;
        cancellationToken.ThrowIfCancellationRequested();

        // Begun, it finishes: the corrections and the write are one save.
        var token = CancellationToken.None;

        var dbScope = scopeFactory.CreateAsyncScope();
        await using (dbScope.ConfigureAwait(false))
        {
            var provider = dbScope.ServiceProvider;
            var db = provider.GetRequiredService<FonotecaDbContext>();
            var events = provider.GetRequiredService<IEventLog>();

            if (await AlbumFolderRowsAsync(db, folder, token).ConfigureAwait(false) is not { } rows)
            {
                return TagEditResult.Refused(TagEditStatus.NotAnAlbumFolder, folder);
            }

            if (!Directory.Exists(Path.Combine(store.Root, folder))) return TagEditResult.Refused(TagEditStatus.NotOnDisk, folder);

            var ids = rows.Select(row => row.Id).ToList();
            var files = await db.MediaFiles.Where(file => ids.Contains(file.Id)).ToListAsync(token).ConfigureAwait(false);

            var album = files.Select(file => file.ReleaseGroupId).Distinct().ToList() is [{ } group]
                ? await db.ReleaseGroups.FirstOrDefaultAsync(candidate => candidate.Id == group, token).ConfigureAwait(false)
                : null;

            var problems = changes.Select(change => Invalid(change, files, album)).OfType<string>().ToList();

            if (problems.Count > 0) return TagEditResult.Refused(TagEditStatus.Invalid, folder) with { Problems = problems };

            var run = Guid.CreateVersion7().ToString("N")[..12];
            var before = files.ToDictionary(file => file.Id, file => file.TagEditsJson);
            var albumBefore = album?.EditsJson;

            foreach (var change in changes)
            {
                if (change.File is { } id)
                {
                    var file = files.First(file => file.Id.Value == id);
                    file.TagEditsJson = Changed(file.TagEditsJson, change.Field, change);

                    // ATL writes no total without its number, so a number taken
                    // out takes its total with it, or the write is refused.
                    if (change is { Value: null, Reset: false } && TotalOf(change.Field) is { } total)
                    {
                        file.TagEditsJson = Changed(file.TagEditsJson, total, change);
                    }
                }
                else if (change.Field == CatalogueTags.Year)
                {
                    // The folder's year is its files', so it stays with them
                    // whatever pressing they are filed under later.
                    foreach (var file in files) file.TagEditsJson = Changed(file.TagEditsJson, change.Field, change);
                }
                else if (album is not null)
                {
                    album.EditsJson = Changed(album.EditsJson, AlbumKey(change.Field, pressed: false), change);
                }
            }

            var now = clock.UtcNow;

            // Every file of the folder, changed or not: an album's correction is
            // one, and this is how Undo finds the save from the folder it was made in.
            foreach (var file in files)
            {
                await events.AppendAsync(
                        DomainEvent.Create(
                            PersonSavedEvent,
                            TagWriter.FileSubject,
                            file.Id.ToString(),
                            caller.ActorId,
                            now,
                            JsonSerializer.Serialize(new SavedPayload(before[file.Id], file.TagEditsJson)),
                            run),
                        token)
                    .ConfigureAwait(false);
            }

            if (album is not null && album.EditsJson != albumBefore)
            {
                await events.AppendAsync(
                        DomainEvent.Create(
                            AlbumSavedEvent,
                            "album",
                            album.Id.ToString(),
                            caller.ActorId,
                            now,
                            JsonSerializer.Serialize(new AlbumSavedPayload(album.Id.Value, albumBefore, album.EditsJson)),
                            run),
                        token)
                    .ConfigureAwait(false);
            }

            await db.SaveChangesAsync(token).ConfigureAwait(false);

            var jobId = Guid.CreateVersion7().ToString("N")[..12];

            TagWriteSummary summary;

            try
            {
                summary = await RunAsync(jobId, TagWriteScope.ForFolder(folder), token, caller.ActorId, run).ConfigureAwait(false);
            }
            finally
            {
                _progress = null;
            }

            var here = await db.MediaFiles
                .AsNoTracking()
                .Where(file => ids.Contains(file.Id))
                .Select(file => file.Path)
                .FirstOrDefaultAsync(token)
                .ConfigureAwait(false);

            return new TagEditResult(
                TagEditStatus.Saved,
                here is null ? folder : AlbumFolder.Of(here),
                summary.Written,
                summary.Unchanged,
                summary.Refused + summary.Failed + summary.Unsupported,
                summary.Renamed,
                []);
        }
    }

    /// <summary>Puts back the corrections a save made, as an undo of it.</summary>
    private static async Task RestoreCorrectionsAsync(FonotecaDbContext db, List<UndoEntry> entries, Undo undo)
    {
        var ids = undo.Rows.Select(row => row.Id).ToList();
        var files = await db.MediaFiles.Where(file => ids.Contains(file.Id)).ToListAsync().ConfigureAwait(false);

        foreach (var entry in entries.Where(entry => entry.Type == PersonSavedEvent))
        {
            if (files.FirstOrDefault(file => file.Id.ToString() == entry.Subject) is not { } file) continue;

            if (JsonSerializer.Deserialize<SavedPayload>(entry.Payload) is { } saved)
            {
                file.TagEditsJson = Restored(file.TagEditsJson, saved.Before, saved.After);
            }
        }

        foreach (var entry in entries.Where(entry => entry.Type == AlbumSavedEvent))
        {
            if (JsonSerializer.Deserialize<AlbumSavedPayload>(entry.Payload) is not { } saved) continue;

            var group = new ReleaseGroupId(saved.Album);

            if (await db.ReleaseGroups.FirstOrDefaultAsync(album => album.Id == group).ConfigureAwait(false) is { } album)
            {
                album.EditsJson = Restored(album.EditsJson, saved.AlbumBefore, saved.AlbumAfter);
            }
        }
    }

    /// <summary>
    /// Corrections as they are now, with the keys one save changed put back as
    /// they were before it. Any other key stays: the album page, or another
    /// folder of the album, may have corrected it since.
    /// </summary>
    private static string? Restored(string? now, string? before, string? after)
    {
        var edits = new Dictionary<string, string?>(PersonEdits.Read(now), StringComparer.Ordinal);
        var was = PersonEdits.Read(before);
        var made = PersonEdits.Read(after);

        foreach (var key in was.Keys.Union(made.Keys))
        {
            var had = was.TryGetValue(key, out var old);

            if (had == made.TryGetValue(key, out var saved) && old == saved) continue;

            if (had) edits[key] = old;
            else edits.Remove(key);
        }

        return PersonEdits.Write(edits);
    }

    /// <summary>The album page's key an album field is corrected under there.</summary>
    /// <param name="pressed">Whether the folder's files are filed under one proven pressing, whose year is the file's.</param>
    private static string AlbumKey(string field, bool pressed) => field switch
    {
        CatalogueTags.Album => "title",
        CatalogueTags.AlbumArtist => "credit",
        _ => pressed ? "releasedYear" : "firstReleaseYear",
    };

    private static string? TotalOf(string field) => field switch
    {
        CatalogueTags.TrackNumber => CatalogueTags.TrackTotal,
        CatalogueTags.DiscNumber => CatalogueTags.DiscTotal,
        _ => null,
    };

    private static string? Changed(string? json, string key, TagEdit change)
    {
        var edits = new Dictionary<string, string?>(PersonEdits.Read(json), StringComparer.Ordinal);

        if (change.Reset) edits.Remove(key);
        else edits[key] = change.Value;

        return PersonEdits.Write(edits);
    }

    private static string? Invalid(TagEdit change, List<MediaFile> files, ReleaseGroup? album)
    {
        if (PersonTags.Problem(change.Field, change.Reset ? null : change.Value) is { } problem) return problem;

        var albums = PersonTags.AlbumFields.Contains(change.Field, StringComparer.Ordinal);

        if (change.File is null)
        {
            return album is null || !albums
                ? "Only an album's title, artist and year are set for a whole folder, and only where the folder is one album."
                : !change.Reset && change.Value is null ? $"An album keeps its {change.Field}; a correction to it needs a value."
                : null;
        }

        if (files.All(file => file.Id.Value != change.File)) return $"{change.File} is not a file in this folder.";

        return album is not null && albums
            ? $"This folder is one album, so its {change.Field} is corrected for the whole album."
            : null;
    }

    private sealed record SavedPayload(
        [property: JsonPropertyName("before")] string? Before,
        [property: JsonPropertyName("after")] string? After);

    private sealed record AlbumSavedPayload(
        [property: JsonPropertyName("album")] Guid Album,
        [property: JsonPropertyName("albumBefore")] string? AlbumBefore,
        [property: JsonPropertyName("albumAfter")] string? AlbumAfter);
}

/// <param name="Value">What the tag write would put in the file now: a person's value, else the catalogue's, else the file's own.</param>
/// <param name="Catalogue">What the catalogue says, or null where it says nothing.</param>
/// <param name="File">What the file carries now; null for an album's own.</param>
/// <param name="Mine">Whether a person set it.</param>
public sealed record TagCell(string Field, string? Value, string? Catalogue, string? File, bool Mine);

/// <param name="Readable">False where neither tag library could read the file; its cells then say nothing about it.</param>
public sealed record FileTags(Guid Id, string Path, bool Readable, IReadOnlyList<TagCell> Cells);

/// <param name="AlbumWide">Whether the folder is one album, whose title, artist and year are then the album's, in <paramref name="Album"/>.</param>
public sealed record FolderTags(
    string Folder,
    bool WillWrite,
    Guid? AlbumId,
    bool AlbumWide,
    IReadOnlyList<string> Fields,
    IReadOnlyList<TagCell> Album,
    IReadOnlyList<FileTags> Files);

/// <param name="File">The file, or null for the album's own title, artist or year.</param>
/// <param name="Value">The value; null takes the field out of the file.</param>
/// <param name="Reset">Forget the person's value, so the catalogue's — or the file's own — is written again.</param>
public sealed record TagEdit(Guid? File, string Field, string? Value, bool Reset = false);

[JsonConverter(typeof(JsonStringEnumConverter<TagEditStatus>))]
public enum TagEditStatus
{
    Saved = 0,
    Invalid = 1,
    NotAnAlbumFolder = 2,
    NotOnDisk = 3,
    MutationOff = 4,
    Busy = 5,
}

/// <param name="Folder">Where the folder is after the save, which may have renamed it.</param>
/// <param name="NotWritten">Files the write refused or could not change; the corrections stay for the next write.</param>
/// <param name="Problems">Why the save was refused, one sentence each.</param>
public sealed record TagEditResult(
    TagEditStatus Status,
    string Folder,
    int Written,
    int Unchanged,
    int NotWritten,
    int Renamed,
    IReadOnlyList<string> Problems)
{
    public static TagEditResult Refused(TagEditStatus status, string folder) => new(status, folder, 0, 0, 0, 0, []);
}
