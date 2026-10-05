using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Fonoteca.Api.Endpoints;
using Fonoteca.Api.Logging;
using Fonoteca.Data;
using Fonoteca.Domain.Abstractions;
using Fonoteca.Domain.Catalogue;
using Fonoteca.Domain.Events;
using Fonoteca.Tagging;
using Microsoft.EntityFrameworkCore;

namespace Fonoteca.Api.Library;

/// <summary>
/// One step back for one album folder: the newest edit to its files, reversed.
/// </summary>
/// <remarks>
/// <b>An edit is one journal correlation as it touched this folder</b> — a tag
/// write run's tags, renames, links and sleeve together, or one identification
/// run's AcoustID — so a press reverses what one press of a button did, and the
/// next press the one before it. The newest is found among the folder's own
/// files by id and, for what the journal keys by path, by where the folder is
/// now: a folder move by its destination, a link by its target.
///
/// <b>It puts tags back whatever is there now</b> — the owner's choice: a field
/// another tagger changed since gets its pre-edit value all the same. A move is
/// the exception, because a move never goes over anything; an old name since
/// taken keeps the file where it is, and the result says so.
///
/// <b>Undoing a tag write reopens the folder</b>, or the next library-wide write
/// would redo it from the same catalogue answer. Undoing an identification takes
/// the AcoustID back out and nothing else.
///
/// <b>What it cannot put back</b> is what the journal never recorded: a date
/// the write cut to a year, a FLAC's leading ID3 block, the ID3 version, the
/// ID3v1 trailer, the repairs (a UFID, a value stored with a NUL), a total ATL
/// could not read, and an artist's pictures carried to a respelled shelf.
/// </remarks>
public sealed partial class TagWriteService
{
    /// <summary>The kind an undo takes the gate as.</summary>
    public const string UndoKind = "library.tags.undo";

    /// <summary>What the journal calls an undo's own writes and moves. Never an edit itself.</summary>
    public const string UndoPrefix = "tagging.undo";

    /// <summary>One per file of the folder: which edit an undo reversed.</summary>
    private const string UndoneEvent = UndoPrefix + ".undone";

    private const string WrittenEvent = EventPrefix + ".written";

    private const string RenamedEvent = EventPrefix + ".renamed";

    private const string CoverEvent = EventPrefix + ".cover";

    private const string FolderSubjectType = "folder";

    /// <summary>The edit an undo of this album folder would reverse, or null when there is none.</summary>
    public async Task<FolderEdit?> LastEditAsync(string folder, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(folder);

        var dbScope = scopeFactory.CreateAsyncScope();
        await using (dbScope.ConfigureAwait(false))
        {
            var db = dbScope.ServiceProvider.GetRequiredService<FonotecaDbContext>();

            return await AlbumFolderRowsAsync(db, folder, cancellationToken).ConfigureAwait(false) is { } rows
                ? await NewestEditAsync(db, folder, rows, cancellationToken).ConfigureAwait(false)
                : null;
        }
    }

