using System.Diagnostics;
using System.Threading.Channels;
using Fonoteca.Api.Configuration;
using Fonoteca.Api.Logging;
using Fonoteca.Api.Realtime;
using Fonoteca.Data;
using Fonoteca.Domain.Abstractions;
using Fonoteca.Domain.Catalogue;
using Fonoteca.Domain.Identification;
using Fonoteca.Ingest;
using Fonoteca.Tagging;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Fonoteca.Api.Library;

/// <summary>
/// Fingerprints the files that have no AcoustID, looks them up, and tags them.
/// </summary>
/// <remarks>
/// The first pass in this application that opens files, and therefore the first
/// that cannot run inside an HTTP request. A walk of 100,000 files is two
/// syscalls each and finishes in seconds; this spawns a subprocess per file and
/// then queues behind somebody else's rate limit for the better part of an hour.
/// <see cref="Start"/> returns immediately with a job id and the work continues
/// on a background task, reporting on <c>JobsHub</c>.
///
/// <b>Two costs, of different kinds, so two stages.</b> Fingerprinting is local
/// CPU and disk and parallelises across <c>Fonoteca:ScanConcurrency</c>. Lookups
/// are serialised at 340ms by <c>RequestGate</c> — a limit AcoustID enforces by
/// blocking, so running more of them concurrently buys no throughput at all and
/// only pins more fingerprints in memory. A bounded channel between the two lets
/// the fast stage run ahead without running away: at a concurrency of four,
/// stage A produces one every ~48ms and stage B consumes one every 340ms, so the
/// gate is the bottleneck by seven times over.
///
/// The tag write sits in stage B rather than stage A on purpose. It means at
/// most one staging file exists at a time — bounding the extra disk to one
/// file's size rather than four — and the rewrite hides inside the 340ms the
/// gate hands us anyway.
///
/// <b>Durability comes from the catalogue, not from a queue.</b> The worklist is
/// a query, and each file's outcome is committed as it is produced, so a process
/// killed at any moment leaves work that the next run simply picks up. That is
/// also why per-file <c>SaveChanges</c> is not batched: 7,735 round trips to a
/// local PostgreSQL at about a millisecond each are invisible next to 340ms per
/// file, and they are the entire resumability story. See ADR 0007.
/// </remarks>
public sealed class IdentificationService(
    LibraryWorkGate gate,
    IServiceScopeFactory scopeFactory,
    FileSystemAudioFileStore store,
    IAudioFingerprinter fingerprinter,
    IAcoustIdLookup acoustId,
    TagReader tagReader,
    TagWriterOptions writerOptions,
    IHubContext<JobsHub, IJobsClient> hub,
    IHostApplicationLifetime lifetime,
    IClock clock,
    IOptions<FonotecaOptions> options,
    ILogger<IdentificationService> logger) : IHostedService, IDisposable
{
    /// <summary>The kind this pass takes the gate as, and the job kind on the wire.</summary>
    public const string JobKind = "library.identify";

    /// <summary>Rows claimed per query. Bounded so a cancelled pass stops promptly.</summary>
    private const int PageSize = 500;

    /// <summary>
    /// Files a fingerprinter may run ahead of the gate.
    /// </summary>
    /// <remarks>
    /// Small deliberately. The gate is seven times slower than fingerprinting,
    /// so anything larger just holds more base64 in memory to no purpose, and an
    /// unbounded channel would fingerprint the entire library into RAM while the
    /// first hundred lookups were still queuing.
    /// </remarks>
    private const int LookaheadFactor = 2;

    /// <summary>
    /// How often progress reaches the browser.
    /// </summary>
    /// <remarks>
    /// Not per file. 7,735 SignalR frames is noise no display can render, and
    /// each one is also a database write for the reconnect case.
    /// </remarks>
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromMilliseconds(250);

    /// <summary>Staging files older than this are somebody's interrupted run, not a live write.</summary>
    private static readonly TimeSpan StaleStagingAge = TimeSpan.FromHours(1);

    private readonly SemaphoreSlim _finished = new(0, 1);

    private CancellationTokenSource? _cancellation;
    private volatile IdentificationProgress? _progress;
    private volatile IdentificationSummary? _lastCompleted;

    public bool IsRunning => _progress is not null;

    /// <summary>Where the running pass has got to, or null when nothing is running.</summary>
    public IdentificationProgress? Progress => _progress;

    /// <summary>
    /// The last pass that finished since startup.
    /// </summary>
    /// <remarks>
    /// In memory, like the scan's. Persisting run history means deciding what a
    /// run <i>is</i> as an entity — id, state machine, retention — and that
    /// decision belongs with a job queue rather than with the first pass that
    /// wanted it.
    /// </remarks>
    public IdentificationSummary? LastCompleted => _lastCompleted;

    /// <summary>Whether this pass would write tags, or only work out what they should be.</summary>
    public bool WritesTags => writerOptions.AllowFileMutation;

    /// <summary>Files with no AcoustID yet — the size of the job, before starting one.</summary>
    public async Task<int> CountPendingAsync(CancellationToken cancellationToken = default)
    {
        var scope = scopeFactory.CreateAsyncScope();
        await using (scope.ConfigureAwait(false))
        {
            var db = scope.ServiceProvider.GetRequiredService<FonotecaDbContext>();

            return await db.MediaFiles
                .Where(f => f.AcoustIdCheckedUtc == null)
                .CountAsync(cancellationToken)
                .ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Starts a pass, or reports why it did not.
    /// </summary>
    /// <remarks>
    /// Returns as soon as the work is launched. The alternative — awaiting a
    /// forty-minute pass inside a request — is what the 202 exists to avoid.
    /// </remarks>
    public IdentificationOutcome Start()
    {
        if (!store.RootExists)
        {
            return new IdentificationOutcome(IdentificationStatus.LibraryRootMissing, null);
        }

        if (!gate.TryEnter(JobKind, out var lease))
        {
            Log.IdentificationBusy(logger, gate.ActiveKind ?? "other work");
            return new IdentificationOutcome(IdentificationStatus.AlreadyRunning, null);
        }

        var jobId = Guid.CreateVersion7().ToString("N")[..12];

        // Linked to ApplicationStopping so a container stop cancels the pass
        // rather than severing it mid-write. StopAsync then waits for it.
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.ApplicationStopping);
        _cancellation = cancellation;

        _progress = new IdentificationProgress(jobId, 0, 0, null);

        _ = Task.Run(
            async () =>
            {
                try
                {
                    _lastCompleted = await RunAsync(jobId, cancellation.Token).ConfigureAwait(false);
                }
#pragma warning disable CA1031 // A background pass must never take the process down.
                catch (Exception cause)
#pragma warning restore CA1031
                {
                    Log.IdentificationAborted(logger, cause.Message);
                }
                finally
                {
                    _progress = null;
                    _cancellation = null;
                    cancellation.Dispose();
                    lease.Dispose();
                    _finished.Release();
                }
            },
            CancellationToken.None);

        return new IdentificationOutcome(IdentificationStatus.Started, jobId);
    }

    /// <summary>Asks the running pass to stop. It finishes the file it is on.</summary>
    public bool Cancel()
    {
        var running = _cancellation;

        if (running is null) return false;

        running.Cancel();
        return true;
    }

    /// <summary>Releases the shutdown latch. The pass itself owns nothing else.</summary>
    public void Dispose() => _finished.Dispose();

    Task IHostedService.StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// Stops a running pass and waits for it, rather than letting the host
    /// disappear underneath a half-written file.
    /// </summary>
    async Task IHostedService.StopAsync(CancellationToken cancellationToken)
    {
        if (!IsRunning) return;

        Cancel();

        // Bounded: shutdown must not hang on a pass that will not stop. The
        // staged write is abandoned rather than committed either way, so the
        // worst case is a temporary file the next run sweeps.
        await _finished.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
    }

    private async Task<IdentificationSummary> RunAsync(string jobId, CancellationToken cancellationToken)
    {
        var config = options.Value;
        var startedAt = clock.UtcNow;
        var elapsed = Stopwatch.StartNew();
        var counts = new Tally();

        var swept = store.RemoveStaleStagingFiles(StaleStagingAge, startedAt);
        if (swept > 0) Log.StagingFilesSwept(logger, swept);

        // One check, before 7,735 identical failures. A missing binary is a
        // configuration problem and has to look like one.
        if (fingerprinter is FpcalcFingerprinter fpcalc)
        {
            await fpcalc.VerifyAvailableAsync(cancellationToken).ConfigureAwait(false);
        }

        var pending = await CountPendingAsync(cancellationToken).ConfigureAwait(false);

        Log.IdentificationStarted(
            logger, jobId, pending, writerOptions.AllowFileMutation ? "enabled" : "DISABLED (dry run)");

        if (!writerOptions.AllowFileMutation) Log.IdentificationWillNotWrite(logger);

        _progress = new IdentificationProgress(jobId, 0, pending, null);

        var correlationId = jobId;
        var lookahead = Math.Max(2, config.ScanConcurrency * LookaheadFactor);
        var channel = Channel.CreateBounded<Fingerprinted>(new BoundedChannelOptions(lookahead)
        {
            SingleReader = true,
            FullMode = BoundedChannelFullMode.Wait,
        });

        // A stage B that stops — for any reason — must release stage A.
        //
        // Without this the two stages deadlock on their own backpressure. The
        // channel is bounded, so stage A blocks in WriteAsync once it is full;
        // if stage B has died, nothing will ever drain it, and Task.WhenAll goes
        // on waiting for a producer that cannot finish. The consumer's exception
        // is then never observed, RunAsync never returns, and the pass hangs
        // *silently* — no summary, no log line, the status endpoint still
        // reporting "running" at whatever file it reached. That is exactly how a
        // single unreadable file stopped a 7,317-file pass dead at 370 with
        // nothing in the log to say so.
        //
        // Cancelling on the consumer's way out, whatever the reason, is what
        // turns that into a prompt, loud failure.
        using var abort = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        var produce = ProduceAsync(channel.Writer, abort.Token);
        var consume = ConsumeThenReleaseAsync(
            channel.Reader, counts, jobId, pending, correlationId, config, abort, cancellationToken);

        await Task.WhenAll(produce, consume).ConfigureAwait(false);

        var completedAt = clock.UtcNow;

        var summary = new IdentificationSummary(
            JobId: jobId,
            StartedAtUtc: startedAt,
            CompletedAtUtc: completedAt,
            DurationMilliseconds: elapsed.ElapsedMilliseconds,
            Examined: counts.Examined,
            AlreadyTagged: counts.Adopted,
            Fingerprinted: counts.Fingerprinted,
            Identified: counts.Identified,
            Unknown: counts.Unknown,
            Ambiguous: counts.Ambiguous,
            BelowThreshold: counts.BelowThreshold,
            Unfingerprintable: counts.Unfingerprintable,
            Tagged: counts.Tagged,
            WriteRefused: counts.WriteRefused,
            TagUnreadable: counts.TagUnreadable,
            Failed: counts.Failed,
            Cancelled: cancellationToken.IsCancellationRequested);

        Log.IdentificationCompleted(
            logger, jobId, counts.Identified, counts.Adopted, counts.Unknown, counts.Ambiguous,
            counts.Tagged, counts.WriteRefused, counts.Failed, summary.DurationMilliseconds);

        await Report(jobId, counts.Examined, pending, null, "completed").ConfigureAwait(false);

        return summary;
    }

    /// <summary>Stage A: read the existing tag, else fingerprint. Parallel.</summary>
    private async Task ProduceAsync(
        ChannelWriter<Fingerprinted> writer,
        CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var file in ClaimAsync(cancellationToken).ConfigureAwait(false))
            {
                cancellationToken.ThrowIfCancellationRequested();
                await writer.WriteAsync(file, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on cancel. The consumer drains what is already queued.
        }
        finally
        {
            writer.TryComplete();
        }
    }

    /// <summary>
    /// The worklist, fingerprinted <c>ScanConcurrency</c> at a time.
    /// </summary>
    /// <remarks>
    /// Paged rather than loaded whole: 100,000 rows of path and id is a few
    /// megabytes, but claiming a page at a time means a cancelled pass stops
    /// after the current page rather than after the current library.
    /// </remarks>
    private async IAsyncEnumerable<Fingerprinted> ClaimAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var config = options.Value;
        var seen = new HashSet<MediaFileId>();

        while (!cancellationToken.IsCancellationRequested)
        {
            List<PendingFile> page;

            var scope = scopeFactory.CreateAsyncScope();
            await using (scope.ConfigureAwait(false))
            {
                var db = scope.ServiceProvider.GetRequiredService<FonotecaDbContext>();

                page = await db.MediaFiles
                    .AsNoTracking()
                    .Where(f => f.AcoustIdCheckedUtc == null)
                    .OrderBy(f => f.Id)
                    .Select(f => new PendingFile(f.Id, f.Path, f.Fingerprint, f.FingerprintDuration))
                    .Take(PageSize)
                    .ToListAsync(cancellationToken)
                    .ConfigureAwait(false);
            }

            // Every row on this page is stamped before the next query runs, so an
            // unchanged page means the pass is done. The guard is for the case
            // where a file failed transiently and stayed pending: without it the
            // same page would be claimed forever.
            var fresh = page.Where(file => seen.Add(file.Id)).ToList();

            if (fresh.Count == 0) yield break;

            var results = Channel.CreateBounded<Fingerprinted>(fresh.Count);

            var work = Parallel.ForEachAsync(
                fresh,
                new ParallelOptions
                {
                    MaxDegreeOfParallelism = config.ScanConcurrency,
                    CancellationToken = cancellationToken,
                },
                async (file, token) =>
                {
                    var outcome = await PrepareAsync(file, token).ConfigureAwait(false);
                    await results.Writer.WriteAsync(outcome, token).ConfigureAwait(false);
                });

            _ = work.ContinueWith(
                _ => results.Writer.TryComplete(),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);

            await foreach (var result in results.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                yield return result;
            }

            await work.ConfigureAwait(false);
        }
    }

    /// <summary>One file's local work: adopt an existing tag, or fingerprint it.</summary>
    private async Task<Fingerprinted> PrepareAsync(PendingFile file, CancellationToken cancellationToken)
    {
        var path = new LibraryPath(file.Path);

        // The cheapest possible answer, and the one that makes "files that have
        // no AcoustID yet" literally true. 227 files in the target library were
        // tagged by Picard years ago; re-deriving what they already state would
        // cost a fingerprint and a turn at the rate limit to learn nothing.
        var existing = await tagReader.ReadAcoustIdAsync(path, cancellationToken).ConfigureAwait(false);

        if (existing is not null && Guid.TryParse(existing, out var already))
        {
            return Fingerprinted.FromExistingTag(file, new AcoustId(already));
        }

        if (file.Fingerprint is { Length: > 0 } && file.FingerprintDuration is { } duration)
        {
            // Already fingerprinted by an earlier run that got as far as the
            // lookup and no further. Re-deriving it would be identical work for
            // an identical answer.
            return Fingerprinted.Ready(file, new AudioFingerprint(file.Fingerprint, duration));
        }

        try
        {
            var fingerprint = await fingerprinter.ComputeAsync(path, cancellationToken).ConfigureAwait(false);
            return Fingerprinted.Ready(file, fingerprint);
        }
        catch (FingerprintFailedException failure) when (failure.IsFileFault)
        {
            return Fingerprinted.NotDecodable(file, failure.Message);
        }
    }

    /// <summary>
    /// Stage B, with the guarantee that stage A is released when it ends.
    /// </summary>
    /// <remarks>
    /// A <c>finally</c> rather than a <c>catch</c>, because the release has to
    /// happen on every exit and not only the interesting ones. On the ordinary
    /// path the producer has already completed and the cancellation is a no-op;
    /// on the failure path it is the only thing that lets the pass report what
    /// went wrong instead of stopping forever.
    /// </remarks>
    private async Task ConsumeThenReleaseAsync(
        ChannelReader<Fingerprinted> reader,
        Tally counts,
        string jobId,
        int total,
        string correlationId,
        FonotecaOptions config,
        CancellationTokenSource abort,
        CancellationToken cancellationToken)
    {
        try
        {
            await ConsumeAsync(reader, counts, jobId, total, correlationId, config, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            await abort.CancelAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Stage B: look up, choose, write, record. Strictly one at a time.</summary>
    private async Task ConsumeAsync(
        ChannelReader<Fingerprinted> reader,
        Tally counts,
        string jobId,
        int total,
        string correlationId,
        FonotecaOptions config,
        CancellationToken cancellationToken)
    {
        var lastReport = clock.UtcNow;

        try
        {
            await foreach (var item in reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                try
                {
                    await HandleAsync(item, counts, correlationId, config, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (ProviderRejectedException)
                {
                    // Not this file's problem — the key is wrong, so every
                    // remaining file would fail in exactly the same way. Stopping
                    // on the first is the whole point of telling the two apart.
                    throw;
                }
#pragma warning disable CA1031 // The backstop: no single file may end the pass.
                catch (Exception cause)
#pragma warning restore CA1031
                {
                    // Anything unforeseen, from any layer. The row keeps whatever
                    // this file's scope had already committed, and stays on the
                    // worklist if it committed nothing, so the next run retries
                    // it. One surprising file costs one file.
                    counts.Failed++;
                    Log.FileFailed(logger, item.File.Path, cause);
                }

                counts.Examined++;

                var now = clock.UtcNow;

                if (now - lastReport >= ProgressInterval)
                {
                    lastReport = now;
                    _progress = new IdentificationProgress(jobId, counts.Examined, total, item.File.Path);

                    await Report(jobId, counts.Examined, total, item.File.Path, "running")
                        .ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Cancelled between files, which is where cancellation is checked.
        }
    }

    /// <summary>
    /// One file: look it up, decide, write, record. All in a single scope.
    /// </summary>
    /// <remarks>
    /// <b>One scope, one <c>SaveChanges</c>, per file.</b> Not tidiness — the
    /// undo journal does not save itself, it enlists in whoever's unit of work
    /// it was appended to. Give the writer a different scope from the row update
    /// and the journal entry is never written at all, while everything appears to
    /// work. Sharing the scope makes "the file was tagged" and "the catalogue
    /// says so" one commit.
    ///
    /// It is also the resumability guarantee. A process killed here has either
    /// recorded this file or not, and the next run's worklist reflects exactly
    /// that. Batching would trade an invisible amount of time — a millisecond
    /// against the 340 the rate limit costs — for the ability to lose a hundred
    /// files.
    /// </remarks>
    private async Task HandleAsync(
        Fingerprinted item,
        Tally counts,
        string correlationId,
        FonotecaOptions config,
        CancellationToken cancellationToken)
    {
        var path = new LibraryPath(item.File.Path);
        var now = clock.UtcNow;

        var scope = scopeFactory.CreateAsyncScope();
        await using (scope.ConfigureAwait(false))
        {
            var db = scope.ServiceProvider.GetRequiredService<FonotecaDbContext>();

            var row = await db.MediaFiles
                .FirstOrDefaultAsync(f => f.Id == item.File.Id, cancellationToken)
                .ConfigureAwait(false);

            // Absent means a scan removed it while we were working. Nothing to
            // update, and nothing wrong.
            if (row is null) return;

            if (item.Adopted is { } adopted)
            {
                counts.Adopted++;

                row.AcoustId = adopted;
                row.AcoustIdCheckedUtc = now;
                row.AcoustIdTaggedUtc = now;
                row.AcoustIdOutcome = AcoustIdOutcome.Identified;
            }
            else if (item.Undecodable is { } reason)
            {
                counts.Unfingerprintable++;
                Log.FileIdentified(logger, item.File.Path, AcoustIdOutcome.Unfingerprintable, reason);

                row.AcoustIdCheckedUtc = now;
                row.AcoustIdOutcome = AcoustIdOutcome.Unfingerprintable;
                row.Integrity = IntegrityState.Unreadable;
            }
            else
            {
                await IdentifyAsync(
                    item, row, counts, correlationId, config, scope, now, path, cancellationToken)
                    .ConfigureAwait(false);
            }

            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>The lookup half, for a file that produced a fingerprint.</summary>
    private async Task IdentifyAsync(
        Fingerprinted item,
        MediaFile row,
        Tally counts,
        string correlationId,
        FonotecaOptions config,
        AsyncServiceScope scope,
        DateTimeOffset now,
        LibraryPath path,
        CancellationToken cancellationToken)
    {
        var fingerprint = item.Fingerprint!.Value;
        counts.Fingerprinted++;

        row.Fingerprint = fingerprint.Value;
        row.FingerprintDuration = fingerprint.Duration;

        IReadOnlyList<AcoustIdMatch> matches;

        try
        {
            matches = await acoustId.LookupAsync(fingerprint, cancellationToken).ConfigureAwait(false);
        }
        catch (ProviderUnavailableException cause)
        {
            // Transient. AcoustIdCheckedUtc stays null, so the row stays on the
            // worklist and the next run retries it — but the fingerprint is kept,
            // so that retry costs no decoding.
            counts.Failed++;
            Log.FileIdentified(logger, item.File.Path, AcoustIdOutcome.NotAttempted, cause.Message);
            return;
        }

        var choice = AcoustIdSelection.Choose(
            matches, config.AcoustIdMinimumScore, config.AcoustIdMinimumMargin);

        // One arm per reason and no catch-all. The two failures used to share a
        // `_`, which is how "ambiguous" came to mean both "two candidate answers"
        // and "no good answer" — and hid that the first outnumbered the second
        // forty to one. The silent default is what allowed that; naming each
        // reason means the compiler asks about the next one somebody adds.
        var outcome = choice.Reason switch
        {
            AcoustIdChoiceReason.Confident => AcoustIdOutcome.Identified,
            AcoustIdChoiceReason.NoMatch => AcoustIdOutcome.Unknown,
            AcoustIdChoiceReason.Ambiguous => AcoustIdOutcome.Ambiguous,
            AcoustIdChoiceReason.BelowThreshold => AcoustIdOutcome.BelowThreshold,
            _ => throw new InvalidOperationException($"Unhandled choice reason {choice.Reason}."),
        };

        switch (outcome)
        {
            case AcoustIdOutcome.Unknown: counts.Unknown++; break;
            case AcoustIdOutcome.Ambiguous: counts.Ambiguous++; break;
            case AcoustIdOutcome.BelowThreshold: counts.BelowThreshold++; break;
            default: counts.Identified++; break;
        }

        row.AcoustIdCheckedUtc = now;
        row.AcoustIdOutcome = outcome;

        if (!choice.IsConfident)
        {
            Log.FileNotIdentified(logger, item.File.Path, outcome, choice.Score);
            return;
        }

        var identified = choice.Value!.Value;
        row.AcoustId = identified;

        // Resolved from this file's scope, so the undo entry it appends shares
        // the DbContext the row above lives in and commits with it.
        var writer = scope.ServiceProvider.GetRequiredService<AcoustIdTagWriter>();

        TagWriteResult write;

        try
        {
            var plan = await writer.PlanAsync(path, identified.Value, cancellationToken)
                .ConfigureAwait(false);

            write = await writer
                .ApplyAsync(
                    plan, row.Id.ToString(), correlationId, SystemCallerContext.SystemId, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (TagReadFailedException cause)
        {
            // Identified, but not writable. The AcoustID above is real and is
            // kept — the lookup succeeded and repeating it would cost another
            // turn at the rate limit for the same answer. What failed is reading
            // the file well enough to change it safely, and the honest response
            // to that is to leave the file alone: AcoustIdTaggedUtc stays null,
            // so it shows up as identified-but-untagged rather than as done.
            //
            // In the author's library this is forty FLACs carrying a prepended
            // ID3v2 header, which is not legal FLAC. `metaflac --remove --block-type=
            // APPLICATION` or a re-tag in Picard fixes them at the source, which
            // is the right place: they were like that before this app saw them.
            counts.TagUnreadable++;
            Log.FileTagUnreadable(logger, item.File.Path, cause.Library, cause.CauseType);
            return;
        }

        switch (write.Status)
        {
            case TagWriteStatus.Written: counts.Tagged++; break;
            case TagWriteStatus.Refused: counts.WriteRefused++; break;
            case TagWriteStatus.NothingToDo: break;
            default:
                counts.Failed++;
                Log.TagWriteFailed(logger, item.File.Path, write.Detail);
                break;
        }

        if (write.Status is TagWriteStatus.Written or TagWriteStatus.NothingToDo)
        {
            row.AcoustIdTaggedUtc = now;
        }

        // The sharpest edge in this feature. A tag write changes the file's
        // bytes, and a catalogue still holding the old size and mtime sees the
        // file as modified on the next scan — which discards the fingerprint and
        // the AcoustID that were just written, and does it again on every pass,
        // forever. Recording the new facts in the same transaction closes it.
        if (write.Committed is { } facts)
        {
            row.SizeBytes = facts.SizeBytes;
            row.LastModifiedUtc = StoreTime.ToStorePrecision(facts.LastModifiedUtc);

            // The bytes really did change, so a content hash taken before the
            // write no longer describes this file. The audio did not, which is
            // why AudioHash is left alone.
            row.ContentHash = null;
        }
    }

    private async Task Report(
        string jobId,
        int processed,
        int total,
        string? current,
        string state)
    {
        var message = new JobProgressMessage(jobId, JobKind, state, processed, total, current, clock.UtcNow);

        await hub.Clients.All.JobProgress(message).ConfigureAwait(false);
    }

    /// <summary>Mutable counters, held by one thread — stage B is the only writer.</summary>
    private sealed class Tally
    {
        public int Examined;
        public int Adopted;
        public int Fingerprinted;
        public int Identified;
        public int Unknown;
        public int Ambiguous;
        public int BelowThreshold;
        public int Unfingerprintable;
        public int Tagged;
        public int WriteRefused;
        public int TagUnreadable;
        public int Failed;
    }

    private readonly record struct PendingFile(
        MediaFileId Id,
        string Path,
        string? Fingerprint,
        TimeSpan? FingerprintDuration);

    /// <summary>What stage A hands to stage B: exactly one of three outcomes.</summary>
    private readonly record struct Fingerprinted(
        PendingFile File,
        AudioFingerprint? Fingerprint,
        AcoustId? Adopted,
        string? Undecodable)
    {
        public static Fingerprinted Ready(PendingFile file, AudioFingerprint fingerprint) =>
            new(file, fingerprint, null, null);

        public static Fingerprinted FromExistingTag(PendingFile file, AcoustId id) =>
            new(file, null, id, null);

        public static Fingerprinted NotDecodable(PendingFile file, string reason) =>
            new(file, null, null, reason);
    }
}

/// <summary>What a request to start a pass did.</summary>
public sealed record IdentificationOutcome(IdentificationStatus Status, string? JobId);

public enum IdentificationStatus
{
    /// <summary>Running in the background; <c>JobId</c> is populated.</summary>
    Started = 0,

    /// <summary>A scan or another identification holds the gate.</summary>
    AlreadyRunning = 1,

    /// <summary>The library root is not present — most likely unmounted.</summary>
    LibraryRootMissing = 2,
}

/// <summary>Where a running pass has got to.</summary>
public sealed record IdentificationProgress(string JobId, int Processed, int Total, string? CurrentFile);

/// <summary>What one pass did.</summary>
/// <remarks>
/// Every count is an <c>int</c> rather than a <c>long</c> so the generated
/// TypeScript types them as <c>number</c> under <c>NumberHandling.Strict</c>,
/// and arithmetic on them in the web client compiles.
/// </remarks>
public sealed record IdentificationSummary(
    string JobId,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset CompletedAtUtc,
    long DurationMilliseconds,

    /// <summary>Files taken off the worklist.</summary>
    int Examined,

    /// <summary>Files that already carried an AcoustID, adopted without asking anyone.</summary>
    int AlreadyTagged,

    /// <summary>Files fpcalc actually ran on.</summary>
    int Fingerprinted,

    int Identified,

    /// <summary>Audio AcoustID has never heard. Not asked about again.</summary>
    int Unknown,

    /// <summary>
    /// Two clusters meant different audio and neither won clearly.
    /// </summary>
    /// <remarks>
    /// A right answer exists and the rule declined to pick it — typically a live
    /// take against a studio one. The follow-up is a human ear, not a knob.
    /// </remarks>
    int Ambiguous,

    /// <summary>
    /// Nothing matched well enough to be worth writing down.
    /// </summary>
    /// <remarks>
    /// Reported apart from <see cref="Ambiguous"/> because the remedies have
    /// nothing in common, and because together they hid their own proportions:
    /// the author's library reported 951 ambiguous files, of which 23 were
    /// actually this. Old, noisy or sparsely-submitted audio, where the
    /// fingerprint is weak rather than contested.
    /// </remarks>
    int BelowThreshold,

    int Unfingerprintable,

    /// <summary>Files whose tag was written and verified.</summary>
    int Tagged,

    /// <summary>Files that would have been tagged if Fonoteca:AllowFileMutation were on.</summary>
    int WriteRefused,

    /// <summary>
    /// Files identified, but left alone because their tags could not be read.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="Failed"/> because nothing went wrong with the
    /// pass and retrying will not help: the file is malformed and the fix is in
    /// the file, not here. Their AcoustID is recorded all the same, so they cost
    /// no further lookups once the file is repaired.
    /// </remarks>
    int TagUnreadable,

    /// <summary>Transient failures. These stay on the worklist for the next run.</summary>
    int Failed,

    bool Cancelled);
