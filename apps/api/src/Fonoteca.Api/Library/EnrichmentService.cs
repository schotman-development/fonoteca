using System.Diagnostics;
using Fonoteca.Api.Logging;
using Fonoteca.Api.Matching;
using Fonoteca.Api.Realtime;
using Fonoteca.Data;
using Fonoteca.Domain.Abstractions;
using Fonoteca.Domain.Catalogue;
using Fonoteca.Domain.Identification;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace Fonoteca.Api.Library;

/// <summary>
/// Turns identified files into a catalogue: recordings, works and the artists behind them.
/// </summary>
/// <remarks>
/// Identification answers "what audio is this" and stops there. It decides an
/// AcoustID cluster, writes it into the file's tags, and discards the MusicBrainz
/// recording MBIDs it looked at on the way past — which is why a fully identified
/// library still has zero artists and nothing to browse. This pass is the other
/// half: cluster to recording, recording to metadata, metadata to rows.
///
/// <b>It opens no files.</b> The fingerprints are already in the catalogue, so
/// the AcoustID lookup is a database read and an HTTP request, with no decoding
/// and no disk. That matters more than it sounds: it means this pass can run
/// against a library whose volume is unmounted, and that re-asking after a rule
/// change costs turns at the rate limit rather than hours of fpcalc.
///
/// <b>Sequential, and deliberately not a pipeline.</b> The identification pass
/// splits into two stages because its halves have different costs — local CPU
/// against a remote rate limit — and pays for a bounded channel and the
/// deadlock hazard that comes with it. Here both halves are gated network calls:
/// AcoustID at 340ms and MusicBrainz at whatever the server earns. Running them
/// concurrently buys nothing at all, because <c>RequestGate</c> would serialise
/// them again one layer down, so a plain loop is both simpler and exactly as
/// fast. No producer, no consumer, nothing to deadlock.
///
/// <b>Three memoisations, all within one run</b>, and each pays for a different
/// repetition:
///
/// <list type="bullet">
/// <item>By AcoustID cluster, so the five encodings of one track that
/// identification found separately cost one lookup between them.</item>
/// <item>By recording MBID, because those five files are one recording and
/// asking MusicBrainz five times would be five identical answers.</item>
/// <item>By work MBID, which is the big one on a classical library: a symphony
/// is four movements across a dozen recordings and one work.</item>
/// </list>
///
/// Durability is the catalogue, as in the identification pass: the worklist is a
/// query, each file is committed as it resolves, and a process killed at any
/// moment leaves work the next run picks up. Hence one scope and one
/// <c>SaveChanges</c> per file rather than a batch.
/// </remarks>
public sealed class EnrichmentService(
    LibraryWorkGate gate,
    IServiceScopeFactory scopeFactory,
    IAcoustIdLookup acoustId,
    IMusicBrainzCatalogue musicBrainz,
    IHubContext<JobsHub, IJobsClient> hub,
    IHostApplicationLifetime lifetime,
    IClock clock,
    ILogger<EnrichmentService> logger) : IHostedService, IDisposable
{
    /// <summary>The kind this pass takes the gate as, and the job kind on the wire.</summary>
    public const string JobKind = "library.enrich";

    /// <summary>Rows claimed per query. Bounded so a cancelled pass stops promptly.</summary>
    private const int PageSize = 500;

    /// <summary>How often progress reaches the browser. Not per file.</summary>
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromMilliseconds(250);

    private readonly SemaphoreSlim _finished = new(0, 1);

    private CancellationTokenSource? _cancellation;
    private volatile EnrichmentProgress? _progress;
    private volatile EnrichmentSummary? _lastCompleted;

    public bool IsRunning => _progress is not null;

    /// <summary>Where the running pass has got to, or null when nothing is running.</summary>
    public EnrichmentProgress? Progress => _progress;

    /// <summary>
    /// The last pass that finished since startup.
    /// </summary>
    /// <remarks>In memory, like the scan's and the identification pass's.</remarks>
    public EnrichmentSummary? LastCompleted => _lastCompleted;

    /// <summary>Identified files nobody has asked about yet — the size of the job.</summary>
    public async Task<int> CountPendingAsync(CancellationToken cancellationToken = default)
    {
        var scope = scopeFactory.CreateAsyncScope();
        await using (scope.ConfigureAwait(false))
        {
            var db = scope.ServiceProvider.GetRequiredService<FonotecaDbContext>();

            var unasked = await db.MediaFiles
                .Where(f => f.AcoustId != null
                    && f.RecordingLookupUtc == null
                    && f.IdentityDecidedUtc == null)
                .CountAsync(cancellationToken)
                .ConfigureAwait(false);

            var personFiled = await db.MediaFiles
                .Where(PersonFiled)
                .CountAsync(cancellationToken)
                .ConfigureAwait(false);

            return unasked + personFiled;
        }
    }

    /// <summary>Starts a pass, or reports why it did not.</summary>
    public EnrichmentOutcomeStatus Start()
    {
        if (!gate.TryEnter(JobKind, out var lease))
        {
            Log.EnrichmentBusy(logger, gate.ActiveKind ?? "other work");
            return new EnrichmentOutcomeStatus(EnrichmentStatus.AlreadyRunning, null);
        }

        var jobId = Guid.CreateVersion7().ToString("N")[..12];

        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.ApplicationStopping);
        _cancellation = cancellation;

        _progress = new EnrichmentProgress(jobId, 0, 0, null);

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
                    Log.EnrichmentAborted(logger, cause.Message);
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

        return new EnrichmentOutcomeStatus(EnrichmentStatus.Started, jobId);
    }

    /// <summary>Asks the running pass to stop. It finishes the file it is on.</summary>
    public bool Cancel()
    {
        var running = _cancellation;

        if (running is null) return false;

        running.Cancel();
        return true;
    }

    public void Dispose() => _finished.Dispose();

    Task IHostedService.StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// Stops a running pass and waits for it.
    /// </summary>
    /// <remarks>
    /// Nothing here writes to a file, so an abandoned pass leaves no debris — but
    /// it does hold the gate, and waiting is what makes the next start succeed.
    /// </remarks>
    async Task IHostedService.StopAsync(CancellationToken cancellationToken)
    {
        if (!IsRunning) return;

        Cancel();

        await _finished.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
    }

    private async Task<EnrichmentSummary> RunAsync(string jobId, CancellationToken cancellationToken)
    {
        var startedAt = clock.UtcNow;
        var elapsed = Stopwatch.StartNew();
        var counts = new Tally();
        var memo = new Memo();

        var pending = await CountPendingAsync(cancellationToken).ConfigureAwait(false);

        Log.EnrichmentStarted(logger, jobId, pending);

        _progress = new EnrichmentProgress(jobId, 0, pending, null);

        var lastReport = clock.UtcNow;

        // One loop, driven twice, because the two worklists differ only in how a
        // file reaches a recording — everything after that is the same rows.
        async Task DrainAsync<T>(
            IAsyncEnumerable<T> work,
            Func<T, Task> handle,
            Func<T, string> pathOf)
        {
            await foreach (var file in work.ConfigureAwait(false))
            {
                try
                {
                    await handle(file).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (ProviderRejectedException)
                {
                    // Not this file's problem — a missing key or a missing
                    // contact, so every remaining file would fail identically.
                    // Stopping on the first is the whole point of the provider
                    // exceptions being two types rather than one.
                    throw;
                }
#pragma warning disable CA1031 // The backstop: no single file may end the pass.
                catch (Exception cause)
#pragma warning restore CA1031
                {
                    counts.Failed++;
                    Log.FileFailed(logger, pathOf(file), cause);
                }

                counts.Examined++;

                var now = clock.UtcNow;

                if (now - lastReport >= ProgressInterval)
                {
                    lastReport = now;
                    var path = pathOf(file);
                    _progress = new EnrichmentProgress(jobId, counts.Examined, pending, path);

                    await Report(jobId, counts.Examined, pending, path, "running").ConfigureAwait(false);
                }
            }
        }

        try
        {
            await DrainAsync(
                ClaimAsync(cancellationToken),
                file => HandleAsync(file, counts, memo, cancellationToken),
                file => file.Path).ConfigureAwait(false);

            // Second, so a run that is cancelled part way has done the cheaper
            // work first: these files already have an identity and are only
            // missing their graph, where the first worklist's have neither.
            await DrainAsync(
                ClaimPersonFiledAsync(cancellationToken),
                file => HandlePersonFiledAsync(file, counts, memo, cancellationToken),
                file => file.Path).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Cancelled between files, which is where cancellation is checked.
        }

        var summary = new EnrichmentSummary(
            JobId: jobId,
            StartedAtUtc: startedAt,
            CompletedAtUtc: clock.UtcNow,
            DurationMilliseconds: elapsed.ElapsedMilliseconds,
            Examined: counts.Examined,
            Linked: counts.Linked,
            NoRecording: counts.NoRecording,
            RecordingNotFound: counts.RecordingNotFound,
            Failed: counts.Failed,
            Recordings: memo.Recordings.Count,
            Works: memo.Works.Count,
            Artists: memo.Artists.Count,
            Cancelled: cancellationToken.IsCancellationRequested);

        Log.EnrichmentCompleted(
            logger, jobId, counts.Linked, counts.NoRecording, counts.RecordingNotFound,
            counts.Failed, memo.Recordings.Count, memo.Artists.Count, summary.DurationMilliseconds);

        await Report(jobId, counts.Examined, pending, null, "completed").ConfigureAwait(false);

        return summary;
    }

    /// <summary>
    /// The worklist, a page at a time.
    /// </summary>
    /// <remarks>
    /// Paged rather than loaded whole, and each page's rows are stamped before
    /// the next query runs — so an unchanged page means the pass is done. The
    /// <c>seen</c> guard covers the case where a file failed transiently and
    /// stayed pending: without it the same page would be claimed forever.
    /// </remarks>
    private async IAsyncEnumerable<PendingFile> ClaimAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
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
                    .Where(f => f.AcoustId != null
                    && f.RecordingLookupUtc == null
                    && f.IdentityDecidedUtc == null)
                    .OrderBy(f => f.Id)
                    .Select(f => new PendingFile(
                        f.Id, f.Path, f.AcoustId, f.Fingerprint, f.FingerprintDuration))
                    .Take(PageSize)
                    .ToListAsync(cancellationToken)
                    .ConfigureAwait(false);
            }

            var fresh = page.Where(file => seen.Add(file.Id)).ToList();

            if (fresh.Count == 0) yield break;

            foreach (var file in fresh) yield return file;
        }
    }

    /// <summary>
    /// Files a person filed under an album, which have an identity and no graph.
    /// </summary>
    /// <remarks>
    /// <see cref="EnrichmentOutcome.LinkedByPerson"/> says it in as many words:
    /// filing files under an album takes each recording's identity off the
    /// release's track list and fetches no recording, so the catalogue knows
    /// what these files are and cannot say who played on them or what they
    /// perform. On the target library that is 615 files, 433 of them with no
    /// work at all — Beethoven's complete symphonies among them, 33 movements
    /// whose album page had nothing to group by.
    ///
    /// **They cannot be reached by the worklist above, on any of its three
    /// clauses.** These are the files AcoustID never placed, so most carry no
    /// <c>AcoustId</c>; the album screen stamps <c>RecordingLookupUtc</c> when
    /// it files them; and a decision sets <c>IdentityDecidedUtc</c>. Hence a
    /// second worklist rather than a widened one — and it needs no AcoustID turn
    /// at all, because the recording MBID is already in the catalogue.
    /// </remarks>
    private static readonly System.Linq.Expressions.Expression<Func<MediaFile, bool>> PersonFiled =
        file => file.EnrichmentOutcome == EnrichmentOutcome.LinkedByPerson
            && file.Recording != null
            && file.Recording.Mbid != null;

    /// <summary>
    /// The second worklist, a page at a time.
    /// </summary>
    /// <remarks>
    /// Paged like the first, and self-draining for the same reason: a file that
    /// is enriched becomes <see cref="EnrichmentOutcome.Linked"/> and leaves the
    /// predicate, so an unchanged page means the pass is done. The <c>seen</c>
    /// guard covers the rows that stayed — a transient failure, or a recording
    /// MusicBrainz no longer knows.
    /// </remarks>
    private async IAsyncEnumerable<PersonFiledFile> ClaimPersonFiledAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var seen = new HashSet<MediaFileId>();

        while (!cancellationToken.IsCancellationRequested)
        {
            List<PersonFiledFile> page;

            var scope = scopeFactory.CreateAsyncScope();
            await using (scope.ConfigureAwait(false))
            {
                var db = scope.ServiceProvider.GetRequiredService<FonotecaDbContext>();

                var rows = await db.MediaFiles
                    .AsNoTracking()
                    .Where(PersonFiled)
                    .OrderBy(f => f.Id)
                    .Select(f => new { f.Id, f.Path, Mbid = f.Recording!.Mbid })
                    .Take(PageSize)
                    .ToListAsync(cancellationToken)
                    .ConfigureAwait(false);

                page = [.. rows.Select(r => new PersonFiledFile(r.Id, r.Path, r.Mbid!.Value))];
            }

            var fresh = page.Where(file => seen.Add(file.Id)).ToList();

            if (fresh.Count == 0) yield break;

            foreach (var file in fresh) yield return file;
        }
    }

    /// <summary>
    /// One person-filed file: recording to rows, with no AcoustID turn spent.
    /// </summary>
    /// <remarks>
    /// The lookup happens outside the scope's transaction, for the reason
    /// <see cref="HandleAsync"/> gives.
    ///
    /// **Nothing here writes <c>RecordingLookupUtc</c> or an outcome other than
    /// <see cref="EnrichmentOutcome.Linked"/>**, and the second half of that is
    /// load-bearing. <c>RecordingNotFound</c> is one of the two values the
    /// by-hand album screen reads as an open question, so writing it onto a file
    /// somebody already answered would put their decision back on the worklist
    /// as a question — undoing the work this pass exists to complete. A file
    /// whose recording MusicBrainz cannot produce keeps
    /// <see cref="EnrichmentOutcome.LinkedByPerson"/>, which is exactly what it
    /// still is, and costs one lookup on the next run. That is a deliberate
    /// exception to the <c>AcoustIdCheckedUtc</c> lesson about recording that we
    /// asked: the MBID came out of MusicBrainz's own release track list and WS/2
    /// follows merges, so the case is pathological rather than routine, and
    /// paying for it is cheaper than a column that has to be cleared by hand.
    /// </remarks>
    private async Task HandlePersonFiledAsync(
        PersonFiledFile file,
        Tally counts,
        Memo memo,
        CancellationToken cancellationToken)
    {
        MusicBrainzRecording? recording;
        MusicBrainzWork? work = null;

        try
        {
            if (!memo.Recordings.TryGetValue(file.Mbid, out recording))
            {
                recording = await musicBrainz
                    .GetRecordingAsync(file.Mbid, cancellationToken)
                    .ConfigureAwait(false);

                memo.Recordings[file.Mbid] = recording;
            }

            if (recording is not null)
            {
                work = await WorkForAsync(recording, memo, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (ProviderUnavailableException cause)
        {
            counts.Failed++;
            Log.FileNotEnriched(logger, file.Path, EnrichmentOutcome.LookupFailed, cause.Message);
            return;
        }

        if (recording is null)
        {
            counts.RecordingNotFound++;

            // Formatted before the call, not inside it: the analyser objects to
            // work done for a log line that may be disabled.
            var missing = file.Mbid.Value.ToString();

            Log.FileNotEnriched(
                logger, file.Path, EnrichmentOutcome.RecordingNotFound, missing);

            return;
        }

        var scope = scopeFactory.CreateAsyncScope();
        await using (scope.ConfigureAwait(false))
        {
            var db = scope.ServiceProvider.GetRequiredService<FonotecaDbContext>();

            var row = await db.MediaFiles
                .FirstOrDefaultAsync(f => f.Id == file.Id, cancellationToken)
                .ConfigureAwait(false);

            // Absent means a scan removed it while we were working.
            if (row is null) return;

            var writer = new CatalogueWriter(db, memo.Artists);

            row.RecordingId = await writer
                .UpsertAsync(recording, work, cancellationToken)
                .ConfigureAwait(false);

            // Now it is what the value promises: linked, with its artists.
            row.EnrichmentOutcome = EnrichmentOutcome.Linked;

            counts.Linked++;

            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// One file: cluster to recording, recording to rows, all in a single scope.
    /// </summary>
    /// <remarks>
    /// The lookups happen <i>outside</i> the scope's transaction on purpose. A
    /// MusicBrainz round trip is tens of milliseconds against a mirror and up to
    /// ten seconds against the public server, and holding a database connection
    /// open across it would tie the connection pool to somebody else's latency.
    /// </remarks>
    private async Task HandleAsync(
        PendingFile file,
        Tally counts,
        Memo memo,
        CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;

        var resolved = await ResolveAsync(file, memo, cancellationToken).ConfigureAwait(false);

        var scope = scopeFactory.CreateAsyncScope();
        await using (scope.ConfigureAwait(false))
        {
            var db = scope.ServiceProvider.GetRequiredService<FonotecaDbContext>();

            var row = await db.MediaFiles
                .FirstOrDefaultAsync(f => f.Id == file.Id, cancellationToken)
                .ConfigureAwait(false);

            // Absent means a scan removed it while we were working. Nothing to
            // update, and nothing wrong.
            if (row is null) return;

            if (resolved.Outcome == EnrichmentOutcome.LookupFailed)
            {
                // Transient. RecordingLookupUtc stays null, so the row stays on
                // the worklist and the next run retries it.
                counts.Failed++;
                Log.FileNotEnriched(logger, file.Path, resolved.Outcome, resolved.Detail);
                return;
            }

            row.RecordingLookupUtc = now;
            row.EnrichmentOutcome = resolved.Outcome;

            // Refreshed on every pass that asks, so the freshest answer wins.
            // Written after the LookupFailed return above, because a lookup that
            // did not answer has no evidence to record and must not overwrite the
            // evidence already there.
            if (resolved.Matches is { } matches)
            {
                row.AcoustIdMatchesJson = AcoustIdEvidence.Serialise(matches);
                row.AcoustIdMatchesUtc = now;
            }

            if (resolved.Recording is { } recording)
            {
                var writer = new CatalogueWriter(db, memo.Artists);

                row.RecordingId = await writer
                    .UpsertAsync(recording, resolved.Work, cancellationToken)
                    .ConfigureAwait(false);

                counts.Linked++;
            }
            else
            {
                switch (resolved.Outcome)
                {
                    case EnrichmentOutcome.NoRecording: counts.NoRecording++; break;
                    default: counts.RecordingNotFound++; break;
                }

                Log.FileNotEnriched(logger, file.Path, resolved.Outcome, resolved.Detail);
            }

            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>A file that has a recording MBID already, and needs its graph.</summary>
    private sealed record PersonFiledFile(MediaFileId Id, string Path, Mbid Mbid);

    /// <summary>The network half: which recording is this, and what is it?</summary>
    private async Task<Resolution> ResolveAsync(
        PendingFile file,
        Memo memo,
        CancellationToken cancellationToken)
    {
        try
        {
            var matches = await MatchesForAsync(file, memo, cancellationToken).ConfigureAwait(false);

            // "Asked and told nothing" is worth storing; "never asked" is not,
            // and an empty document for the second would read as the first. Both
            // arrive as an empty list, so the two are told apart by the file's
            // own columns — which is sound only because a fingerprintless file
            // never writes its emptiness into the memo. See MatchesForAsync.
            var asked = file.Fingerprint is { Length: > 0 } && file.FingerprintDuration is not null;
            var evidence = asked ? matches : null;

            // The recording-level collapse, and the right one here — its own
            // documentation says so, and says equally plainly that it is the
            // wrong one for AcoustIdSelection, which is choosing something else.
            // Derived from the answer on every call rather than memoised beside
            // it, because it is a rule and the answer is a fact: caching a rule's
            // output leaves the cache quietly wrong the day the rule changes.
            var candidates = RecordingCandidates.From(matches);

            if (candidates.Count == 0)
            {
                return new Resolution(EnrichmentOutcome.NoRecording, null, null, null, evidence);
            }

            var mbid = candidates[0].Id;

            if (!memo.Recordings.TryGetValue(mbid, out var recording))
            {
                recording = await musicBrainz.GetRecordingAsync(mbid, cancellationToken).ConfigureAwait(false);
                memo.Recordings[mbid] = recording;
            }

            if (recording is null)
            {
                // AcoustID still points at an MBID MusicBrainz has merged away.
                // Recorded rather than retried: the answer will not change until
                // AcoustID's links are updated, which is not on our schedule.
                return new Resolution(
                    EnrichmentOutcome.RecordingNotFound, null, null, mbid.Value.ToString(), evidence);
            }

            var work = await WorkForAsync(recording, memo, cancellationToken).ConfigureAwait(false);

            return new Resolution(EnrichmentOutcome.Linked, recording, work, null, evidence);
        }
        catch (ProviderUnavailableException cause)
        {
            return new Resolution(EnrichmentOutcome.LookupFailed, null, null, cause.Message);
        }
    }

    /// <summary>
    /// What AcoustID says about this file's audio, asked once per cluster.
    /// </summary>
    /// <remarks>
    /// The whole answer rather than the recording it collapses to, so the row
    /// can keep the evidence — see <c>MediaFile.AcoustIdMatchesJson</c>. Which
    /// recording it names is <see cref="RecordingCandidates"/>'s job and is
    /// worked out by the caller: one AcoustID cluster is routinely linked to
    /// several recordings by people who disagreed about which it was, and one
    /// recording routinely spans several clusters.
    ///
    /// No threshold is applied. Identification already refused to record an
    /// AcoustID for anything below one, so every file reaching this pass carries
    /// a cluster that cleared both the score and the margin; asking for a second
    /// opinion here would be re-litigating a decision with less evidence than the
    /// decision had.
    /// </remarks>
    private async Task<IReadOnlyList<AcoustIdMatch>> MatchesForAsync(
        PendingFile file,
        Memo memo,
        CancellationToken cancellationToken)
    {
        if (memo.Clusters.TryGetValue(file.AcoustId, out var cached)) return cached;

        // A file with no stored fingerprint cannot be asked about without
        // reopening it, which is the one thing this pass does not do. It happens
        // when identification adopted an AcoustID straight from the file's tags
        // — the cheap path that never fingerprints — so the tag is trustworthy
        // and the fingerprint simply does not exist yet. The next identification
        // pass has no work for it either; that is a gap worth closing when
        // something needs it, and not by opening files here.
        //
        // It does not reach the memo, and that is the correction rather than an
        // omission. The memo is keyed on the *cluster* and answers "what did
        // AcoustID say about this audio"; this is a fact about one file's
        // columns. Recorded against the cluster, the next file sharing it — with
        // a perfectly good fingerprint — reads an empty answer nobody asked for,
        // is never looked up, and now has that emptiness written into its
        // evidence column and believed for a week.
        if (file.Fingerprint is not { Length: > 0 } || file.FingerprintDuration is not { } duration)
        {
            return [];
        }

        var matches = await acoustId
            .LookupAsync(new AudioFingerprint(file.Fingerprint, duration), cancellationToken)
            .ConfigureAwait(false);

        memo.Clusters[file.AcoustId] = matches;
        return matches;
    }

    /// <summary>The composition, asked once per work rather than once per file.</summary>
    private async Task<MusicBrainzWork?> WorkForAsync(
        MusicBrainzRecording recording,
        Memo memo,
        CancellationToken cancellationToken)
    {
        if (recording.WorkId is not { } workId) return null;

        if (memo.Works.TryGetValue(workId, out var cached)) return cached;

        var work = await musicBrainz.GetWorkAsync(workId, cancellationToken).ConfigureAwait(false);

        memo.Works[workId] = work;
        return work;
    }

    private async Task Report(string jobId, int processed, int total, string? current, string state)
    {
        var message = new JobProgressMessage(jobId, JobKind, state, processed, total, current, clock.UtcNow);

        await hub.Clients.All.JobProgress(message).ConfigureAwait(false);
    }

    /// <summary>Mutable counters, held by one thread — the loop is the only writer.</summary>
    private sealed class Tally
    {
        public int Examined;
        public int Linked;
        public int NoRecording;
        public int RecordingNotFound;
        public int Failed;
    }

    /// <summary>
    /// What this run has already asked about.
    /// </summary>
    /// <remarks>
    /// Per run rather than a long-lived cache, because a cache that outlives the
    /// pass has to decide when a MusicBrainz answer goes stale, and nothing here
    /// needs that decision yet. The dictionaries store nulls too: "we asked and
    /// MusicBrainz said no" is exactly as worth remembering as an answer, and a
    /// <c>TryGetValue</c> that misses on it would re-ask once per file.
    ///
    /// <see cref="Artists"/> is not a lookup cache but a count — how many
    /// distinct artists this run touched, for the summary.
    /// </remarks>
    private sealed class Memo
    {
        /// <summary>
        /// AcoustID's whole answer per cluster, not the recording it collapses to.
        /// </summary>
        /// <remarks>
        /// It used to hold the winning MBID. Holding the answer instead is what
        /// lets every file sharing a cluster have the evidence stamped on it
        /// rather than only the first one that asked — the memo exists so five
        /// encodings of a track cost one lookup, and a per-file cache that only
        /// the first of the five got would make that saving visible as a gap.
        /// It is also smaller than it looks beside <see cref="Recordings"/>,
        /// which holds whole MusicBrainz recordings with up to 25 releases each.
        /// </remarks>
        public Dictionary<AcoustId, IReadOnlyList<AcoustIdMatch>> Clusters { get; } = [];

        public Dictionary<Mbid, MusicBrainzRecording?> Recordings { get; } = [];

        public Dictionary<Mbid, MusicBrainzWork?> Works { get; } = [];

        public HashSet<Mbid> Artists { get; } = [];
    }

    /// <remarks>
    /// <see cref="Cluster"/> is nullable only because the column is; the worklist
    /// predicate guarantees it is set on every row that reaches here.
    /// </remarks>
    private readonly record struct PendingFile(
        MediaFileId Id,
        string Path,
        AcoustId? Cluster,
        string? Fingerprint,
        TimeSpan? FingerprintDuration)
    {
        public AcoustId AcoustId => Cluster!.Value;
    }

    /// <summary>What the network said about one file, before any row is touched.</summary>
    /// <remarks>
    /// <see cref="Matches"/> is AcoustID's raw answer, carried back so the row
    /// can keep it. Null means nothing was asked — a file with no stored
    /// fingerprint, or a lookup that failed — which is different from an answer
    /// that named nothing, and only the second is worth writing down.
    /// </remarks>
    private readonly record struct Resolution(
        EnrichmentOutcome Outcome,
        MusicBrainzRecording? Recording,
        MusicBrainzWork? Work,
        string? Detail,
        IReadOnlyList<AcoustIdMatch>? Matches = null);
}

/// <summary>
/// The <c>SourceType</c> and <c>TargetType</c> strings <see cref="Relationship"/> uses.
/// </summary>
/// <remarks>
/// Constants rather than literals because they are matched in queries as well as
/// written, and a typo in one of the two places is a link that silently never
/// resolves — the failure mode a browse page shows as "this artist has no
/// tracks" rather than as an error.
/// </remarks>
public static class RelationshipTargets
{
    public const string Artist = "artist";
    public const string Recording = "recording";
    public const string Work = "work";
}

/// <summary>What a request to start a pass did.</summary>
public sealed record EnrichmentOutcomeStatus(EnrichmentStatus Status, string? JobId);

public enum EnrichmentStatus
{
    /// <summary>Running in the background; <c>JobId</c> is populated.</summary>
    Started = 0,

    /// <summary>A scan, an identification or another enrichment holds the gate.</summary>
    AlreadyRunning = 1,
}

/// <summary>Where a running pass has got to.</summary>
public sealed record EnrichmentProgress(string JobId, int Processed, int Total, string? CurrentFile);

/// <summary>
/// What one pass did.
/// </summary>
/// <remarks>
/// Every count is an <c>int</c> rather than a <c>long</c> so the generated
/// TypeScript types them as <c>number</c> under <c>NumberHandling.Strict</c>.
/// </remarks>
public sealed record EnrichmentSummary(
    string JobId,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset CompletedAtUtc,
    long DurationMilliseconds,

    /// <summary>Files taken off the worklist.</summary>
    int Examined,

    /// <summary>Files now linked to a recording.</summary>
    int Linked,

    /// <summary>
    /// Files whose AcoustID cluster names no MusicBrainz recording.
    /// </summary>
    /// <remarks>
    /// The ordinary shortfall, and not a failure: a cluster is a fingerprint
    /// grouping, and linking one to MusicBrainz is a separate act of curation
    /// that nobody may have performed for this audio.
    /// </remarks>
    int NoRecording,

    /// <summary>Files whose recording MBID MusicBrainz no longer has. Merged away.</summary>
    int RecordingNotFound,

    /// <summary>Transient failures. These stay on the worklist for the next run.</summary>
    int Failed,

    /// <summary>Distinct recordings this run looked up.</summary>
    int Recordings,

    /// <summary>Distinct works this run looked up.</summary>
    int Works,

    /// <summary>Distinct artists this run credited.</summary>
    int Artists,

    bool Cancelled);