    /// <summary>Reverses the newest edit to an album folder's files.</summary>
    /// <remarks>
    /// Runs in the request, because one folder is bounded, and is not cancelled
    /// once the gate is taken: a move stopped between its rows and its bytes is
    /// the one state nothing here repairs.
    /// </remarks>
    public async Task<TagUndoResult> UndoAsync(string folder, ICallerContext caller, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(folder);
        ArgumentNullException.ThrowIfNull(caller);

        if (!writerOptions.AllowFileMutation) return TagUndoResult.Refused(TagUndoStatus.MutationOff, folder);

        if (!gate.TryEnter(UndoKind, out var lease)) return TagUndoResult.Refused(TagUndoStatus.Busy, folder);

        using var held = lease;
        cancellationToken.ThrowIfCancellationRequested();

        var token = CancellationToken.None;

        var dbScope = scopeFactory.CreateAsyncScope();
        await using (dbScope.ConfigureAwait(false))
        {
            var provider = dbScope.ServiceProvider;
            var db = provider.GetRequiredService<FonotecaDbContext>();
            var events = provider.GetRequiredService<IEventLog>();

            if (await AlbumFolderRowsAsync(db, folder, token).ConfigureAwait(false) is not { } rows)
            {
                return TagUndoResult.Refused(TagUndoStatus.NotAnAlbumFolder, folder);
            }

            // Rule 3: an unmounted library has not lost this album, and nothing
            // on it can be put back.
            if (!Directory.Exists(Path.Combine(store.Root, folder)))
            {
                return TagUndoResult.Refused(TagUndoStatus.NotOnDisk, folder);
            }

            if (await NewestEditAsync(db, folder, rows, token).ConfigureAwait(false) is not { } edit)
            {
                return TagUndoResult.Refused(TagUndoStatus.NothingToUndo, folder);
            }

            var run = Guid.CreateVersion7().ToString("N")[..12];
            var undo = new Undo(folder, rows, caller.ActorId, run);
            var subjects = rows.Select(row => row.Id.ToString()).ToList();
            var loaded = await EntriesAsync(db, edit.Id, undo, token).ConfigureAwait(false);

            var entries = loaded
                .Where(entry => !((entry.Type == RenamedEvent && entry.SubjectType == FolderSubjectType) || entry.Type == LinkedEvent)
                    || Vouches(entry.Type, entry.Payload, entry.Correlation, subjects, loaded))
                .ToList();

            await RestoreTagsAsync(entries, undo).ConfigureAwait(false);

            // A file whose tags did not go back stops the undo here: nothing is
            // moved or reopened and the edit is not marked, so the next press
            // tries again — the files already put back are no-ops then. Marked,
            // the edit would be past and the file's tags beyond reach.
            if (undo.Unrestored > 0)
            {
                await db.SaveChangesAsync(token).ConfigureAwait(false);

                return new TagUndoResult(
                    TagUndoStatus.Incomplete, folder, edit, undo.Restored, 0, 0, 0, 0, 0, undo.Problems);
            }

            if (edit.Kind == FolderEditKind.TagWrite)
            {
                await MoveBackAsync(db, events, entries, undo).ConfigureAwait(false);
                await RelinkAsync(db, events, entries, undo).ConfigureAwait(false);
                await RestoreCoversAsync(events, entries, undo).ConfigureAwait(false);

                var ids = rows.Select(row => row.Id).ToList();

                var placed = await db.MediaFiles
                    .Where(file => ids.Contains(file.Id)
                        && (file.RecordingId != null || file.ReleaseGroupId != null || file.ReleaseId != null || file.TrackId != null))
                    .ToListAsync(token)
                    .ConfigureAwait(false);

                foreach (var row in placed) CatalogueEndpoints.Reopen(row, caller);

                if (placed.Count > 0)
                {
                    await events.AppendAsync(
                            CatalogueEndpoints.FolderReopened(
                                undo.Here, placed.Count, caller, StoreTime.ToStorePrecision(clock.UtcNow), run),
                            token)
                        .ConfigureAwait(false);
                }

                undo.Reopened = placed.Count;
            }

            // Every file of the folder, whatever the edit did to it: the folder's
            // moves and links are keyed by path, and a file with no entry of its
            // own is still how the next press knows this edit is behind it.
            foreach (var row in rows)
            {
                await events.AppendAsync(
                        DomainEvent.Create(
                            UndoneEvent,
                            TagWriter.FileSubject,
                            row.Id.ToString(),
                            caller.ActorId,
                            clock.UtcNow,
                            JsonSerializer.Serialize(new UndonePayload(edit.Id)),
                            run),
                        token)
                    .ConfigureAwait(false);
            }

            await db.SaveChangesAsync(token).ConfigureAwait(false);

            Log.TagsUndone(logger, folder, edit.Id, undo.Restored, undo.Moved, undo.Problems.Count);

            return new TagUndoResult(
                TagUndoStatus.Undone,
                undo.Here,
                edit,
                undo.Restored,
                undo.Moved,
                undo.Unlinked,
                undo.Relinked,
                undo.Covers,
                undo.Reopened,
                undo.Problems);
        }
    }

