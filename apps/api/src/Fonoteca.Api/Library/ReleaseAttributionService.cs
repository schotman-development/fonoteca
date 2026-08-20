using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Fonoteca.Api.Configuration;
using Fonoteca.Api.Logging;
using Fonoteca.Api.Matching;
using Fonoteca.Api.Realtime;
using Fonoteca.Data;
using Fonoteca.Domain.Abstractions;
using Fonoteca.Domain.Catalogue;
using Fonoteca.Domain.Identification;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Fonoteca.Api.Library;

/// <summary>
/// Works out which album each identified file came from, and writes the albums down.
/// </summary>
/// <remarks>
/// The third pass, and the one that finally fills <c>Releases</c>,
/// <c>ReleaseGroups</c> and <c>Tracks</c> — empty tables since the first
/// migration, because deciding which of a recording's forty releases a given
/// file came from is a rule of its own rather than a lookup.
///
/// <b>It has no per-file unit of work, and that is the structural difference
/// from the two passes before it.</b> Identification and enrichment both take
/// one file, ask a question about it, and commit the answer; each file is a
/// transaction and resumability comes for free. A single file cannot name its
/// release — see <see cref="ReleaseAttribution"/> — so the unit here is a
/// <b>component</b>: a set of files that share candidate releases, decided
/// together and committed together. A component is usually an album and
/// occasionally an artist's whole catalogue, which is small enough that
/// interrupting the pass loses seconds.
///
/// <b>Components are discovered, not assumed.</b> The obvious grouping is the
/// directory, and using it is exactly what the whole design refuses: the folder
/// is a claim made by whatever wrote the files. So a component starts from one
/// unattributed file and grows outwards — browse its recording's releases, fetch
/// those releases' track lists, and every track naming a recording this library
/// also holds pulls that file in and puts its recording on the frontier. What
/// closes is a real component of the file-to-release graph.
///
/// <b>Two stages, forced by the web service.</b> A browse cannot carry track
/// lists (asking for them makes it silently drop releases — see
/// <c>MusicBrainzCatalogue.BrowseIncludes</c>), and a lookup cannot carry the
/// full candidate set (it caps at 25). So candidates come from a browse and
/// track lists from a lookup, with a prune in between so that a 150-track
/// anthology contributing one song is not worth a request. The prune is a
/// heuristic and calling it a bound was worth two failed live runs — see
/// <see cref="WorthFetching"/>.
///
/// <b>Everything is memoised for the whole run.</b> The same compilations recur
/// across every artist in a library, and one browse per <i>recording</i> would
/// be 7,000 requests where one per component is a few hundred. This is what
/// keeps the pass near the number of albums rather than the number of files.
/// </remarks>
public sealed class ReleaseAttributionService(
    LibraryWorkGate gate,
    IServiceScopeFactory scopeFactory,
    IMusicBrainzCatalogue musicBrainz,
    IHubContext<JobsHub, IJobsClient> hub,
    IHostApplicationLifetime lifetime,
    IOptions<FonotecaOptions> options,
    IClock clock,
    ILogger<ReleaseAttributionService> logger) : IHostedService, IDisposable
{
    public const string JobKind = "library.attribute";

    private static readonly TimeSpan ProgressInterval = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// How far a component may grow before expansion stops.
    /// </summary>
    /// <remarks>
    /// Jazz standards are the reason. One recording of a standard appears on
    /// hundreds of anthologies, each of which names hundreds of other recordings,
    /// and a component allowed to close naturally would swallow half the library
    /// and spend an hour doing it. The caps stop the <i>expansion</i>, never the
    /// attribution: whatever has been gathered is still decided on, and the files
    /// that were not reached stay on the worklist for a component of their own.
    ///
    /// Reaching a cap is logged. A silent cap would look exactly like a component
    /// that closed on its own, and the difference is whether the answer used all
    /// the evidence.
    /// </remarks>
    private const int MaximumComponentFiles = 600;

    /// <summary>
    /// Where a component stops fetching the long tail of one-song appearances.
    /// </summary>
    /// <remarks>
    /// A flat ceiling is the wrong shape here and both settings of one proved it.
    /// Candidates are confirmed in order of how many of the component's
    /// recordings each holds, so a flat cut takes the tail — but at 400 the tail
    /// reached up into releases the library owns whole, and <i>Off the Wall</i>
    /// was discarded in favour of a box set that merely reprints it. Raising the
    /// number to 2,000 fixed that and made one component spend 1,662 requests on
    /// 21 files, which is the same mistake pointed the other way.
    ///
    /// So the cut is by <i>kind</i> rather than by count. A release sharing two
    /// or more recordings with the component is one the library plausibly owns
    /// part of, and is always fetched — that is the same judgement
    /// <see cref="WorthFetching"/> makes, applied to the ceiling as well. A
    /// release sharing exactly one is a long-tail appearance, and past this many
    /// confirmations those stop being worth a request.
    /// </remarks>
    private const int SoftCandidateCap = 300;

    /// <summary>The backstop, for a component where even the plausible releases run away.</summary>
    private const int MaximumComponentCandidates = 2_000;

    private readonly SemaphoreSlim _finished = new(0, 1);

    private CancellationTokenSource? _cancellation;
    private volatile AttributionProgress? _progress;
    private volatile AttributionSummary? _lastCompleted;

    public bool IsRunning => _progress is not null;

    public AttributionProgress? Progress => _progress;

    public AttributionSummary? LastCompleted => _lastCompleted;

    /// <summary>Identified files that have never been asked about an album.</summary>
    public async Task<int> CountPendingAsync(CancellationToken cancellationToken = default)
    {
        var scope = scopeFactory.CreateAsyncScope();

        await using (scope.ConfigureAwait(false))
        {
            var db = scope.ServiceProvider.GetRequiredService<FonotecaDbContext>();

            return await db.MediaFiles
                .Where(f => f.RecordingId != null
                    && f.ReleaseLookupUtc == null
                    && f.ReleaseDecidedUtc == null)
                .CountAsync(cancellationToken)
                .ConfigureAwait(false);
        }
    }

    public AttributionStartOutcome Start()
    {
        if (!gate.TryEnter(JobKind, out var lease))
        {
            Log.AttributionBusy(logger, gate.ActiveKind ?? "other work");
            return new AttributionStartOutcome(AttributionStatus.AlreadyRunning, null);
        }

        var jobId = Guid.CreateVersion7().ToString("N")[..12];
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.ApplicationStopping);

        _cancellation = cancellation;
        _progress = new AttributionProgress(jobId, 0, 0, null);

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
                    Log.AttributionAborted(logger, cause.Message);
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

        return new AttributionStartOutcome(AttributionStatus.Started, jobId);
    }

    public bool Cancel()
    {
        var running = _cancellation;
        if (running is null) return false;

        running.Cancel();
        return true;
    }

    public void Dispose() => _finished.Dispose();

    Task IHostedService.StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    async Task IHostedService.StopAsync(CancellationToken cancellationToken)
    {
        if (!IsRunning) return;

        Cancel();
        await _finished.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
    }

    private async Task<AttributionSummary> RunAsync(string jobId, CancellationToken cancellationToken)
    {
        var startedAt = clock.UtcNow;
        var elapsed = Stopwatch.StartNew();
        var counts = new Tally();
        var memo = new Memo();

        var pending = await CountPendingAsync(cancellationToken).ConfigureAwait(false);

        await ForgetAnsweredCandidatesAsync(cancellationToken).ConfigureAwait(false);

        Log.AttributionStarted(logger, jobId, pending);

        _progress = new AttributionProgress(jobId, 0, pending, null);

        var lastReport = clock.UtcNow;

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var seed = await NextSeedAsync(cancellationToken).ConfigureAwait(false);
                if (seed is null) break;

                try
                {
                    await HandleComponentAsync(seed, counts, memo, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (ProviderRejectedException)
                {
                    // A missing contact or a refused request: every remaining
                    // component would fail identically, so the first one ends it.
                    throw;
                }
#pragma warning disable CA1031 // The backstop: no one component may end the pass.
                catch (Exception cause)
#pragma warning restore CA1031
                {
                    // The seed is stamped so the pass cannot spin on it forever.
                    // Everything else in the component stays pending, which is
                    // right — they were never decided.
                    counts.Failed++;
                    Log.FileFailed(logger, seed.Path, cause);
                    await RefuseAsync(seed, cancellationToken).ConfigureAwait(false);
                    counts.Examined++;
                }

                var now = clock.UtcNow;

                if (now - lastReport >= ProgressInterval)
                {
                    lastReport = now;
                    _progress = new AttributionProgress(jobId, counts.Examined, pending, seed.Path);

                    await Report(jobId, counts.Examined, pending, seed.Path, "running").ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Cancelled between components, which is where cancellation is checked.
        }

        var summary = new AttributionSummary(
            JobId: jobId,
            StartedAtUtc: startedAt,
            CompletedAtUtc: clock.UtcNow,
            DurationMilliseconds: elapsed.ElapsedMilliseconds,
            Examined: counts.Examined,
            Attributed: counts.Attributed,
            Ambiguous: counts.Ambiguous,
            GroupOnly: counts.GroupOnly,
            NoConfidentFit: counts.NoConfidentFit,
            NoCandidate: counts.NoCandidate,
            Failed: counts.Failed,
            Components: counts.Components,
            Releases: counts.Releases,
            Cancelled: cancellationToken.IsCancellationRequested);

        Log.AttributionCompleted(
            logger, jobId, counts.Attributed, counts.Ambiguous, counts.GroupOnly,
            counts.NoConfidentFit, counts.NoCandidate, counts.Failed, counts.Releases,
            summary.DurationMilliseconds);

        await Report(jobId, counts.Examined, pending, null, "completed").ConfigureAwait(false);

        return summary;
    }

    /// <summary>
    /// One unattributed file to start the next component from.
    /// </summary>
    /// <remarks>
    /// Deliberately one row rather than a page. Every component stamps every file
    /// it decided, so the worklist shrinks by a whole album between calls, and a
    /// page fetched in advance would be mostly stale by the time it was read.
    /// </remarks>
    private async Task<PendingFile?> NextSeedAsync(CancellationToken cancellationToken)
    {
        var scope = scopeFactory.CreateAsyncScope();

        await using (scope.ConfigureAwait(false))
        {
            var db = scope.ServiceProvider.GetRequiredService<FonotecaDbContext>();

            return await db.MediaFiles
                .AsNoTracking()
                .Where(f => f.RecordingId != null
                    && f.ReleaseLookupUtc == null
                    && f.ReleaseDecidedUtc == null
                    && f.Recording!.Mbid != null)
                .OrderBy(f => f.Id)
                .Select(f => new PendingFile(
                    f.Id,
                    f.Path,
                    f.Recording!.Title,
                    f.Recording!.Mbid!.Value,
                    f.FingerprintDuration ?? f.Quality!.Duration,
                    f.SizeBytes))
                .FirstOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Grows a component out from one file, decides it, and writes it.
    /// </summary>
    /// <remarks>
    /// The network work happens entirely outside the database scope, as it does
    /// in the enrichment pass and for the same reason: holding a connection open
    /// across a remote call ties the pool to somebody else's latency. The scope
    /// opens once the answer is known and closes when it is committed.
    /// </remarks>
    private async Task HandleComponentAsync(
        PendingFile seed,
        Tally counts,
        Memo memo,
        CancellationToken cancellationToken)
    {
        var component = await GatherAsync(seed, memo, cancellationToken).ConfigureAwait(false);

        var assignments = ReleaseAttribution.Assign(
            component.Files.Select(file => file.ToAttributionFile()).ToList(),
            component.Candidates,
            new AttributionThresholds(
                options.Value.ReleaseMinimumCoverage,
                TimeSpan.FromMilliseconds(options.Value.ReleaseMaximumDriftMs)));

        var chosen = component.Candidates
            .Where(release => assignments.Any(a => a.Release == release.Id))
            .ToList();

        var scope = scopeFactory.CreateAsyncScope();

        await using (scope.ConfigureAwait(false))
        {
            var db = scope.ServiceProvider.GetRequiredService<FonotecaDbContext>();
            var writer = new ReleaseWriter(db);

            // Only the releases something was actually filed under. The candidate
            // set for one component runs to hundreds, and persisting all of them
            // would fill the catalogue with albums nobody owns — then a release
            // list would be a browse of MusicBrainz rather than of the library.
            var releases = new Dictionary<Mbid, (ReleaseId Release, ReleaseGroupId? Group)>();

            foreach (var release in chosen)
            {
                releases[release.Id] = await writer
                    .UpsertAsync(release, component.Formats.GetValueOrDefault(release.Id), cancellationToken)
                    .ConfigureAwait(false);

                counts.Releases++;
            }

            // Floored, and that is load-bearing rather than tidy. This one value
            // is stamped on every file the component decides, which makes it the
            // component's identity — the id the worklist prints and the key the
            // candidate document is stored under. PostgreSQL keeps microseconds
            // and .NET keeps 100ns ticks, so an unfloored stamp is a key that
            // never matches what comes back out of the column.
            var now = StoreTime.ToStorePrecision(clock.UtcNow);
            var recordings = component.Files.ToDictionary(file => file.Id, file => file.Recording);

            // The files this component is about to leave open, collected as the
            // outcomes are written rather than read off the rule: the outcome on
            // the row is not always the one `Assign` returned — a `NoCandidate`
            // whose only candidates the prune discarded is rewritten to
            // `NoConfidentFit` a few lines below.
            var refused = new List<MediaFileId>();

            foreach (var assignment in assignments)
            {
                var row = await db.MediaFiles
                    .FirstOrDefaultAsync(f => f.Id == assignment.File, cancellationToken)
                    .ConfigureAwait(false);

                // Absent means a scan removed it while the lookups were running.
                if (row is null) continue;

                row.ReleaseLookupUtc = now;

                // The prune discarded releases the rule never saw, so a bare
                // "no candidate" from the rule may only mean "none survived".
                row.AttributionOutcome =
                    assignment.Outcome == ReleaseAttributionOutcome.NoCandidate
                    && component.Placed.Contains(recordings[assignment.File])
                        ? ReleaseAttributionOutcome.NoConfidentFit
                        : assignment.Outcome;
                row.EditionAlternatives = assignment.EditionAlternatives;
                row.ReleaseId = null;
                row.TrackId = null;
                row.ReleaseGroupId = null;

                if (assignment.Release is { } releaseMbid && releases.TryGetValue(releaseMbid, out var written))
                {
                    row.ReleaseId = written.Release;
                    row.ReleaseGroupId = written.Group;
                    row.TrackId = writer.TrackIdAt(
                        written.Release,
                        assignment.DiscNumber ?? 1,
                        assignment.Position ?? 0);
                }
                else if (assignment.ReleaseGroup is { } groupMbid)
                {
                    // The album without the pressing. The group row is written on
                    // its own here, because no release was chosen to carry it.
                    row.ReleaseGroupId = await writer
                        .UpsertGroupAsync(groupMbid, component.GroupTitle(groupMbid), cancellationToken)
                        .ConfigureAwait(false);
                }

                counts.Record(row.AttributionOutcome);
                counts.Examined++;

                if (Unanswered.Contains(row.AttributionOutcome)) refused.Add(assignment.File);
            }

            counts.Components++;

            await KeepCandidatesAsync(db, now, component, refused, cancellationToken).ConfigureAwait(false);

            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Drops candidate documents for components nobody can be asked about any more.
    /// </summary>
    /// <remarks>
    /// A component is a timestamp, and a re-run mints new ones — so without this
    /// the table keeps a document per component the library has ever had rather
    /// than per question it currently holds. Nothing reads an orphan (the file
    /// count check would miss it anyway), so this is housekeeping rather than
    /// correctness, and it belongs at the start of a pass because that is the
    /// only moment the whole set is about to be rewritten.
    ///
    /// One statement, and it names the same predicate the worklist does: a
    /// component with no file still waiting on an answer is not a question.
    /// </remarks>
    private async Task ForgetAnsweredCandidatesAsync(CancellationToken cancellationToken)
    {
        var scope = scopeFactory.CreateAsyncScope();

        await using (scope.ConfigureAwait(false))
        {
            var db = scope.ServiceProvider.GetRequiredService<FonotecaDbContext>();

            await db.ReleaseCandidateSets
                .Where(set => !db.MediaFiles.Any(file =>
                    file.ReleaseLookupUtc == set.ComponentUtc
                    && file.ReleaseDecidedUtc == null
                    && Unanswered.Contains(file.AttributionOutcome)))
                .ExecuteDeleteAsync(cancellationToken)
                .ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The two outcomes that leave a person a question to answer.
    /// </summary>
    /// <remarks>
    /// The same pair <c>CatalogueEndpoints.UnattributedOutcomes</c> builds the
    /// worklist from. A component with none of these was decided, and a decided
    /// component is not a question — so it gets no candidate document, and the
    /// table stays the size of the worklist rather than the size of the library.
    /// </remarks>
    private static readonly ReleaseAttributionOutcome[] Unanswered =
        [ReleaseAttributionOutcome.NoConfidentFit, ReleaseAttributionOutcome.NoCandidate];

    /// <summary>
    /// Keeps the candidate set this component was refused against.
    /// </summary>
    /// <remarks>
    /// <b>The gather is already paid for, and throwing it away was the expensive
    /// part.</b> Everything a person needs to answer this question — every
    /// release confirmed for the component, with its whole track list — is in
    /// memory at this moment and about to be discarded. Recovering it later cost
    /// a browse per recording and a lookup per release offered: measured against
    /// a live mirror, up to two minutes and 38 turns at the rate limit, on a
    /// click, for something the pass had already computed. This is the same
    /// mistake <c>MediaFile.AcoustIdMatchesJson</c> exists because of, one pass
    /// along; see <see cref="ReleaseCandidateSet"/>.
    ///
    /// Scored against the <i>refused</i> files rather than the whole component,
    /// because that is the question: a component may file most of itself
    /// confidently and leave three files over, and coverage figures computed
    /// over the files that already have an album would describe a set nobody is
    /// being asked about.
    ///
    /// It shares the component's <c>SaveChanges</c> — the document and the
    /// outcomes it explains are one fact, and a crash between them should lose
    /// both or neither. Nothing here can fail the pass: a component whose
    /// document cannot be built is simply a component the endpoint will gather
    /// for, which is what it did for all of them before this existed.
    /// </remarks>
    private async Task KeepCandidatesAsync(
        FonotecaDbContext db,
        DateTimeOffset componentUtc,
        Component component,
        List<MediaFileId> refused,
        CancellationToken cancellationToken)
    {
        if (refused.Count == 0) return;

        var open = new HashSet<MediaFileId>(refused);

        var members = component.Files
            .Where(file => open.Contains(file.Id))
            .OrderBy(file => file.Path, StringComparer.Ordinal)
            .Select(file => file.ToMember())
            .ToList();

        if (members.Count == 0) return;

        var recordings = members.Select(member => member.Recording).Distinct().ToList();

        var document = ComponentCandidates.Build(
            componentUtc.UtcTicks.ToString(CultureInfo.InvariantCulture),
            componentUtc,
            members,
            component.Candidates,
            component.Formats,
            recordings.Count,
            recordings.Count(component.Browsed.Contains),
            component.Candidates.Count,
            options.Value.ReleaseMinimumCoverage,
            options.Value.ReleaseMaximumDriftMs);

        var existing = await db.ReleaseCandidateSets
            .FirstOrDefaultAsync(set => set.ComponentUtc == componentUtc, cancellationToken)
            .ConfigureAwait(false);

        var json = JsonSerializer.Serialize(
            document, MatchingJson.Default.ComponentCandidatesResponse);

        if (existing is null)
        {
            db.ReleaseCandidateSets.Add(new ReleaseCandidateSet
            {
                ComponentUtc = componentUtc,
                DocumentJson = json,
                Files = document.Files,
                GatheredUtc = componentUtc,
            });
        }
        else
        {
            existing.DocumentJson = json;
            existing.Files = document.Files;
            existing.GatheredUtc = componentUtc;
        }
    }

    /// <summary>
    /// Stamps a file whose component could not be worked out, so the pass moves on.
    /// </summary>
    private async Task RefuseAsync(PendingFile seed, CancellationToken cancellationToken)
    {
        var scope = scopeFactory.CreateAsyncScope();

        await using (scope.ConfigureAwait(false))
        {
            var db = scope.ServiceProvider.GetRequiredService<FonotecaDbContext>();

            var row = await db.MediaFiles
                .FirstOrDefaultAsync(f => f.Id == seed.Id, CancellationToken.None)
                .ConfigureAwait(false);

            if (row is null) return;

            row.ReleaseLookupUtc = clock.UtcNow;
            row.AttributionOutcome = ReleaseAttributionOutcome.LookupFailed;

            await db.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Expands outwards from a seed until the component closes or a cap stops it.
    /// </summary>
    /// <remarks>
    /// The frontier is recordings, not files: a recording is what a browse takes,
    /// and every file holding it shares its candidates exactly. Each round browses
    /// the new recordings, prunes, fetches the surviving track lists, and reads
    /// back from the database which of those tracks the library actually holds.
    ///
    /// Files already attributed by an earlier component are never pulled in.
    /// Without that the last component of a large artist would re-decide the first
    /// one, and a run would not converge.
    /// </remarks>
    private async Task<Component> GatherAsync(
        PendingFile seed,
        Memo memo,
        CancellationToken cancellationToken)
    {
        var files = new Dictionary<MediaFileId, PendingFile> { [seed.Id] = seed };
        var confirmed = new Dictionary<Mbid, MusicBrainzRelease>();
        var formats = new Dictionary<Mbid, string?>();

        // Which of *our* recordings each candidate release is known to contain.
        // This is what makes the prune exact, and it is why browsing happens a
        // round at a time rather than one recording at a time: a single browse
        // says only that one recording is on a release, which is never enough to
        // rule the release out. The counts here only ever grow, so a release
        // pruned in one round can be admitted in the next.
        var hits = new Dictionary<Mbid, HashSet<Mbid>>();
        var summaries = new Dictionary<Mbid, MusicBrainzReleaseCandidate>();
        var placed = new HashSet<Mbid>();

        var browsed = new HashSet<Mbid>();
        var frontier = new List<Mbid> { seed.Recording };
        var capped = false;

        while (frontier.Count > 0 && !capped)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var round = frontier;
            frontier = [];

            foreach (var recording in round)
            {
                if (!browsed.Add(recording)) continue;

                var candidates = await BrowseAsync(recording, memo, cancellationToken).ConfigureAwait(false);

                if (candidates.Count > 0) placed.Add(recording);

                foreach (var candidate in candidates)
                {
                    summaries[candidate.Id] = candidate;

                    if (!hits.TryGetValue(candidate.Id, out var holding))
                    {
                        holding = [];
                        hits[candidate.Id] = holding;
                    }

                    holding.Add(recording);
                }
            }

            // Best-supported first, so that if a cap does stop the round, what
            // was fetched is the part most likely to matter.
            foreach (var (id, holding) in hits.OrderByDescending(entry => entry.Value.Count))
            {
                if (confirmed.ContainsKey(id)) continue;

                // Not applied on the opening round, where every release has
                // exactly one hit and a twelve-track album would score 1/12.
                if (browsed.Count > 1 && !WorthFetching(summaries[id], holding.Count)) continue;

                var release = await LookupAsync(id, memo, cancellationToken).ConfigureAwait(false);
                if (release is null) continue;

                confirmed[release.Id] = release;
                formats[release.Id] = Formats(summaries[id]);

                // Sorted by hits descending, so once the one-hit tail is reached
                // everything after it is tail too, and breaking here loses
                // nothing the library plausibly owns.
                if (confirmed.Count >= MaximumComponentCandidates
                    || (confirmed.Count >= SoftCandidateCap && holding.Count < 2))
                {
                    capped = true;
                    break;
                }

                foreach (var joined in await AdmitAsync(release, files, cancellationToken).ConfigureAwait(false))
                {
                    if (files.Count >= MaximumComponentFiles)
                    {
                        capped = true;
                        break;
                    }

                    files[joined.Id] = joined;

                    if (!browsed.Contains(joined.Recording)) frontier.Add(joined.Recording);
                }

                if (capped) break;
            }
        }

        if (capped) Log.AttributionComponentCapped(logger, seed.Path, files.Count, confirmed.Count);

        return new Component([.. files.Values], [.. confirmed.Values], formats, browsed, placed);
    }

    /// <summary>
    /// Is this release worth a request, given what the component knows so far?
    /// </summary>
    /// <remarks>
    /// <b>Not a bound on the final coverage, and treating it as one was a bug
    /// that reached live data twice.</b> The tempting formulation —
    /// <c>hits / trackCount</c> must already clear the floor — reads like an
    /// exact bound and is not one: <c>hits</c> counts only the recordings a
    /// browse has <i>so far</i> placed on the release, and the whole point of
    /// the expansion is that more are still arriving. A 25-track live album
    /// sharing five songs with a compilation scores 5/25, falls under a floor of
    /// 0.25, and is discarded — so the compilation keeps those five files and the
    /// other twenty sit unattributed. That is exactly what happened to
    /// <i>Muddy Wolf at Red Rocks</i>.
    ///
    /// So the test is deliberately generous, and the two clauses cover different
    /// shapes:
    ///
    /// <list type="bullet">
    /// <item><b>Two or more shared recordings</b> means a release this library
    /// plausibly owns a chunk of. Fetching its track list is what lets the rest
    /// of that chunk be found — the five hits become twenty-five once its own
    /// files are admitted and browsed.</item>
    /// <item><b>One shared recording</b> is only worth fetching when the release
    /// is small enough for one track to matter: a two-track single at 1/2 clears
    /// any floor, a 150-track anthology at 1/150 never will.</item>
    /// </list>
    ///
    /// What is discarded is therefore only the long anthology contributing a
    /// single song, which is the case the prune was for. Nothing is lost even
    /// then: files it would have claimed stay on the worklist and get a component
    /// of their own, seeded from a file whose album is in the opening round.
    /// </remarks>
    private bool WorthFetching(MusicBrainzReleaseCandidate candidate, int held)
    {
        if (held >= 2) return true;

        var total = candidate.TrackCount;
        return total <= 0 || (double)held / total >= options.Value.ReleaseMinimumCoverage;
    }

    /// <summary>"CD", "Digital Media", "CD+DVD-Video" — the honest reason a rip is partial.</summary>
    internal static string? Formats(MusicBrainzReleaseCandidate candidate)
    {
        var named = candidate.Media
            .Select(medium => medium.Format)
            .OfType<string>()
            .Distinct(StringComparer.Ordinal)
            .ToList();

        return named.Count == 0 ? null : string.Join("+", named);
    }

    /// <summary>
    /// Which of this release's tracks the library holds and has not yet filed.
    /// </summary>
    private async Task<List<PendingFile>> AdmitAsync(
        MusicBrainzRelease release,
        Dictionary<MediaFileId, PendingFile> known,
        CancellationToken cancellationToken)
    {
        var recordings = release.Tracks
            .Select(track => track.RecordingId)
            .OfType<Mbid>()
            .Distinct()
            .ToList();

        if (recordings.Count == 0) return [];

        var scope = scopeFactory.CreateAsyncScope();

        await using (scope.ConfigureAwait(false))
        {
            var db = scope.ServiceProvider.GetRequiredService<FonotecaDbContext>();

            var rows = await db.MediaFiles
                .AsNoTracking()
                .Where(f => f.ReleaseLookupUtc == null
                    && f.ReleaseDecidedUtc == null
                    && f.Recording!.Mbid != null
                    && recordings.Contains(f.Recording.Mbid.Value))
                .Select(f => new PendingFile(
                    f.Id,
                    f.Path,
                    f.Recording!.Title,
                    f.Recording!.Mbid!.Value,
                    f.FingerprintDuration ?? f.Quality!.Duration,
                    f.SizeBytes))
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            return [.. rows.Where(row => !known.ContainsKey(row.Id))];
        }
    }

    private async Task<IReadOnlyList<MusicBrainzReleaseCandidate>> BrowseAsync(
        Mbid recording,
        Memo memo,
        CancellationToken cancellationToken)
    {
        if (memo.Browses.TryGetValue(recording, out var cached)) return cached;

        var candidates = await musicBrainz
            .BrowseReleasesForRecordingAsync(recording, cancellationToken)
            .ConfigureAwait(false);

        memo.Browses[recording] = candidates;
        return candidates;
    }

    private async Task<MusicBrainzRelease?> LookupAsync(
        Mbid release,
        Memo memo,
        CancellationToken cancellationToken)
    {
        if (memo.Releases.TryGetValue(release, out var cached)) return cached;

        var fetched = await musicBrainz.GetReleaseAsync(release, cancellationToken).ConfigureAwait(false);

        memo.Releases[release] = fetched;
        return fetched;
    }

    private async Task Report(string jobId, int processed, int total, string? current, string state)
    {
        var message = new JobProgressMessage(jobId, JobKind, state, processed, total, current, clock.UtcNow);
        await hub.Clients.All.JobProgress(message).ConfigureAwait(false);
    }

    /// <summary>Everything fetched once and reused for the rest of the run.</summary>
    private sealed class Memo
    {
        public Dictionary<Mbid, IReadOnlyList<MusicBrainzReleaseCandidate>> Browses { get; } = [];

        public Dictionary<Mbid, MusicBrainzRelease?> Releases { get; } = [];
    }

    /// <summary>A set of files decided together, with the releases they were decided against.</summary>
    private sealed record Component(
        IReadOnlyList<PendingFile> Files,
        IReadOnlyList<MusicBrainzRelease> Candidates,
        IReadOnlyDictionary<Mbid, string?> Formats,

        /// <summary>
        /// Recordings a browse was actually sent for.
        /// </summary>
        /// <remarks>
        /// Every recording in a component that closed, and fewer when a cap
        /// stopped the expansion. Carried so the stored candidate document can
        /// say which — a list cut because half the set was never asked about
        /// looks exactly like a complete one otherwise.
        /// </remarks>
        IReadOnlySet<Mbid> Browsed,

        /// <summary>
        /// Recordings a browse returned at least one release for, whether or not
        /// that release survived the prune.
        /// </summary>
        /// <remarks>
        /// Kept because the prune destroys the difference between the two refusal
        /// outcomes. A recording on nothing but a 150-track anthology has its
        /// only candidate discarded before the lookup, so the rule sees an empty
        /// candidate set and reports <c>NoCandidate</c> — "MusicBrainz has this on
        /// no release", which is false and points at the wrong fix. This restores
        /// the honest answer: releases existed, none was worth believing.
        /// </remarks>
        IReadOnlySet<Mbid> Placed)
    {
        /// <summary>A title for a release group nothing was filed under directly.</summary>
        public string GroupTitle(Mbid group) =>
            Candidates.FirstOrDefault(release => release.ReleaseGroupId == group)?.ReleaseGroupTitle
            ?? Candidates.FirstOrDefault(release => release.ReleaseGroupId == group)?.Title
            ?? "Unknown album";
    }

    /// <summary>
    /// One file waiting on an album.
    /// </summary>
    /// <remarks>
    /// <see cref="Title"/> and <see cref="SizeBytes"/> are along for the ride
    /// rather than for the decision: the rule reads the recording and the
    /// duration, and these two are what the candidate document this pass now
    /// stores has to print. Selected here because they are on the row already —
    /// fetching them later would be a second query per refused component.
    ///
    /// <see cref="Duration"/> is <c>FingerprintDuration ?? Quality.Duration</c>,
    /// which is also what the endpoint scores with. A third of this worklist has
    /// no fingerprint duration at all.
    /// </remarks>
    private sealed record PendingFile(
        MediaFileId Id,
        string Path,
        string? Title,
        Mbid Recording,
        TimeSpan? Duration,
        long SizeBytes)
    {
        public AttributionFile ToAttributionFile() => new(Id, Recording, Duration);

        public ComponentMember ToMember() => new(Id, Path, Title, Recording, Duration, SizeBytes);
    }

    private sealed class Tally
    {
        public int Examined { get; set; }
        public int Attributed { get; set; }
        public int Ambiguous { get; set; }
        public int GroupOnly { get; set; }
        public int NoConfidentFit { get; set; }
        public int NoCandidate { get; set; }
        public int Failed { get; set; }
        public int Components { get; set; }
        public int Releases { get; set; }

        public void Record(ReleaseAttributionOutcome outcome)
        {
            switch (outcome)
            {
                case ReleaseAttributionOutcome.Attributed: Attributed++; break;
                case ReleaseAttributionOutcome.AttributedAmbiguously: Ambiguous++; break;
                case ReleaseAttributionOutcome.GroupOnly: GroupOnly++; break;
                case ReleaseAttributionOutcome.NoCandidate: NoCandidate++; break;
                default: NoConfidentFit++; break;
            }
        }
    }

    /// <summary>
    /// Writes a chosen release, its group and its whole track list.
    /// </summary>
    /// <remarks>
    /// The whole track list, not only the tracks the library holds — which is
    /// what makes "you are missing track 7" answerable, and what
    /// <c>Release.TrackCount</c> was declared for in the first migration.
    ///
    /// Upserts on MBID against the unique filtered indexes, so a rerun converges.
    /// Reads through the scope's context so rows added earlier in the same
    /// <c>SaveChanges</c> are found rather than duplicated.
    /// </remarks>
    internal sealed class ReleaseWriter(FonotecaDbContext db)
    {
        private readonly Dictionary<(ReleaseId, int, int), TrackId> _tracks = [];

        /// <summary>
        /// Recordings minted in this unit of work, by MBID.
        /// </summary>
        /// <remarks>
        /// EF queries the database, not the change tracker, so a recording added
        /// for one release's track list is invisible to the next release that
        /// names it — and two releases in one component sharing a track the
        /// library does not own is completely ordinary. Without this the second
        /// adds a duplicate and the whole component fails on
        /// <c>IX_Recordings_Mbid</c>.
        /// </remarks>
        private readonly Dictionary<Mbid, Recording> _minted = [];

        /// <summary>
        /// Release groups touched in this unit of work, by MBID.
        /// </summary>
        /// <remarks>
        /// The same trap as <see cref="_minted"/>, and it fires far more often:
        /// two pressings of one album share a release group by definition, and a
        /// component that files anything under both adds the group twice and
        /// dies on <c>IX_ReleaseGroups_Mbid</c> — taking every file in the
        /// component with it. 51 components in one live run.
        /// </remarks>
        private readonly Dictionary<Mbid, ReleaseGroup> _groups = [];

        public async Task<(ReleaseId Release, ReleaseGroupId? Group)> UpsertAsync(
            MusicBrainzRelease source,
            string? mediumFormats,
            CancellationToken cancellationToken)
        {
            ReleaseGroupId? groupId = source.ReleaseGroupId is { } group
                ? await UpsertGroupAsync(
                        group,
                        source.ReleaseGroupTitle ?? source.Title,
                        cancellationToken,
                        source.PrimaryType,
                        source.SecondaryTypes)
                    .ConfigureAwait(false)
                : null;

            var release = await db.Releases
                .FirstOrDefaultAsync(r => r.Mbid == source.Id, cancellationToken)
                .ConfigureAwait(false);

            if (release is null)
            {
                release = new Release { Id = ReleaseId.New(), Title = source.Title, Mbid = source.Id };
                db.Releases.Add(release);
            }
            else
            {
                release.Title = source.Title;
            }

            release.ReleaseGroupId = groupId;
            release.Released = source.ReleasedOn;
            release.Country = source.Country;
            release.Status = source.Status;
            release.Barcode = source.Barcode;
            // The first label, where there is one. A release pressed under two
            // labels has two catalogue numbers, and the schema holds one; taking
            // the first is a choice rather than a merge.
            var label = source.Labels is { Count: > 0 } labels ? labels[0] : null;

            release.Label = label?.Name;
            release.CatalogNumber = label?.CatalogNumber;
            release.TrackCount = source.Tracks.Count;
            release.DiscCount = source.Tracks.Select(track => track.DiscNumber).Distinct().Count();
            // Coalesced rather than assigned. The pass knows the disc formats
            // because a browse told it; a person's decision comes from a release
            // lookup, which does not carry them, and blanking "CD" on the way
            // past would lose a fact nothing else can restore.
            release.MediumFormats = mediumFormats ?? release.MediumFormats;

            await ApplyTracksAsync(release, source, cancellationToken).ConfigureAwait(false);
            await ApplyCreditsAsync(release, source, cancellationToken).ConfigureAwait(false);

            return (release.Id, groupId);
        }

        public async Task<ReleaseGroupId> UpsertGroupAsync(
            Mbid mbid,
            string title,
            CancellationToken cancellationToken,
            string? primaryType = null,
            IReadOnlyList<string>? secondaryTypes = null)
        {
            if (!_groups.TryGetValue(mbid, out var group))
            {
                group = await db.ReleaseGroups
                    .FirstOrDefaultAsync(g => g.Mbid == mbid, cancellationToken)
                    .ConfigureAwait(false);

                if (group is null)
                {
                    group = new ReleaseGroup { Id = ReleaseGroupId.New(), Title = title, Mbid = mbid };
                    db.ReleaseGroups.Add(group);
                }

                _groups[mbid] = group;
            }

            group.Title = title;

            // Coalesced rather than assigned: a group first written by a
            // GroupOnly file knows only its title, and the release that later
            // names it properly must be able to fill the rest in.
            group.PrimaryType ??= primaryType;

            if (secondaryTypes is { Count: > 0 })
            {
                group.SecondaryTypes = string.Join(", ", secondaryTypes);
            }

            return group.Id;
        }

        /// <summary>
        /// The track at a slot, for linking a file to its exact position.
        /// </summary>
        /// <remarks>
        /// From the map this writer filled, never from a query. The track list is
        /// removed and rewritten wholesale, and none of the new rows exists in
        /// the database until <c>SaveChanges</c> — so a read here would miss
        /// every one of them and return the rows just deleted instead.
        /// </remarks>
        public TrackId? TrackIdAt(ReleaseId release, int disc, int position) =>
            _tracks.TryGetValue((release, disc, position), out var known) ? known : null;

        /// <summary>
        /// The name on the front of the album.
        /// </summary>
        /// <remarks>
        /// The release's own billing line, which is not the same thing as the
        /// billing on any of its tracks — a compilation's tracks each name their
        /// own performer while the release is credited to "Various Artists", and
        /// a release page that derived its heading from the first track would
        /// call that album by whoever happened to be first on it.
        ///
        /// Only artists the catalogue already knows are linked. Enrichment writes
        /// artists, this pass does not, and minting one here from a release credit
        /// would create a row with a name and nothing else — no sort name, no
        /// type — that the artist list would then show as a real artist.
        /// </remarks>
        private async Task ApplyCreditsAsync(
            Release release,
            MusicBrainzRelease source,
            CancellationToken cancellationToken)
        {
            var stale = await db.ArtistCredits
                .Where(c => c.ReleaseId == release.Id)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            db.ArtistCredits.RemoveRange(stale);

            var position = 0;

            foreach (var credit in source.Credits)
            {
                if (credit.ArtistId is not { } mbid) continue;

                var artist = await db.Artists
                    .FirstOrDefaultAsync(a => a.Mbid == mbid, cancellationToken)
                    .ConfigureAwait(false);

                if (artist is null) continue;

                db.ArtistCredits.Add(new ArtistCredit
                {
                    Id = Guid.CreateVersion7(),
                    ArtistId = artist.Id,
                    ReleaseId = release.Id,
                    Position = position++,
                    JoinPhrase = credit.JoinPhrase,
                    CreditedAs = credit.Name,
                });
            }
        }

        /// <summary>
        /// Replaces the release's track list rather than merging into it.
        /// </summary>
        /// <remarks>
        /// The same discipline as the enrichment pass's credits: a second run
        /// over unchanged data produces an identical list, and a run after a
        /// MusicBrainz correction produces the corrected one, where merging would
        /// accumulate every track the release ever had. The unique index on
        /// (release, disc, position) would eventually catch that; this stops it
        /// happening.
        ///
        /// <b>Replaced slot by slot, not row by row, and that distinction is a
        /// bug that reached live data.</b> Deleting the whole list and minting
        /// new ids looks equivalent and is not: <c>MediaFiles.TrackId</c> is
        /// <c>ON DELETE SET NULL</c>, so every file already filed under this
        /// release silently loses its position the moment anything writes the
        /// release again. Two components picking the same release is ordinary in
        /// a pass, and a person answering an album question a second time reaches
        /// it in one click — on the endpoint whose whole point is that the track
        /// list is written. So a track at a slot the release still has is
        /// <i>updated</i>, keeping its id, and only the surplus is deleted.
        /// </remarks>
        private async Task ApplyTracksAsync(
            Release release,
            MusicBrainzRelease source,
            CancellationToken cancellationToken)
        {
            var existing = await db.Tracks
                .Where(t => t.ReleaseId == release.Id)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            // Keyed on the unique index's own columns. A duplicated slot in the
            // database cannot survive that index, so the first wins and the rest
            // fall through to the surplus below.
            var bySlot = new Dictionary<(int Disc, int Position), Track>();

            foreach (var row in existing)
            {
                bySlot.TryAdd((row.DiscNumber, row.Position), row);
            }

            var kept = new HashSet<TrackId>();

            foreach (var track in source.Tracks)
            {
                if (track.RecordingId is not { } mbid) continue;

                if (!_minted.TryGetValue(mbid, out var recording))
                {
                    recording = await db.Recordings
                        .FirstOrDefaultAsync(r => r.Mbid == mbid, cancellationToken)
                        .ConfigureAwait(false);
                }

                // Recordings this library has never identified are real tracks on
                // a real release, so they are written: that is what makes a
                // release page able to show what is missing rather than only what
                // is present.
                if (recording is null)
                {
                    recording = new Recording
                    {
                        Id = RecordingId.New(),
                        Title = track.Title,
                        Mbid = mbid,
                        Duration = track.Length,
                    };

                    db.Recordings.Add(recording);
                }

                _minted[mbid] = recording;

                bySlot.TryGetValue((track.DiscNumber, track.Position), out var row);

                // A slot MusicBrainz has re-pointed at a different recording is
                // not the same track any more, and `Track.RecordingId` is
                // init-only precisely because that pair is the row's identity. So
                // it is replaced rather than updated, and the file filed there
                // loses its position — which is the honest answer: the slot now
                // holds different music.
                if (row is not null && row.RecordingId != recording.Id) row = null;

                if (row is null)
                {
                    row = new Track
                    {
                        Id = TrackId.New(),
                        ReleaseId = release.Id,
                        RecordingId = recording.Id,
                        Position = track.Position,
                        DiscNumber = track.DiscNumber,
                    };

                    db.Tracks.Add(row);
                    bySlot[(track.DiscNumber, track.Position)] = row;
                }

                row.Number = track.Number;
                row.Title = track.Title;
                row.Length = track.Length;

                kept.Add(row.Id);
                _tracks[(release.Id, row.DiscNumber, row.Position)] = row.Id;
            }

            // What the release no longer prints. Removing these still nulls the
            // TrackId of anything filed there, which is correct: the slot is gone.
            db.Tracks.RemoveRange(existing.Where(row => !kept.Contains(row.Id)));
        }
    }
}

/// <summary>Whether a pass was started, and its job id when it was.</summary>
public sealed record AttributionStartOutcome(AttributionStatus Status, string? JobId);

public enum AttributionStatus
{
    Started = 0,
    AlreadyRunning = 1,
}

/// <summary>Where a running pass has got to.</summary>
public sealed record AttributionProgress(string JobId, int Processed, int Total, string? Current);

/// <summary>
/// What a finished pass did.
/// </summary>
/// <remarks>
/// Every count is an <c>int</c> rather than a <c>long</c>, because
/// <c>NumberHandling.Strict</c> types a <c>long</c> as <c>string | number</c> in
/// the generated client and arithmetic on one fails to compile.
///
/// The four refusal counts are separate on purpose. A large
/// <see cref="NoConfidentFit"/> is the strict gate working — compilations of
/// licensed catalogue land there — while a large <see cref="NoCandidate"/> means
/// MusicBrainz has recordings on no release at all, and a large
/// <see cref="Failed"/> means the mirror is unwell. Summed into one "unresolved"
/// they would be indistinguishable, and only one of the three is worth acting on.
/// </remarks>
public sealed record AttributionSummary(
    string JobId,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset CompletedAtUtc,
    long DurationMilliseconds,
    int Examined,
    int Attributed,
    int Ambiguous,
    int GroupOnly,
    int NoConfidentFit,
    int NoCandidate,
    int Failed,
    int Components,
    int Releases,
    bool Cancelled);
