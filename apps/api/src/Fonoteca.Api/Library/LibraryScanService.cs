using Fonoteca.Api.Logging;
using Fonoteca.Data;
using Fonoteca.Domain.Abstractions;
using Fonoteca.Domain.Catalogue;
using Fonoteca.Ingest;
using Microsoft.EntityFrameworkCore;

namespace Fonoteca.Api.Library;

/// <summary>
/// Reconciles the catalogue's file list with what is actually on disk.
/// </summary>
/// <remarks>
/// The first real feature, and deliberately the smallest one that is useful:
/// it answers "which files exist" and nothing else. No hashing, no probing, no
/// fingerprinting, no identification — those are later passes over the rows
/// this creates, and folding them in now would turn a scan that takes seconds
/// into one that takes hours and cannot be run casually.
///
/// It is a foreground operation on purpose. A walk of 100,000 files is two
/// syscalls each and a handful of batched inserts, which finishes inside an
/// HTTP request; the moment a pass has to open files, it belongs behind
/// <see cref="IJobQueue"/> with progress on <c>JobsHub</c> instead. Treat this
/// class as the thing that gets replaced when that happens, not as the
/// foundation it grows on.
///
/// A singleton holding a gate, because two concurrent scans over one library
/// are never what anyone wanted: they would race on the same unique paths and
/// the loser would fail on a constraint violation halfway through.
/// </remarks>
public sealed class LibraryScanService(
    IServiceScopeFactory scopeFactory,
    FileSystemAudioFileStore store,
    LibraryScanner scanner,
    IClock clock,
    ILogger<LibraryScanService> logger)
{
    /// <summary>
    /// Rows per <c>SaveChanges</c>. Large enough that a 100k-file first scan is
    /// a hundred round trips rather than a hundred thousand, small enough that
    /// a failure loses a bounded amount of work.
    /// </summary>
    private const int InsertBatchSize = 1_000;

    /// <summary>
    /// Ids per <c>WHERE Id = ANY(...)</c>. PostgreSQL will take far more, but a
    /// bounded parameter array keeps the query plan stable and the statement
    /// readable in a log.
    /// </summary>
    private const int IdBatchSize = 500;

    /// <summary>0 while idle, 1 while a scan holds the gate.</summary>
    /// <remarks>
    /// An interlocked flag rather than a <c>SemaphoreSlim</c>: nothing here ever
    /// waits for the gate, so a waitable primitive would only add a disposable
    /// field to a singleton for the sake of a compare-and-swap.
    /// </remarks>
    private int _running;

    private LibraryScanSummary? _lastCompleted;

    public bool IsRunning => Volatile.Read(ref _running) == 1;

    /// <summary>The last scan that finished, or null if none has since startup.</summary>
    /// <remarks>
    /// In memory, so it is forgotten on restart. Persisting scan history means
    /// deciding what a scan <i>is</i> as a first-class entity — id, state
    /// machine, cancellation, retention — and that decision belongs with the
    /// job queue, not with a stopgap.
    /// </remarks>
    public LibraryScanSummary? LastCompleted => _lastCompleted;

    public async Task<LibraryScanOutcome> ScanAsync(CancellationToken cancellationToken = default)
    {
        // Refuse rather than queue. A second request arriving mid-scan is a
        // mistake to report back, not a queue to join — the caller wants to
        // know a scan is already running, not to be blocked for six minutes.
        if (Interlocked.Exchange(ref _running, 1) == 1)
        {
            Log.ScanAlreadyRunning(logger);
            return new LibraryScanOutcome(LibraryScanStatus.AlreadyRunning, null);
        }

        try
        {
            if (!store.RootExists)
            {
                // Refusing beats scanning an absent mount: an empty walk with a
                // full catalogue is indistinguishable from a deleted library.
                Log.ScanRootMissing(logger, store.Root);
                return new LibraryScanOutcome(LibraryScanStatus.LibraryRootMissing, null);
            }

            var summary = await RunAsync(cancellationToken).ConfigureAwait(false);
            _lastCompleted = summary;
            return new LibraryScanOutcome(LibraryScanStatus.Completed, summary);
        }
        finally
        {
            Volatile.Write(ref _running, 0);
        }
    }

    private async Task<LibraryScanSummary> RunAsync(CancellationToken cancellationToken)
    {
        var startedAt = clock.UtcNow;
        Log.ScanStarted(logger, store.Root);

        var scope = scopeFactory.CreateAsyncScope();
        await using (scope.ConfigureAwait(false))
        {
            var db = scope.ServiceProvider.GetRequiredService<FonotecaDbContext>();

            // The entire file list up front, projected to the three columns
            // change detection actually reads. One sequential scan of a few
            // megabytes beats 100,000 point lookups by orders of magnitude, and
            // it is the only way the walk itself stays allocation-cheap.
            var known = await db.MediaFiles
                .AsNoTracking()
                .Select(f => new { f.Id, f.Path, f.SizeBytes, f.LastModifiedUtc })
                .ToDictionaryAsync(
                    f => f.Path,
                    f => new KnownFile(f.Id, f.SizeBytes, f.LastModifiedUtc),
                    StringComparer.Ordinal,
                    cancellationToken)
                .ConfigureAwait(false);

            var vanished = new HashSet<string>(known.Keys, StringComparer.Ordinal);
            var inserts = new List<MediaFile>(InsertBatchSize);
            var changes = new List<ChangedFile>();

            var seen = 0;
            var added = 0;
            var updated = 0;
            var unchanged = 0;

            var report = new LibraryWalkReport();
            var files = scanner.EnumerateAsync(report, cancellationToken).ConfigureAwait(false);

            await foreach (var file in files)
            {
                seen++;

                var path = file.Path.Value;
                var modifiedAt = ToStorePrecision(file.LastModifiedUtc);

                if (!known.TryGetValue(path, out var record))
                {
                    inserts.Add(new MediaFile
                    {
                        Id = MediaFileId.New(),
                        Path = path,
                        SizeBytes = file.SizeBytes,
                        LastModifiedUtc = modifiedAt,
                        LastScannedUtc = startedAt,
                    });

                    added++;

                    if (inserts.Count >= InsertBatchSize)
                    {
                        await FlushInsertsAsync(db, inserts, cancellationToken).ConfigureAwait(false);
                    }

                    continue;
                }

                vanished.Remove(path);

                // Size and mtime, which is what every scanner in this space
                // uses and what a rescan can afford. It misses a file edited
                // in place without changing either — rare, and what the
                // integrity and hashing passes exist to catch.
                if (record.SizeBytes == file.SizeBytes && record.LastModifiedUtc == modifiedAt)
                {
                    unchanged++;
                    continue;
                }

                changes.Add(new ChangedFile(record.Id, file.SizeBytes, modifiedAt));
                updated++;
            }

            await FlushInsertsAsync(db, inserts, cancellationToken).ConfigureAwait(false);
            await ApplyChangesAsync(db, changes, startedAt, cancellationToken).ConfigureAwait(false);

            var removed = await RemoveVanishedAsync(db, known, vanished, seen, report, cancellationToken)
                .ConfigureAwait(false);

            var completedAt = clock.UtcNow;

            var summary = new LibraryScanSummary(
                StartedAtUtc: startedAt,
                CompletedAtUtc: completedAt,
                DurationMilliseconds: (long)(completedAt - startedAt).TotalMilliseconds,
                FilesSeen: seen,
                Added: added,
                Updated: updated,
                Unchanged: unchanged,
                Removed: removed,
                UnreadableDirectories: report.UnreadableDirectories);

            Log.ScanCompleted(
                logger, seen, added, updated, unchanged, removed, summary.DurationMilliseconds);

            return summary;
        }
    }

    private static async Task FlushInsertsAsync(
        FonotecaDbContext db,
        List<MediaFile> inserts,
        CancellationToken cancellationToken)
    {
        if (inserts.Count == 0) return;

        db.MediaFiles.AddRange(inserts);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        // The change tracker keeps everything it has ever seen. Without this a
        // first scan degrades quadratically: each batch's change detection walks
        // a graph containing every row inserted so far.
        db.ChangeTracker.Clear();
        inserts.Clear();
    }

    /// <summary>Rewrites the rows whose file changed on disk.</summary>
    /// <remarks>
    /// Loads only the changed subset. On a steady-state rescan that is a handful
    /// of rows out of 100,000, which is why this is a tracked read-modify-write
    /// rather than the bulk update the first scan would want.
    /// </remarks>
    private static async Task ApplyChangesAsync(
        FonotecaDbContext db,
        List<ChangedFile> changes,
        DateTimeOffset scannedAt,
        CancellationToken cancellationToken)
    {
        if (changes.Count == 0) return;

        foreach (var chunk in changes.Chunk(IdBatchSize))
        {
            var ids = Array.ConvertAll(chunk, c => c.Id);

            var rows = await db.MediaFiles
                .Where(f => ids.Contains(f.Id))
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            var byId = rows.ToDictionary(r => r.Id);

            foreach (var change in chunk)
            {
                // Absent means another writer removed the row between the
                // snapshot and now. Nothing to update, and nothing wrong.
                if (!byId.TryGetValue(change.Id, out var row)) continue;

                row.SizeBytes = change.SizeBytes;
                row.LastModifiedUtc = change.LastModifiedUtc;
                row.LastScannedUtc = scannedAt;

                // The bytes changed, so everything derived from them now
                // describes a file that no longer exists. Clearing costs a
                // recompute; keeping a stale audio hash is what makes dedupe
                // delete the wrong copy.
                row.ContentHash = null;
                row.AudioHash = null;
                row.Fingerprint = null;
                row.FingerprintDuration = null;
                row.Quality = null;
                row.Integrity = IntegrityState.Unchecked;
                row.LastVerifiedUtc = null;

                // The identification too, and for the same reason: an AcoustID
                // describes audio, and these bytes are not the audio it was
                // derived from. Re-encoded, replaced, restored from a different
                // rip — the identifier has to be earned again.
                //
                // Note what makes this safe rather than a treadmill: our own tag
                // writes update SizeBytes and LastModifiedUtc as they commit, so
                // a file this application tagged does not arrive here. Only a
                // file something else changed does. Break that and the two
                // passes undo each other forever.
                row.AcoustId = null;
                row.AcoustIdCheckedUtc = null;
                row.AcoustIdTaggedUtc = null;
                row.AcoustIdOutcome = AcoustIdOutcome.NotAttempted;

                // And what the identification was turned into. The link to a
                // recording rests entirely on the AcoustID cleared above, so
                // leaving it would keep the file filed under an artist on the
                // strength of evidence that has just been withdrawn. The
                // Recording, Artist and Work rows themselves stay — they are
                // shared with every other file that resolved to them, and are
                // facts about MusicBrainz rather than about this file.
                row.RecordingId = null;
                row.RecordingLookupUtc = null;
                row.EnrichmentOutcome = EnrichmentOutcome.NotAttempted;
            }

            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            db.ChangeTracker.Clear();
        }
    }

    private async Task<int> RemoveVanishedAsync(
        FonotecaDbContext db,
        Dictionary<string, KnownFile> known,
        HashSet<string> vanished,
        int seen,
        LibraryWalkReport report,
        CancellationToken cancellationToken)
    {
        if (vanished.Count == 0) return 0;

        // A walk that could not open every directory is not evidence that
        // anything was deleted — the files behind a directory the process cannot
        // read are exactly as invisible as files that no longer exist. Removing
        // on that evidence means one chmod costs a branch of the catalogue and
        // every hash in it.
        if (!report.IsComplete)
        {
            Log.ScanWalkIncomplete(logger, report.UnreadableDirectories, vanished.Count);
            return 0;
        }

        // A root that exists but contains nothing is an unmounted volume far
        // more often than a library someone emptied — an NFS mount that failed
        // to come back after a reboot leaves exactly this: a present, empty
        // directory. Deleting the catalogue there would turn a thirty-second
        // mount problem into a full rescan, so it refuses and says why.
        if (seen == 0 && known.Count > 0)
        {
            Log.ScanFoundNothing(logger, store.Root, known.Count);
            return 0;
        }

        var ids = vanished.Select(path => known[path].Id).ToArray();
        var removed = 0;

        foreach (var chunk in ids.Chunk(IdBatchSize))
        {
            // ExecuteDelete: no reason to materialise rows that are about to
            // stop existing. It bypasses the change tracker, which is safe here
            // because nothing in this scope holds these entities.
            removed += await db.MediaFiles
                .Where(f => chunk.Contains(f.Id))
                .ExecuteDeleteAsync(cancellationToken)
                .ConfigureAwait(false);
        }

        return removed;
    }

    /// <summary>
    /// Truncates a timestamp to what PostgreSQL will actually store.
    /// </summary>
    /// <remarks>
    /// Delegates to <see cref="StoreTime"/>, which is where the rule lives now
    /// that a second pass depends on it: the tag writer floors the modification
    /// time of a file it has just replaced, so this scan sees it as unchanged.
    /// Two copies of the rule that drifted would put the two into a loop.
    /// </remarks>
    private static DateTimeOffset ToStorePrecision(DateTimeOffset value) =>
        StoreTime.ToStorePrecision(value);

    private readonly record struct KnownFile(
        MediaFileId Id,
        long SizeBytes,
        DateTimeOffset LastModifiedUtc);

    private readonly record struct ChangedFile(
        MediaFileId Id,
        long SizeBytes,
        DateTimeOffset LastModifiedUtc);
}

/// <summary>What a scan request did.</summary>
public sealed record LibraryScanOutcome(LibraryScanStatus Status, LibraryScanSummary? Summary);

public enum LibraryScanStatus
{
    /// <summary>The scan ran to completion; <c>Summary</c> is populated.</summary>
    Completed = 0,

    /// <summary>Another scan holds the gate. Nothing was read or written.</summary>
    AlreadyRunning = 1,

    /// <summary>The configured library root is not present — most likely unmounted.</summary>
    LibraryRootMissing = 2,
}

/// <summary>What one scan found.</summary>
/// <remarks>
/// Counts rather than a file list: the interesting question after a scan is
/// "did this do what I expected", and four numbers answer it. The files
/// themselves are in the catalogue, which is queryable.
/// </remarks>
public sealed record LibraryScanSummary(
    DateTimeOffset StartedAtUtc,
    DateTimeOffset CompletedAtUtc,
    long DurationMilliseconds,
    int FilesSeen,
    int Added,
    int Updated,
    int Unchanged,
    int Removed,

    /// <summary>
    /// Directories the walk could not open. Non-zero means this scan saw less
    /// than the whole library, and removed nothing as a result.
    /// </summary>
    int UnreadableDirectories);