    /// <summary>
    /// Every file under an album folder, or null when the path is not one.
    /// </summary>
    /// <remarks>
    /// The unit everything else here is cut by: an artist's folder would reach
    /// into every album under it, and one press must reach one album.
    /// </remarks>
    private static async Task<List<FolderFile>?> AlbumFolderRowsAsync(
        FonotecaDbContext db,
        string folder,
        CancellationToken cancellationToken)
    {
        var prefix = folder + "/";

        var rows = await db.MediaFiles
            .AsNoTracking()
            .Where(file => file.Path.StartsWith(prefix))
            .Select(file => new FolderFile(file.Id, file.Path))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return rows.Count > 0 && rows.All(row => AlbumFolder.Of(row.Path) == folder) ? rows : null;
    }

    /// <summary>The newest edit to these files that no undo has reversed.</summary>
    private static async Task<FolderEdit?> NewestEditAsync(
        FonotecaDbContext db,
        string folder,
        List<FolderFile> rows,
        CancellationToken cancellationToken)
    {
        var subjects = rows.Select(row => row.Id.ToString()).ToList();

        // An undo's own run is never an edit to step back from, and neither is
        // the edit it reversed.
        var markers = await db.DomainEvents
            .AsNoTracking()
            .Where(entry => entry.Type == UndoneEvent && subjects.Contains(entry.SubjectId))
            .Select(entry => new { entry.CorrelationId, entry.PayloadJson })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var skipped = new HashSet<string>(StringComparer.Ordinal);

        foreach (var marker in markers)
        {
            if (marker.CorrelationId is { } run) skipped.Add(run);
            if (JsonSerializer.Deserialize<UndonePayload>(marker.PayloadJson)?.Undoes is { } undone) skipped.Add(undone);
        }

        string[] kinds = [WrittenEvent, RenamedEvent, CoverEvent, AcoustIdTagWriter.WrittenEventType];

        var own = await db.DomainEvents
            .AsNoTracking()
            .Where(entry => entry.SubjectType == TagWriter.FileSubject
                && subjects.Contains(entry.SubjectId)
                && kinds.Contains(entry.Type)
                && entry.CorrelationId != null)
            .Select(entry => new UndoEntry(entry.Type, entry.SubjectType, entry.SubjectId, entry.PayloadJson, entry.OccurredAtUtc, entry.CorrelationId!))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var candidates = own.Select(entry => new EditEntry(entry.Correlation, entry.At, entry.Type, entry.Subject)).ToList();

        // ponytail: every folder move and link in the journal, filtered here —
        // hundreds today. A jsonb query on the payload when it is tens of thousands.
        var placed = await db.DomainEvents
            .AsNoTracking()
            .Where(entry => entry.CorrelationId != null
                && ((entry.Type == RenamedEvent && entry.SubjectType == FolderSubjectType) || entry.Type == LinkedEvent))
            .Select(entry => new { entry.CorrelationId, entry.OccurredAtUtc, entry.Type, entry.SubjectId, entry.PayloadJson })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var entries = own
            .Concat(placed.Select(entry => new UndoEntry(
                entry.Type, FolderSubjectType, entry.SubjectId, entry.PayloadJson, entry.OccurredAtUtc, entry.CorrelationId!)))
            .ToList();

        candidates.AddRange(placed
            .Where(entry => (entry.Type == LinkedEvent ? Read(entry.PayloadJson, "target") : Read(entry.PayloadJson, "to")) == folder
                && Vouches(entry.Type, entry.PayloadJson, entry.CorrelationId!, subjects, entries))
            .Select(entry => new EditEntry(entry.CorrelationId!, entry.OccurredAtUtc, entry.Type, entry.SubjectId)));

        var newest = candidates
            .Where(entry => !skipped.Contains(entry.Correlation))
            .OrderByDescending(entry => entry.At)
            .FirstOrDefault();

        if (newest is null) return null;

        var edit = candidates.Where(entry => entry.Correlation == newest.Correlation).ToList();

        return new FolderEdit(
            newest.Correlation,
            edit.All(entry => entry.Type == AcoustIdTagWriter.WrittenEventType) ? FolderEditKind.Identification : FolderEditKind.TagWrite,
            edit.Max(entry => entry.At),
            edit.Where(entry => subjects.Contains(entry.Subject)).Select(entry => entry.Subject).Distinct(StringComparer.Ordinal).Count());
    }

