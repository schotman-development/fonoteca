using System.Globalization;
using System.Text.Json;
using Fonoteca.Api.Configuration;
using Fonoteca.Api.Logging;
using Fonoteca.Data;
using Fonoteca.Domain.Abstractions;
using Fonoteca.Domain.Catalogue;
using Fonoteca.Domain.Events;
using Fonoteca.Ingest;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Fonoteca.Api.Library;

/// <summary>
/// The library as a directory tree, and the three things a person can do to it.
/// </summary>
/// <remarks>
/// <b>Every other screen in this application is about music. This one is about
/// files</b>, and it exists because the two disagree: a duplicate rip is one
/// album to MusicBrainz and two folders on disk, and nothing that reasons about
/// recordings can tell you which of the two is the mess.
///
/// The split that makes it cheap:
///
/// <list type="bullet">
/// <item><b>What is there comes from the disk, one directory at a time.</b> The
/// catalogue holds audio and only audio — <c>LibraryScanner</c> filters on
/// <see cref="AudioFormats"/> — so a tree derived from <c>MediaFiles.Path</c>
/// cannot show a stray <c>cover.jpg</c>, a leftover <c>.m3u</c>, or the album
/// that was uploaded four seconds ago and has not been scanned. Those are
/// exactly the things somebody opens a file manager to deal with.</item>
///
/// <item><b>What it means comes from the catalogue, in one query per
/// listing.</b> Rows under the folder's prefix, grouped onto its immediate
/// children by <see cref="FolderRollup"/>. That is what puts "12 files, all
/// matched" beside "31 files, 3 matched" and answers the question the screen was
/// opened for.</item>
/// </list>
///
/// <b>Nothing is deleted.</b> Trash is a move to <c>Fonoteca:TrashPath</c> under
/// a timestamp, keeping the library-relative layout, so undoing a wrong click is
/// a <c>mv</c> rather than a restore from backup. It is outside the library root
/// or the next scan would catalogue the bin and the folder would appear to still
/// be there. This is <c>AlbumReplacementService</c>'s bargain, and it is the
/// right one for the same reason: disk is cheap and a deleted album is not.
///
/// <b>A move updates the rows, and that is the sharp edge here.</b> Renaming a
/// folder changes no bytes, but to a scan it looks like a deletion and an
/// arrival — so the rows would be dropped and re-created empty, taking every
/// AcoustID, recording link, release attribution and human decision under that
/// folder with them. Hours of rate-limited lookups, lost to a typo correction.
/// The rename therefore carries the paths with it, in the same transaction, and
/// nothing derived is touched because nothing derived has changed.
///
/// <b>Trash does delete the rows, and that is not the same act as the scan's.</b>
/// The scan refuses to remove rows for files it did not see, because absence of
/// evidence is not evidence of deletion — an unreadable directory and an
/// unmounted volume both look like a deletion from the outside. Here there is
/// direct evidence: this process moved these files, and knows their paths. Left
/// behind, the rows would keep the folder on every other screen until somebody
/// thought to rescan.
/// </remarks>
public sealed class FileManagerService(
    FonotecaDbContext db,
    FileSystemAudioFileStore store,
    LibraryWorkGate gate,
    IEventLog events,
    ICallerContext caller,
    IOptions<FonotecaOptions> options,
    IClock clock,
    ILogger<FileManagerService> logger)
{
    /// <summary>What holds the gate while files are being moved.</summary>
    private const string WorkKind = "files";

    /// <summary>
    /// One directory, from the disk, annotated from the catalogue.
    /// </summary>
    public async Task<FolderListing> ListAsync(
        string? path,
        CancellationToken cancellationToken = default)
    {
        var folder = Normalise(path);
        var absolute = store.AbsolutePathFor(new LibraryPath(folder));

        if (!Directory.Exists(absolute))
        {
            return new FolderListing(folder, ParentOf(folder), Exists: false, []);
        }

        var rollups = await RollupsAsync(folder, cancellationToken).ConfigureAwait(false);

        var entries = new List<FolderEntry>();

        // The walk's own exclusions, and for the walk's own reasons: hidden and
        // system entries are other tools' metadata — @eaDir, .Trash-1000 — and
        // the staging files this application writes are hidden precisely so that
        // nothing has to know about them by name.
        var listing = new DirectoryInfo(absolute).EnumerateFileSystemInfos()
            .Where(entry => (entry.Attributes & (FileAttributes.Hidden | FileAttributes.System)) == 0)
            // Reparse points go too, matching LibraryTreeEnumerator, and here it
            // is containment rather than loop-avoidance: Resolve is lexical, so
            // a directory symlink pointing out of the library would be listed,
            // navigable, and a way to browse — and trash — files the catalogue
            // has never seen. Symlinked files stay, as they do in the walk.
            .Where(entry => (entry.Attributes & FileAttributes.Directory) == 0
                || (entry.Attributes & FileAttributes.ReparsePoint) == 0);

        foreach (var entry in listing)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var isDirectory = (entry.Attributes & FileAttributes.Directory) != 0;
            var childPath = folder.Length == 0 ? entry.Name : $"{folder}/{entry.Name}";

            rollups.TryGetValue(entry.Name, out var rollup);

            entries.Add(new FolderEntry(
                Name: entry.Name,
                Path: childPath,
                IsDirectory: isDirectory,
                // A directory's own byte count would need a recursive walk per
                // row, which at the root is the whole library on every
                // navigation. The catalogue already knows what the audio under
                // it weighs, and that is the number anybody comparing two rips
                // is after.
                SizeBytes: isDirectory ? rollup.SizeBytes : ((FileInfo)entry).Length,
                ModifiedUtc: new DateTimeOffset(entry.LastWriteTimeUtc),
                IsAudio: !isDirectory && AudioFormats.IsAudioFile(childPath),
                // From the server's own allowlist rather than from a second
                // extension table in the client. The same value decides what
                // Content-Type the bytes are served with, so a row that offers
                // to show a picture and an endpoint that refuses to send one
                // cannot disagree.
                Kind: isDirectory ? nameof(PreviewKind.None) : FilePreview.Of(childPath).Kind.ToString(),
                CataloguedFiles: rollup.Files,
                Identified: rollup.Identified,
                Attributed: rollup.Attributed));
        }

        // Folders first, then by name — the order every file manager uses, and
        // the one that keeps an album's discs together above its cover art.
        entries.Sort(static (left, right) => left.IsDirectory == right.IsDirectory
            ? string.Compare(left.Name, right.Name, StringComparison.OrdinalIgnoreCase)
            : right.IsDirectory.CompareTo(left.IsDirectory));

        return new FolderListing(folder, ParentOf(folder), Exists: true, entries);
    }

    /// <summary>
    /// Moves entries out of the library, into a stamped folder under the trash.
    /// </summary>
    public async Task<FileOperation> TrashAsync(
        IReadOnlyList<string> paths,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(paths);

        if (paths.Count == 0) return FileOperation.Refused("Nothing was selected.");

        if (!gate.TryEnter(WorkKind, out var lease))
        {
            return FileOperation.Refused($"{gate.ActiveKind} is running. Nothing was moved.");
        }

        using (lease)
        {
            var stamp = clock.UtcNow.ToString("yyyy-MM-dd HHmmss", CultureInfo.InvariantCulture);
            var destinationRoot = Path.Combine(TrashRoot(), stamp);

            var moved = new List<string>(paths.Count);
            var refused = new List<string>();
            var rows = 0;

            foreach (var requested in paths)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var path = Normalise(requested);

                // The library root is not an entry, and "trash everything" is
                // not an operation this screen offers.
                if (path.Length == 0)
                {
                    return FileOperation.Refused("The library root cannot be trashed.");
                }

                // Containment, in the one place it is enforced.
                var absolute = store.AbsolutePathFor(new LibraryPath(path));
                var isDirectory = Directory.Exists(absolute);

                if (!isDirectory && !File.Exists(absolute)) continue;

                var destination = Path.Combine(
                    destinationRoot, path.Replace('/', Path.DirectorySeparatorChar));

                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

                // The log line comes before the move, so a crash part-way
                // through leaves something naming what was being emptied.
                Log.FilesTrashing(logger, path, destination);

                // Per entry, and this is the difference between a report and a
                // lie. RemoveRowsAsync autocommits, so a throw on the third of
                // five used to escape with two albums already in the trash and
                // their rows already deleted — and the endpoint answered "the
                // move failed", so the person was told nothing had happened
                // while two albums had gone, with no journal entry either.
                try
                {
                    // A rename when the trash is a sibling of the library, which
                    // is the default and the reason it is the default. Pointed
                    // at another volume this throws rather than silently copying
                    // an album, and the entry is reported as refused.
                    if (isDirectory) Directory.Move(absolute, destination);
                    else File.Move(absolute, destination);

                    moved.Add(path);

                    // The bytes first, then the rows — a crash between the two
                    // leaves rows for files that are gone, which is precisely
                    // what the next scan is for. The reverse leaves a catalogue
                    // that has forgotten music still sitting on disk.
                    rows += await RemoveRowsAsync(path, cancellationToken).ConfigureAwait(false);

                    var parent = ParentOf(path);
                    if (parent is not null) store.PruneEmptyDirectories(new LibraryPath(parent));
                }
                catch (Exception cause) when (cause is IOException or UnauthorizedAccessException)
                {
                    // Not the containment check — that one is above, outside the
                    // try, because a path escaping the root is a request that
                    // should never have been made rather than an entry that
                    // would not move.
                    Log.FilesTrashRefused(logger, path, cause.Message);
                    refused.Add(path);
                }
            }

            if (moved.Count == 0)
            {
                return FileOperation.Refused(refused.Count == 0
                    ? "Nothing there to move."
                    : $"Nothing moved: {string.Join(", ", refused)}.");
            }

            await JournalAsync(
                "files.trashed",
                stamp,
                new { paths = moved, refused, destination = destinationRoot, catalogueRows = rows },
                cancellationToken).ConfigureAwait(false);

            Log.FilesTrashed(logger, moved.Count, destinationRoot, rows);

            // Applied, with the shortfall named. A partial batch is a success
            // for what moved and has to say what did not — reporting it as a
            // failure is how somebody goes looking in the library for an album
            // that is in the trash.
            return new FileOperation(
                true,
                moved.Count,
                rows,
                destinationRoot,
                refused.Count == 0 ? null : $"{refused.Count} would not move: {string.Join(", ", refused)}.");
        }
    }

    /// <summary>
    /// Renames or moves one entry within the library, carrying its rows with it.
    /// </summary>
    /// <param name="to">The entry's new library-relative path, not its new parent.</param>
    public async Task<FileOperation> MoveAsync(
        string from,
        string to,
        CancellationToken cancellationToken = default)
    {
        var source = Normalise(from);
        var target = Normalise(to);

        if (source.Length == 0) return FileOperation.Refused("The library root cannot be moved.");
        if (target.Length == 0) return FileOperation.Refused("A destination is required.");
        if (source == target) return FileOperation.Refused("That is where it already is.");

        // A folder cannot be moved inside itself: the rename would succeed on
        // some platforms and the prefix rewrite below would produce paths that
        // grow forever.
        if (target.StartsWith(source + "/", StringComparison.Ordinal))
        {
            return FileOperation.Refused($"'{target}' is inside '{source}'.");
        }

        if (!gate.TryEnter(WorkKind, out var lease))
        {
            return FileOperation.Refused($"{gate.ActiveKind} is running. Nothing was moved.");
        }

        using (lease)
        {
            var sourceAbsolute = store.AbsolutePathFor(new LibraryPath(source));
            var targetAbsolute = store.AbsolutePathFor(new LibraryPath(target));

            var isDirectory = Directory.Exists(sourceAbsolute);

            if (!isDirectory && !File.Exists(sourceAbsolute))
            {
                return FileOperation.Refused($"'{source}' is not there.");
            }

            // Never an overwrite. A move that merges two folders or replaces a
            // file is a different act with a different confirmation.
            if (File.Exists(targetAbsolute) || Directory.Exists(targetAbsolute))
            {
                return FileOperation.Refused($"'{target}' already exists.");
            }

            // MediaFiles.Path is unique, so a row left behind by a file deleted
            // outside this application — no scan since — makes the rewrite below
            // collide halfway through. Better a refusal naming the problem than
            // a constraint violation out of an UPDATE.
            if (await HoldsAnythingUnderAsync(target, cancellationToken).ConfigureAwait(false))
            {
                return FileOperation.Refused(
                    $"The catalogue still holds files under '{target}' although nothing is there. "
                    + "Run a scan and try again.");
            }

            // Rows first, then the bytes, then commit. A rename that fails —
            // cross-device, permissions, a name the filesystem refuses — rolls
            // the paths back; the other order can only be undone by a scan, and
            // by then the scan has already discarded everything derived.
            var transaction = await db.Database.BeginTransactionAsync(cancellationToken)
                .ConfigureAwait(false);

            await using (transaction.ConfigureAwait(false))
            {
                var rows = await RewritePathsAsync(source, target, cancellationToken)
                    .ConfigureAwait(false);

                // Before the branch, not inside the file half of it. Moving a
                // folder to a home that does not exist yet is the ordinary case
                // — there is no "create folder" on the screen — and without this
                // Directory.Move throws a DirectoryNotFoundException naming the
                // *source*, so the refusal points at the one path that is fine.
                Directory.CreateDirectory(Path.GetDirectoryName(targetAbsolute)!);

                if (isDirectory) Directory.Move(sourceAbsolute, targetAbsolute);
                else File.Move(sourceAbsolute, targetAbsolute);

                await JournalAsync(
                    "files.moved",
                    $"{source} -> {target}",
                    new { from = source, to = target, catalogueRows = rows },
                    cancellationToken).ConfigureAwait(false);

                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

                // After the commit, so a failure here is tidying that did not
                // happen rather than a move that did not. Reported as a failed
                // move it would send somebody looking for a folder that has
                // already moved.
                try
                {
                    var parent = ParentOf(source);
                    if (parent is not null) store.PruneEmptyDirectories(new LibraryPath(parent));
                }
                catch (Exception cause) when (cause is IOException or UnauthorizedAccessException)
                {
                    Log.FilesTrashRefused(logger, source, cause.Message);
                }

                Log.FilesMoved(logger, source, target, rows);

                return new FileOperation(true, 1, rows, target, null);
            }
        }
    }

    /// <summary>
    /// Writes an uploaded file into the library, staged and swapped into place.
    /// </summary>
    /// <remarks>
    /// No gate: this only adds paths nothing else knows about yet, and the
    /// passes are all worklists over rows that do not exist until a scan. It
    /// writes no rows for the same reason — the scan is the one thing allowed to
    /// decide what is in the catalogue, and it is seconds.
    /// </remarks>
    /// <param name="folder">
    /// The folder being browsed, believed verbatim. It already exists and its
    /// name is a fact about the disk.
    /// </param>
    /// <param name="name">
    /// The file's own path, from the browser — one segment from a file picker,
    /// <c>Album/CD1/01.flac</c> from a directory one. Untrusted, so sanitised.
    /// </param>
    public async Task<FileOperation> SaveAsync(
        string folder,
        string name,
        Stream content,
        long? expectedBytes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);

        // The two halves are sanitised differently, and conflating them was a
        // bug that manufactured the exact confusion this screen exists to
        // resolve. StagedFileName.Segment drops ':' among other characters —
        // and seven album folders in the target library have a colon in them,
        // including the duplicated Brahms one. Run over the browsed prefix, an
        // upload into `Essential Brahms, Volume 1: 50 Tracks…` lands in a NEW
        // sibling called `Essential Brahms, Volume 1 50 Tracks…`: a third
        // duplicate, created silently by the feature meant to remove the second.
        //
        // So the prefix is taken as it is. Containment is Resolve's job and
        // always was; Segment is about names a filesystem or another tool will
        // later refuse, which is a question about the new segments only.
        var parent = Normalise(folder);
        var relative = SanitisePath(name);

        if (relative.Length == 0) return FileOperation.Refused("A file name is required.");

        var target = parent.Length == 0 ? relative : $"{parent}/{relative}";

        var staged = await store
            .OpenForCreateAsync(new LibraryPath(target), expectedBytes ?? 0, cancellationToken)
            .ConfigureAwait(false);

        await using (staged.ConfigureAwait(false))
        {
            await content.CopyToAsync(staged.Content, cancellationToken).ConfigureAwait(false);
            await staged.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        Log.FileUploaded(logger, target);

        return new FileOperation(true, 1, 0, target, null);
    }

    /// <summary>
    /// The catalogue under one folder, grouped onto its immediate children.
    /// </summary>
    /// <remarks>
    /// One query and one pass, rather than a query per row on screen.
    ///
    /// ponytail: the grouping is in memory, so listing the root reads every
    /// MediaFiles row — four small columns, a sequential scan, and fine at this
    /// library's 8,192. Push it into SQL (group on the segment after the prefix)
    /// if the root listing gets slow at 100,000.
    /// </remarks>
    private async Task<Dictionary<string, Rollup>> RollupsAsync(
        string folder,
        CancellationToken cancellationToken)
    {
        var query = db.MediaFiles.AsNoTracking();

        if (folder.Length > 0)
        {
            // The trailing slash, or 'Brahms' also matches 'Brahms Live' and the
            // duplicate this screen exists to find is counted on its neighbour.
            var prefix = folder + "/";
            query = query.Where(file => file.Path.StartsWith(prefix));
        }

        var rows = await query
            .Select(file => new
            {
                file.Path,
                file.SizeBytes,
                // The identification pass's own column, not the enrichment
                // pass's. The pair is what makes a folder's state readable in
                // one glance: "31 files, 0 identified" is AcoustID refusing,
                // and "31 identified, 0 filed" is attribution refusing. Reading
                // RecordingId here would name the second failure after the
                // first and send a person to the wrong screen.
                Identified = file.AcoustId != null,
                Attributed = file.ReleaseId != null,
            })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var rollups = new Dictionary<string, Rollup>(StringComparer.Ordinal);

        foreach (var row in rows)
        {
            if (FolderRollup.Under(folder, row.Path) is not { } child) continue;

            rollups.TryGetValue(child.Name, out var running);

            rollups[child.Name] = new Rollup(
                running.Files + 1,
                running.Identified + (row.Identified ? 1 : 0),
                running.Attributed + (row.Attributed ? 1 : 0),
                running.SizeBytes + row.SizeBytes);
        }

        return rollups;
    }

    /// <summary>Rows for one entry and everything below it.</summary>
    private Task<int> RemoveRowsAsync(string path, CancellationToken cancellationToken)
    {
        var prefix = path + "/";

        return db.MediaFiles
            .Where(file => file.Path == path || file.Path.StartsWith(prefix))
            .ExecuteDeleteAsync(cancellationToken);
    }

    /// <summary>
    /// Repoints one entry and everything below it, leaving every derived column
    /// alone because nothing derived has changed. Same bytes, same size, same
    /// modification time — a rename is not an edit.
    /// </summary>
    private async Task<int> RewritePathsAsync(
        string source,
        string target,
        CancellationToken cancellationToken)
    {
        var exact = await db.MediaFiles
            .Where(file => file.Path == source)
            .ExecuteUpdateAsync(set => set.SetProperty(file => file.Path, target), cancellationToken)
            .ConfigureAwait(false);

        var prefix = source + "/";
        var cut = source.Length;

        // CA1845 wants AsSpan here and is wrong about this one: the lambda is an
        // expression tree that EF translates into a SQL UPDATE, and a span has
        // no translation. Written its way, this stops being a query.
#pragma warning disable CA1845
        var below = await db.MediaFiles
            .Where(file => file.Path.StartsWith(prefix))
            .ExecuteUpdateAsync(
                set => set.SetProperty(file => file.Path, file => target + file.Path.Substring(cut)),
                cancellationToken)
            .ConfigureAwait(false);
#pragma warning restore CA1845

        return exact + below;
    }

    private Task<bool> HoldsAnythingUnderAsync(string path, CancellationToken cancellationToken)
    {
        var prefix = path + "/";

        return db.MediaFiles
            .AsNoTracking()
            .AnyAsync(file => file.Path == path || file.Path.StartsWith(prefix), cancellationToken);
    }

    /// <summary>
    /// One entry in the log per operation, with the paths in the payload.
    /// </summary>
    /// <remarks>
    /// <b>Never keyed by path.</b> <c>DomainEvent.SubjectId</c> is
    /// <c>varchar(200)</c> and a library path runs to 4096 — the lesson the undo
    /// journal already paid for on a box set at file 76. The subject is the
    /// operation; the paths are payload, where length is not a constraint.
    /// </remarks>
    private async Task JournalAsync(
        string type,
        string subject,
        object payload,
        CancellationToken cancellationToken)
    {
        await events.AppendAsync(
            DomainEvent.Create(
                type,
                "library",
                Truncate(subject),
                caller.ActorId,
                clock.UtcNow,
                JsonSerializer.Serialize(payload)),
            cancellationToken).ConfigureAwait(false);

        // AppendAsync does not save itself, by design — it shares whatever scope
        // its caller is committing. Nothing else here writes through the change
        // tracker, so this is the save.
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string Truncate(string subject) =>
        subject.Length <= 200 ? subject : subject[..200];

    private string TrashRoot()
    {
        var configured = options.Value.TrashPath;

        return string.IsNullOrWhiteSpace(configured)
            ? Path.TrimEndingDirectorySeparator(Path.GetFullPath(options.Value.LibraryPath)) + "-trash"
            : Path.TrimEndingDirectorySeparator(Path.GetFullPath(configured));
    }

    /// <summary>A library-relative path with no surrounding or doubled separators.</summary>
    private static string Normalise(string? path) =>
        string.Join('/', (path ?? string.Empty).Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries));

    /// <summary>
    /// The same, with every segment run through the rule that already sanitises
    /// provider metadata.
    /// </summary>
    /// <remarks>
    /// <b>Only for segments the caller is inventing</b> — an uploaded file's own
    /// path, never a folder already on disk. See <see cref="SaveAsync"/> for
    /// what running it over a browsed prefix costs.
    ///
    /// Containment is enforced by the store regardless, but a segment of ".."
    /// reaching that check at all means the refusal is the last line rather than
    /// a formality — and <c>StagedFileName.Segment</c> also removes the control
    /// characters and trailing dots that make a name unopenable later.
    /// </remarks>
    private static string SanitisePath(string? path) =>
        string.Join('/', (path ?? string.Empty)
            .Replace('\\', '/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            // Dropped rather than sanitised. Segment turns ".." into "Unknown"
            // — safe, since containment is Resolve's job either way, but it
            // lands "../../etc/passwd" at "Unknown/Unknown/etc/passwd", which
            // is two directories nobody asked for. A traversal token carries no
            // name, so there is nothing to keep.
            .Where(segment => segment is not "." and not "..")
            .Select(Domain.Acquisition.StagedFileName.Segment));

    private static string? ParentOf(string path)
    {
        if (path.Length == 0) return null;

        var separator = path.LastIndexOf('/');

        return separator < 0 ? string.Empty : path[..separator];
    }

    private readonly record struct Rollup(int Files, int Identified, int Attributed, long SizeBytes);
}

