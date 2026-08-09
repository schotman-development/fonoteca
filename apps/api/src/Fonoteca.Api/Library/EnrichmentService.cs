using System.Diagnostics;
using Fonoteca.Api.Logging;
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

            return await db.MediaFiles
                .Where(f => f.AcoustId != null && f.RecordingLookupUtc == null)
                .CountAsync(cancellationToken)
                .ConfigureAwait(false);
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

        try
        {
            await foreach (var file in ClaimAsync(cancellationToken).ConfigureAwait(false))
            {
                try
                {
                    await HandleAsync(file, counts, memo, cancellationToken).ConfigureAwait(false);
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
                    Log.FileFailed(logger, file.Path, cause);
                }

                counts.Examined++;

                var now = clock.UtcNow;

                if (now - lastReport >= ProgressInterval)
                {
                    lastReport = now;
                    _progress = new EnrichmentProgress(jobId, counts.Examined, pending, file.Path);

                    await Report(jobId, counts.Examined, pending, file.Path, "running").ConfigureAwait(false);
                }
            }
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
                    .Where(f => f.AcoustId != null && f.RecordingLookupUtc == null)
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

            if (resolved.Recording is { } recording)
            {
                var writer = new CatalogueWriter(db, memo);

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

    /// <summary>The network half: which recording is this, and what is it?</summary>
    private async Task<Resolution> ResolveAsync(
        PendingFile file,
        Memo memo,
        CancellationToken cancellationToken)
    {
        try
        {
            var recordingId = await RecordingForAsync(file, memo, cancellationToken).ConfigureAwait(false);

            if (recordingId is not { } mbid)
            {
                return new Resolution(EnrichmentOutcome.NoRecording, null, null, null);
            }

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
                    EnrichmentOutcome.RecordingNotFound, null, null, mbid.Value.ToString());
            }

            var work = await WorkForAsync(recording, memo, cancellationToken).ConfigureAwait(false);

            return new Resolution(EnrichmentOutcome.Linked, recording, work, null);
        }
        catch (ProviderUnavailableException cause)
        {
            return new Resolution(EnrichmentOutcome.LookupFailed, null, null, cause.Message);
        }
    }

    /// <summary>
    /// Which MusicBrainz recording this file's audio is, asked once per cluster.
    /// </summary>
    /// <remarks>
    /// <see cref="RecordingCandidates"/> rather than the first recording on the
    /// first match: one AcoustID cluster is routinely linked to several
    /// recordings by people who disagreed about which it was, and one recording
    /// routinely spans several clusters. Collapsing onto recordings is the right
    /// rule <i>here</i> — its own documentation says so — and the wrong one for
    /// <see cref="AcoustIdSelection"/>, which is choosing something else.
    ///
    /// No threshold is applied. Identification already refused to record an
    /// AcoustID for anything below one, so every file reaching this pass carries
    /// a cluster that cleared both the score and the margin; asking for a second
    /// opinion here would be re-litigating a decision with less evidence than the
    /// decision had.
    /// </remarks>
    private async Task<Mbid?> RecordingForAsync(
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
        if (file.Fingerprint is not { Length: > 0 } || file.FingerprintDuration is not { } duration)
        {
            memo.Clusters[file.AcoustId] = null;
            return null;
        }

        var matches = await acoustId
            .LookupAsync(new AudioFingerprint(file.Fingerprint, duration), cancellationToken)
            .ConfigureAwait(false);

        var candidates = RecordingCandidates.From(matches);
        var best = candidates.Count == 0 ? (Mbid?)null : candidates[0].Id;

        memo.Clusters[file.AcoustId] = best;
        return best;
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
        public Dictionary<AcoustId, Mbid?> Clusters { get; } = [];

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
    private readonly record struct Resolution(
        EnrichmentOutcome Outcome,
        MusicBrainzRecording? Recording,
        MusicBrainzWork? Work,
        string? Detail);

    /// <summary>
    /// Writes one recording's graph into the catalogue, creating only what is missing.
    /// </summary>
    /// <remarks>
    /// Every upsert is keyed on the MBID, which carries a unique filtered index
    /// on all four entity types — so a rerun over an unchanged library converges
    /// on the same rows instead of duplicating them, and two files that resolve
    /// to the same recording share it, which is the whole point of the
    /// <c>Recording ↔ MediaFile</c> split.
    ///
    /// It reads through the scope's <c>DbContext</c> rather than keeping its own
    /// identity map, so rows created for an earlier file in the same
    /// <c>SaveChanges</c> are found by the later one.
    /// </remarks>
    private sealed class CatalogueWriter(FonotecaDbContext db, Memo memo)
    {
        public async Task<RecordingId> UpsertAsync(
            MusicBrainzRecording source,
            MusicBrainzWork? work,
            CancellationToken cancellationToken)
        {
            var workRow = work is null
                ? null
                : await UpsertWorkAsync(work, cancellationToken).ConfigureAwait(false);

            var recording = await db.Recordings
                .FirstOrDefaultAsync(r => r.Mbid == source.Id, cancellationToken)
                .ConfigureAwait(false);

            if (recording is null)
            {
                recording = new Recording
                {
                    Id = RecordingId.New(),
                    Title = source.Title,
                    Mbid = source.Id,
                };

                db.Recordings.Add(recording);
            }
            else
            {
                recording.Title = source.Title;
            }

            recording.Duration = source.Length;
            recording.WorkId = workRow?.Id;

            var credits = PrimaryCredits.From(source, work);

            foreach (var credit in credits) memo.Artists.Add(credit.ArtistId);

            await ApplyCreditsAsync(recording, workRow, credits, cancellationToken).ConfigureAwait(false);

            return recording.Id;
        }

        private async Task<Work> UpsertWorkAsync(
            MusicBrainzWork source,
            CancellationToken cancellationToken)
        {
            var work = await db.Works
                .FirstOrDefaultAsync(w => w.Mbid == source.Id, cancellationToken)
                .ConfigureAwait(false);

            if (work is null)
            {
                work = new Work
                {
                    Id = WorkId.New(),
                    Title = source.Title,
                    Mbid = source.Id,
                };

                db.Works.Add(work);
            }
            else
            {
                work.Title = source.Title;
            }

            work.Type = source.Type;
            return work;
        }

        /// <summary>
        /// Billed credits become <c>ArtistCredit</c>; everything else becomes a
        /// <c>Relationship</c>.
        /// </summary>
        /// <remarks>
        /// The split is not cosmetic. <see cref="ArtistCredit"/>'s
        /// <c>Position</c> and <c>JoinPhrase</c> describe a printed billing line —
        /// "Beth Hart &amp; Joe Bonamassa" — and inserting a conductor into that
        /// sequence corrupts the meaning for every consumer that reads it back as
        /// a credit line. Conductors and ensembles are typed links to the
        /// recording; writers are typed links to the <i>work</i>, which is where
        /// MusicBrainz puts them and what makes "everything this composer wrote"
        /// answerable across performances.
        ///
        /// Existing rows for this recording are replaced rather than merged. A
        /// second pass over unchanged data produces the identical set, and a pass
        /// after a MusicBrainz correction produces the corrected one — whereas
        /// merging would accumulate every credit the recording ever had.
        /// </remarks>
        private async Task ApplyCreditsAsync(
            Recording recording,
            Work? work,
            IReadOnlyList<PrimaryCredit> credits,
            CancellationToken cancellationToken)
        {
            var stale = await db.ArtistCredits
                .Where(c => c.RecordingId == recording.Id)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            db.ArtistCredits.RemoveRange(stale);

            var recordingKey = recording.Id.Value;

            var staleLinks = await db.Relationships
                .Where(r => r.TargetType == RelationshipTargets.Recording && r.TargetId == recordingKey)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            if (work is not null)
            {
                var workKey = work.Id.Value;

                staleLinks.AddRange(await db.Relationships
                    .Where(r => r.TargetType == RelationshipTargets.Work && r.TargetId == workKey)
                    .ToListAsync(cancellationToken)
                    .ConfigureAwait(false));
            }

            db.Relationships.RemoveRange(staleLinks);

            foreach (var credit in credits)
            {
                var artist = await UpsertArtistAsync(credit, cancellationToken).ConfigureAwait(false);

                if (credit.Role == CreditRole.Billed)
                {
                    db.ArtistCredits.Add(new ArtistCredit
                    {
                        Id = Guid.CreateVersion7(),
                        ArtistId = artist.Id,
                        RecordingId = recording.Id,
                        Position = credit.Position,
                        JoinPhrase = credit.JoinPhrase,
                        CreditedAs = credit.Name == artist.Name ? null : credit.Name,
                    });

                    continue;
                }

                // A writer is a fact about the composition, not about this
                // performance of it — so it hangs off the work when there is one.
                // Without a work there is nowhere else to put it, and the
                // recording is the honest second choice.
                var toWork = credit.Role == CreditRole.Writer && work is not null;

                db.Relationships.Add(new Relationship
                {
                    Id = Guid.CreateVersion7(),
                    SourceType = RelationshipTargets.Artist,
                    SourceId = artist.Id.Value,
                    TargetType = toWork ? RelationshipTargets.Work : RelationshipTargets.Recording,
                    TargetId = toWork ? work!.Id.Value : recording.Id.Value,
                    Type = RoleName(credit.Role),
                    Attribute = credit.ArtistType,
                    WorkId = toWork ? work!.Id : null,
                    RecordingId = toWork ? null : recording.Id,
                });
            }
        }

        private async Task<Artist> UpsertArtistAsync(
            PrimaryCredit credit,
            CancellationToken cancellationToken)
        {
            var artist = await db.Artists
                .FirstOrDefaultAsync(a => a.Mbid == credit.ArtistId, cancellationToken)
                .ConfigureAwait(false);

            if (artist is null)
            {
                artist = new Artist
                {
                    Id = ArtistId.New(),
                    Name = credit.Name,
                    Mbid = credit.ArtistId,
                };

                db.Artists.Add(artist);
            }

            // The sort name is what the artist list orders by, and a credit that
            // carries one is better evidence than the last one that did not.
            // The display name is left alone once set: a credit line prints what
            // that release printed, and overwriting the canonical name with it
            // would rename "David Bowie" to "Bowie" on a sleeve's say-so.
            artist.SortName ??= credit.SortName;
            artist.Type ??= credit.ArtistType;
            artist.Disambiguation ??= credit.Disambiguation;

            return artist;
        }

        /// <summary>
        /// The relationship type as stored, which is the role rather than
        /// MusicBrainz's own relation name.
        /// </summary>
        /// <remarks>
        /// Deliberate narrowing. MusicBrainz distinguishes "performing orchestra"
        /// from a "performer" relation on an artist typed Orchestra, and
        /// <see cref="PrimaryCredits"/> has already decided those mean the same
        /// thing; storing the raw name would make the browse query re-derive that
        /// decision in SQL, in a second place, where it would drift.
        /// </remarks>
        private static string RoleName(CreditRole role) => role switch
        {
            CreditRole.Conductor => "conductor",
            CreditRole.Ensemble => "ensemble",
            CreditRole.Writer => "composer",
            _ => throw new InvalidOperationException($"Role {role} is not a relationship."),
        };
    }
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
