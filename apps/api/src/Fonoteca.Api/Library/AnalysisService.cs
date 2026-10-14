using System.Diagnostics;
using Fonoteca.Api.Configuration;
using Fonoteca.Api.Logging;
using Fonoteca.Api.Realtime;
using Fonoteca.Data;
using Fonoteca.Domain.Abstractions;
using Fonoteca.Domain.Catalogue;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Fonoteca.Api.Library;

/// <summary>
/// Analyses what every file's audio is like: loudness, dynamic range, the bits
/// it really uses, silence and clipping.
/// </summary>
/// <remarks>
/// <b>Its own pass and not the probe's, by the owner's choice</b>, and called
/// Analyse because the dashboard already calls the probe "measure". The probe's
/// decode is the integrity check, and it reads anything ffmpeg says as the
/// decoder objecting; the analysis filters talk on the same channel. Kept apart,
/// the probe stays exactly as it was, at the cost of a second decode per file.
///
/// <see cref="ProbeService"/>'s shape throughout, for its reasons, which are
/// written down there: the worklist keys on "we asked" (<c>MeasuredUtc</c>),
/// stamped whatever the answer; a missing binary ends the pass while a file
/// that will not decode marks one row; a file not on disk, or changed under the
/// pass, stays pending so an unmounted volume is never recorded as anything;
/// parallel over a claimed page with no channel; one scope and one save per
/// file. A scan that sees the audio change clears the measurement with the rest.
/// </remarks>
public sealed class AnalysisService(
    LibraryWorkGate gate,
    IServiceScopeFactory scopeFactory,
    IAudioMeasurer measurer,
    IAudioFileStore files,
    IHubContext<JobsHub, IJobsClient> hub,
    IHostApplicationLifetime lifetime,
    IOptions<FonotecaOptions> options,
    IClock clock,
    ILogger<AnalysisService> logger) : IHostedService
{
    /// <summary>The kind this pass takes the gate as, and the job kind on the wire.</summary>
    public const string JobKind = "library.analyse";

    private const int PageSize = 200;

    private static readonly TimeSpan ProgressInterval = TimeSpan.FromMilliseconds(250);

    private CancellationTokenSource? _cancellation;
    private volatile AnalysisProgress? _progress;
    private volatile AnalysisSummary? _lastCompleted;
    private volatile string? _lastError;
    private Task? _pass;

    public bool IsRunning => _progress is not null;

    public AnalysisProgress? Progress => _progress;

    public AnalysisSummary? LastCompleted => _lastCompleted;

    /// <summary>Why the last pass stopped without finishing — usually ffmpeg missing from PATH — or null.</summary>
    public string? LastError => _lastError;

    /// <summary>How many files are measured, how many could not be, and how many nobody has asked about.</summary>
    public async Task<AnalysisCoverage> CoverageAsync(CancellationToken cancellationToken = default)
    {
        var scope = scopeFactory.CreateAsyncScope();
        await using (scope.ConfigureAwait(false))
        {
            var db = scope.ServiceProvider.GetRequiredService<FonotecaDbContext>();

            var counts = await db.MediaFiles
                .GroupBy(_ => 1)
                .Select(g => new
                {
                    Files = g.Count(),
                    Pending = g.Count(f => f.MeasuredUtc == null),
                    Measured = g.Count(f => f.MeasuredUtc != null && f.Measurement != null),
                })
                .FirstOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false);

            return counts is null
                ? new AnalysisCoverage(0, 0, 0, 0)
                : new AnalysisCoverage(
                    counts.Files,
                    counts.Pending,
                    counts.Measured,
                    counts.Files - counts.Pending - counts.Measured);
        }
    }

    /// <summary>Starts a pass, or reports why it did not.</summary>
    public AnalysisOutcomeStatus Start()
    {
        if (!gate.TryEnter(JobKind, out var lease))
        {
            Log.AnalysisBusy(logger, gate.ActiveKind ?? "other work");
            return new AnalysisOutcomeStatus(AnalysisStatus.AlreadyRunning, null);
        }

        var jobId = Guid.CreateVersion7().ToString("N")[..12];

        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.ApplicationStopping);
        _cancellation = cancellation;

        // In ProbeService's order and for its reason: cleared before _progress.
        _pass = null;
        _progress = new AnalysisProgress(jobId, 0, 0, null);
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
                    Log.AnalysisAborted(logger, cause.Message);
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

        return new AnalysisOutcomeStatus(AnalysisStatus.Started, jobId);
    }

    /// <summary>Asks the running pass to stop. It finishes the page it is on.</summary>
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

    async Task IHostedService.StopAsync(CancellationToken cancellationToken)
    {
        var pass = _pass;

        if (pass is null || !IsRunning) return;

        Cancel();

        try
        {
            await pass.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception cause) when (cause is TimeoutException or OperationCanceledException)
        {
            Log.AnalysisAborted(logger, cause.Message);
        }
    }

    private async Task<AnalysisSummary> RunAsync(string jobId, CancellationToken cancellationToken)
    {
        var startedAt = clock.UtcNow;
        var elapsed = Stopwatch.StartNew();
        var counts = new Tally();

        var pending = (await CoverageAsync(cancellationToken).ConfigureAwait(false)).Pending;

        Log.AnalysisStarted(logger, jobId, pending);

        _progress = new AnalysisProgress(jobId, 0, pending, null);

        var lastReportTicks = clock.UtcNow.UtcTicks;
        var concurrency = Math.Clamp(options.Value.ScanConcurrency, 1, 16);

        try
        {
            await foreach (var page in ClaimAsync(counts, cancellationToken).ConfigureAwait(false))
            {
                await Parallel.ForEachAsync(
                    page,
                    new ParallelOptions { MaxDegreeOfParallelism = concurrency, CancellationToken = cancellationToken },
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
                            // The tool, not the file: every remaining file fails alike.
                            throw;
                        }
#pragma warning disable CA1031 // The backstop: no single file may end the pass.
                        catch (Exception cause)
#pragma warning restore CA1031
                        {
                            Interlocked.Increment(ref counts.Failed);
                            Log.FileFailed(logger, file.Path, cause);
                        }

                        var seen = Interlocked.Increment(ref counts.Examined);
                        var now = clock.UtcNow;
                        var previous = Interlocked.Read(ref lastReportTicks);

                        if (now.UtcTicks - previous < ProgressInterval.Ticks) return;

                        if (Interlocked.CompareExchange(ref lastReportTicks, now.UtcTicks, previous) != previous) return;

                        _progress = new AnalysisProgress(jobId, seen, pending, file.Path);

                        await Report(jobId, seen, pending, file.Path, "running").ConfigureAwait(false);
                    })
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Cancelled between pages, or mid-page by Parallel.ForEachAsync.
        }

        var summary = new AnalysisSummary(
            JobId: jobId,
            StartedAtUtc: startedAt,
            CompletedAtUtc: clock.UtcNow,
            DurationMilliseconds: elapsed.ElapsedMilliseconds,
            Examined: counts.Examined,
            Measured: counts.Measured,
            Unmeasurable: counts.Unmeasurable,
            Failed: counts.Failed,
            Skipped: counts.Missing,
            Cancelled: cancellationToken.IsCancellationRequested);

        Log.AnalysisCompleted(logger, jobId, counts.Measured, counts.Unmeasurable, counts.Failed, summary.DurationMilliseconds);

        await Report(jobId, counts.Examined, pending, null, "completed").ConfigureAwait(false);

        return summary;
    }

    /// <summary>The worklist a page at a time, stepping over what it cannot stamp — see <see cref="ProbeService"/>.</summary>
    private async IAsyncEnumerable<List<PendingFile>> ClaimAsync(
        Tally counts,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var stuck = Volatile.Read(ref counts.Missing) + Volatile.Read(ref counts.Failed);

            List<PendingFile> page;

            var scope = scopeFactory.CreateAsyncScope();
            await using (scope.ConfigureAwait(false))
            {
                var db = scope.ServiceProvider.GetRequiredService<FonotecaDbContext>();

                page = await db.MediaFiles
                    .AsNoTracking()
                    .Where(f => f.MeasuredUtc == null)
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

    /// <summary>One file: decode it outside any scope, then write what came back.</summary>
    private async Task HandleAsync(PendingFile file, Tally counts, CancellationToken cancellationToken)
    {
        var path = new LibraryPath(file.Path);

        // Not on disk — an unmounted volume, most likely — stays pending, for
        // the probe's reason: stamped, it would never be asked again.
        if (await files.StatAsync(path, cancellationToken).ConfigureAwait(false) is null)
        {
            Interlocked.Increment(ref counts.Missing);
            return;
        }

        AudioMeasurement? measurement;

        try
        {
            measurement = await measurer.MeasureAsync(path, cancellationToken).ConfigureAwait(false);
        }
        catch (AudioProbeFailedException cause) when (cause.IsFileFault)
        {
            measurement = null;
            Log.FileNotAnalysed(logger, file.Path, cause.Message);
        }

        var scope = scopeFactory.CreateAsyncScope();
        await using (scope.ConfigureAwait(false))
        {
            var db = scope.ServiceProvider.GetRequiredService<FonotecaDbContext>();

            var row = await db.MediaFiles.FirstOrDefaultAsync(f => f.Id == file.Id, cancellationToken).ConfigureAwait(false);

            if (row is null) return;

            // Still the file that was measured: a scan is not on the gate.
            if (row.SizeBytes != file.SizeBytes || row.LastModifiedUtc != file.LastModifiedUtc)
            {
                Interlocked.Increment(ref counts.Missing);
                return;
            }

            row.Measurement = measurement;
            row.MeasuredUtc = StoreTime.ToStorePrecision(clock.UtcNow);

            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        Interlocked.Increment(ref measurement is null ? ref counts.Unmeasurable : ref counts.Measured);
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
        public int Unmeasurable;
        public int Failed;

        /// <summary>Not on disk, or changed under the pass. Left pending either way.</summary>
        public int Missing;
    }

    private readonly record struct PendingFile(MediaFileId Id, string Path, long SizeBytes, DateTimeOffset LastModifiedUtc);
}

public sealed record AnalysisOutcomeStatus(AnalysisStatus Status, string? JobId);

public enum AnalysisStatus
{
    Started,
    AlreadyRunning,
}

public sealed record AnalysisProgress(string JobId, int Processed, int Total, string? CurrentFile);

/// <param name="Unmeasurable">Files ffmpeg would not decode, or that held no audio; stamped all the same.</param>
/// <param name="Skipped">Not on disk, or changed while being measured. Left pending.</param>
public sealed record AnalysisSummary(
    string JobId,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset CompletedAtUtc,
    long DurationMilliseconds,
    int Examined,
    int Measured,
    int Unmeasurable,
    int Failed,
    int Skipped,
    bool Cancelled);

/// <param name="Pending">Files the pass has never asked about: its worklist.</param>
/// <param name="Unmeasurable">Asked, and no measurement came back.</param>
public sealed record AnalysisCoverage(int Files, int Pending, int Measured, int Unmeasurable);
