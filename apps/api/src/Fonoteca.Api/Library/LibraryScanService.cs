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
/// it answers "which files exist" and nothing else. No probing, no
/// fingerprinting, no identification — those are later passes over the rows
/// this creates, and folding them in now would turn a scan that takes seconds
/// into one that takes hours and cannot be run casually.
///
/// <b>The one read it does make is for files something else changed.</b> The
/// library is the files, and other programs edit them: a file whose bytes
/// changed has its audio hashed against <c>AudioHash</c>, and a file that left
/// its path — vanished, or replaced there by other audio — is looked for where
/// files arrived. A file this application wrote never gets here (rule 2), and
/// an untouched library reads nothing.
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
    FfmpegAudioHasher hasher,
    Fonoteca.Tagging.TagReader tagReader,
    IClock clock,
    PlaylistFiles playlists,
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

            LibraryScanSummary summary;

            try
            {
                summary = await RunAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (AudioProbeFailedException cause) when (!cause.IsFileFault)
            {
                // Every hash precedes every write, so this changed nothing.
                // Carrying on would read every retagged file as new audio.
                Log.ScanHasherUnavailable(logger, cause.Message);
                return new LibraryScanOutcome(LibraryScanStatus.HasherUnavailable, null);
            }

            _lastCompleted = summary;

            // The one piece of library work that never takes the gate, so
            // the smart playlists are not refreshed by its release.
            playlists.Refresh();
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

            // The entire file list up front, projected to the columns change
            // detection actually reads. One sequential scan of a few
            // megabytes beats 100,000 point lookups by orders of magnitude, and
            // it is the only way the walk itself stays allocation-cheap.
            var known = await db.MediaFiles
                .AsNoTracking()
                .Select(f => new { f.Id, f.Path, f.SizeBytes, f.LastModifiedUtc, f.AudioHash })
                .ToDictionaryAsync(
                    f => f.Path,
                    f => new KnownFile(f.Id, f.SizeBytes, f.LastModifiedUtc, f.AudioHash),
                    StringComparer.Ordinal,
                    cancellationToken)
                .ConfigureAwait(false);

            var vanished = new HashSet<string>(known.Keys, StringComparer.Ordinal);
            var walked = new List<string>();
            var arrived = new List<ScannedFile>();
            var changes = new List<ChangedFile>();

            var unchanged = 0;

            var report = new LibraryWalkReport();
            var files = scanner.EnumerateAsync(report, cancellationToken).ConfigureAwait(false);

            await foreach (var file in files)
            {
                var path = file.Path.Value;
                var modifiedAt = ToStorePrecision(file.LastModifiedUtc);

                walked.Add(path);

                if (!known.TryGetValue(path, out var record))
                {
                    arrived.Add(file with { LastModifiedUtc = modifiedAt });
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

                changes.Add(new ChangedFile(record.Id, path, file.SizeBytes, modifiedAt, record.AudioHash));
            }

            // Every hash is taken before anything is written, so a hasher that
            // cannot run leaves the catalogue as it was. A move is only looked
            // for on the evidence that would delete the row: a walk that
            // missed a directory has not seen the file gone.
            var hashed = new Dictionary<string, string>(StringComparer.Ordinal);

            for (var index = 0; index < changes.Count; index++)
            {
                if (changes[index].AudioHash is null) continue;

                var now = await hasher.HashAsync(new LibraryPath(changes[index].Path), cancellationToken)
                    .ConfigureAwait(false);

                changes[index] = changes[index] with { HashNow = now };
                if (now is not null) hashed[changes[index].Path] = now;
            }

            // A changed path that no longer holds its row's audio is both a
            // place a file left and a place one arrived: another program
            // renumbering an album moves each file onto a name another held.
            var displaced = changes.Where(change => !change.SameAudio).ToList();

            List<Source> sources =
            [
                .. vanished.Select(path => new Source(path, known[path])),
                .. displaced.Select(change => new Source(change.Path, known[change.Path])),
            ];

            List<Destination> destinations =
            [
                .. arrived.Select(file => new Destination(file, null)),
                .. displaced.Select(change => new Destination(
                    new ScannedFile(new LibraryPath(change.Path), change.SizeBytes, change.LastModifiedUtc),
                    change.HashNow)),
            ];

            var moves = report.IsComplete
                ? await MatchMovesAsync(sources, destinations, hashed, cancellationToken).ConfigureAwait(false)
                : [];

            var moving = moves.Select(move => move.Id).ToHashSet();
            var landed = moves.Select(move => move.To.Path.Value).ToHashSet(StringComparer.Ordinal);

            vanished.ExceptWith(moves.Select(move => move.From));
            arrived.RemoveAll(file => landed.Contains(file.Path.Value));

            // Taken by another row while its own audio went nowhere this walk
            // saw: gone, as a vanished row is. Left by its row and taken by
            // none: a new file there.
            var overwritten = displaced
                .Where(change => landed.Contains(change.Path) && !moving.Contains(change.Id))
                .Select(change => change.Id)
                .ToList();

            arrived.AddRange(displaced
                .Where(change => moving.Contains(change.Id) && !landed.Contains(change.Path))
                .Select(change => new ScannedFile(new LibraryPath(change.Path), change.SizeBytes, change.LastModifiedUtc)));

            changes.RemoveAll(change => moving.Contains(change.Id) || landed.Contains(change.Path));

            var keepsAlbum = KeepingTheirAlbum(moves, known.Keys, walked);

            var numbers = new Dictionary<MediaFileId, (int? Disc, int? Track)>();

            foreach (var move in moves.Where(move => !move.SameBytes))
            {
                numbers[move.Id] = await ReleaseAttributionService
                    .TagNumbersAsync(tagReader, move.To.Path, cancellationToken)
                    .ConfigureAwait(false);
            }

            await ApplyMovesAsync(db, moves, overwritten, keepsAlbum, numbers, startedAt, cancellationToken)
                .ConfigureAwait(false);
            await ApplyChangesAsync(db, changes, startedAt, cancellationToken).ConfigureAwait(false);

            foreach (var chunk in arrived.Chunk(InsertBatchSize))
            {
                await InsertAsync(db, chunk, hashed, startedAt, cancellationToken).ConfigureAwait(false);
            }

            var removed = overwritten.Count
                + await RemoveVanishedAsync(db, known, vanished, walked.Count, report, cancellationToken)
                    .ConfigureAwait(false);

            var completedAt = clock.UtcNow;

            var summary = new LibraryScanSummary(
                StartedAtUtc: startedAt,
                CompletedAtUtc: completedAt,
                DurationMilliseconds: (long)(completedAt - startedAt).TotalMilliseconds,
                FilesSeen: walked.Count,
                Added: arrived.Count,
                Updated: changes.Count,
                Unchanged: unchanged,
                Moved: moves.Count,
                Removed: removed,
                UnreadableDirectories: report.UnreadableDirectories);

            Log.ScanCompleted(
                logger, walked.Count, arrived.Count, changes.Count, unchanged, moves.Count, removed,
                summary.DurationMilliseconds);

            return summary;
        }
    }

    /// <summary>
    /// Pairs each place a file left with the place it arrived, where exactly
    /// one fits.
    /// </summary>
    /// <remarks>
    /// <b>Another program moving a file is not the file going away.</b> Read as a
    /// deletion and an arrival, a rename by a tagger or a file manager takes
    /// every AcoustID, album, measurement and answer a person gave with the row,
    /// and the undo journal is keyed by the row.
    ///
    /// Two pieces of evidence, cheapest first. A rename keeps the size and the
    /// mtime, so those match without opening anything. A program that retags on
    /// the way changes both, so the audio hash is asked next — of a destination
    /// only where a row that left has a hash to match and shares its extension,
    /// which keeps a first scan and a new album from being read at all. Two
    /// candidates either side is no answer: the file is a deletion and an
    /// arrival, as it always was. A place cannot pair with itself: its size and
    /// mtime changed, and its hash is not its row's.
    /// </remarks>
    private async Task<List<Move>> MatchMovesAsync(
        List<Source> sources,
        List<Destination> destinations,
        Dictionary<string, string> hashed,
        CancellationToken cancellationToken)
    {
        var moves = new List<Move>();

        if (sources.Count == 0 || destinations.Count == 0) return moves;

        foreach (var (from, to) in Unique(
            sources,
            destinations,
            source => (source.Was.SizeBytes, source.Was.LastModifiedUtc, Extension(source.Path)),
            destination => (destination.File.SizeBytes, destination.File.LastModifiedUtc, Extension(destination.File.Path.Value))))
        {
            moves.Add(new Move(from.Was.Id, from.Path, to.File, SameBytes: true));
        }

        var moving = moves.Select(move => move.Id).ToHashSet();
        var landed = moves.Select(move => move.To.Path.Value).ToHashSet(StringComparer.Ordinal);

        var hashedGone = sources.Where(source => !moving.Contains(source.Was.Id) && source.Was.AudioHash is not null).ToList();
        var extensions = hashedGone.Select(source => Extension(source.Path)).ToHashSet(StringComparer.Ordinal);

        if (extensions.Count == 0) return moves;

        // ponytail: serial, one read per candidate; parallel if a scan after a
        // bulk retag is measured slow on a disk that is not the bottleneck.
        var candidates = new List<(ScannedFile File, string Hash)>();

        foreach (var destination in destinations.Where(destination =>
            !landed.Contains(destination.File.Path.Value) && extensions.Contains(Extension(destination.File.Path.Value))))
        {
            var hash = destination.Hash
                ?? await hasher.HashAsync(destination.File.Path, cancellationToken).ConfigureAwait(false);

            if (hash is null) continue;

            hashed[destination.File.Path.Value] = hash;
            candidates.Add((destination.File, hash));
        }

        foreach (var (from, to) in Unique(
            hashedGone,
            candidates,
            source => (source.Was.AudioHash!, Extension(source.Path)),
            candidate => (candidate.Hash, Extension(candidate.File.Path.Value))))
        {
            moves.Add(new Move(from.Was.Id, from.Path, to.File, SameBytes: false));
        }

        return moves;
    }

    /// <summary>The pairs whose key is held by exactly one item on each side.</summary>
    private static List<(TLeft Left, TRight Right)> Unique<TLeft, TRight, TKey>(
        IEnumerable<TLeft> left,
        IEnumerable<TRight> right,
        Func<TLeft, TKey> leftKey,
        Func<TRight, TKey> rightKey)
        where TKey : notnull
    {
        var lefts = left
            .GroupBy(leftKey)
            .Where(group => group.Count() == 1)
            .ToDictionary(group => group.Key, group => group.Single());

        return right
            .GroupBy(rightKey)
            .Where(group => group.Count() == 1 && lefts.ContainsKey(group.Key))
            .Select(group => (lefts[group.Key], group.Single()))
            .ToList();
    }

    private static string Extension(string path) => System.IO.Path.GetExtension(path).ToUpperInvariant();

    /// <summary>The moved files whose album folder went whole to one new place.</summary>
    /// <remarks>
    /// The album folder is what attribution decided, and the names inside it are
    /// evidence for its order (<c>FolderOrder</c>). So a moved file keeps its
    /// album's stamp only where every file the folder held went to one new
    /// folder that holds nothing else, each at the same path inside it — an
    /// album or artist folder renamed. Anything else puts the folder back
    /// through attribution, which reads a person's answers and keeps them.
    /// </remarks>
    private static HashSet<MediaFileId> KeepingTheirAlbum(
        List<Move> moves,
        IEnumerable<string> before,
        IEnumerable<string> after)
    {
        if (moves.Count == 0) return [];

        var was = before.CountBy(AlbumFolder.Of).ToDictionary();
        var now = after.CountBy(AlbumFolder.Of).ToDictionary();

        return moves
            .GroupBy(move => (From: AlbumFolder.Of(move.From), To: AlbumFolder.Of(move.To.Path.Value)))
            .Where(group => group.Key.From != group.Key.To
                && group.Count() == was[group.Key.From]
                && group.Count() == now[group.Key.To]
                && group.All(move => move.From[group.Key.From.Length..] == move.To.Path.Value[group.Key.To.Length..]))
            .SelectMany(group => group)
            .Select(move => move.Id)
            .ToHashSet();
    }

    /// <summary>Points each moved row at the path its file now has.</summary>
    /// <remarks>
    /// One transaction, and every moving row parked on a path no file can have
    /// before any takes its new one: a renumbered album moves each file onto a
    /// name another row still holds, and <c>Path</c> is unique. Through the
    /// execution strategy, which refuses a transaction it did not open; the
    /// delegate may run twice, so it starts from nothing each time.
    /// </remarks>
    private static async Task ApplyMovesAsync(
        FonotecaDbContext db,
        List<Move> moves,
        List<MediaFileId> overwritten,
        HashSet<MediaFileId> keepsAlbum,
        Dictionary<MediaFileId, (int? Disc, int? Track)> numbers,
        DateTimeOffset scannedAt,
        CancellationToken cancellationToken)
    {
        if (moves.Count == 0) return;

        // ponytail: every moved row tracked at once; chunk the two phases if a
        // whole-library rename is measured slow.
        await db.Database.CreateExecutionStrategy().ExecuteAsync(
            async token =>
            {
                db.ChangeTracker.Clear();

                var transaction = await db.Database.BeginTransactionAsync(token).ConfigureAwait(false);

                await using (transaction.ConfigureAwait(false))
                {
                    foreach (var chunk in overwritten.Chunk(IdBatchSize))
                    {
                        await db.MediaFiles
                            .Where(f => chunk.Contains(f.Id))
                            .ExecuteDeleteAsync(token)
                            .ConfigureAwait(false);
                    }

                    var rows = new List<(Move Move, MediaFile Row)>();

                    foreach (var chunk in moves.Chunk(IdBatchSize))
                    {
                        var ids = Array.ConvertAll(chunk, move => move.Id);

                        var byId = await db.MediaFiles
                            .Where(f => ids.Contains(f.Id))
                            .ToDictionaryAsync(f => f.Id, token)
                            .ConfigureAwait(false);

                        foreach (var move in chunk)
                        {
                            if (!byId.TryGetValue(move.Id, out var row)) continue;

                            // A library path is relative, so one starting with a slash is nobody's.
                            row.Path = $"/moving/{row.Id.Value}";
                            rows.Add((move, row));
                        }
                    }

                    await db.SaveChangesAsync(token).ConfigureAwait(false);

                    foreach (var (move, row) in rows)
                    {
                        row.Path = move.To.Path.Value;
                        row.LastScannedUtc = scannedAt;

                        if (!move.SameBytes)
                        {
                            Retagged(row, move.To.SizeBytes, move.To.LastModifiedUtc, numbers[move.Id]);
                        }

                        if (!keepsAlbum.Contains(move.Id)) CatalogueWriter.ReopenAttribution(row);
                    }

                    await db.SaveChangesAsync(token).ConfigureAwait(false);
                    await transaction.CommitAsync(token).ConfigureAwait(false);
                }
            },
            cancellationToken).ConfigureAwait(false);

        db.ChangeTracker.Clear();
    }

    /// <summary>
    /// The same audio in new bytes: another program retagged the file, and
    /// only what was read off its tags is in doubt.
    /// </summary>
    /// <remarks>
    /// Identity, measurements, the album and every answer a person gave are
    /// about the audio, and stay. The file's own track and disc numbers are
    /// read again, and where they moved the folder goes back through
    /// attribution, since they are its order.
    /// </remarks>
    private static void Retagged(
        MediaFile row,
        long sizeBytes,
        DateTimeOffset modifiedAt,
        (int? Disc, int? Track) numbers)
    {
        row.SizeBytes = sizeBytes;
        row.LastModifiedUtc = modifiedAt;
        row.ContentHash = null;

        // Whether the file still carries its AcoustID is the file's to say again.
        row.AcoustIdTaggedUtc = null;

        if (numbers.Disc == row.TagDiscNumber && numbers.Track == row.TagTrackNumber) return;

        row.TagDiscNumber = numbers.Disc;
        row.TagTrackNumber = numbers.Track;
        CatalogueWriter.ReopenAttribution(row);
    }

    private static async Task InsertAsync(
        FonotecaDbContext db,
        ScannedFile[] files,
        Dictionary<string, string> hashed,
        DateTimeOffset scannedAt,
        CancellationToken cancellationToken)
    {
        db.MediaFiles.AddRange(files.Select(file => new MediaFile
        {
            Id = MediaFileId.New(),
            Path = file.Path.Value,
            SizeBytes = file.SizeBytes,
            LastModifiedUtc = file.LastModifiedUtc,
            LastScannedUtc = scannedAt,

            // Read while looking for a move it turned out not to be.
            AudioHash = hashed.GetValueOrDefault(file.Path.Value),
        }));

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        // The change tracker keeps everything it has ever seen. Without this a
        // first scan degrades quadratically: each batch's change detection walks
        // a graph containing every row inserted so far.
        db.ChangeTracker.Clear();
    }

    /// <summary>Rewrites the rows whose file changed on disk.</summary>
    /// <remarks>
    /// Loads only the changed subset. On a steady-state rescan that is a handful
    /// of rows out of 100,000, which is why this is a tracked read-modify-write
    /// rather than the bulk update the first scan would want.
    /// </remarks>
    private async Task ApplyChangesAsync(
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

                row.LastScannedUtc = scannedAt;

                if (change.SameAudio)
                {
                    var numbers = await ReleaseAttributionService
                        .TagNumbersAsync(tagReader, new LibraryPath(row.Path), cancellationToken)
                        .ConfigureAwait(false);

                    Retagged(row, change.SizeBytes, change.LastModifiedUtc, numbers);
                    continue;
                }

                row.SizeBytes = change.SizeBytes;
                row.LastModifiedUtc = change.LastModifiedUtc;

                // The audio changed — or nothing hashed it before, so nothing can
                // say it did not — and everything derived from the bytes now
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
                row.Measurement = null;
                row.MeasuredUtc = null;
                row.TempoAndKey = null;
                row.TempoAndKeyMeasuredUtc = null;

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

                // Including the record of having contributed the fingerprint
                // back, which reads at first like history rather than a derived
                // fact and is not: what it does is suppress the offer to submit,
                // and it was earned by a fingerprint of audio that has gone. Kept
                // through a replacement, the new audio would be filed under an
                // album, be eligible to contribute, and never be offered — the
                // one shape of staleness that shows up as nothing happening. The
                // history is in the event log, which is where it belongs.
                row.AcoustIdSubmittedUtc = null;

                // Including an answer a person gave, and that is not a slight on
                // the person. They decided what *those* bytes were, and these are
                // different bytes; a decision about audio that is no longer here
                // is not a decision about the audio that is.
                //
                // Leaving it is worse than losing it, and specifically: both
                // passes' worklists exclude a decided stamp (identification
                // AcoustIdDecidedUtc, enrichment IdentityDecidedUtc), and the reset
                // above sets AcoustIdOutcome to NotAttempted, which the worklist
                // of refusals deliberately does not count as a question. A file
                // keeping this stamp through a replacement would be invisible to
                // every pass and to the screen at once, with nothing in the
                // application able to reach it again.
                row.IdentityDecidedUtc = null;
                row.AcoustIdDecidedUtc = null;

                // And the evidence, which is the sharpest of these. It is
                // believed for a week, and the candidates endpoint checks that
                // freshness *before* it checks for a fingerprint — so a stale
                // answer here does not merely go unused, it answers. Left in
                // place, opening this file would offer the recordings the
                // previous audio matched, and committing one of them would write
                // that cluster into the new bytes. That is the exact thing the
                // paragraph above refuses to let happen by another route.
                row.AcoustIdMatchesJson = null;
                row.AcoustIdMatchesUtc = null;
                row.RecordingCandidatesJson = null;
                row.RecordingCandidatesUtc = null;

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

                // And the album, which rests on the recording in turn. It also
                // rests on something the other resets do not: attribution chose
                // this edition by comparing the file's *measured length* against
                // each edition's printed one. New bytes are a new length, so the
                // evidence that picked this pressing over its remaster is
                // precisely what has just been invalidated.
                row.ReleaseId = null;
                row.TrackId = null;
                row.ReleaseGroupId = null;
                row.ReleaseLookupUtc = null;
                row.AttributionOutcome = ReleaseAttributionOutcome.NotAttempted;
                row.EditionAlternatives = 0;

                // The file's own numbers and its place in the folder's order were
                // read off the old bytes, under the stamp just cleared.
                row.TagDiscNumber = null;
                row.TagTrackNumber = null;
                row.FolderPosition = null;
                row.OrderOutcome = FolderOrderOutcome.NotChecked;

                // Including a person's answer, for the reason the identity
                // decision above is cleared: a file that kept the guard through a
                // replacement would be excluded from the pass's worklist and
                // carry NotAttempted, which the open-questions screen does not
                // count as a question either — invisible to both at once, with
                // nothing able to reach it again.
                row.ReleaseDecidedUtc = null;
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
        DateTimeOffset LastModifiedUtc,
        string? AudioHash);

    private readonly record struct ChangedFile(
        MediaFileId Id,
        string Path,
        long SizeBytes,
        DateTimeOffset LastModifiedUtc,
        string? AudioHash)
    {
        /// <summary>The new bytes' hash, taken only where the row has one to compare.</summary>
        public string? HashNow { get; init; }

        /// <summary>True when the new bytes hash to the audio the row was measured from.</summary>
        public bool SameAudio => AudioHash is not null && HashNow == AudioHash;
    }

    /// <summary>A row whose file left its path: vanished, or displaced by other audio.</summary>
    private readonly record struct Source(string Path, KnownFile Was);

    /// <summary>A file at a path no row's own audio holds: arrived, or displacing another.</summary>
    private readonly record struct Destination(ScannedFile File, string? Hash);

    /// <param name="SameBytes">Matched on size and mtime, so nothing about the file changed but its path.</param>
    private readonly record struct Move(MediaFileId Id, string From, ScannedFile To, bool SameBytes);
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

    /// <summary>A changed file needed hashing and ffmpeg could not be run. Nothing was written.</summary>
    HasherUnavailable = 3,
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

    /// <summary>Files found at a new path and kept as the row they were.</summary>
    int Moved,
    int Removed,

    /// <summary>
    /// Directories the walk could not open. Non-zero means this scan saw less
    /// than the whole library, and removed nothing as a result.
    /// </summary>
    int UnreadableDirectories);
