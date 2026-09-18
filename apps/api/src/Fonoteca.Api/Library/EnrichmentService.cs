using System.Diagnostics;
using Fonoteca.Api.Configuration;
using Fonoteca.Api.Logging;
using Fonoteca.Api.Matching;
using Fonoteca.Api.Realtime;
using Fonoteca.Data;
using Fonoteca.Domain.Abstractions;
using Fonoteca.Domain.Catalogue;
using Fonoteca.Domain.Identification;
using Fonoteca.Providers.AudioDb;
using Fonoteca.Providers.Qobuz;
using Fonoteca.Providers.Wikidata;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

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
    [FromKeyedServices(ArtistPortraitSources.Wikidata)] IArtistPortraits portraits,
    [FromKeyedServices(ArtistPortraitSources.Qobuz)] IArtistPortraits pressPhotos,
    [FromKeyedServices(ArtistPortraitSources.AudioDb)] IArtistPortraits audioDbPhotos,
    [FromKeyedServices(ReleaseDiscoverySources.Qobuz)] IReleaseDiscovery qobuzReleases,
    IOptions<WikidataOptions> portraitOptions,
    IOptions<FonotecaOptions> options,
    IHubContext<JobsHub, IJobsClient> hub,
    IHostApplicationLifetime lifetime,
    IClock clock,
    ILogger<EnrichmentService> logger) : IHostedService, IDisposable
{
    /// <summary>The kind this pass takes the gate as, and the job kind on the wire.</summary>
    public const string JobKind = "library.enrich";

    /// <summary>Rows claimed per query. Bounded so a cancelled pass stops promptly.</summary>
    private const int PageSize = 500;

    /// <summary>
    /// The widest <c>Artists.Genres</c> the column can hold.
    /// </summary>
    /// <remarks>
    /// <b>A clamp on the string, because the column's limit is on the string.</b>
    /// This was a cap on the <i>count</i> first, which is not the same bound:
    /// twelve genres is about 560 characters in practice and would not have
    /// tripped, but "in practice" is not what a <c>varchar(1000)</c> checks. A
    /// write that overruns throws out of <c>SaveChangesAsync</c>, the backstop
    /// rolls back the stamp with it, and the row is then re-asked about on every
    /// run forever, spending a turn at the rate limit each time to fail
    /// identically — which is precisely the failure the bound exists to prevent.
    /// </remarks>
    private const int MaxGenresLength = 1000;

    /// <summary>
    /// Genres kept per artist, most-voted first.
    /// </summary>
    /// <remarks>
    /// Four times what any screen prints, and the mapper has already ordered
    /// them by votes — so the ones dropped are the ones somebody would argue
    /// with. Unlike <see cref="MaxGenresLength"/> this one is a preference, and
    /// it is what keeps the clamp below from ever having anything to do.
    /// </remarks>
    private const int MaxGenres = 12;

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

    /// <summary>
    /// What is left to ask about, split by what kind of question it is.
    /// </summary>
    /// <remarks>
    /// <b>Two numbers rather than one, and that is the artist stage leaking into
    /// an existing screen.</b> It used to be "identified files nobody has asked
    /// about yet", and `EnrichmentPanel` prints it in those words — so the
    /// moment artists joined the total, a library whose files are all enriched
    /// rendered "2,838 identified files with no recording yet", which is false
    /// in both nouns. Summing them here and letting the caller word it would
    /// only move the lie.
    ///
    /// <see cref="EnrichmentPending.Total"/> is what the button's "anything to
    /// do?" test reads, and is the sum, so the enable rule did not change.
    /// </remarks>
    public async Task<EnrichmentPending> CountPendingAsync(
        CancellationToken cancellationToken = default)
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

            var artists = await db.Artists
                .Where(UnaskedArtist)
                .CountAsync(cancellationToken)
                .ConfigureAwait(false);

            var portraits = await db.Artists
                .Where(UnpicturedArtist)
                .CountAsync(cancellationToken)
                .ConfigureAwait(false);

            var discographies = await db.Artists
                .Where(UnbrowsedArtist(DiscographyCutoff))
                .CountAsync(cancellationToken)
                .ConfigureAwait(false);

            return new EnrichmentPending(unasked + personFiled, artists, portraits, discographies);
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

        var pending = (await CountPendingAsync(cancellationToken).ConfigureAwait(false)).Total;

        Log.EnrichmentStarted(logger, jobId, pending);

        _progress = new EnrichmentProgress(jobId, 0, pending, null);

        var lastReport = clock.UtcNow;

        // One loop, driven three times. The two file worklists differ only in how
        // a file reaches a recording, and the artist one shares the shape rather
        // than the subject: claim, ask, commit, report, and no single item may
        // end the pass.
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

            // Third, and last because it depends on the first two: the artists
            // those files just credited are in the catalogue by now, so one
            // stage drains both them and whatever backlog was already there.
            await DrainAsync(
                ClaimArtistsAsync(cancellationToken),
                artist => DescribeAsync(artist, counts, cancellationToken),
                artist => artist.Name).ConfigureAwait(false);

            // Fourth, and not through DrainAsync, because its unit is a set —
            // see PicturesAsync. Last because it is the cheapest thing here by
            // two orders of magnitude: the whole library is a dozen requests, so
            // a run cancelled before it has lost seconds rather than hours.
            await PicturesAsync(counts, jobId, pending, cancellationToken).ConfigureAwait(false);

            // Fifth, and last — and the reason changed out from under this line
            // when the worklist widened past the followed set. It used to be
            // last because it was the cheapest thing here: a handful of followed
            // rows against stages counting thousands, so a cancelled run lost
            // seconds of it. It is now the catalogue, one gated browse plus the
            // shops per artist, which makes it the *most* expensive stage on the
            // list rather than the least.
            //
            // It stays last anyway, for a reason that survives the change: every
            // stage above writes facts other screens already depend on, and this
            // one only adds records nobody owns. A run cancelled part way should
            // lose the shopping list rather than the catalogue. What a cancelled
            // run now loses is real, though, so `ClaimDiscographiesAsync` drains
            // followed artists first — see the ordering there.
            //
            // The artists it browses for were all in the catalogue before the
            // run started: following somebody mints their row at the moment of
            // the click, not here.
            await DrainAsync(
                ClaimDiscographiesAsync(cancellationToken),
                artist => FetchDiscographyAsync(artist, counts, cancellationToken),
                artist => artist.Name).ConfigureAwait(false);
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
            ArtistsDescribed: counts.ArtistsDescribed,
            ArtistsPictured: counts.ArtistsPictured,
            Cancelled: cancellationToken.IsCancellationRequested);

        Log.EnrichmentCompleted(
            logger, jobId, counts.Linked, counts.NoRecording, counts.RecordingNotFound,
            counts.Failed, memo.Recordings.Count, memo.Artists.Count, counts.ArtistsDescribed,
            summary.DurationMilliseconds);

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
        file => (file.EnrichmentOutcome == EnrichmentOutcome.LinkedByPerson
                || file.EnrichmentOutcome == EnrichmentOutcome.LinkedByAgent)
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

    /// <summary>
    /// Artists nobody has asked MusicBrainz about — the third worklist.
    /// </summary>
    /// <remarks>
    /// <b>Every unasked artist, not only the ones this run credited.</b> The
    /// obvious wiring is to drain <c>Memo.Artists</c>, which is already sitting
    /// there holding exactly the artists <see cref="CatalogueWriter"/> touched —
    /// and it is wrong in the direction that is hard to notice: a library whose
    /// files are all enriched has an empty file worklist, so the pass would
    /// touch no artists, so it would describe none. The 2,838 artists already in
    /// the catalogue when this was written would have needed every file
    /// re-enriched to be reached. Keyed on the artist's own column instead, the
    /// backlog and the new arrivals are the same query.
    ///
    /// <c>Mbid != null</c> is a real filter and not defensive noise: nothing
    /// mints an artist without one today, but a name is not something
    /// MusicBrainz can be asked about, and a null here would be an infinite
    /// worklist rather than a failure.
    /// </remarks>
    private static readonly System.Linq.Expressions.Expression<Func<Artist, bool>> UnaskedArtist =
        artist => artist.LookupUtc == null && artist.Mbid != null;

    /// <summary>
    /// The third worklist, a page at a time.
    /// </summary>
    /// <remarks>
    /// Paged and self-draining like the two above it, and the <c>seen</c> guard
    /// covers the same case: an artist whose lookup failed transiently keeps a
    /// null stamp and would otherwise be claimed forever.
    /// </remarks>
    private async IAsyncEnumerable<PendingArtist> ClaimArtistsAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var seen = new HashSet<ArtistId>();

        while (!cancellationToken.IsCancellationRequested)
        {
            List<PendingArtist> page;

            var scope = scopeFactory.CreateAsyncScope();
            await using (scope.ConfigureAwait(false))
            {
                var db = scope.ServiceProvider.GetRequiredService<FonotecaDbContext>();

                page = await db.Artists
                    .AsNoTracking()
                    .Where(UnaskedArtist)
                    .OrderBy(a => a.Id)
                    .Select(a => new PendingArtist(a.Id, a.Name, a.Mbid!.Value))
                    .Take(PageSize)
                    .ToListAsync(cancellationToken)
                    .ConfigureAwait(false);
            }

            var fresh = page.Where(artist => seen.Add(artist.Id)).ToList();

            if (fresh.Count == 0) yield break;

            foreach (var artist in fresh) yield return artist;
        }
    }

    /// <summary>
    /// Artists nobody has asked for a discography — the fifth worklist.
    /// </summary>
    /// <remarks>
    /// <b>Every artist with an MBID, not only the followed ones.</b> This was the
    /// followed set, and that made a record reachable only for somebody already
    /// followed: <c>ArtistCredit.ReleaseGroupId</c> is written by this browse and
    /// by nothing else, so every other artist's page showed an empty discography
    /// — not a filtered one, an unfetched one — and the only way to find an album
    /// was to follow its artist first. Measured on this library when it changed,
    /// 27 artists of 3,004 had ever been browsed.
    ///
    /// <b>The clock-driven re-ask stays followed-only, and that asymmetry is the
    /// design rather than an oversight.</b> An artist nobody has browsed is asked
    /// about once; a followed one is asked again when
    /// <see cref="DiscographyCutoff"/> says their answer has gone stale. Widening
    /// the re-ask as well would multiply a weekly sweep by a hundred — see that
    /// property's own remarks on why it is affordable at all — and would buy no
    /// monitoring, because <c>ReleaseGroup.Monitored</c> means "released after
    /// you followed them" and is undefined for an artist nobody follows.
    ///
    /// <b>It does cost something, and monitoring is not the whole of it.</b> An
    /// unfollowed artist's discography is fetched once and then frozen: nothing
    /// in the application clears <c>DiscographyLookupUtc</c>, so a record they
    /// release afterwards is invisible on their page for good, and the complaint
    /// this widening answers — an album nobody can find — comes back for
    /// everything released after the one browse. Re-asking is the hand-written
    /// <c>UPDATE</c> this codebase already documents for <c>AcoustIdCheckedUtc</c>.
    /// Following the artist is the supported way to keep their page current, and
    /// that is a real limitation rather than a tidy division of labour.
    ///
    /// The stamp rather than "has any credited release group" for the reason
    /// every other stamp in this file exists: an artist MusicBrainz credits with
    /// nothing would otherwise be browsed for again on every run forever.
    /// </remarks>
    private static System.Linq.Expressions.Expression<Func<Artist, bool>> UnbrowsedArtist(
        DateTimeOffset cutoff) =>
        artist => artist.Mbid != null
            && (artist.DiscographyLookupUtc == null
                || (artist.Followed && artist.DiscographyLookupUtc < cutoff));

    /// <summary>
    /// How stale a discography may be before this pass asks again.
    /// </summary>
    /// <remarks>
    /// <b>The clause that makes monitoring possible, and the only clock-driven
    /// re-ask in the application.</b> Keyed on the stamp being null alone — which
    /// is what every other worklist here does and what this one did — a followed
    /// artist is browsed once and never again, so "released after you followed
    /// them" has nothing to compare against and <c>ReleaseGroup.Monitored</c>
    /// could never become true for anybody.
    ///
    /// It is affordable only because of what it re-asks about: the followed set,
    /// a few dozen artists at one gated browse each. The same idea applied to any
    /// file-keyed worklist would re-ask about a hundred thousand rows, which is
    /// exactly why <c>AcoustIdCheckedUtc</c> and friends stay keyed on null
    /// forever and are re-asked by a hand-written <c>UPDATE</c>.
    ///
    /// Clamped at both ends rather than floored at one. Zero means "re-ask on
    /// every run", which is a legitimate thing to configure and costs one browse
    /// per followed artist per press; negative would put the cutoff in the
    /// future and re-ask identically, so it is floored rather than rejected.
    ///
    /// <b>The ceiling is the one that matters, because without it a setting
    /// takes the application down.</b> <see cref="TimeSpan.FromDays"/> throws
    /// <see cref="OverflowException"/> past about ten million days, and this
    /// property is read by <c>CountPendingAsync</c> — which the dashboard polls.
    /// So a fat-fingered <c>Fonoteca:DiscographyRecheckDays</c> would not
    /// misconfigure the re-ask, it would 500 the status endpoint on a timer,
    /// nowhere near the setting that caused it. Ten years is past any useful
    /// value and cannot overflow.
    ///
    /// The ceiling is a real narrowing and not only a crash guard: anything from
    /// ten years up to about ten million days used to work and now silently
    /// clamps. Both mean "never re-ask in practice", so nothing is lost, but it
    /// is a behaviour change rather than a pure fix.
    /// </remarks>
    private DateTimeOffset DiscographyCutoff =>
        clock.UtcNow
        - TimeSpan.FromDays(Math.Clamp(options.Value.DiscographyRecheckDays, 0, MaxRecheckDays));

    /// <summary>The widest re-ask interval that cannot overflow a <see cref="TimeSpan"/>.</summary>
    private const int MaxRecheckDays = 3_650;

    /// <summary>The fifth worklist, a page at a time.</summary>
    private async IAsyncEnumerable<PendingArtist> ClaimDiscographiesAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var seen = new HashSet<ArtistId>();

        // Read once, not per page: the cutoff must not slide forward while the
        // pass drains, or an artist browsed early in a long run falls back onto
        // the worklist before that run has finished.
        var cutoff = DiscographyCutoff;

        while (!cancellationToken.IsCancellationRequested)
        {
            List<PendingArtist> page;

            var scope = scopeFactory.CreateAsyncScope();
            await using (scope.ConfigureAwait(false))
            {
                var db = scope.ServiceProvider.GetRequiredService<FonotecaDbContext>();

                // Followed first, and that ordering earns its keep now the
                // worklist is the catalogue rather than a handful of rows. This
                // drain is thousands of gated browses and a person can cancel it
                // — so the artists somebody said they cared about are the ones
                // that must not be left to a run that does not finish. Within
                // each half the id order is the stable one the paging needs.
                page = await db.Artists
                    .AsNoTracking()
                    .Where(UnbrowsedArtist(cutoff))
                    .OrderByDescending(a => a.Followed)
                    .ThenBy(a => a.Id)
                    .Select(a => new PendingArtist(a.Id, a.Name, a.Mbid!.Value))
                    .Take(PageSize)
                    .ToListAsync(cancellationToken)
                    .ConfigureAwait(false);
            }

            var fresh = page.Where(artist => seen.Add(artist.Id)).ToList();

            if (fresh.Count == 0) yield break;

            foreach (var artist in fresh) yield return artist;
        }
    }

    /// <summary>
    /// Writes down what MusicBrainz says one artist released.
    /// </summary>
    /// <remarks>
    /// <b>Every release group is stored, and the rule that hides most of them
    /// runs when somebody opens the page.</b> A discography is mostly
    /// compilations and live bootlegs, and cutting here would make the stored
    /// answer a function of a rule — so changing the rule would mean re-browsing
    /// for every artist in the catalogue at a turn each, which since this
    /// worklist widened past the followed set is thousands rather than dozens.
    /// <c>Discography.IsGap</c> is applied in <c>CatalogueEndpoints.GetArtist</c>
    /// instead.
    ///
    /// <b>The groups are minted through the attribution pass's own writer.</b>
    /// Two pressings of an album share a release group by definition and two
    /// artists share one whenever they collaborated, so a second upsert here
    /// would duplicate rows and die on <c>IX_ReleaseGroups_Mbid</c> — the
    /// failure that writer's memo already exists to prevent, and which took out
    /// 51 components in one live run before it did.
    /// </remarks>
    private async Task FetchDiscographyAsync(
        PendingArtist artist,
        Tally counts,
        CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;

        IReadOnlyList<MusicBrainzReleaseGroup> released;

        try
        {
            released = await musicBrainz
                .BrowseReleaseGroupsForArtistAsync(artist.Mbid, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (ProviderUnavailableException cause)
        {
            // Transient. The stamp stays null, so the row stays on the worklist
            // and the next run retries it — the same bargain DescribeAsync takes.
            counts.Failed++;
            Log.DiscographyNotFetched(logger, artist.Name, cause.Message);
            return;
        }

        var scope = scopeFactory.CreateAsyncScope();
        await using (scope.ConfigureAwait(false))
        {
            var db = scope.ServiceProvider.GetRequiredService<FonotecaDbContext>();

            var row = await db.Artists
                .FirstOrDefaultAsync(a => a.Id == artist.Id, cancellationToken)
                .ConfigureAwait(false);

            // Unfollowed, or removed, since the page was claimed. Nothing to
            // record and nothing wrong.
            if (row is null) return;

            // Whether anybody has ever browsed this artist, read *before* the
            // stamp below overwrites it. This is what separates the two cases
            // that decide monitoring: a first browse is the baseline — the back
            // catalogue as it stood when somebody followed them, monitored by
            // nothing — and a later one can only be turning up records that did
            // not exist last time we looked, which is what "future releases"
            // means here. Read after the assignment it is always false and every
            // record ever written would be monitored.
            var baseline = row.DiscographyLookupUtc is null;

            // Stamped whether or not MusicBrainz credited them with anything.
            // "We asked and they have released nothing else" is an answer, and a
            // row left unstamped on it is re-asked about on every run forever.
            row.DiscographyLookupUtc = now;

            if (released.Count == 0)
            {
                Log.DiscographyNotFetched(logger, artist.Name, "no release groups");
            }

            var writer = new ReleaseAttributionService.ReleaseWriter(db);

            // The credits this artist already has on release groups, so a second
            // run over the same artist adds none. EF queries the database rather
            // than the change tracker, so this is read once up front and added to
            // as we go — the same trap the writer's own memos exist for.
            var already = (await db.ArtistCredits
                    .Where(credit => credit.ArtistId == artist.Id && credit.ReleaseGroupId != null)
                    .Select(credit => credit.ReleaseGroupId!.Value)
                    .ToListAsync(cancellationToken)
                    .ConfigureAwait(false))
                .ToHashSet();

            // Records this artist was not credited with last time, on a run that
            // is not the first. Collected rather than flagged in place because
            // the writer hands back an id and the entity it minted is not
            // necessarily loaded — see the monitoring loop below.
            var arrived = new List<ReleaseGroupId>();

            // What the catalogue already holds by them, as titles rather than
            // ids. `already` answers "have we written this credit"; this answers
            // "is this record already here", which is a different question and
            // the only one a shop's album can be put to — it arrives with no
            // MBID, so a title and a year are all there is to compare.
            //
            // <b>Seeded before the loop, on purpose.</b> The groups that loop
            // mints are Added and unsaved, and EF queries the database rather
            // than the change tracker — so the same query run afterwards would
            // miss exactly the rows just written and re-mint every one of them
            // through the provider path. It is appended to as the loop goes.
            var held = await db.ReleaseGroups
                .Where(group => already.Contains(group.Id))
                .Select(group => new HeldRecord(group.Title, group.FirstReleaseYear))
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            foreach (var group in released)
            {
                var groupId = await writer
                    .UpsertGroupAsync(
                        group.Id,
                        group.Title,
                        cancellationToken,
                        group.PrimaryType,
                        group.SecondaryTypes,
                        group.FirstReleaseYear)
                    .ConfigureAwait(false);

                held.Add(new HeldRecord(group.Title, group.FirstReleaseYear));

                if (!already.Add(groupId)) continue;

                if (!baseline) arrived.Add(groupId);

                // Position 0 and no join phrase: this is not a printed billing
                // line, it is "MusicBrainz credits this artist with this record".
                // Inventing a position would corrupt the ordering for anything
                // that ever reads a real credit line off a release group.
                db.ArtistCredits.Add(new ArtistCredit
                {
                    Id = Guid.CreateVersion7(),
                    ArtistId = artist.Id,
                    ReleaseGroupId = groupId,
                    Position = 0,
                });
            }

            await DiscoverAsync(db, artist, held, arrived, baseline, counts, cancellationToken)
                .ConfigureAwait(false);

            // A record that turned up after somebody followed this artist is
            // monitored; everything the first browse wrote is not. `FindAsync`
            // rather than a query, because the writer has either just Added
            // these or loaded them a moment ago — both are in the change
            // tracker, which `FindAsync` checks before it touches the database,
            // and an Added row no query would find yet is exactly the common
            // case here.
            foreach (var id in arrived)
            {
                var record = await db.ReleaseGroups
                    .FindAsync([id], cancellationToken)
                    .ConfigureAwait(false);

                if (record is not null) record.Monitored = true;
            }

            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>One record the catalogue already holds by an artist, as a shop could recognise it.</summary>
    /// <remarks>
    /// A title and a year, because a provider's album arrives with no MBID and
    /// those are the only comparable facts — see <see cref="ReleaseTitleMatch"/>
    /// for what that costs and why it errs towards showing a duplicate rather
    /// than hiding a gap.
    /// </remarks>
    private sealed record HeldRecord(string Title, int? Year);

    /// <summary>
    /// The fewest tracks a discovered record may have before it is worth offering.
    /// </summary>
    /// <remarks>
    /// <b>A shop's artist page is not a discography, and without this the
    /// feature makes the problem it exists to solve worse.</b> Measured against
    /// this installation, one artist's <c>artist/get?extra=albums</c> reports
    /// <b>166</b> records — singles, EPs, one-track promos and compilations
    /// alongside the albums. Minted with no <c>PrimaryType</c> (the shop never
    /// said "album" and inventing the word would be a claim it did not make),
    /// every one of them satisfies <c>Discography.IsGap</c>, which deliberately
    /// keeps untyped records. The next re-browse would then mark all 166
    /// monitored, and the shelf a person asked to be made shorter would grow by
    /// two orders of magnitude.
    ///
    /// Four is a knob and not a principle, in <c>ArtistNameMatch.CatalogueMargin</c>'s
    /// sense: it keeps EPs, which are records somebody made on purpose, and drops
    /// the one-to-three-track rows that are overwhelmingly singles and promos.
    /// An unknown count is <b>kept</b> rather than dropped — a silence is not a
    /// small number, the same reading <c>Dwarfs</c> gives an absent album count —
    /// which errs towards a visible extra row over a hidden gap, the direction
    /// <see cref="ReleaseTitleMatch"/> states.
    /// </remarks>
    private const int MinimumTracksToOffer = 4;

    /// <summary>
    /// What the shops say this artist released, for records the catalogue has no row for.
    /// </summary>
    /// <remarks>
    /// <b>A failure here must never cost the MusicBrainz answer.</b> The browse
    /// above has already succeeded and the artist is about to be stamped;
    /// letting a shop's failure escape would leave the stamp unwritten, discard a
    /// perfectly good discography and re-ask for it on every run. So every source
    /// is caught individually and the pass carries on — which is the lesson
    /// <c>BetterPictureAsync</c> already paid for in its own words, where a Qobuz
    /// outage meant no artist got a picture at all.
    ///
    /// <b><see cref="ProviderException"/>, not just the unavailable half, and the
    /// difference is not pedantry.</b> <c>QobuzClient.Diagnose</c> treats only
    /// 429, 408 and 5xx as transient; every other non-2xx — <b>401 included</b> —
    /// is a <c>ProviderRejectedException</c>. The user auth token comes from a
    /// signed-in web player session, so it expiring is routine rather than
    /// exotic. Caught narrowly, that exception escapes to <c>DrainAsync</c>,
    /// which rethrows rejections deliberately, and the whole pass aborts — having
    /// first discarded this artist's groups, credits and stamp with the unsaved
    /// scope.
    ///
    /// <b>That rethrow is right for MusicBrainz and wrong here</b>, which is why
    /// the catch belongs at this level rather than that one: a missing AcoustID
    /// key should stop a pass on the first file instead of the
    /// hundred-thousandth, because nothing downstream can work without it. A shop
    /// is supplementary by construction — the catalogue half has already
    /// succeeded — so its refusal is worth a line and nothing more.
    ///
    /// <b>Minted with a null <c>Mbid</c>, and that is a one-way door.</b>
    /// <c>ReleaseGroup</c> has no barcode column — a group spans every pressing
    /// and each pressing has its own UPC — so nothing can later recognise one of
    /// these as a MusicBrainz record. The alternative was inventing an
    /// identifier, which is worse. <c>DiscoveredRelease.Barcode</c> is carried
    /// against the day a <c>Release</c> is written from one of these, where ADR
    /// 0011's key does work.
    ///
    /// <b>Monitoring is decided by the caller's baseline rule, not here.</b> A
    /// discovered record joins <paramref name="arrived"/> on exactly the same
    /// terms as a MusicBrainz one, so "released after you followed them" means
    /// one thing across both sources rather than two.
    /// </remarks>
    private async Task DiscoverAsync(
        FonotecaDbContext db,
        PendingArtist artist,
        List<HeldRecord> held,
        List<ReleaseGroupId> arrived,
        bool baseline,
        Tally counts,
        CancellationToken cancellationToken)
    {
        foreach (var (name, source) in ReleaseSources)
        {
            DiscoveredReleases found;

            try
            {
                found = await source
                    .FindAsync(new ArtistToPicture(artist.Mbid, artist.Name), cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (ProviderException cause)
            {
                Log.ReleasesNotDiscovered(logger, name, artist.Name, cause.Message);
                continue;
            }

            foreach (var record in found.Releases)
            {
                if (string.IsNullOrWhiteSpace(record.Title)) continue;

                if (record.TrackCount is { } tracks && tracks < MinimumTracksToOffer) continue;

                // Against everything already known by this artist, including the
                // records minted moments ago in this same scope — which is why
                // the list is passed in and appended to rather than re-queried.
                if (held.Any(entry => ReleaseTitleMatch.IsSameRecord(
                        record.Title, record.Year, entry.Title, entry.Year)))
                {
                    continue;
                }

                var minted = new ReleaseGroup
                {
                    Id = ReleaseGroupId.New(),
                    Title = record.Title,
                    FirstReleaseYear = record.Year,

                    // Deliberately untyped. The shop said it sells this record;
                    // it did not say what kind of record it is, and "Album" here
                    // would be this application's word rather than anybody's
                    // fact. `Discography.IsGap` keeps untyped records for exactly
                    // this reason, and the tile prints "Untyped" so the screen
                    // says which it is.
                    PrimaryType = null,
                };

                db.ReleaseGroups.Add(minted);

                db.ArtistCredits.Add(new ArtistCredit
                {
                    Id = Guid.CreateVersion7(),
                    ArtistId = artist.Id,
                    ReleaseGroupId = minted.Id,
                    Position = 0,
                });

                held.Add(new HeldRecord(record.Title, record.Year));

                if (!baseline) arrived.Add(minted.Id);

                counts.RecordsDiscovered++;
            }
        }
    }

    /// <summary>
    /// Artists nobody has looked for a picture of — the fourth worklist.
    /// </summary>
    /// <remarks>
    /// Deliberately not <see cref="UnaskedArtist"/> and deliberately not
    /// <c>PortraitUrl == null</c>. The first would tie a picture to a
    /// description and the 2,902 artists this catalogue already holds were all
    /// described before pictures existed — sharing the stamp means re-asking
    /// MusicBrainz about every one of them, at a gated turn each, to find out
    /// what they look like. The second is the mistake this codebase has now paid
    /// for five times: keyed on the answer, the 73 artists in 307 that Wikidata
    /// holds no image for are asked about on every run forever.
    ///
    /// <b>No partial index, where <c>LookupUtc</c> has one.</b> The asymmetry is
    /// deliberate rather than an omission: measured on the real catalogue, the
    /// sequential scan behind this is 0.9ms over 2,906 rows, and the panel polls
    /// it every five seconds. An index for that is a write cost on every artist
    /// upsert to save under a millisecond on a read nobody is waiting for. It
    /// becomes worth adding at the same point the artists table does — which is
    /// a long way past a hundred thousand files, since a library has an order of
    /// magnitude fewer artists than tracks.
    /// </remarks>
    private static readonly System.Linq.Expressions.Expression<Func<Artist, bool>> UnpicturedArtist =
        artist => artist.PortraitLookupUtc == null && artist.Mbid != null;

    /// <summary>
    /// Find a picture for every artist that has not been looked for.
    /// </summary>
    /// <remarks>
    /// <b>The one stage here whose unit is a batch, which is why it does not go
    /// through <c>DrainAsync</c>.</b> Every other worklist in this application is
    /// per item because every other provider answers about one thing and queues
    /// behind a gate at one request a second; asked that way this stage would be
    /// 2,902 turns and the better part of an hour. Wikidata's query language
    /// takes a list, so the same question is a dozen requests, and the shape of
    /// the loop has to match the shape of the question rather than the shape of
    /// its neighbours.
    ///
    /// <b>Every artist in the batch is stamped, found or not, in the same
    /// <c>SaveChanges</c> as the pictures.</b> That is what makes the worklist
    /// reach empty — see <see cref="UnpicturedArtist"/> — and doing it in one
    /// save is what stops a crash mid-batch recording "we asked" for artists
    /// whose answer was lost.
    ///
    /// <b>A failed batch stamps nothing and ends the stage.</b> Nothing is known
    /// about any artist in it, so leaving them null is the honest record and the
    /// next run retries them; ending rather than continuing is not politeness
    /// but termination — the claim query is keyed on the stamp, so a batch that
    /// fails without stamping is a batch the next iteration claims again,
    /// forever. The two file worklists solve the same problem with a
    /// <c>seen</c> set because their unit is small enough to skip past; here the
    /// whole remaining worklist is behind one failure, and a service that just
    /// refused a request is not one to ask eleven more times.
    ///
    /// <b>Nothing but cancellation leaves this method, and the claim and the
    /// save are inside the guard rather than around it.</b> The drains above
    /// rethrow <see cref="ProviderRejectedException"/> so a missing key stops
    /// the pass on the first file instead of the hundred-thousandth — but they
    /// are the first three stages, and this is the last. A throw from here
    /// unwinds past the summary, so <c>_lastCompleted</c> is never assigned and
    /// a run that did every file and every artist reports nothing at all,
    /// because it could not find a photograph. Ending the stage achieves
    /// everything the rethrow would: there is no later work to protect.
    ///
    /// The database calls are inside for the same reason and it is not
    /// theoretical symmetry — an exception out of the claim or the save is the
    /// one thing here that is not the provider's fault, and it is the case where
    /// discarding a completed run's summary would be least explicable.
    /// </remarks>
    private async Task PicturesAsync(
        Tally counts,
        string jobId,
        int pending,
        CancellationToken cancellationToken)
    {
        var size = Math.Max(portraitOptions.Value.BatchSize, 1);

        // Read once for the run rather than per batch: it is a scan of every
        // credit in the catalogue, it does not move while a pass holds the gate,
        // and there are a dozen batches.
        var shelf = await AlbumArtistMbidsAsync(cancellationToken).ConfigureAwait(false);

        var down = new HashSet<string>(StringComparer.Ordinal);

        while (!cancellationToken.IsCancellationRequested)
        {
            PictureBatch batch;

            try
            {
                batch = await PictureBatchAsync(
                        size, shelf, down, counts, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
#pragma warning disable CA1031 // The backstop: see the remarks. Nothing else leaves here.
            catch (Exception cause)
#pragma warning restore CA1031
            {
                counts.Failed++;
                Log.PicturesNotFound(logger, size, cause.Message);
                return;
            }

            if (batch.Claimed == 0) return;

            counts.Examined += batch.Claimed;

            await Report(jobId, counts.Examined, pending, "artist pictures", "running")
                .ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The MusicBrainz ids of the artists an album is billed to.
    /// </summary>
    /// <remarks>
    /// <c>CatalogueEndpoints.AlbumArtistsAsync</c> is the rule and this is the
    /// only translation: it answers in <see cref="ArtistId"/> because that is
    /// what the artist list filters on, and the picture sources are keyed on
    /// MBIDs because that is what a provider can be asked about. Artists with no
    /// MBID drop out, which is the same set <see cref="UnpicturedArtist"/>
    /// already excludes.
    /// </remarks>
    private async Task<IReadOnlySet<Mbid>> AlbumArtistMbidsAsync(
        CancellationToken cancellationToken)
    {
        var scope = scopeFactory.CreateAsyncScope();
        await using (scope.ConfigureAwait(false))
        {
            var db = scope.ServiceProvider.GetRequiredService<FonotecaDbContext>();

            var billed = await Endpoints.CatalogueEndpoints
                .AlbumArtistsAsync(db, cancellationToken)
                .ConfigureAwait(false);

            var mbids = await db.Artists
                .AsNoTracking()
                .Where(artist => artist.Mbid != null && billed.Contains(artist.Id))
                .Select(artist => artist.Mbid!.Value)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            return mbids.ToHashSet();
        }
    }

    /// <summary>
    /// One batch: claim, ask, and stamp every artist in it. Returns how many
    /// were claimed, which is zero when the worklist is empty.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="PicturesAsync"/> only so that one <c>try</c> can
    /// cover the claim, the lookup and the save together — see its remarks.
    /// </remarks>
    private async Task<PictureBatch> PictureBatchAsync(
        int size,
        IReadOnlySet<Mbid> shelf,
        HashSet<string> down,
        Tally counts,
        CancellationToken cancellationToken)
    {
        List<PendingArtist> page;

        var claim = scopeFactory.CreateAsyncScope();
        await using (claim.ConfigureAwait(false))
        {
            var db = claim.ServiceProvider.GetRequiredService<FonotecaDbContext>();

            page = await db.Artists
                .AsNoTracking()
                .Where(UnpicturedArtist)
                .OrderBy(a => a.Id)
                .Select(a => new PendingArtist(a.Id, a.Name, a.Mbid!.Value))
                .Take(size)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
        }

        if (page.Count == 0) return new PictureBatch(0);

        var wanted = page
            .Select(artist => new ArtistToPicture(artist.Mbid, artist.Name))
            .ToList();

        // The source that covers everyone, first and unguarded: a failure here
        // means nothing is known about any artist in the batch, so it ends the
        // stage without stamping. Asking it first is also what keeps a Wikidata
        // outage from spending a batch of Qobuz's hourly allowance to be thrown
        // away.
        //
        // Outside the scope, like every other lookup here, so a round trip does
        // not hold a connection from the pool.
        var found = new Dictionary<Mbid, Uri>(
            await portraits.FindAsync(wanted, cancellationToken).ConfigureAwait(false));

        // And the better picture, for the artists a person actually browses.
        // Overwriting rather than filling in: these are the preferred sources,
        // so an artist Wikidata also answered for gets the better photograph.
        var billed = wanted.Where(artist => shelf.Contains(artist.Id)).ToList();

        var upgraded = await UpgradeAsync(billed, down, counts, cancellationToken)
            .ConfigureAwait(false);

        foreach (var better in upgraded)
        {
            found[better.Key] = better.Value;
        }

        var stampedAt = StoreTime.ToStorePrecision(clock.UtcNow);

        var save = scopeFactory.CreateAsyncScope();
        await using (save.ConfigureAwait(false))
        {
            var db = save.ServiceProvider.GetRequiredService<FonotecaDbContext>();

            var ids = page.Select(artist => artist.Id).ToList();

            var rows = await db.Artists
                .Where(artist => ids.Contains(artist.Id))
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            foreach (var row in rows)
            {
                row.PortraitLookupUtc = stampedAt;

                if (row.Mbid is { } mbid && found.TryGetValue(mbid, out var picture))
                {
                    row.PortraitUrl = picture.AbsoluteUri;
                    counts.ArtistsPictured++;
                }
            }

            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        return new PictureBatch(page.Count);
    }

    /// <summary>How many artists a batch claimed.</summary>
    /// <remarks>
    /// Which sources have given up travels beside this in a set the caller owns,
    /// rather than living in a field, because it is state for one run and this
    /// service outlives a run. A source that has failed once is not asked again
    /// for the rest of the pass: every remaining batch would fail identically,
    /// and a dozen more requests to be told so is the traffic pattern this
    /// codebase's provider notes are about. <b>It is per source</b> — Qobuz
    /// being down must not stop Deezer being asked, which is the whole point of
    /// there being four of them.
    /// </remarks>
    private readonly record struct PictureBatch(int Claimed);

    /// <summary>
    /// The per-artist picture sources, best first.
    /// </summary>
    /// <remarks>
    /// <b>The order is a claim about what kind of picture comes back.</b> Both
    /// cost one request per artist.
    ///
    /// The rule this codebase now holds is that <b>an album sleeve is never an
    /// artist's picture</b> — it was the fallback once and it read backwards on
    /// the page, because the artists reaching a fallback are very nearly the
    /// artists who are not on the front of their own records. Dropping the
    /// catalogue-derived fallback does not finish that job: <b>a source can hand
    /// back a sleeve too</b>, and one of these routinely does.
    ///
    /// <list type="bullet">
    /// <item><b>Qobuz</b> is the press photograph on their own artist page.
    /// Rationed at 600 an hour, shared with downloading, and asked first because
    /// it is the one measured against this library.</item>
    /// <item><b>TheAudioDB</b> keeps artist thumbnails in a different field from
    /// album art, so <c>strArtistThumb</c> is a photograph of somebody by
    /// construction. It is looked up by MusicBrainz id, so it also cannot be
    /// wrong about <i>who</i> — and it is the only source that reaches an artist
    /// whose name is not written in Latin script.</item>
    /// </list>
    ///
    /// <b>A wider source was measured and left out, which is the part worth
    /// keeping.</b> Deezer's search API is open and answers for 30 of the 40
    /// album artists Qobuz cannot place, against TheAudioDB's 16 — and
    /// inspected one by one, <b>16 of those 20 pictures were album covers</b>,
    /// because its artist image is whatever the label supplied. It filled the
    /// grid and filled it with sleeves. Coverage is not the thing being
    /// maximised here.
    ///
    /// Wikidata is in neither list — it runs over the whole batch first and is
    /// what these upgrade <i>from</i>.
    /// </remarks>
    private IEnumerable<(string Name, IArtistPortraits Source)> PictureSources =>
    [
        (QobuzPortraits.ProviderName, pressPhotos),
        (AudioDbPortraits.ProviderName, audioDbPhotos),
    ];

    /// <summary>
    /// The shops that can say what an artist released, in the order they are asked.
    /// </summary>
    /// <remarks>
    /// <b>MusicBrainz is a volunteer catalogue and it is late.</b> A record
    /// reaches it when an editor adds it; a shop lists it the day it goes on
    /// sale. Since monitoring is defined as "turned up after you followed them",
    /// the window these sources cover is exactly the one that matters.
    ///
    /// Ordered, and the order is a decision rather than a registration accident —
    /// the same reasoning as <see cref="PictureSources"/>. Qobuz first because it
    /// is the one that can also <i>sell</i> the record: a gap it names is
    /// actionable in one click, where a gap only a free source knows about is a
    /// lead. A second entry costs one line here.
    /// </remarks>
    private IEnumerable<(string Name, IReleaseDiscovery Source)> ReleaseSources =>
    [
        (QobuzReleaseDiscovery.ProviderName, qobuzReleases),
    ];

    /// <summary>
    /// The better picture, from the first preferred source that has one.
    /// </summary>
    /// <remarks>
    /// <b>Each source is asked only about the artists still without an
    /// answer</b>, so the second costs a request only for the gaps the first
    /// left. On this library that is 326 Qobuz requests and then about 40.
    ///
    /// <b>A preferred source is allowed to fail without taking the others with
    /// it.</b> Written as a plain await, a Qobuz outage — an expired
    /// subscription, a rotated token, or their unofficial API changing, which it
    /// does without warning — discarded the whole batch and ended the stage. So
    /// the failure of the source that makes pictures <i>better</i> meant no
    /// artist got a picture <i>at all</i>, which is the exact opposite of what a
    /// preferred source is for. Now it means the next source answers, and
    /// failing that, the fallback's picture stands.
    ///
    /// <b>A source that fails once is not asked again this run.</b> Every
    /// remaining batch would fail identically; <paramref name="down"/> is how
    /// that is remembered, and it is per source rather than a single flag
    /// precisely so one dead service does not silence the others.
    ///
    /// The artists a dead source would have improved are still stamped, with
    /// whatever a live one or the fallback found. That is a deliberate trade and
    /// it is the direction that fails quietly: nothing clears the stamp, so they
    /// keep the lesser photograph until somebody re-asks by hand. The
    /// alternative — leaving them unstamped — makes a broken subscription into a
    /// worklist that never empties and an enrichment button that never goes
    /// quiet, which is worse and also silent.
    ///
    /// Cancellation passes straight through, or a person pressing stop would
    /// mark every source dead for the rest of the run.
    /// </remarks>
    private async Task<IReadOnlyDictionary<Mbid, Uri>> UpgradeAsync(
        List<ArtistToPicture> artists,
        HashSet<string> down,
        Tally counts,
        CancellationToken cancellationToken)
    {
        var found = new Dictionary<Mbid, Uri>();

        foreach (var (name, source) in PictureSources)
        {
            var missing = artists.Where(artist => !found.ContainsKey(artist.Id)).ToList();

            if (missing.Count == 0) break;
            if (down.Contains(name)) continue;

            try
            {
                var answered = await source.FindAsync(missing, cancellationToken)
                    .ConfigureAwait(false);

                foreach (var picture in answered) found[picture.Key] = picture.Value;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
#pragma warning disable CA1031 // A preferred source failing is not the pass failing.
            catch (Exception cause)
#pragma warning restore CA1031
            {
                counts.Failed++;
                down.Add(name);
                Log.PicturesNotFound(logger, missing.Count, $"{name}: {cause.Message}");
            }
        }

        return found;
    }

    /// <summary>
    /// One artist: ask MusicBrainz who they are, and write it onto the row.
    /// </summary>
    /// <remarks>
    /// The lookup happens outside the scope, as everywhere else here, so a
    /// MusicBrainz round trip does not hold a connection from the pool.
    ///
    /// <b>The name and the sort name are overwritten; the type and the
    /// disambiguation are not merely filled in.</b> This is the one place in the
    /// application where those four fields have a source better than a credit
    /// line. <see cref="CatalogueWriter.UpsertArtistAsync"/> deliberately writes
    /// them with <c>??=</c> and leaves the name alone, because a sleeve printing
    /// "Bowie" must not rename David Bowie — but an artist lookup is not a
    /// sleeve, it is the artist's own record, and deferring to what one release
    /// happened to print would make this pass unable to correct the very thing
    /// it exists to improve.
    /// </remarks>
    private async Task DescribeAsync(
        PendingArtist artist,
        Tally counts,
        CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;

        MusicBrainzArtist? described;

        try
        {
            described = await musicBrainz
                .GetArtistAsync(artist.Mbid, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (ProviderUnavailableException cause)
        {
            // Transient. LookupUtc stays null, so the row stays on the worklist
            // and the next run retries it — the same bargain a failed file
            // enrichment takes, and the reason the stamp is separate from every
            // field it fills.
            counts.Failed++;
            Log.ArtistNotDescribed(logger, artist.Name, cause.Message);
            return;
        }

        var scope = scopeFactory.CreateAsyncScope();
        await using (scope.ConfigureAwait(false))
        {
            var db = scope.ServiceProvider.GetRequiredService<FonotecaDbContext>();

            var row = await db.Artists
                .FirstOrDefaultAsync(a => a.Id == artist.Id, cancellationToken)
                .ConfigureAwait(false);

            // Gone since the page was claimed — a cascade from a recording a
            // scan removed. Nothing to update, and nothing wrong.
            if (row is null) return;

            // Stamped whether or not MusicBrainz had them. "We asked and the
            // artist has been merged away" is an answer, and a row left unstamped
            // on it is re-asked about on every run forever.
            row.LookupUtc = now;

            if (described is null)
            {
                Log.ArtistNotDescribed(logger, artist.Name, "no such artist");
            }
            else
            {
                // Guarded where the three below are, and for a reason they do not
                // have: MusicBrainz sends "" for absent text rather than null,
                // so `ToArtist`'s `source.Name ?? string.Empty` can hand over a
                // blank. `Name` is NOT NULL and "" satisfies that, so an
                // unguarded write replaces a usable credit-line name with
                // nothing — permanently, because the stamp goes on in the same
                // save and no endpoint clears it.
                if (!string.IsNullOrWhiteSpace(described.Name)) row.Name = described.Name;

                row.SortName = described.SortName ?? row.SortName;

                // Assigned rather than coalesced, and that is the opposite of
                // the line above on purpose. This is derived from the name that
                // was just written, so a correction that turns a non-Latin name
                // into a Latin one has to be able to clear it — coalescing would
                // leave the old transliteration printed over a name that no
                // longer needs one.
                row.LatinName = LatinNames.Of(row.Name, described.Aliases);
                row.Type = described.Type ?? row.Type;
                row.Disambiguation = described.Disambiguation ?? row.Disambiguation;
                row.Country = described.Country;
                row.Gender = described.Gender;
                row.BeganYear = described.BeganYear;
                row.EndedYear = described.EndedYear;
                row.Ended = described.HasEnded;
                row.Genres = Joined(described.Genres);

                await RecordBandsAsync(db, row, described.Bands, cancellationToken)
                    .ConfigureAwait(false);

                counts.ArtistsDescribed++;
            }

            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The bands this artist played in, as links to artists the library holds.
    /// </summary>
    /// <remarks>
    /// <b>Only groups already in the catalogue.</b> MusicBrainz knows every band
    /// a session player passed through, and minting a row for each would put
    /// artists with no tracks into a list whose whole promise is that it browses
    /// what you own — Mark Knopfler alone brings four groups this library holds
    /// nothing by. Nothing is lost: the rule this feeds asks whether the artist
    /// credited on a release is a band the artist was in, and that artist is by
    /// construction a row already.
    ///
    /// <b>Existing links are not rewritten.</b> Re-asking the whole artist
    /// worklist is a hand-written <c>UPDATE</c> clearing <c>LookupUtc</c> — the
    /// same documented path <c>AcoustIdCheckedUtc</c> takes — so this method runs
    /// again over artists it has already described, and there is no unique index
    /// on <c>Relationships</c> to catch a second copy. A membership recorded
    /// twice would count a band twice on every screen that ever groups by it.
    ///
    /// <b>Nothing is deleted either</b>, and that is a decision rather than an
    /// omission: a membership MusicBrainz has since removed leaves a stale row,
    /// which is a wrong shelf on one page, where a delete-and-reinsert would
    /// throw away a correct row every time the lookup failed halfway.
    /// </remarks>
    private static async Task RecordBandsAsync(
        FonotecaDbContext db,
        Artist member,
        IReadOnlyList<Mbid> bands,
        CancellationToken cancellationToken)
    {
        if (bands.Count == 0) return;

        var known = await db.Artists
            .Where(a => a.Mbid != null && bands.Contains(a.Mbid.Value))
            .Select(a => a.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        if (known.Count == 0) return;

        var already = await db.Relationships
            .Where(r => r.ArtistId == member.Id && r.Type == RelationshipTargets.Member)
            .Select(r => r.TargetId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var seen = already.ToHashSet();

        foreach (var band in known)
        {
            // A group that somehow lists itself. Cheap to rule out, and a
            // self-membership would make every one of its own albums read as
            // somebody else's band.
            if (band == member.Id || !seen.Add(band.Value)) continue;

            db.Relationships.Add(new Relationship
            {
                Id = Guid.CreateVersion7(),
                SourceType = RelationshipTargets.Artist,
                SourceId = member.Id.Value,
                TargetType = RelationshipTargets.Artist,
                TargetId = band.Value,
                Type = RelationshipTargets.Member,
                ArtistId = member.Id,
            });
        }
    }

    /// <summary>The genres as the column stores them, or null when there are none.</summary>
    /// <remarks>
    /// Cut on a separator rather than mid-word: a clamp that can leave
    /// "progressive ro" would put a genre in the catalogue that does not exist,
    /// which is worse than dropping it.
    /// </remarks>
    private static string? Joined(IReadOnlyList<string> genres)
    {
        var kept = new List<string>(Math.Min(genres.Count, MaxGenres));
        var length = 0;

        foreach (var genre in genres.Take(MaxGenres))
        {
            var cost = genre.Length + (kept.Count == 0 ? 0 : 2);

            if (length + cost > MaxGenresLength) break;

            kept.Add(genre);
            length += cost;
        }

        return kept.Count == 0 ? null : string.Join(", ", kept);
    }

    /// <summary>An artist the catalogue holds and MusicBrainz has not been asked about.</summary>
    private readonly record struct PendingArtist(ArtistId Id, string Name, Mbid Mbid);

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
        public int ArtistsDescribed;

        public int ArtistsPictured;

        /// <summary>Records a shop named that the catalogue had no row for.</summary>
        public int RecordsDiscovered;
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
    /// <summary>
    /// The relationship type recording that one artist played in another.
    /// </summary>
    /// <remarks>
    /// The only artist-to-artist link the catalogue stores, and the only
    /// <c>Relationship</c> row with neither a recording nor a work on the far
    /// end. Existing queries cannot pick it up by accident: they all reach
    /// relationships through <c>recording.Relationships</c> or
    /// <c>work.Relationships</c>, which are scoped by those foreign keys, and
    /// both are null here.
    /// </remarks>
    public const string Member = "member";

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

/// <summary>What a run would have to ask about, by kind of question.</summary>
/// <param name="Files">Identified files with no recording yet.</param>
/// <param name="Artists">Artists MusicBrainz has never been asked to describe.</param>
/// <param name="Portraits">
/// Artists nobody has looked for a picture of. Counted separately because it is
/// not the same work: a described artist cost a gated MusicBrainz turn, and a
/// picture costs a two-hundred-and-fiftieth of one batched query.
/// </param>
/// <param name="Discographies">
/// Artists nobody has asked MusicBrainz what they released. Counted apart from
/// <paramref name="Artists"/> for the reason the portraits are: same rows,
/// different work, and a person reading one number would be told to expect the
/// wrong wait.
///
/// <b>No longer the small one.</b> While this was the followed set it was single
/// figures beside three counts in the thousands; it is now every artist with an
/// MBID that has never been browsed, so on a fresh library it is the largest
/// number on the panel and the slowest stage behind it — one gated browse each,
/// plus the shops.
/// </param>
public sealed record EnrichmentPending(int Files, int Artists, int Portraits, int Discographies)
{
    /// <summary>The size of the job, which is what "is there anything to do" reads.</summary>
    /// <remarks>
    /// Portraits are in the sum, and they had to be. Every artist in this
    /// catalogue was described before pictures existed, so on the library this
    /// was written against <see cref="Files"/> and <see cref="Artists"/> are both
    /// zero and the button is disabled — a stage left out of this total is a
    /// stage that can never be reached.
    /// </remarks>
    public int Total => Files + Artists + Portraits + Discographies;
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

    /// <summary>
    /// Artists MusicBrainz described this run.
    /// </summary>
    /// <remarks>
    /// Not a subset of <see cref="Artists"/> and routinely much larger than it:
    /// that count is the artists this run's <i>files</i> credited, and this one
    /// is the artists the catalogue held that nobody had asked about — which on
    /// the first run is every artist in the library and on later runs is
    /// whatever the files added.
    /// </remarks>
    int ArtistsDescribed,

    /// <summary>Artists a picture was found for this run.</summary>
    /// <remarks>
    /// Reported beside <paramref name="ArtistsDescribed"/> and not folded into
    /// it: they are two services answering two questions, and about a quarter of
    /// a library has a description and no photograph. A single number would make
    /// that quarter look like a failure of the artist stage.
    /// </remarks>
    int ArtistsPictured,

    bool Cancelled);
