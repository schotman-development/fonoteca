using System.Diagnostics;
using Fonoteca.Api.Configuration;
using Fonoteca.Api.Realtime;
using Fonoteca.Api.Logging;
using Fonoteca.Data;
using Fonoteca.Domain.Abstractions;
using Fonoteca.Domain.Catalogue;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Fonoteca.Api.Library;

/// <summary>
/// Measures every file in the library: codec, depth, rate, bitrate, and whether
/// the decoder objected on the way through.
/// </summary>
/// <remarks>
/// <b>The pass the layering table has listed under <c>Fonoteca.Ingest</c> since
/// the first commit and nothing implemented.</b> <c>MediaFile.Quality</c> has
/// been in the schema from the first migration with one thing filling it in —
/// <c>GET /api/catalogue/matching/files/{id}</c>, one file at a time, when
/// somebody opens it. Measured on the target library that is <b>125 rows out of
/// 8,140</b>, which is why the upgrade list could see the 87 lossy album folders
/// and none of the 434 that are CD-quality FLAC against a hi-res master.
///
/// <b>It answers two questions with one read, and that is why it is one pass.</b>
/// <c>-count_frames</c> decodes the stream rather than parsing its header, so
/// the run that fills in <see cref="AudioQuality"/> is also the run that can say
/// whether the bytes are intact — the <c>IntegrityState</c> pass
/// <see cref="IAudioProbe"/>'s own remarks defer to. Splitting them would mean
/// decoding the library twice.
///
/// The sharp edges, in the order they were paid for:
///
/// <list type="bullet">
/// <item><b>The worklist is <c>LastVerifiedUtc IS NULL</c>, not
/// <c>Quality IS NULL</c>.</b> The lesson <c>AcoustIdCheckedUtc</c> already
/// bought: a library contains files no decoder will ever measure — a text file
/// named <c>.flac</c>, a truncated rip — and keyed on the answer every one of
/// them is re-decoded on every pass forever. Thirty such files exist here.
/// Recording <i>that we asked</i> lets the worklist reach empty.</item>
///
/// <item><b>Only a clean decode is written to <see cref="AudioQuality"/>.</b>
/// The same rule the single-file endpoint holds, and for a stronger reason at
/// this scale: at <c>-v error</c> a healthy stream says nothing, so anything on
/// stderr is the decoder objecting to these bytes while reading them — and it
/// exits <b>zero</b> having done so. <c>AudioQuality</c> decides which duplicate
/// to keep and whether a candidate is an upgrade; a number the decoder objected
/// to has no business being an input to that. The objection is not lost, it
/// becomes <see cref="IntegrityState.Corrupt"/>.</item>
///
/// <item><b><c>LastVerifiedUtc</c> is stamped whatever happened, including on
/// the files that got no measurement.</b> It is the "we asked" column, not the
/// "it worked" column; conflating them is the treadmill above.</item>
///
/// <item><b>ffprobe failures are split by whose fault they are.</b> A truncated
/// FLAC marks one row <see cref="IntegrityState.Unreadable"/>; a missing binary
/// must stop the pass on the first file. Merged, a <c>PATH</c> problem marks
/// 100,000 files as damaged audio and the next scan is the only thing that could
/// undo it. Identification learned this from <c>fpcalc</c>.</item>
///
/// <item><b>Parallel over the page, and no channel.</b> Decoding is local CPU
/// work with no rate gate to serialise behind, so concurrency is the whole
/// speed-up: measured on the target library, <c>-count_frames</c> is 0.63s a
/// file serially — 85 minutes — against roughly 20 at four at once. The shape is
/// deliberately claim-a-page-then-<c>Parallel.ForEachAsync</c> rather than a
/// producer and a consumer joined by a channel: that arrangement deadlocks when
/// the consumer dies, silently, and cost a live identification run twenty-five
/// minutes and a process dump. There is no channel here to deadlock.</item>
///
/// <item><b>One scope and one <c>SaveChanges</c> per file.</b> The resumability
/// story, exactly as identification's is — a pass killed at minute nineteen of
/// twenty keeps everything it measured.</item>
/// </list>
///
/// A scan that sees the bytes change already clears <c>Quality</c>,
/// <c>Integrity</c> and <c>LastVerifiedUtc</c> together, so re-measuring a
/// replaced file needs no work here and no new column: the file simply reappears
/// on the worklist.
/// </remarks>
public sealed class ProbeService(
    LibraryWorkGate gate,
    IServiceScopeFactory scopeFactory,
    IAudioProbe probe,
    IAudioFileStore files,
    IHubContext<JobsHub, IJobsClient> hub,
    IHostApplicationLifetime lifetime,
    IOptions<FonotecaOptions> options,
    IClock clock,
    ILogger<ProbeService> logger) : IHostedService
{
    /// <summary>The kind this pass takes the gate as, and the job kind on the wire.</summary>
    public const string JobKind = "library.probe";

    /// <summary>Rows claimed per query. Bounded so a cancelled pass stops promptly.</summary>
    private const int PageSize = 200;

    private static readonly TimeSpan ProgressInterval = TimeSpan.FromMilliseconds(250);

    /// <summary>How many damaged paths come back with the reading. A sample, not a worklist.</summary>
    private const int DamagedShown = 25;

    private CancellationTokenSource? _cancellation;
    private volatile ProbeProgress? _progress;
    private volatile ProbeSummary? _lastCompleted;
    private volatile string? _lastError;
    private Task? _pass;

    public bool IsRunning => _progress is not null;

    public ProbeProgress? Progress => _progress;

    public ProbeSummary? LastCompleted => _lastCompleted;

    /// <summary>
    /// Why the last pass stopped without finishing, or null.
    /// </summary>
    /// <remarks>
    /// The one failure this pass has that the others do not is boring and
    /// likely: <c>ffprobe</c> not on <c>PATH</c>. Without this the pass ends on
    /// the first file, logs a line nobody is reading, and the card goes back to
    /// showing the same pending count it showed before — a button that visibly
    /// does nothing.
    /// </remarks>
    public string? LastError => _lastError;

    /// <summary>How many files nothing has measured yet.</summary>
    public async Task<int> CountPendingAsync(CancellationToken cancellationToken = default)
    {
        var scope = scopeFactory.CreateAsyncScope();
        await using (scope.ConfigureAwait(false))
        {
            var db = scope.ServiceProvider.GetRequiredService<FonotecaDbContext>();

            return await db.MediaFiles
                .Where(f => f.LastVerifiedUtc == null)
                .CountAsync(cancellationToken)
                .ConfigureAwait(false);
        }
    }

    /// <summary>
    /// What the library measured, once. The integrity half of the pass.
    /// </summary>
    /// <remarks>
    /// Counted rather than listed, because the number is the thing worth seeing
    /// on a dashboard and the list is a worklist somebody would have to act on
    /// one file at a time. A file that reads as damaged is already reachable —
    /// the matching screen shows the decoder's own sentence about it.
    /// </remarks>
    public async Task<ProbeCoverage> CoverageAsync(CancellationToken cancellationToken = default)
    {
        var scope = scopeFactory.CreateAsyncScope();
        await using (scope.ConfigureAwait(false))
        {
            var db = scope.ServiceProvider.GetRequiredService<FonotecaDbContext>();

            var counts = await db.MediaFiles
                .GroupBy(f => f.Integrity)
                .Select(g => new
                {
                    State = g.Key,
                    Count = g.Count(),

                    // Pending in the same query as the rest, because the two used
                    // to be separate reads and the card shows both: mid-pass they
                    // disagreed by a dozen, which reads as a bug in whichever
                    // number the eye lands on second.
                    Pending = g.Count(f => f.LastVerifiedUtc == null),
                })
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            int For(IntegrityState state) =>
                counts.FirstOrDefault(row => row.State == state)?.Count ?? 0;

            // The paths, not just the count. The card used to say "open one on
            // the matching screen" and the matching screen lists none of them —
            // every damaged file on the target library is either already
            // identified or Unfingerprintable, and both are deliberately not
            // questions. Naming them here is the only route to them short of SQL.
            var damaged = await db.MediaFiles
                .AsNoTracking()
                .Where(f => f.Integrity == IntegrityState.Corrupt
                    || f.Integrity == IntegrityState.Unreadable)
                .OrderBy(f => f.Path)
                .Select(f => f.Path)
                .Take(DamagedShown)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            return new ProbeCoverage(
                Files: counts.Sum(row => row.Count),
                Pending: counts.Sum(row => row.Pending),
                Measured: For(IntegrityState.Intact),
                Corrupt: For(IntegrityState.Corrupt),
                Unreadable: For(IntegrityState.Unreadable),
                Unchecked: For(IntegrityState.Unchecked),
                Damaged: damaged);
        }
    }

    /// <summary>Starts a pass, or reports why it did not.</summary>
    public ProbeOutcomeStatus Start()
    {
        if (!gate.TryEnter(JobKind, out var lease))
        {
            Log.ProbeBusy(logger, gate.ActiveKind ?? "other work");
            return new ProbeOutcomeStatus(ProbeStatus.AlreadyRunning, null);
        }

        var jobId = Guid.CreateVersion7().ToString("N")[..12];

        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.ApplicationStopping);
        _cancellation = cancellation;

        // Cleared before _progress, not after. StopAsync reads IsRunning (which
        // _progress backs) and then _pass; assigned the other way round, a stop
        // landing between the two lines awaits the *previous* pass's completed
        // task and returns immediately — the SemaphoreSlim bug this replaced,
        // wearing a race for a hat. Null means "nothing to wait for yet", and
        // the pass's own token comes from ApplicationStopping regardless.
        _pass = null;
        _progress = new ProbeProgress(jobId, 0, 0, null);
        _lastError = null;

        _pass = Task.Run(
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
                    _lastError = cause.Message;
                    Log.ProbeAborted(logger, cause.Message);
                }
                finally
                {
                    _progress = null;
                    _cancellation = null;
                    cancellation.Dispose();
                    lease.Dispose();
                }
            },
            CancellationToken.None);

        return new ProbeOutcomeStatus(ProbeStatus.Started, jobId);
    }

    /// <summary>Asks the running pass to stop. It finishes the page it is on.</summary>
    /// <remarks>
    /// The catch is not defensive padding. The pass's <c>finally</c> nulls the
    /// source and then disposes it, so a cancel that reads the field in between
    /// gets a disposed one — and this is reachable from an HTTP DELETE, where it
    /// would be a 500 on a request whose answer is "it already stopped".
    /// </remarks>
    public bool Cancel()
    {
        var running = _cancellation;

        if (running is null) return false;

        try
        {
            running.Cancel();
            return true;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
    }

    Task IHostedService.StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <remarks>
    /// Nothing here writes to a file — it only reads them — so an abandoned pass
    /// leaves no debris. Waiting is about the gate, which is what makes the next
    /// start succeed rather than 409.
    /// </remarks>
    async Task IHostedService.StopAsync(CancellationToken cancellationToken)
    {
        var pass = _pass;

        if (pass is null || !IsRunning) return;

        Cancel();

        // The task itself, rather than a SemaphoreSlim(0, 1) signalled from the
        // finally: that counts, and a second completed pass in one process
        // throws SemaphoreFullException out of the finally while leaving the
        // count at one — so the shutdown after it returns immediately on a stale
        // signal instead of waiting for anything. Awaiting the task is both
        // shorter and correct for any number of passes.
        try
        {
            await pass.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception cause) when (cause is TimeoutException or OperationCanceledException)
        {
            // Shutdown is not the place to throw. The pass writes no files, so
            // an abandoned one leaves nothing behind but a log line.
            Log.ProbeAborted(logger, cause.Message);
        }
    }

    private async Task<ProbeSummary> RunAsync(string jobId, CancellationToken cancellationToken)
    {
        var startedAt = clock.UtcNow;
        var elapsed = Stopwatch.StartNew();
        var counts = new Tally();

        var pending = await CountPendingAsync(cancellationToken).ConfigureAwait(false);

        Log.ProbeStarted(logger, jobId, pending);

        _progress = new ProbeProgress(jobId, 0, pending, null);

        // Ticks rather than a DateTimeOffset, because several threads write it:
        // a 16-byte struct has no atomic assignment, so a torn write puts a
        // nonsense timestamp in a progress frame.
        var lastReportTicks = clock.UtcNow.UtcTicks;

        // Past the core count it is disk and process spawning rather than
        // decoding, and ScanConcurrency is already the knob for exactly this.
        var concurrency = Math.Clamp(options.Value.ScanConcurrency, 1, 16);

        try
        {
            await foreach (var page in ClaimAsync(counts, cancellationToken).ConfigureAwait(false))
            {
                await Parallel.ForEachAsync(
                    page,
                    new ParallelOptions
                    {
                        MaxDegreeOfParallelism = concurrency,
                        CancellationToken = cancellationToken,
                    },
                    async (file, token) =>
                    {
                        try
                        {
                            await HandleAsync(file, counts, token).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException)
                        {
                            throw;
                        }
                        catch (AudioProbeFailedException cause) when (!cause.IsFileFault)
                        {
                            // The tool, not the file. Every remaining file fails
                            // identically, so this one ends the pass.
                            throw;
                        }
#pragma warning disable CA1031 // The backstop: no single file may end the pass.
                        catch (Exception cause)
#pragma warning restore CA1031
                        {
                            // Left pending on purpose — LastVerifiedUtc is only
                            // stamped by a write that succeeded, so a transient
                            // database error means this row comes back next run.
                            Interlocked.Increment(ref counts.Failed);
                            Log.FileFailed(logger, file.Path, cause);
                        }

                        var seen = Interlocked.Increment(ref counts.Examined);
                        var now = clock.UtcNow;
                        var previous = Interlocked.Read(ref lastReportTicks);

                        if (now.UtcTicks - previous < ProgressInterval.Ticks) return;

                        // Exactly one thread reports per interval, rather than
                        // every thread that happened to read the old value —
                        // which at four at once is four identical frames.
                        if (Interlocked.CompareExchange(ref lastReportTicks, now.UtcTicks, previous)
                            != previous)
                        {
                            return;
                        }

                        _progress = new ProbeProgress(jobId, seen, pending, file.Path);

                        await Report(jobId, seen, pending, file.Path, "running").ConfigureAwait(false);
                    })
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Cancelled between pages, or mid-page by Parallel.ForEachAsync.
        }

        var summary = new ProbeSummary(
            JobId: jobId,
            StartedAtUtc: startedAt,
            CompletedAtUtc: clock.UtcNow,
            DurationMilliseconds: elapsed.ElapsedMilliseconds,
            Examined: counts.Examined,
            Measured: counts.Measured,
            Complained: counts.Complained,
            Unreadable: counts.Unreadable,
            Failed: counts.Failed,
            Skipped: counts.Missing,
            Cancelled: cancellationToken.IsCancellationRequested);

        Log.ProbeCompleted(
            logger, jobId, counts.Measured, counts.Complained, counts.Unreadable, counts.Failed,
            summary.DurationMilliseconds);

        await Report(jobId, counts.Examined, pending, null, "completed").ConfigureAwait(false);

        return summary;
    }

    /// <summary>
    /// The worklist, a page at a time.
    /// </summary>
    /// <remarks>
    /// <b>The offset is what makes this terminate, and a <c>seen</c> set was not
    /// enough.</b> Rows the pass cannot stamp — a file that is not on disk, a
    /// transient database error — stay on the worklist and stay at the head of
    /// the id order. Filtering them out of the page after the fact meant a page
    /// where every row was skippable came back empty of anything new and ended
    /// the pass, silently, with the rest of the library still pending: 200 such
    /// rows in a row was all it took, and Guid v7 ids are creation-ordered, so a
    /// directory that went missing in one import is exactly that contiguous
    /// block.
    ///
    /// Counting them and stepping over them instead means each page either
    /// stamps something — and the worklist shrinks — or advances the offset by a
    /// page. Either way the walk moves. A keyset cursor would be the usual
    /// answer and cannot be written here: <c>MediaFileId</c> is value-converted,
    /// and EF translates no comparison on it into SQL.
    /// </remarks>
    private async IAsyncEnumerable<List<PendingFile>> ClaimAsync(
        Tally counts,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            // Everything this run has left behind, which is exactly what is
            // still sitting in front of the next page.
            var stuck = Volatile.Read(ref counts.Missing) + Volatile.Read(ref counts.Failed);

            List<PendingFile> page;

            var scope = scopeFactory.CreateAsyncScope();
            await using (scope.ConfigureAwait(false))
            {
                var db = scope.ServiceProvider.GetRequiredService<FonotecaDbContext>();

                page = await db.MediaFiles
                    .AsNoTracking()
                    .Where(f => f.LastVerifiedUtc == null)
                    .OrderBy(f => f.Id)
                    .Skip(stuck)
                    .Select(f => new PendingFile(f.Id, f.Path, f.SizeBytes, f.LastModifiedUtc))
                    .Take(PageSize)
                    .ToListAsync(cancellationToken)
                    .ConfigureAwait(false);
            }

            if (page.Count == 0) yield break;

            yield return page;
        }
    }

    /// <summary>One file: decode it, then write what came back.</summary>
    /// <remarks>
    /// The decode happens <i>outside</i> the scope on purpose, as enrichment's
    /// lookups do. <c>-count_frames</c> is 0.63s on an average file and 3.9s on
    /// this library's largest; holding a database connection open across that,
    /// four at a time, ties the pool to ffmpeg.
    /// </remarks>
    private async Task HandleAsync(PendingFile file, Tally counts, CancellationToken cancellationToken)
    {
        var path = new LibraryPath(file.Path);

        // Is it there at all, before ffprobe is asked to have an opinion?
        //
        // <b>This is the guard that keeps an unmounted volume from destroying the
        // catalogue.</b> ffprobe exits non-zero on a missing file exactly as it
        // does on a corrupt one — "No such file or directory" — and the probe
        // reports both as the file's fault, which is right for one file and
        // catastrophic for all of them: run the pass with the library unmounted
        // and every row is stamped Unreadable, leaves the worklist, and nothing
        // ever re-opens it. Only a scan clears LastVerifiedUtc, and a scan over
        // an empty root deliberately changes nothing. Recovery would be an UPDATE
        // written by hand.
        //
        // A file that is genuinely gone is a row for the scan to delete, so
        // leaving it pending costs one stat on the next run and nothing else.
        var facts = await files.StatAsync(path, cancellationToken).ConfigureAwait(false);

        if (facts is null)
        {
            Interlocked.Increment(ref counts.Missing);
            return;
        }

        AudioProbeReading? reading;
        IntegrityState integrity;

        try
        {
            reading = await probe.ProbeAsync(path, cancellationToken).ConfigureAwait(false);

            if (reading is null)
            {
                // ffprobe ran, exited zero, and described no audio: an empty
                // file, a text file with an audio extension, a container whose
                // stream is not audio. An answer about the file, not a failure.
                integrity = IntegrityState.Unreadable;
                Interlocked.Increment(ref counts.Unreadable);
            }
            else if (reading.DecodedCleanly)
            {
                integrity = IntegrityState.Intact;
                Interlocked.Increment(ref counts.Measured);
            }
            else
            {
                integrity = IntegrityState.Corrupt;
                Interlocked.Increment(ref counts.Complained);
                Log.FileDecodeComplaint(logger, file.Path, reading.Complaint ?? "(no detail)");
            }
        }
        catch (AudioProbeFailedException cause) when (!cause.IsFileFault)
        {
            // The tool, not the file — a missing binary, output that will not
            // deserialise. Every remaining file fails identically, so the first
            // one ends the pass rather than writing 100,000 rows of nonsense.
            throw;
        }
        catch (AudioProbeFailedException cause)
        {
            reading = null;
            integrity = IntegrityState.Unreadable;
            Interlocked.Increment(ref counts.Unreadable);
            Log.FileNotProbed(logger, file.Path, cause.Message);
        }

        var scope = scopeFactory.CreateAsyncScope();
        await using (scope.ConfigureAwait(false))
        {
            var db = scope.ServiceProvider.GetRequiredService<FonotecaDbContext>();

            var row = await db.MediaFiles
                .FirstOrDefaultAsync(f => f.Id == file.Id, cancellationToken)
                .ConfigureAwait(false);

            // Gone since the page was claimed — deleted by a scan, most likely.
            if (row is null) return;

            // Still the file that was measured?
            //
            // The scan has never been on LibraryWorkGate — it guards itself with
            // a private flag — so it can run mid-pass, see these bytes change and
            // null every derived column on the row, including the stamp this is
            // about to write. Without the check the pass then re-stamps it with a
            // measurement of audio that is gone, and the file looks measured
            // forever. The window is one ffprobe wide, up to sixty seconds.
            //
            // Comparing what was claimed against what is on the row now closes
            // that window down to the microseconds between this read and the
            // save. It is not a compare-and-swap — MediaFiles carries no
            // concurrency token, so a scan committing inside *that* gap still
            // wins — but it trades a sixty-second window for one with no I/O in
            // it, and needs no new column and no reference to the scan.
            if (row.SizeBytes != file.SizeBytes || row.LastModifiedUtc != file.LastModifiedUtc)
            {
                Interlocked.Increment(ref counts.Missing);
                return;
            }

            row.Integrity = integrity;

            // Stamped whatever happened. This is the "we asked" column; keyed on
            // the answer instead, the thirty files no decoder can measure come
            // back on every pass forever.
            row.LastVerifiedUtc = StoreTime.ToStorePrecision(clock.UtcNow);

            // Only a clean decode is remembered. A complaint means the numbers
            // came out of a header the decoder then disagreed with, and
            // AudioQuality is read by the rules that pick between duplicates.
            if (reading is { } measured && measured.DecodedCleanly)
            {
                row.Quality = measured.Quality;
            }

            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task Report(string jobId, int processed, int total, string? current, string state)
    {
        var message = new JobProgressMessage(jobId, JobKind, state, processed, total, current, clock.UtcNow);

        await hub.Clients.All.JobProgress(message).ConfigureAwait(false);
    }

    /// <summary>Counters written from several threads, so every increment is interlocked.</summary>
    private sealed class Tally
    {
        public int Examined;
        public int Measured;
        public int Complained;
        public int Unreadable;
        public int Failed;

        /// <summary>Not on disk, or changed under the pass. Left pending either way.</summary>
        public int Missing;
    }

    /// <remarks>
    /// The size and the time come along so the write can check the file is still
    /// the one that was measured. See <see cref="HandleAsync"/>.
    /// </remarks>
    private readonly record struct PendingFile(
        MediaFileId Id,
        string Path,
        long SizeBytes,
        DateTimeOffset LastModifiedUtc);
}

public sealed record ProbeOutcomeStatus(ProbeStatus Status, string? JobId);

public enum ProbeStatus
{
    Started,
    AlreadyRunning,
}

public sealed record ProbeProgress(string JobId, int Processed, int Total, string? CurrentFile);

/// <param name="Measured">Files decoded end to end with no complaint. These have an <c>AudioQuality</c>.</param>
/// <param name="Complained">Files the decoder objected to. Measured, but not remembered.</param>
/// <param name="Unreadable">Files that describe no audio at all.</param>
/// <param name="Skipped">
/// Files that were not on disk, or changed while being measured. Left pending
/// rather than stamped — an unmounted volume must not be recorded as a library
/// of broken audio.
/// </param>
public sealed record ProbeSummary(
    string JobId,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset CompletedAtUtc,
    long DurationMilliseconds,
    int Examined,
    int Measured,
    int Complained,
    int Unreadable,
    int Failed,
    int Skipped,
    bool Cancelled);

/// <summary>What the library looks like after however many passes have run.</summary>
/// <param name="Pending">Files nothing has asked about. The pass's worklist.</param>
/// <param name="Unchecked">
/// Files with no integrity verdict. Nearly always the same as
/// <paramref name="Pending"/>, and not the same column: a row measured before
/// this pass existed carries a verdict with no stamp.
/// </param>
/// <param name="Damaged">The first few paths that will not decode, so they can be reached at all.</param>
public sealed record ProbeCoverage(
    int Files,
    int Pending,
    int Measured,
    int Corrupt,
    int Unreadable,
    int Unchecked,
    IReadOnlyList<string> Damaged);