/// <param name="Parent">Null at the root, so the client knows there is no way up.</param>
/// <param name="Exists">
/// False for a folder that is not there — an unmounted volume, or a path
/// somebody kept in a bookmark after trashing it.
/// </param>
public sealed record FolderListing(
    string Path,
    string? Parent,
    bool Exists,
    IReadOnlyList<FolderEntry> Entries);

/// <param name="SizeBytes">
/// The file's own size, or for a directory the catalogued audio beneath it.
/// </param>
/// <param name="CataloguedFiles">
/// Audio files the catalogue holds here or below. Zero for anything not yet
/// scanned, which is not the same as an empty folder.
/// </param>
/// <param name="Identified">
/// Of those, how many carry an AcoustID — what the identification pass decided.
/// </param>
/// <param name="Attributed">
/// Of those, how many are filed under a release — what attribution decided.
/// </param>
public sealed record FolderEntry(
    string Name,
    string Path,
    bool IsDirectory,
    long SizeBytes,
    DateTimeOffset ModifiedUtc,
    bool IsAudio,
    /// <summary>
    /// What can be shown of this file — <c>None</c>, <c>Audio</c>, <c>Image</c>
    /// or <c>Text</c>. Always <c>None</c> for a directory, which has a picture
    /// rather than a preview.
    /// </summary>
    string Kind,
    int CataloguedFiles,
    int Identified,
    int Attributed);

/// <param name="Entries">Entries acted on. Zero on every refusal.</param>
/// <param name="CatalogueRows">Rows removed or repointed.</param>
/// <param name="Destination">Where they went, so a wrong click can be undone by hand.</param>
public sealed record FileOperation(
    bool Applied,
    int Entries,
    int CatalogueRows,
    string? Destination,
    string? Detail)
{
    internal static FileOperation Refused(string detail) => new(false, 0, 0, null, detail);
}
