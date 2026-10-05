using System.Text.Json;
using Fonoteca.Api.Logging;
using Fonoteca.Data;
using Fonoteca.Domain.Abstractions;
using Fonoteca.Domain.Catalogue;
using Fonoteca.Domain.Events;
using Fonoteca.Tagging;
using Microsoft.EntityFrameworkCore;

namespace Fonoteca.Api.Library;

/// <summary>
/// The tag write's last step: every album folder it covered, moved to where
/// <c>Fonoteca:FileNaming</c> puts it.
/// </summary>
/// <remarks>
/// <b>Named from the catalogue, never from the file's tags</b> — the facts the
/// tags were just written from — so a file whose write was refused still gets
/// its name, and the name says what the catalogue says.
///
/// <b>The unit is the album folder</b> (<see cref="AlbumFolder"/>), moved in
/// one rename with everything in it: the sleeve, a booklet, files nothing has
/// identified. A folder whose files would name two album folders is left where
/// it is, since splitting it would hand attribution two half-albums; so is a
/// file loose under an artist, which has no album folder to move.
///
/// <b>Rows first, then the bytes, one transaction per move</b>, the file
/// manager's rule and for its reason: a rename the scan sees as a path that
/// vanished and one that arrived takes every AcoustID, link and human answer
/// with it. Nothing is ever overwritten, and nothing is moved through a
/// directory symlink — a lexical path under the root can be anywhere on disk,
/// the lesson <c>EnsureNoLinkedDirectory</c> already paid for.
///
/// <b>A collaboration lives under its first-billed artist, and every other
/// billed artist gets a directory symlink to it</b> — relative, so it survives
/// the library being mounted elsewhere. Each link is journalled, and the
/// journal is the only way the pass knows a link is its own: anything else is
/// somebody's and is never touched. The scan does not recurse through
/// directory links, so the album is catalogued once.
/// </remarks>
public sealed partial class TagWriteService
{
    private const string LinkedEvent = EventPrefix + ".linked";

    private const string UnlinkedEvent = EventPrefix + ".unlinked";

    private async Task RenameAsync(
        string jobId,
        TagWriteScope scope,
        Tally counts,
        int pending,
        string correlationId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(options.Value.FileNaming)) return;

        var root = store.Root;

        // Rule 3: an unmounted library has not been emptied, and there is
        // nothing on it to rename.
        if (!Directory.Exists(root))
        {
            Log.NotRenamed(logger, root, "the library root is not there");
            return;
        }

        List<string> folders;