    /// <summary>The edit's entries that concern this folder, in the order they were made.</summary>
    private static async Task<List<UndoEntry>> EntriesAsync(
        FonotecaDbContext db,
        string correlationId,
        Undo undo,
        CancellationToken cancellationToken)
    {
        var subjects = undo.Rows.Select(row => row.Id.ToString()).ToList();

        // A sleeve or a lyric moved with its file is keyed by where it was,
        // cut to the journal's 200 characters.
        var companions = Subject(undo.Folder + "/");

        return await db.DomainEvents
            .AsNoTracking()
            .Where(entry => entry.CorrelationId == correlationId
                && (subjects.Contains(entry.SubjectId)
                    || (entry.Type == RenamedEvent
                        && (entry.SubjectType == FolderSubjectType || entry.SubjectId.StartsWith(companions)))
                    || entry.Type == LinkedEvent
                    || entry.Type == UnlinkedEvent))
            .OrderBy(entry => entry.OccurredAtUtc)
            .ThenBy(entry => entry.Id)
            .Select(entry => new UndoEntry(entry.Type, entry.SubjectType, entry.SubjectId, entry.PayloadJson, entry.OccurredAtUtc, entry.CorrelationId!))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Whether a folder move or a link is this folder's.
    /// </summary>
    /// <remarks>
    /// A path is not an album: a library-wide run moves every album, and another
    /// rip can sit where it once put this one. So a move or a link counts where
    /// it names these files, or — on an entry older than that list — where the
    /// same run wrote, renamed or moved these files under the place it names.
    /// </remarks>
    private static bool Vouches(
        string type,
        string payload,
        string correlation,
        IReadOnlyCollection<string> subjects,
        IReadOnlyList<UndoEntry> entries)
    {
        if (Files(payload) is { Length: > 0 } named) return named.Any(subjects.Contains);

        string?[] places = type == LinkedEvent ? [Read(payload, "target")] : [Read(payload, "from"), Read(payload, "to")];

        var theirs = entries
            .Where(entry => entry.Correlation == correlation && subjects.Contains(entry.Subject))
            .SelectMany(entry => new[] { Read(entry.Payload, "path"), Read(entry.Payload, "from"), Read(entry.Payload, "to") })
            .OfType<string>()
            .ToList();

        // A link of an older run to where that run moved the album.
        if (type == LinkedEvent)
        {
            theirs.AddRange(entries
                .Where(entry => entry.Correlation == correlation
                    && entry.Type == RenamedEvent
                    && entry.SubjectType == FolderSubjectType
                    && Vouches(entry.Type, entry.Payload, correlation, subjects, entries))
                .Select(entry => Read(entry.Payload, "to") + "/"));
        }

        return places.OfType<string>().Any(place => theirs.Any(path => path.StartsWith(place + "/", StringComparison.Ordinal)));
    }

    /// <summary>Each file's fields back to what the edit found, one scope and one save per file.</summary>
    private async Task RestoreTagsAsync(List<UndoEntry> entries, Undo undo)
    {
        foreach (var entry in entries.Where(entry =>
                     entry.SubjectType == TagWriter.FileSubject
                     && (entry.Type == WrittenEvent || entry.Type == AcoustIdTagWriter.WrittenEventType)))
        {
            if (undo.Rows.FirstOrDefault(row => row.Id.ToString() == entry.Subject) is not { } row) continue;

            var restore = Reversal(JsonSerializer.Deserialize(entry.Payload, TaggingJson.Default.TagWritePayload));

            if (restore.Count == 0) continue;

            var scope = scopeFactory.CreateAsyncScope();
            await using (scope.ConfigureAwait(false))
            {
                var provider = scope.ServiceProvider;
                var db = provider.GetRequiredService<FonotecaDbContext>();
                var writer = provider.GetRequiredService<TagWriter>();
                var path = new LibraryPath(row.Path);

                try
                {
                    var plan = await writer.PlanRestoreAsync(path, restore, CancellationToken.None).ConfigureAwait(false);

                    // ATL writes no total without its number, so a number the
                    // edit added stays where a total is to stay beside it.
                    if (plan is not null && plan.Changes.Where(change => Unwritable(change, plan)).ToList() is { Count: > 0 } kept)
                    {
                        plan = plan with { Changes = [.. plan.Changes.Except(kept)] };
                        undo.Problems.Add($"{row.Path}: kept {string.Join(" and ", kept.Select(change => change.Field))}, which a total cannot go without");
                    }

                    var write = await writer
                        .ApplyAsync(plan, row.Id.ToString(), undo.Run, undo.Actor, UndoPrefix, CancellationToken.None)
                        .ConfigureAwait(false);

                    if (write.Status == TagWriteStatus.Written)
                    {
                        undo.Restored++;
                    }
                    else if (write.Status != TagWriteStatus.NothingToDo)
                    {
                        undo.Unrestored++;
                        undo.Problems.Add($"{row.Path}: {write.Detail ?? write.Status.ToString()}");
                    }

                    if (write.Committed is { } facts
                        && await db.MediaFiles.FirstOrDefaultAsync(file => file.Id == row.Id, CancellationToken.None).ConfigureAwait(false) is { } file)
                    {
                        // Rule 2, as for every write: the new size and mtime in the
                        // same save as the journal entry.
                        file.SizeBytes = facts.SizeBytes;
                        file.LastModifiedUtc = StoreTime.ToStorePrecision(facts.LastModifiedUtc);
                        file.ContentHash = null;

                        // The file no longer says what the catalogue knows.
                        if (write.Plan!.Changes.Any(change => change.Field == AcoustIdTagField.For(path))) file.AcoustIdTaggedUtc = null;
                    }

                    await db.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);
                }
#pragma warning disable CA1031 // As in the pass: one file that will not go back is that file.
                catch (Exception cause)
#pragma warning restore CA1031
                {
                    undo.Unrestored++;
                    undo.Problems.Add($"{row.Path}: {Because(cause)}");
                }
            }
        }
    }

    /// <summary>
    /// What a journalled write's fields go back to: each one's value before it,
    /// absent meaning removed.
    /// </summary>
    /// <remarks>
    /// Not a value the journal could only store with its NUL replaced — every
    /// <c>UFID</c>, an owner and an identifier either side of one, a repaired
    /// one included — since writing that back would be a damaged file rather
    /// than the old one. And never a total removed on an
    /// entry older than <see cref="TagWritePayload.TotalsVerified"/>, whose "none
    /// before" may be ATL failing to read one.
    /// </remarks>
    internal static IReadOnlyList<KeyValuePair<string, string?>> Reversal(TagWritePayload? payload)
    {
        var changes = (payload?.Changes ?? [])
            .Where(change => !Stored(change.Previous) && !Stored(change.Written))
            .ToList();

        // Unless the number goes too: ATL writes no total without its number,
        // so a total cannot stay behind a number that was not there before.
        bool Unknown(TagFieldWrite change) =>
            !payload!.TotalsVerified
            && change.Previous is null
            && NumberOf(change.Field) is { } number
            && !changes.Any(other => other.Field == number && other.Previous is null);

        return [.. changes.Where(change => !Unknown(change)).Select(change => KeyValuePair.Create(change.Field, change.Previous))];
    }

    private static string? NumberOf(string total) => total switch
    {
        CatalogueTags.TrackTotal => CatalogueTags.TrackNumber,
        CatalogueTags.DiscTotal => CatalogueTags.DiscNumber,
        _ => null,
    };

    /// <summary>Whether a rename keyed by path went with one of these files' own renames in the same edit.</summary>
    /// <remarks>By the rule <c>MoveCompanionsAsync</c> names one: the file's names, the companion's own extension.</remarks>
    private static bool Companion(string from, string to, List<UndoEntry> entries, Dictionary<string, FolderFile> files) =>
        entries.Any(entry => entry.Type == RenamedEvent
            && files.ContainsKey(entry.Subject)
            && Read(entry.Payload, "from") is { } own
            && Read(entry.Payload, "to") is { } renamed
            && Path.Join(Path.GetDirectoryName(own), Path.GetFileNameWithoutExtension(own)) == Path.Join(Path.GetDirectoryName(from), Path.GetFileNameWithoutExtension(from))
            && Stem(renamed) + Path.GetExtension(from) == to);

    private static string Stem(string path) => path[..^Path.GetExtension(path).Length];