        var dbScope = scopeFactory.CreateAsyncScope();
        await using (dbScope.ConfigureAwait(false))
        {
            var db = dbScope.ServiceProvider.GetRequiredService<FonotecaDbContext>();

            var paths = await (await NarrowAsync(db, scope, cancellationToken).ConfigureAwait(false))
                .Select(file => file.Path)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            // Only album folders: a file loose under an artist has the artist as
            // its folder, and moving that would move the whole shelf.
            folders = [.. paths
                .Select(AlbumFolder.Of)
                .Where(folder => folder.Contains('/', StringComparison.Ordinal))
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)];
        }

        var links = await OwnLinksAsync(root, cancellationToken).ConfigureAwait(false);
        var shelves = new List<ShelfMove>();
        var lastReport = clock.UtcNow;

        foreach (var folder in folders)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                await RenameFolderAsync(folder, counts, links, shelves, correlationId, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
#pragma warning disable CA1031 // As in the tag loop: no single folder may end the run.
            catch (Exception cause)
#pragma warning restore CA1031
            {
                counts.NotRenamed++;
                Log.NotRenamed(logger, folder, cause.Message);
            }

            var now = clock.UtcNow;
            if (now - lastReport < ProgressInterval) continue;

            lastReport = now;
            _progress = new TagWriteProgress(jobId, scope.Label, counts.Examined, pending, folder);
            await Report(jobId, counts.Examined, pending, folder, "running").ConfigureAwait(false);
        }

        // The pass's own links left pointing at nothing — a folder since moved,
        // trashed or renamed by hand. A link to nothing lists an empty album on
        // every player that follows it.
        if (!writerOptions.AllowFileMutation) return;

        var sweep = scopeFactory.CreateAsyncScope();
        await using (sweep.ConfigureAwait(false))
        {
            var db = sweep.ServiceProvider.GetRequiredService<FonotecaDbContext>();
            var events = sweep.ServiceProvider.GetRequiredService<IEventLog>();

            await CarryShelvesAsync(root, shelves, db, events, correlationId).ConfigureAwait(false);

            foreach (var link in links.Values.SelectMany(found => found).Where(link => !Directory.Exists(link)).ToList())
            {
                try
                {
                    await UnlinkAsync(root, link, links, db, events, correlationId).ConfigureAwait(false);
                }
#pragma warning disable CA1031 // A link that will not go is one link; the run's summary still stands.
                catch (Exception cause)
#pragma warning restore CA1031
                {
                    Log.NotRenamed(logger, Relative(root, link), cause.Message);
                }
            }
        }
    }

    private async Task RenameFolderAsync(
        string folder,
        Tally counts,
        Dictionary<string, List<string>> links,
        List<ShelfMove> shelves,
        string correlationId,
        CancellationToken cancellationToken)
    {
        var pattern = options.Value.FileNaming;
        var mutate = writerOptions.AllowFileMutation;
        var root = store.Root;

        var dbScope = scopeFactory.CreateAsyncScope();
        await using (dbScope.ConfigureAwait(false))
        {
            var provider = dbScope.ServiceProvider;
            var db = provider.GetRequiredService<FonotecaDbContext>();
            var events = provider.GetRequiredService<IEventLog>();

            var prefix = folder + "/";

            var rows = await db.MediaFiles
                .AsNoTracking()
                .Where(file => file.Path.StartsWith(prefix))
                .OrderBy(file => file.Id)
                .Select(file => new { file.Id, file.Path, file.ReleaseId })
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            var source = Path.Combine(root, folder);

            // Rule 3: a folder that is not there has not moved. Its rows are the
            // scan's to settle, on a volume that is mounted. And a folder holding
            // linked files is somebody's arrangement of albums kept elsewhere,
            // never an album to name.
            if (!Directory.Exists(source)
                || ThroughLink(root, folder)
                || IsLink(source)
                || rows.Any(row => IsLink(Path.Combine(root, row.Path))))
            {
                counts.NotRenamed += rows.Count;
                Log.NotRenamed(logger, folder, "it is not on disk, is reached through a link, or holds links");
                return;
            }

            var facts = await ProjectAsync(
                    db,
                    Writable(db.MediaFiles.AsNoTracking()).Where(file => file.Path.StartsWith(prefix)),
                    cancellationToken)
                .ConfigureAwait(false);

            if (facts.Count == 0) return;

            var named = facts.ToDictionary(file => file.Id, Naming);

            // Held to its album alone, a file keeps a track and disc number of
            // its own, and is given the folder's order only where it has none —
            // so it is named by what its tag says, read off the file now that
            // the write is done. None readable, no name.
            var reader = provider.GetRequiredService<TagReader>();
            var carried = new Dictionary<MediaFileId, (int? Track, int? Disc)>();

            foreach (var row in rows.Where(row => row.ReleaseId is null && named.ContainsKey(row.Id)))
            {
                try
                {
                    var tags = await reader.ReadAsync(new LibraryPath(row.Path), null, cancellationToken)
                        .ConfigureAwait(false);

                    carried[row.Id] = (Leading(tags.Find(CatalogueTags.TrackNumber)), Leading(tags.Find(CatalogueTags.DiscNumber)));
                }
                catch (Exception cause) when (cause is TagReadFailedException or IOException or UnauthorizedAccessException)
                {
                    carried[row.Id] = (null, null);
                }
            }

            var discs = carried.Values.Max(numbers => numbers.Disc) ?? 1;

            // The folders they sit in, where nothing numbers the discs: CD1 and
            // CD2 are the only record of which disc is which, and a file keeps
            // its folder rather than lose it.
            var unnumbered = rows
                .Where(row => carried.TryGetValue(row.Id, out var numbers) && numbers.Disc is null)
                .Select(row => Path.GetDirectoryName(row.Path[prefix.Length..]) ?? string.Empty)
                .Distinct(StringComparer.Ordinal)
                .Count() > 1;

            foreach (var (id, (track, disc)) in carried)
            {
                named[id] = named[id] with
                {
                    Facts = named[id].Facts with { Track = track, Disc = disc, DiscCount = discs },
                };
            }

            var albums = named.Values
                .Select(naming => FileNaming.Folder(pattern, naming.Facts))
                .Distinct(StringComparer.Ordinal)
                .ToList();

            if (albums is not [{ } target])
            {
                counts.NotRenamed += rows.Count;
                Log.NotRenamed(
                    logger,
                    folder,
                    albums.Contains(null)
                        ? "the catalogue lacks a fact the naming pattern needs"
                        : "its files would name more than one album folder");
                return;
            }

            if (target != folder)
            {
                var absolute = Path.Combine(root, target);

                // A link of this pass's own standing where the album is to go,
                // pointing at it, is from a billing that has since changed.
                if (mutate && Owned(links, absolute) && LinksTo(absolute, source))
                {
                    await UnlinkAsync(root, absolute, links, db, events, correlationId).ConfigureAwait(false);
                }

                if (Taken(absolute)
                    || ThroughLink(root, target)
                    || await db.MediaFiles.AnyAsync(file => file.Path.StartsWith(target + "/"), cancellationToken)
                        .ConfigureAwait(false))
                {
                    counts.NotRenamed += rows.Count;
                    Log.NotRenamed(logger, folder, $"'{target}' already exists or is reached through a link");
                    return;
                }

                if (mutate)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    await MoveAsync(
                            db,
                            events,
                            source,
                            absolute,
                            token => FileManagerService.RewritePathsAsync(db, folder, target, token),
                            "folder",
                            folder,
                            new { from = folder, to = target, files = rows.Select(row => row.Id.ToString()).ToArray() },
                            correlationId)
                        .ConfigureAwait(false);

                    var parent = Path.GetDirectoryName(folder);
                    if (!string.IsNullOrEmpty(parent)) store.PruneEmptyDirectories(new LibraryPath(parent));

                    Log.Renamed(logger, folder, target);

                    if (target.Contains('/', StringComparison.Ordinal) && Shelf(folder) != Shelf(target))
                    {
                        // The billed artist the old folder was named for under
                        // another spelling, if any: their folder is where it went,
                        // a link in it for all but the first.
                        var album = named.Values.First();
                        var shelf = ArtistNameMatch.Normalise(Shelf(folder));
                        var respelled = album.Spellings
                            .FirstOrDefault(artist => artist.Value.Any(name => ArtistNameMatch.Normalise(name) == shelf))
                            .Key;

                        shelves.Add(respelled is null
                            ? new ShelfMove(Shelf(folder), Shelf(target), Spelled: false)
                            : new ShelfMove(
                                Shelf(folder),
                                Shelf(FileNaming.Folder(pattern, album.Facts with { AlbumArtist = respelled }) ?? target),
                                Spelled: true));
                    }
                }
            }

            var emptied = new HashSet<string>(StringComparer.Ordinal);

            // Where the files are now: moved with their folder, or on a dry run
            // still where they were.
            var here = mutate ? target : folder;

            var catalogued = rows
                .Select(row => Path.Combine(root, $"{here}/{row.Path[prefix.Length..]}"))
                .ToHashSet(StringComparer.Ordinal);

            foreach (var row in rows)
            {
                var from = $"{here}/{row.Path[prefix.Length..]}";

                // Not the pass's file — nothing has answered what it is — so it
                // goes where its folder goes and keeps its name.
                if (!named.TryGetValue(row.Id, out var naming)) continue;

                if (FileNaming.File(pattern, naming.Facts, Extension(row.Path)) is not { } name)
                {
                    counts.NotRenamed++;
                    Log.NotRenamed(logger, from, "the catalogue lacks a fact the naming pattern needs");
                    continue;
                }

                var inside = Path.GetDirectoryName(row.Path[prefix.Length..]) ?? string.Empty;
                var to = unnumbered && inside.Length > 0 && carried.TryGetValue(row.Id, out var own) && own.Disc is null
                    ? $"{here}/{inside}/{Path.GetFileName(name)}"
                    : $"{here}/{name}";

                if (from == to)
                {
                    if (target != folder) counts.Renamed++;
                    continue;
                }

                // Never from beyond a link, never over anything — including a
                // file this run has yet to move out of the way, whose name a
                // later run finds free — and never a file that is not there.
                var problem = !Taken(Path.Combine(root, from)) ? "it is not on disk"
                    : ThroughLink(root, from) ? "it is reached through a link"

                    // Its link text is relative to where it is, and would point
                    // at nothing from anywhere else.
                    : IsLink(Path.Combine(root, from)) ? "it is a link"
                    : Taken(Path.Combine(root, to)) || ThroughLink(root, to) ? $"'{to}' already exists or is reached through a link"
                    : null;

                if (problem is not null)
                {
                    counts.NotRenamed++;
                    Log.NotRenamed(logger, from, problem);
                    continue;
                }

                if (mutate)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    try
                    {
                        await MoveAsync(
                                db,
                                events,
                                Path.Combine(root, from),
                                Path.Combine(root, to),
                                token => db.MediaFiles
                                    .Where(file => file.Id == row.Id)
                                    .ExecuteUpdateAsync(set => set.SetProperty(file => file.Path, to), token),
                                "file",
                                row.Id.ToString(),
                                new { from, to },
                                correlationId)
                            .ConfigureAwait(false);
                    }
#pragma warning disable CA1031 // One file that will not move is one file; its folder goes on.
                    catch (Exception cause)
#pragma warning restore CA1031
                    {
                        counts.NotRenamed++;
                        Log.NotRenamed(logger, from, cause.Message);
                        continue;
                    }

                    catalogued.Add(Path.Combine(root, to));
                    await MoveCompanionsAsync(root, from, to, catalogued, db, events, correlationId).ConfigureAwait(false);

                    var left = Path.GetDirectoryName(from);
                    if (!string.IsNullOrEmpty(left) && left != here) emptied.Add(left);
                }

                counts.Renamed++;
            }

            foreach (var directory in emptied) store.PruneEmptyDirectories(new LibraryPath(directory));

            await LinkAsync(
                    root, source, target, named.Values.First(), pattern, links, counts, mutate, db, events, correlationId,
                    [.. rows.Select(row => row.Id.ToString())])
                .ConfigureAwait(false);
        }
    }

    /// <summary>
    /// What sits beside a file under its name — the lyrics in <c>01.lrc</c>
    /// beside <c>01.flac</c> — is found by that name, so it takes the new one.
    /// </summary>
    private async Task MoveCompanionsAsync(
        string root,
        string from,
        string to,
        HashSet<string> catalogued,
        FonotecaDbContext db,
        IEventLog events,
        string correlationId)
    {
        var stem = Path.GetFileNameWithoutExtension(from);
        var renamed = to[..^Path.GetExtension(to).Length];

        var companions = Directory.EnumerateFiles(Path.Combine(root, Path.GetDirectoryName(from)!))
            .Where(path => Path.GetFileNameWithoutExtension(path) == stem && !catalogued.Contains(path) && !IsLink(path))
            .ToList();

        foreach (var companion in companions)
        {
            var was = Relative(root, companion);
            var now = renamed + Path.GetExtension(companion);

            await MoveLooseAsync(root, was, now, db, events, correlationId).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// An artist folder whose every album this run moved to one folder of the
    /// same artist under another spelling — "gretchenwilson" to "Gretchen
    /// Wilson" — is that folder renamed, so what else it holds, the artist's
    /// pictures, goes too. A person's albums going to their band's folder is
    /// not a spelling: the band has pictures of its own.
    /// </summary>
    private async Task CarryShelvesAsync(
        string root,
        List<ShelfMove> shelves,
        FonotecaDbContext db,
        IEventLog events,
        string correlationId)
    {
        foreach (var moves in shelves.GroupBy(move => move.From, StringComparer.Ordinal))
        {
            if (!moves.All(move => move.Spelled) || moves.Select(move => move.To).Distinct(StringComparer.Ordinal).Count() != 1)
            {
                continue;
            }

            var from = Path.Combine(root, moves.Key);
            var to = moves.First().To;

            if (to == moves.Key || !Directory.Exists(from) || IsLink(from) || IsLink(Path.Combine(root, to))) continue;

            var entries = Directory.GetFileSystemEntries(from);

            // Only plain files: an album the run did not cover, or a link, and
            // it is still a shelf of its own.
            if (entries.Any(entry => !File.Exists(entry) || IsLink(entry))
                || await db.MediaFiles.AnyAsync(file => file.Path.StartsWith(moves.Key + "/")).ConfigureAwait(false))
            {
                continue;
            }

            foreach (var entry in entries)
            {
                await MoveLooseAsync(root, Relative(root, entry), $"{to}/{Path.GetFileName(entry)}", db, events, correlationId)
                    .ConfigureAwait(false);
            }

            store.PruneEmptyDirectories(new LibraryPath(moves.Key));
        }
    }

    /// <summary>A file with no row, moved and journalled; never over anything, and one that will not go stays.</summary>
    private async Task MoveLooseAsync(
        string root,
        string from,
        string to,
        FonotecaDbContext db,
        IEventLog events,
        string correlationId)
    {
        if (Taken(Path.Combine(root, to)) || ThroughLink(root, to))
        {
            Log.NotRenamed(logger, from, $"'{to}' already exists or is reached through a link");
            return;
        }

        try
        {
            await MoveAsync(
                    db,
                    events,
                    Path.Combine(root, from),
                    Path.Combine(root, to),
                    _ => Task.FromResult(0),
                    "file",
                    from,
                    new { from, to },
                    correlationId)
                .ConfigureAwait(false);

            Log.Renamed(logger, from, to);
        }
#pragma warning disable CA1031 // A sleeve or a lyric that will not move is that file; the album has moved.
        catch (Exception cause)
#pragma warning restore CA1031
        {
            Log.NotRenamed(logger, from, cause.Message);
        }
    }

    /// <summary>
    /// A link in every other billed artist's folder to the album's own, and
    /// none of this pass's own left pointing at it, or at where it was, from
    /// anywhere else.
    /// </summary>
    private async Task LinkAsync(
        string root,
        string source,
        string target,
        Named album,
        string pattern,
        Dictionary<string, List<string>> links,
        Tally counts,
        bool mutate,
        FonotecaDbContext db,
        IEventLog events,
        string correlationId,
        IReadOnlyList<string> files)
    {
        var absolute = Path.Combine(root, target);

        var wanted = album.Others
            .Select(artist => FileNaming.Folder(pattern, album.Facts with { AlbumArtist = artist }))
            .OfType<string>()
            .Where(link => link != target)
            .Select(link => Path.Combine(root, link))
            .ToHashSet(StringComparer.Ordinal);

        // A collaborator no longer billed, or a folder that moved away: the
        // pass's own links to either go.
        if (mutate)
        {
            foreach (var stale in new[] { source, absolute }
                         .Distinct(StringComparer.Ordinal)
                         .SelectMany(folder => links.TryGetValue(folder, out var found) ? found : [])
                         .Where(link => !(wanted.Contains(link) && LinksTo(link, absolute)))
                         .ToList())
            {
                await UnlinkAsync(root, stale, links, db, events, correlationId).ConfigureAwait(false);
            }
        }

        foreach (var path in wanted)
        {
            if (LinksTo(path, absolute)) continue;

            var link = Relative(root, path);

            // Something already there — a folder, a file, or any link but one of
            // this pass's own left dangling — is somebody's, and stays.
            if (Taken(path) && !(Owned(links, path) && !Directory.Exists(path)))
            {
                Log.NotRenamed(logger, link, "something of that name is already there, so no link was made");
                continue;
            }

            if (ThroughLink(root, link))
            {
                Log.NotRenamed(logger, link, "it would be reached through a link, so no link was made");
                continue;
            }

            if (mutate)
            {
                if (IsLink(path)) await UnlinkAsync(root, path, links, db, events, correlationId).ConfigureAwait(false);

                var parent = Path.GetDirectoryName(path)!;
                Directory.CreateDirectory(parent);
                Directory.CreateSymbolicLink(path, Path.GetRelativePath(parent, absolute));

                // Journalled, because the journal is how a later run knows the
                // link is its own to move or remove — a person's link of the same
                // shape is never touched.
                await JournalLinkAsync(LinkedEvent, link, target, db, events, correlationId, files: files).ConfigureAwait(false);

                if (!links.TryGetValue(absolute, out var found)) links[absolute] = found = [];
                found.Add(path);
            }

            counts.Linked++;
        }
    }

    private async Task UnlinkAsync(
        string root,
        string path,
        Dictionary<string, List<string>> links,
        FonotecaDbContext db,
        IEventLog events,
        string correlationId,
        string? actor = null)
    {
        if (IsLink(path)) Unlink(path);

        if (Path.GetDirectoryName(Relative(root, path)) is { Length: > 0 } parent) store.PruneEmptyDirectories(new LibraryPath(parent));

        foreach (var found in links.Values) found.Remove(path);

        await JournalLinkAsync(UnlinkedEvent, Relative(root, path), null, db, events, correlationId, actor).ConfigureAwait(false);
    }

    private async Task JournalLinkAsync(
        string type,
        string link,
        string? target,
        FonotecaDbContext db,
        IEventLog events,
        string correlationId,
        string? actor = null,
        IReadOnlyList<string>? files = null)
    {
        // The album's files, so an undo can tell this link from one to another
        // album since put at the same path.
        await events.AppendAsync(
                DomainEvent.Create(
                    type,
                    "link",
                    Subject(link),
                    actor ?? _actor,
                    clock.UtcNow,
                    files is null ? JsonSerializer.Serialize(new { link, target }) : JsonSerializer.Serialize(new { link, target, files }),
                    correlationId),
                CancellationToken.None)
            .ConfigureAwait(false);

        await db.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>
    /// The links this pass made and has not since removed, still on disk and
    /// pointing into the library, by the folder each resolves to.
    /// </summary>
    /// <remarks>
    /// Read from the journal, never guessed from a link's shape: a person's own
    /// <c>Favourites/Album -> ../Artist/Album</c> looks exactly like one of
    /// these, and is not this pass's to take.
    /// </remarks>
    private async Task<Dictionary<string, List<string>>> OwnLinksAsync(string root, CancellationToken cancellationToken)
    {
        var journalled = new Dictionary<string, bool>(StringComparer.Ordinal);

        var dbScope = scopeFactory.CreateAsyncScope();
        await using (dbScope.ConfigureAwait(false))
        {
            var db = dbScope.ServiceProvider.GetRequiredService<FonotecaDbContext>();

            var entries = await db.DomainEvents
                .AsNoTracking()
                .Where(entry => entry.Type == LinkedEvent || entry.Type == UnlinkedEvent)
                .OrderBy(entry => entry.OccurredAtUtc)
                .Select(entry => new { entry.Type, entry.PayloadJson })
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            foreach (var entry in entries)
            {
                using var payload = JsonDocument.Parse(entry.PayloadJson);

                if (payload.RootElement.GetProperty("link").GetString() is { } link)
                {
                    journalled[link] = entry.Type == LinkedEvent;
                }
            }
        }

        var found = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        foreach (var (link, made) in journalled)
        {
            var path = Path.Combine(root, link);

            if (!made || !IsLink(path)) continue;

            var target = Resolved(path);

            if (!target.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal)) continue;

            if (!found.TryGetValue(target, out var list)) found[target] = list = [];
            list.Add(path);
        }

        return found;
    }

    private static bool Owned(Dictionary<string, List<string>> links, string path) =>
        links.Values.Any(found => found.Contains(path));

    private static string Relative(string root, string path) =>
        Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/');

    /// <summary>Rows first, then the bytes, then the journal and the commit — one move.</summary>
    private async Task MoveAsync(
        FonotecaDbContext db,
        IEventLog events,
        string from,
        string to,
        Func<CancellationToken, Task<int>> repoint,
        string subjectType,
        string subject,
        object payload,
        string correlationId,
        string eventType = EventPrefix + ".renamed",
        string? actor = null)
    {
        actor ??= _actor;

        // Through the execution strategy, as FileManagerService.MoveAsync is and
        // for its reason. The retried delegate re-runs the rows; the check
        // before the move keeps a retry from moving twice.
        //
        // And never cancelled once begun: a stop landing between the rename and
        // the commit would roll the rows back over bytes that had already moved,
        // and the next scan would discard everything derived from them. The run
        // stops between moves instead.
        var cancellationToken = CancellationToken.None;
        await db.Database.CreateExecutionStrategy().ExecuteAsync(
            async token =>
            {
                var transaction = await db.Database.BeginTransactionAsync(token).ConfigureAwait(false);

                await using (transaction.ConfigureAwait(false))
                {
                    await repoint(token).ConfigureAwait(false);

                    var moved = !Taken(from) && Taken(to);
                    var moving = !moved;

                    if (moving)
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(to)!);

                        if (Directory.Exists(from)) Directory.Move(from, to);
                        else File.Move(from, to);
                    }

                    try
                    {
                        await events.AppendAsync(
                                DomainEvent.Create(
                                    eventType,
                                    subjectType,
                                    Subject(subject),
                                    actor,
                                    clock.UtcNow,
                                    JsonSerializer.Serialize(payload),
                                    correlationId),
                                token)
                            .ConfigureAwait(false);

                        await db.SaveChangesAsync(token).ConfigureAwait(false);
                        await transaction.CommitAsync(token).ConfigureAwait(false);
                    }
                    catch
                    {
                        // The rows roll back with the transaction; the bytes have
                        // to be put back by hand, or the next scan finds the
                        // file somewhere its row does not say.
                        if (moving)
                        {
                            if (Directory.Exists(to)) Directory.Move(to, from);
                            else File.Move(to, from);
                        }

                        throw;
                    }
                }
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>A journal subject within <c>varchar(200)</c>, never cut through a character.</summary>
    private static string Subject(string value)
    {
        if (value.Length <= 200) return value;

        var cut = char.IsHighSurrogate(value[199]) ? 199 : 200;
        return value[..cut];
    }

    /// <summary>What a file is named from: the facts, and the album's other billed artists.</summary>
    /// <param name="Spellings">
    /// Each billed artist's own name, to the names that are theirs: own and
    /// displayed. Never the billed one: a band billed under its leader's name
    /// is not the leader respelled.
    /// </param>
    private sealed record Named(
        NamingFacts Facts,
        IReadOnlyList<string> Others,
        IReadOnlyDictionary<string, IReadOnlyList<string>> Spellings);

    /// <summary>An album this run moved from one artist folder to another.</summary>
    /// <param name="Spelled">Whether the old folder is the new one's artist under another spelling.</param>
    private sealed record ShelfMove(string From, string To, bool Spelled);

    private static string Shelf(string path) => path[..path.IndexOf('/', StringComparison.Ordinal)];

    /// <summary>
    /// The facts the tags were written from, read back out of the tag plan, so
    /// a file is named what its tags say — but for the numbers of a file held
    /// to its album alone, which the caller reads off the file.
    /// </summary>
    private static Named Naming(PendingTagWrite file)
    {
        var tags = file.Tags();

        // Each billed artist once, by their own name: "Bowie" and "David Bowie"
        // are one shelf.
        var billed = file.ReleaseCredits.Select(credit => credit.Own).Distinct(StringComparer.Ordinal).ToList();

        // An album artist a person typed is the shelf, as typed: it names no
        // artist the catalogue holds, so there is nobody to link it from.
        var typed = file.Person.TryGetValue(CatalogueTags.AlbumArtist, out var own) ? own : file.AlbumArtistEdit;

        if (typed is not null || file.Person.ContainsKey(CatalogueTags.AlbumArtist)) billed = typed is null ? [] : [typed];

        return new Named(
            new NamingFacts(
                billed.FirstOrDefault(),
                tags.GetValueOrDefault(CatalogueTags.Album),
                Number(CatalogueTags.Year),
                Number(CatalogueTags.DiscNumber),
                Number(CatalogueTags.DiscTotal),
                Number(CatalogueTags.TrackNumber),
                tags.GetValueOrDefault(CatalogueTags.Title),
                tags.GetValueOrDefault(CatalogueTags.Artist)),
            [.. billed.Skip(1)],
            file.ReleaseCredits
                .Where(_ => typed is null)
                .GroupBy(credit => credit.Own, StringComparer.Ordinal)
                .ToDictionary(
                    artist => artist.Key,
                    IReadOnlyList<string> (artist) => [.. artist.SelectMany(credit => new[] { credit.Own, credit.Latin }).OfType<string>()],
                    StringComparer.Ordinal));

        int? Number(string field) =>
            tags.TryGetValue(field, out var value)
            && int.TryParse(value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var number)
                ? number
                : null;
    }

    /// <summary>The number a tag starts with: "3" of "3/12".</summary>
    private static int? Leading(string? value) =>
        value?.Split('/')[0].Trim() is { Length: > 0 } number
        && int.TryParse(number, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var parsed)
        && parsed > 0
            ? parsed
            : null;

    private static void Unlink(string path) => new FileInfo(path).Delete();

    private static string Extension(string path) => Path.GetExtension(path).TrimStart('.').ToLowerInvariant();

    private static bool IsLink(string path) => new FileInfo(path).LinkTarget is not null;

    /// <summary>Whether any folder on the way to a library-relative path is a link.</summary>
    private static bool ThroughLink(string root, string relative)
    {
        var current = root;

        foreach (var part in relative.Split('/')[..^1])
        {
            current = Path.Combine(current, part);
            if (IsLink(current)) return true;
        }

        return false;
    }

    /// <summary>Anything at all at that path: a file, a folder, or a link, dangling or not.</summary>
    private static bool Taken(string path) => File.Exists(path) || Directory.Exists(path) || IsLink(path);

    private static string Resolved(string link) =>
        Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(Path.Combine(Path.GetDirectoryName(link)!, new FileInfo(link).LinkTarget!)));

    private static bool LinksTo(string path, string target) =>
        IsLink(path) && Resolved(path) == Path.TrimEndingDirectorySeparator(target);
}