    /// <summary>A number removed while its total stays: what ATL cannot write.</summary>
    private static bool Unwritable(TagFieldChange change, TagWritePlan plan)
    {
        if (change.To is not null) return false;

        var total = change.Field switch
        {
            CatalogueTags.TrackNumber => CatalogueTags.TrackTotal,
            CatalogueTags.DiscNumber => CatalogueTags.DiscTotal,
            _ => null,
        };

        if (total is null) return false;

        var after = plan.Changes.FirstOrDefault(other => other.Field == total) is { } planned
            ? planned.To
            : plan.Before.Find(total);

        return after is not null;
    }

    private static bool Stored(string? value) => value?.Contains('\u2400', StringComparison.Ordinal) == true;

    /// <summary>
    /// The edit's renames, newest first: each file back to its old name, then
    /// what moved with it, then the folder.
    /// </summary>
    private async Task MoveBackAsync(FonotecaDbContext db, IEventLog events, List<UndoEntry> entries, Undo undo)
    {
        var root = store.Root;
        var prefix = undo.Folder + "/";
        var files = undo.Rows.ToDictionary(row => row.Id.ToString(), StringComparer.Ordinal);
        var paths = undo.Rows.ToDictionary(row => row.Id.ToString(), row => row.Path, StringComparer.Ordinal);

        foreach (var entry in entries.Where(entry => entry.Type == RenamedEvent && entry.SubjectType == TagWriter.FileSubject).Reverse())
        {
            if (Read(entry.Payload, "from") is not { } from || Read(entry.Payload, "to") is not { } to) continue;

            var row = files.GetValueOrDefault(entry.Subject);

            if (row is not null && paths[entry.Subject] != to)
            {
                undo.Problems.Add($"{to}: moved since, so it stays at {paths[entry.Subject]}");
                continue;
            }

            // A shelf's pictures carried to a respelled artist are the artist's.
            if (!to.StartsWith(prefix, StringComparison.Ordinal)) continue;

            // A lyric or a cue sheet that went with a file is keyed by its own
            // path, and a path is not an album: it is this folder's only where
            // one of these files was renamed from and to its stem.
            if (row is null && !Companion(from, to, entries, files)) continue;

            if (row is null && !Taken(Path.Combine(root, to))) continue;

            if (Taken(Path.Combine(root, from)) || ThroughLink(root, from))
            {
                undo.Problems.Add($"{to}: '{from}' is taken, so it keeps its new name");
                continue;
            }

            await MoveAsync(
                    db,
                    events,
                    Path.Combine(root, to),
                    Path.Combine(root, from),
                    row is null
                        ? _ => Task.FromResult(0)
                        : token => db.MediaFiles
                            .Where(file => file.Id == row.Id)
                            .ExecuteUpdateAsync(set => set.SetProperty(file => file.Path, from), token),
                    TagWriter.FileSubject,
                    row?.Id.ToString() ?? to,
                    new { from = to, to = from },
                    undo.Run,
                    UndoPrefix + ".renamed",
                    undo.Actor)
                .ConfigureAwait(false);

            if (row is not null) paths[entry.Subject] = from;
            undo.Moved++;
        }

        var moved = entries
            .Where(entry => entry.Type == RenamedEvent && entry.SubjectType == FolderSubjectType && Read(entry.Payload, "to") == undo.Folder)
            .Select(entry => Read(entry.Payload, "from"))
            .LastOrDefault();

        if (moved is not { } origin) return;

        if (Taken(Path.Combine(root, origin))
            || ThroughLink(root, origin)
            || await db.MediaFiles.AnyAsync(file => file.Path.StartsWith(origin + "/")).ConfigureAwait(false))
        {
            undo.Problems.Add($"{undo.Folder}: '{origin}' is taken, so the album stays where it is");
            return;
        }

        await MoveAsync(
                db,
                events,
                Path.Combine(root, undo.Folder),
                Path.Combine(root, origin),
                token => FileManagerService.RewritePathsAsync(db, undo.Folder, origin, token),
                FolderSubjectType,
                undo.Folder,
                new { from = undo.Folder, to = origin },
                undo.Run,
                UndoPrefix + ".renamed",
                undo.Actor)
            .ConfigureAwait(false);

        if (Path.GetDirectoryName(undo.Folder) is { Length: > 0 } shelf) store.PruneEmptyDirectories(new LibraryPath(shelf));

        undo.Here = origin;
        undo.Moved++;
    }

    /// <summary>
    /// The links the edit made to this album go, and the ones it took away from
    /// where the album is again come back.
    /// </summary>
    private async Task RelinkAsync(FonotecaDbContext db, IEventLog events, List<UndoEntry> entries, Undo undo)
    {
        var root = store.Root;
        var links = await OwnLinksAsync(root, CancellationToken.None).ConfigureAwait(false);

        foreach (var entry in entries.Where(entry => entry.Type == LinkedEvent && Read(entry.Payload, "target") == undo.Folder))
        {
            if (Read(entry.Payload, "link") is not { } link) continue;

            var path = Path.Combine(root, link);

            // Only the pass's own: the journal says it made this one.
            if (!Owned(links, path) || !IsLink(path)) continue;

            await UnlinkAsync(root, path, links, db, events, undo.Run, undo.Actor).ConfigureAwait(false);
            undo.Unlinked++;
        }

        var removed = entries.Where(entry => entry.Type == UnlinkedEvent).ToList();

        if (removed.Count == 0) return;

        var started = entries.Min(entry => entry.At);

        var history = await db.DomainEvents
            .AsNoTracking()
            .Where(entry => entry.Type == LinkedEvent && entry.OccurredAtUtc < started)
            .OrderBy(entry => entry.OccurredAtUtc)
            .Select(entry => entry.PayloadJson)
            .ToListAsync()
            .ConfigureAwait(false);

        foreach (var entry in removed)
        {
            if (Read(entry.Payload, "link") is not { } link) continue;

            // Where that link pointed before this edit took it away.
            var before = history.LastOrDefault(payload => Read(payload, "link") == link);

            if (before is null || Read(before, "target") != undo.Here) continue;

            var path = Path.Combine(root, link);

            if (Taken(path) || ThroughLink(root, link))
            {
                undo.Problems.Add($"{link}: something is there now, so the link was not made again");
                continue;
            }

            var parent = Path.GetDirectoryName(path)!;
            Directory.CreateDirectory(parent);
            Directory.CreateSymbolicLink(path, Path.GetRelativePath(parent, Path.Combine(root, undo.Here)));

            await JournalLinkAsync(LinkedEvent, link, undo.Here, db, events, undo.Run, undo.Actor).ConfigureAwait(false);
            undo.Relinked++;
        }
    }

    /// <summary>
    /// The sleeve the edit wrote goes to the trash, and the ones it displaced come back.
    /// </summary>
    /// <remarks>
    /// Recognised by its size, the only fact the edit's entry records about the
    /// bytes: a cover somebody has since replaced is somebody's, and stays.
    /// </remarks>
    private async Task RestoreCoversAsync(IEventLog events, List<UndoEntry> entries, Undo undo)
    {
        var stamp = clock.UtcNow.ToString("yyyy-MM-dd HHmmss", CultureInfo.InvariantCulture);

        foreach (var entry in entries.Where(entry => entry.Type == CoverEvent && entry.SubjectType == TagWriter.FileSubject))
        {
            try
            {
                using var payload = JsonDocument.Parse(entry.Payload);
                var root = payload.RootElement;

                if (root.GetProperty("path").GetString() is not { } written) continue;

                var name = Path.GetFileName(written);
                var here = $"{undo.Here}/{name}";
                var absolute = store.AbsolutePathFor(new LibraryPath(here));
                string? removed = null;

                if (File.Exists(absolute) && !IsLink(absolute) && new FileInfo(absolute).Length == root.GetProperty("bytes").GetInt64())
                {
                    removed = Displace(here, absolute, stamp);
                }

                var restored = new List<string>();

                if (root.TryGetProperty("displaced", out var displaced) && displaced.ValueKind == JsonValueKind.Array)
                {
                    foreach (var trashed in displaced.EnumerateArray().Select(item => item.GetString()).OfType<string>())
                    {
                        var back = $"{undo.Here}/{Path.GetFileName(trashed)}";
                        var target = store.AbsolutePathFor(new LibraryPath(back));

                        if (!File.Exists(trashed) || Taken(target))
                        {
                            undo.Problems.Add($"{back}: the sleeve it replaced could not be put back");
                            continue;
                        }

                        File.Move(trashed, target);
                        restored.Add(back);
                    }
                }

                if (removed is null && restored.Count == 0) continue;

                await events.AppendAsync(
                        DomainEvent.Create(
                            UndoPrefix + ".cover",
                            TagWriter.FileSubject,
                            entry.Subject,
                            undo.Actor,
                            clock.UtcNow,
                            JsonSerializer.Serialize(new { path = here, removed, restored }),
                            undo.Run),
                        CancellationToken.None)
                    .ConfigureAwait(false);

                undo.Covers++;
            }
#pragma warning disable CA1031 // A sleeve is not the album: the rest of the undo stands.
            catch (Exception cause)
#pragma warning restore CA1031
            {
                undo.Problems.Add($"{undo.Here}: the sleeve could not be put back ({Because(cause)})");
            }
        }
    }

    /// <summary>The file ids a folder move or a link names, on entries made since they named them.</summary>
    private static string[] Files(string json)
    {
        using var payload = JsonDocument.Parse(json);

        return payload.RootElement.TryGetProperty("files", out var files) && files.ValueKind == JsonValueKind.Array
            ? [.. files.EnumerateArray().Select(file => file.GetString()).OfType<string>()]
            : [];
    }

    private static string? Read(string json, string property)
    {
        using var payload = JsonDocument.Parse(json);

        return payload.RootElement.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private sealed record FolderFile(MediaFileId Id, string Path);

    private sealed record EditEntry(string Correlation, DateTimeOffset At, string Type, string Subject);

    private sealed record UndoEntry(string Type, string SubjectType, string Subject, string Payload, DateTimeOffset At, string Correlation);

    private sealed record UndonePayload([property: JsonPropertyName("undoes")] string Undoes);

    /// <summary>What one undo has done so far, and where the folder is now.</summary>
    private sealed class Undo(string folder, List<FolderFile> rows, string actor, string run)
    {
        public string Folder { get; } = folder;

        public List<FolderFile> Rows { get; } = rows;

        public string Actor { get; } = actor;

        public string Run { get; } = run;

        public string Here { get; set; } = folder;

        public int Restored { get; set; }

        public int Unrestored { get; set; }

        public int Moved { get; set; }

        public int Unlinked { get; set; }

        public int Relinked { get; set; }

        public int Covers { get; set; }

        public int Reopened { get; set; }

        public List<string> Problems { get; } = [];
    }
}

/// <summary>Which pass an edit to a folder's files came from.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<FolderEditKind>))]
public enum FolderEditKind
{
    /// <summary>The catalogue tag write: tags, renames, links and a sleeve.</summary>
    TagWrite = 0,

    /// <summary>Identification, which writes the AcoustID and nothing else.</summary>
    Identification = 1,
}

/// <param name="Id">The journal correlation the edit was made under.</param>
/// <param name="Files">How many of the folder's files it wrote to or renamed.</param>
public sealed record FolderEdit(string Id, FolderEditKind Kind, DateTimeOffset At, int Files);

[JsonConverter(typeof(JsonStringEnumConverter<TagUndoStatus>))]
public enum TagUndoStatus
{
    Undone = 0,
    NothingToUndo = 1,
    NotAnAlbumFolder = 2,
    MutationOff = 3,
    Busy = 4,

    /// <summary>A file's tags could not be put back, so nothing else was undone and the edit stays to retry.</summary>
    Incomplete = 5,

    /// <summary>The folder is not on disk — an unmounted library — so nothing was touched.</summary>
    NotOnDisk = 6,
}

/// <param name="Folder">Where the album folder is after the undo.</param>
/// <param name="Problems">What could not be put back, one sentence each.</param>
public sealed record TagUndoResult(
    TagUndoStatus Status,
    string Folder,
    FolderEdit? Edit,
    int Restored,
    int Moved,
    int Unlinked,
    int Relinked,
    int Covers,
    int Reopened,
    IReadOnlyList<string> Problems)
{
    public static TagUndoResult Refused(TagUndoStatus status, string folder) =>
        new(status, folder, null, 0, 0, 0, 0, 0, 0, []);
}
