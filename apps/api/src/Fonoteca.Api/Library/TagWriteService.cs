using System.Diagnostics;
using Fonoteca.Api.Endpoints;
using Fonoteca.Api.Logging;
using Fonoteca.Api.Realtime;
using Fonoteca.Data;
using Fonoteca.Domain.Abstractions;
using Fonoteca.Domain.Catalogue;
using Fonoteca.Tagging;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace Fonoteca.Api.Library;

/// <summary>
/// Writes what the catalogue worked out back into the files it came from.
/// </summary>
/// <remarks>
/// <b>The last step of the whole chain, and the only one that is not automatic.</b>
/// Scan finds the files, identification says what the audio is, enrichment says
/// who made it, attribution says which album it came from — and until now every
/// bit of that lived only in PostgreSQL. Copy the library to another machine, or
/// open it in any other player, and none of it exists. This pass is what makes
/// the answers portable.
///
/// <b>It never starts on its own, and that is a decision rather than an
/// omission.</b> No timer, no <c>IdentifyAfterScan</c>-style follow-on, nothing
/// in <c>Fonoteca.Jobs</c>. Every other pass writes to a database that can be
/// dropped and rebuilt from the audio; this one rewrites the audio. It is
/// reached by three buttons — one album, one artist, the whole library — and by
/// nothing else. <c>Fonoteca:AllowFileMutation</c> is the second lock, and with
/// it off a run does everything except the write and reports exactly what it
/// would have done.
///
/// The sharp edges, in the order they cost something:
///
/// <list type="bullet">
/// <item><b>A committed write records the file's new size and mtime in the same
/// transaction.</b> The loop the identification pass documents, reached by a
/// third route and this time over the whole library at once: a tag write changes
/// the bytes, and a catalogue still holding the old size reads that as "modified"
/// on the next scan and discards every derived column — including the tags this
/// pass just wrote, the AcoustID, the recording link and the album. Forgetting
/// this line does not corrupt a file; it silently empties the catalogue.</item>
///
/// <item><b>The worklist is not a column, and does not need to be.</b> There is
/// no <c>TagsWrittenUtc</c> and no migration, because the diff already answers
/// the question the column would: a file that already says what the catalogue
/// says comes back <see cref="TagWriteStatus.NothingToDo"/> having been read and
/// not opened for writing. Re-running over a tagged library therefore costs a
/// tag read per file rather than a rewrite, and the pass is idempotent by
/// construction rather than by bookkeeping.</item>
///
/// <item><b>Serial, deliberately.</b> ATL renders the entire file — audio and
/// all — into a staged sibling, so a page of concurrent writes is a page of
/// whole-album-sized copies competing for one disk, and the failure mode of
/// getting it wrong is not a slow pass. The probe pass parallelises because it
/// only reads.
/// ponytail: one at a time. Worth revisiting only if measured against an SSD and
/// found to be the bottleneck rather than the disk.</item>
///
/// <item><b>One scope and one <c>SaveChanges</c> per file.</b> The resumability
/// story every other pass here has: a run killed at file 4,000 of 8,000 keeps
/// the 4,000, and the rest are picked up by running it again.</item>
///
/// <item><b>The file is committed before the row.</b> Identification's rule, for
/// identification's reason: a crash in the gap leaves a file carrying correct
/// tags and a row with a stale size, which the next scan notices and repairs.
/// The reverse leaves a row claiming bytes that were never written.</item>
///
/// <item><b>A file that is not fully identified is not on the worklist.</b>
/// Recording, track and release, all three — writing an album name with no track
/// number, or a title with no album, produces a file that reads as a half-tagged
/// rip in every player. The narrower question is also the honest one: these are
/// the files the catalogue actually has an answer for.</item>
/// </list>
/// </remarks>
public sealed class TagWriteService(
    LibraryWorkGate gate,
    IServiceScopeFactory scopeFactory,
    IHubContext<JobsHub, IJobsClient> hub,
    IHostApplicationLifetime lifetime,
    TagWriterOptions writerOptions,
    IClock clock,
    ILogger<TagWriteService> logger) : IHostedService
{
    /// <summary>The kind this pass takes the gate as, and the job kind on the wire.</summary>
    public const string JobKind = "library.tags";

    /// <summary>What the journal calls a catalogue write. Not the AcoustID pass's prefix.</summary>
    public const string EventPrefix = "tagging.catalogue";

    /// <summary>Rows read per query. Bounded so a cancelled pass stops promptly.</summary>
    private const int PageSize = 100;

    /// <summary>
    /// Who the journal says did this.
    /// </summary>
    /// <remarks>
    /// The owner, not <c>SystemCallerContext.SystemId</c>, which is what the
    /// identification pass records for its own tag writes. The difference is the
    /// whole design: that pass runs because a scan finished, and this one runs
    /// because a person pressed a button. A journal that attributed both to the
    /// system would lose the only fact worth keeping about a run that rewrote
    /// eight thousand files.
    /// </remarks>
    private const string Actor = SingleUserCallerContext.OwnerId;

    private static readonly TimeSpan ProgressInterval = TimeSpan.FromMilliseconds(250);

    private CancellationTokenSource? _cancellation;
    private volatile TagWriteProgress? _progress;
    private volatile TagWriteSummary? _lastCompleted;
    private volatile string? _lastError;
    private Task? _pass;

    /// <summary>
    /// Whether a run would change a byte.
    /// </summary>
    /// <remarks>
    /// Read from the same single registration <c>AcoustIdTagWriter</c> reads, so
    /// there is one answer rather than two. The screen needs it <i>before</i> the
    /// run rather than after: with the flag off the pass does everything except
    /// the write, and a card reporting "8,140 files done" without saying so would
    /// be true and useless.
    /// </remarks>
    public bool MutationAllowed => writerOptions.AllowFileMutation;

    public bool IsRunning => _progress is not null;

    public TagWriteProgress? Progress => _progress;

    public TagWriteSummary? LastCompleted => _lastCompleted;

    /// <summary>Why the last pass stopped without finishing, or null.</summary>
    public string? LastError => _lastError;

    /// <summary>
    /// Files whose catalogue answer is complete enough to write.
    /// </summary>
    /// <remarks>
    /// All three links, for the reason in the class remarks. One expression,
    /// used by the counts the buttons show and by the pass itself, because two
    /// copies of it would drift and the visible symptom is a button offering a
    /// number it then does not write.
    /// </remarks>
    public static IQueryable<MediaFile> Writable(IQueryable<MediaFile> files) =>
        files.Where(file =>
            file.RecordingId != null && file.ReleaseId != null && file.TrackId != null);

    /// <summary>How many files one scope would consider.</summary>
    public async Task<int> CountAsync(TagWriteScope scope, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);

        var dbScope = scopeFactory.CreateAsyncScope();
        await using (dbScope.ConfigureAwait(false))
        {
            var db = dbScope.ServiceProvider.GetRequiredService<FonotecaDbContext>();

            return await (await NarrowAsync(db, scope, cancellationToken).ConfigureAwait(false))
                .CountAsync(cancellationToken)
                .ConfigureAwait(false);
        }
    }

    /// <summary>Starts a pass, or reports why it did not.</summary>
    public TagWriteOutcome Start(TagWriteScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);

        if (!gate.TryEnter(JobKind, out var lease))
        {
            Log.TagWriteBusy(logger, gate.ActiveKind ?? "other work");
            return new TagWriteOutcome(TagWriteStartStatus.AlreadyRunning, null);
        }

        var jobId = Guid.CreateVersion7().ToString("N")[..12];

        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.ApplicationStopping);
        _cancellation = cancellation;

        // Cleared before _progress, not after — ProbeService's race, and the same
        // fix: a stop landing between the two lines would otherwise await the
        // previous pass's completed task and return immediately.
        _pass = null;
        _progress = new TagWriteProgress(jobId, scope.Label, 0, 0, null);
        _lastError = null;

        _pass = Task.Run(
            async () =>
            {
                try
                {
                    _lastCompleted = await RunAsync(jobId, scope, cancellation.Token).ConfigureAwait(false);
                }
#pragma warning disable CA1031 // A background pass must never take the process down.
                catch (Exception cause)
#pragma warning restore CA1031
                {
                    _lastError = cause.Message;
                    Log.TagWriteAborted(logger, cause.Message);
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

        return new TagWriteOutcome(TagWriteStartStatus.Started, jobId);
    }

    /// <summary>Asks the running pass to stop. It finishes the file it is on.</summary>
    /// <remarks>
    /// Finishing the file is not politeness. A staged write that is abandoned
    /// mid-render leaves a temporary sibling and no commit — recoverable, but the
    /// commit itself is a rename and must not be interrupted between the journal
    /// entry and the swap.
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
            // Reachable from an HTTP DELETE that raced the pass's own finally,
            // where the honest answer is "it already stopped" rather than a 500.
            return false;
        }
    }

    Task IHostedService.StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <remarks>
    /// The one pass whose abandonment matters, which is why the wait is longer
    /// than the probe's: this writes files. A shutdown that severs a render
    /// leaves a staged sibling behind, and the commit it never reached is the
    /// only step that touches the original.
    /// </remarks>
    async Task IHostedService.StopAsync(CancellationToken cancellationToken)
    {
        var pass = _pass;

        if (pass is null || !IsRunning) return;

        Cancel();

        try
        {
            await pass.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception cause) when (cause is TimeoutException or OperationCanceledException)
        {
            Log.TagWriteAborted(logger, cause.Message);
        }
    }

    /// <summary>The scope's files, as a query.</summary>
    /// <remarks>
    /// An artist is resolved to a set of recordings first, through
    /// <c>BrowsableAsync</c> — the same rule the artist page browses by, so the
    /// button on that page writes to exactly the tracks the page lists. Reading
    /// <c>ArtistCredits</c> alone would file every symphony under a composer and
    /// miss every conductor and orchestra, which is the lesson
    /// <c>PrimaryCredits</c> already paid for.
    /// </remarks>
    private static async Task<IQueryable<MediaFile>> NarrowAsync(
        FonotecaDbContext db,
        TagWriteScope scope,
        CancellationToken cancellationToken)
    {
        var files = Writable(db.MediaFiles.AsNoTracking());

        if (scope.Release is { } release)
        {
            return files.Where(file => file.ReleaseId == release);
        }

        if (scope.Artist is { } artist)
        {
            var theirs = await CatalogueEndpoints
                .RecordingsOfAsync(db, artist, cancellationToken)
                .ConfigureAwait(false);

            return files.Where(file => theirs.Contains(file.RecordingId!.Value));
        }

        return files;
    }

    private async Task<TagWriteSummary> RunAsync(
        string jobId,
        TagWriteScope scope,
        CancellationToken cancellationToken)
    {
        var startedAt = clock.UtcNow;
        var elapsed = Stopwatch.StartNew();
        var counts = new Tally();
        var correlationId = Guid.CreateVersion7().ToString("N")[..12];

        var pending = await CountAsync(scope, cancellationToken).ConfigureAwait(false);

        Log.TagWriteStarted(logger, jobId, scope.Label, pending);

        _progress = new TagWriteProgress(jobId, scope.Label, 0, pending, null);

        var lastReport = clock.UtcNow;
        var offset = 0;

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var page = await ClaimAsync(scope, offset, cancellationToken).ConfigureAwait(false);

                if (page.Count == 0) break;

                // The offset advances by the page rather than by what was
                // written, because nothing here removes a row from the worklist:
                // a file that is already correct stays on it forever, and a
                // worklist that does not shrink plus a query that never moves is
                // a pass that reads page one until it is cancelled.
                offset += page.Count;

                foreach (var file in page)
                {
                    if (cancellationToken.IsCancellationRequested) break;

                    try
                    {
                        await HandleAsync(file, counts, correlationId, cancellationToken)
                            .ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
#pragma warning disable CA1031 // The backstop: no single file may end the pass.
                    catch (Exception cause)
#pragma warning restore CA1031
                    {
                        // <b>Wider than it looks like it needs to be, and that is
                        // the point.</b> This pass has no "done" column — the diff
                        // is the worklist, which is what makes re-running cheap —
                        // so nothing steps over a row that failed. An escaping
                        // exception unwinds to `Start`'s catch-all and ends the
                        // run at the same file every time, which means one
                        // unreadable file blocks every file behind it forever.
                        //
                        // <c>TagReadFailedException</c> alone is not enough:
                        // <c>TagReader.ReadAsync</c> opens the stream outside its
                        // own try, so a permissions error or an I/O error on open
                        // arrives as itself. Unlike the probe pass there is no
                        // subprocess here whose absence would fail every file
                        // identically, so there is no failure worth ending the run
                        // over — a run that skips one file and reports it beats a
                        // run that stops.
                        counts.Failed++;
                        Log.TagsNotWritten(logger, file.Path, Because(cause));
                    }

                    counts.Examined++;

                    var now = clock.UtcNow;

                    if (now - lastReport < ProgressInterval) continue;

                    lastReport = now;
                    _progress = new TagWriteProgress(jobId, scope.Label, counts.Examined, pending, file.Path);

                    await Report(jobId, counts.Examined, pending, file.Path, "running").ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Between files, or between pages. The file being written finished.
        }

        var summary = new TagWriteSummary(
            JobId: jobId,
            Scope: scope.Label,
            StartedAtUtc: startedAt,
            CompletedAtUtc: clock.UtcNow,
            DurationMilliseconds: elapsed.ElapsedMilliseconds,
            Examined: counts.Examined,
            Written: counts.Written,
            Unchanged: counts.Unchanged,
            Refused: counts.Refused,
            Unsupported: counts.Unsupported,
            Failed: counts.Failed,
            Skipped: counts.Missing,
            Cancelled: cancellationToken.IsCancellationRequested);

        Log.TagWriteCompleted(
            logger, jobId, counts.Written, counts.Unchanged, counts.Refused, counts.Failed,
            summary.DurationMilliseconds);

        await Report(jobId, counts.Examined, pending, null, "completed").ConfigureAwait(false);

        return summary;
    }

    /// <summary>
    /// One page of the worklist, with everything a tag needs already joined.
    /// </summary>
    /// <remarks>
    /// Projected flat rather than loaded as a graph, and every id crosses
    /// <i>whole</i>. The ids in this schema are value-converted <c>readonly
    /// record struct</c>s and EF translates no member access on one into SQL, so
    /// <c>Release.Mbid!.Value.Value</c> compiles and then fails at runtime;
    /// selecting the <c>Mbid?</c> itself is what works, exactly as
    /// <c>FiledUnderAsync</c> already does it. Unwrapping happens in memory.
    ///
    /// Ordered by id, which is <c>Guid.CreateVersion7()</c> and therefore
    /// creation-ordered, so a page boundary is stable across the whole run even
    /// though the rows underneath are being rewritten.
    /// </remarks>
    private async Task<List<PendingTagWrite>> ClaimAsync(
        TagWriteScope scope,
        int offset,
        CancellationToken cancellationToken)
    {
        var dbScope = scopeFactory.CreateAsyncScope();
        await using (dbScope.ConfigureAwait(false))
        {
            var db = dbScope.ServiceProvider.GetRequiredService<FonotecaDbContext>();

            var files = await NarrowAsync(db, scope, cancellationToken).ConfigureAwait(false);

            return await files
                .OrderBy(file => file.Id)
                .Skip(offset)
                .Take(PageSize)
                .Select(file => new PendingTagWrite(
                    file.Id,
                    file.Path,
                    file.AcoustId,
                    file.Track!.Title,
                    file.Recording!.Title,
                    file.Recording.Mbid,
                    file.Track.Position,
                    file.Release!.TrackCount,
                    file.Track.DiscNumber,
                    file.Release.DiscCount,
                    file.Release.Title,
                    file.Release.Mbid,
                    file.Release.ReleaseGroup!.Mbid,
                    file.Release.ReleasedYear,
                    file.Recording.Work!.Mbid,
                    file.Recording.Credits
                        .OrderBy(credit => credit.Position)
                        .Select(credit => new PendingCredit(
                            credit.CreditedAs ?? credit.Artist!.Name,
                            credit.JoinPhrase,
                            credit.Artist!.Mbid))
                        .ToList(),
                    file.Release.Credits
                        .OrderBy(credit => credit.Position)
                        .Select(credit => new PendingCredit(
                            credit.CreditedAs ?? credit.Artist!.Name,
                            credit.JoinPhrase,
                            credit.Artist!.Mbid))
                        .ToList()))
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
        }
    }

    /// <summary>One file: plan it, write it, and record what the bytes became.</summary>
    private async Task HandleAsync(
        PendingTagWrite file,
        Tally counts,
        string correlationId,
        CancellationToken cancellationToken)
    {
        var scope = scopeFactory.CreateAsyncScope();
        await using (scope.ConfigureAwait(false))
        {
            var provider = scope.ServiceProvider;
            var db = provider.GetRequiredService<FonotecaDbContext>();
            var writer = provider.GetRequiredService<TagWriter>();
            var files = provider.GetRequiredService<IAudioFileStore>();

            var path = new LibraryPath(file.Path);

            // Is it there at all? A missing file is a row for the next scan to
            // delete, and asking ATL to parse an absent path is an exception per
            // file across an unmounted volume.
            if (await files.StatAsync(path, cancellationToken).ConfigureAwait(false) is null)
            {
                counts.Missing++;
                return;
            }

            // The forty FLACs carrying a prepended ID3v2 header throw out of here
            // rather than returning: two libraries have to agree about what is in
            // a file before it is changed, and one of them cannot parse it. The
            // backstop in RunAsync counts it and moves on.
            var plan = await writer
                .PlanAsync(path, CatalogueTags.For(file.Describe()), cancellationToken)
                .ConfigureAwait(false);

            var write = await writer
                .ApplyAsync(
                    plan, file.Id.ToString(), correlationId, Actor, EventPrefix, cancellationToken)
                .ConfigureAwait(false);

            switch (write.Status)
            {
                case TagWriteStatus.Written: counts.Written++; break;
                case TagWriteStatus.NothingToDo: counts.Unchanged++; break;
                case TagWriteStatus.Refused: counts.Refused++; break;
                case TagWriteStatus.Unsupported: counts.Unsupported++; break;

                default:
                    counts.Failed++;
                    Log.TagsNotWritten(logger, file.Path, write.Detail ?? write.Status.ToString());
                    break;
            }

            if (write.Committed is { } facts)
            {
                var row = await db.MediaFiles
                    .FirstOrDefaultAsync(candidate => candidate.Id == file.Id, cancellationToken)
                    .ConfigureAwait(false);

                // Null when a scan deleted the row since the page was read. The
                // file on disk carries correct tags and nothing points at it,
                // which the next scan repairs by cataloguing it afresh — and the
                // journal entry below still has to be saved, so this branch
                // narrows rather than returns.
                if (row is not null)
                {
                    // THE line. A tag write changes the bytes; a catalogue still
                    // holding the old size and mtime reads that as "this file was
                    // modified" on the next scan and discards every derived
                    // column on it. Over a library at a time that is the whole
                    // catalogue.
                    row.SizeBytes = facts.SizeBytes;
                    row.LastModifiedUtc = StoreTime.ToStorePrecision(facts.LastModifiedUtc);
                    row.ContentHash = null;

                    // The AcoustID rides along with the rest, so a file this pass
                    // tags is one the identification pass no longer has to open.
                    // Stamped only when it was actually among the fields written.
                    if (write.Plan is { } applied
                        && applied.Changes.Any(change => change.Field == AcoustIdTagField.For(path)))
                    {
                        row.AcoustIdTaggedUtc = StoreTime.ToStorePrecision(clock.UtcNow);
                    }
                }
            }

            // Always, and not only when something was committed.
            // `IEventLog.AppendAsync` does not save itself — that is what makes
            // one scope and one save per file the resumability story — so
            // returning early here silently discards the journal. It discards it
            // for exactly the two cases that have nothing else to show for
            // themselves: a refusal, which is the whole content of a dry run,
            // and an abandoned write, which is the only record that a file
            // failed verification.
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>The sentence the log gets, with the tag library named where one failed.</summary>
    private static string Because(Exception cause) => cause is TagReadFailedException read
        ? $"{read.Library} could not read it ({read.CauseType})"
        : $"{cause.GetType().Name}: {cause.Message}";

    private async Task Report(string jobId, int processed, int total, string? current, string state)
    {
        var message = new JobProgressMessage(jobId, JobKind, state, processed, total, current, clock.UtcNow);

        await hub.Clients.All.JobProgress(message).ConfigureAwait(false);
    }

    /// <summary>Counters. Serial pass, so plain fields rather than interlocked ones.</summary>
    private sealed class Tally
    {
        public int Examined;
        public int Written;
        public int Unchanged;
        public int Refused;
        public int Unsupported;
        public int Failed;

        /// <summary>Not on disk. Left alone — an unmounted volume is not an empty library.</summary>
        public int Missing;
    }

    private sealed record PendingCredit(string Name, string? JoinPhrase, Mbid? Mbid);

    /// <summary>
    /// One file's row, flattened out of the entity graph.
    /// </summary>
    /// <remarks>
    /// <see cref="Describe"/> is where it becomes the domain's vocabulary. That
    /// step is here rather than in the projection because <c>CatalogueTagSource</c>
    /// holds strongly-typed ids and lists, and EF can build neither in SQL.
    /// </remarks>
    private sealed record PendingTagWrite(
        MediaFileId Id,
        string Path,
        AcoustId? AcoustId,
        string? TrackTitle,
        string RecordingTitle,
        Mbid? RecordingMbid,
        int TrackNumber,
        int? TrackTotal,
        int DiscNumber,
        int? DiscCount,
        string AlbumTitle,
        Mbid? ReleaseMbid,
        Mbid? ReleaseGroupMbid,
        int? Year,
        Mbid? WorkMbid,
        List<PendingCredit> RecordingCredits,
        List<PendingCredit> ReleaseCredits)
    {
        public CatalogueTagSource Describe() => new()
        {
            TrackTitle = TrackTitle,
            RecordingTitle = RecordingTitle,
            RecordingMbid = RecordingMbid,
            TrackNumber = TrackNumber,
            TrackTotal = TrackTotal,
            DiscNumber = DiscNumber,
            DiscTotal = DiscCount,
            AlbumTitle = AlbumTitle,
            ReleaseMbid = ReleaseMbid,
            ReleaseGroupMbid = ReleaseGroupMbid,
            Year = Year,
            WorkMbid = WorkMbid,
            AcoustId = AcoustId,
            ArtistCredit = Line(RecordingCredits),
            ArtistMbids = Mbids(RecordingCredits),
            AlbumArtistCredit = Line(ReleaseCredits),
            AlbumArtistMbids = Mbids(ReleaseCredits),
        };

        /// <summary>
        /// "Beth Hart &amp; Joe Bonamassa" from the parts that printed it.
        /// </summary>
        /// <remarks>
        /// The endpoints' own helper, called rather than copied: a credit line
        /// rendered one way on the album page and another way in the file's tags
        /// is the kind of disagreement nobody reports as a bug.
        /// </remarks>
        private static string? Line(List<PendingCredit> credits) =>
            CatalogueEndpoints.CreditLine(credits.Select(credit => (credit.Name, credit.JoinPhrase)));

        private static IReadOnlyList<Mbid> Mbids(List<PendingCredit> credits) =>
            [.. credits.Where(credit => credit.Mbid != null).Select(credit => credit.Mbid!.Value)];
    }
}

/// <summary>Which files a run covers.</summary>
/// <remarks>
/// <b>One pass, three scopes, rather than three mechanisms.</b> An album is a
/// dozen files and an artist is a few hundred, so both are quick — but "quick"
/// is not a property of the request, it is a property of the library, and a
/// synchronous endpoint that is fine on twelve files is a two-hour HTTP request
/// on eight thousand. The same background pass, the same gate, the same progress
/// frames and the same status endpoint serve all three.
/// </remarks>
public sealed record TagWriteScope(ReleaseId? Release, ArtistId? Artist, string Label)
{
    public static TagWriteScope Library => new(null, null, "the whole library");

    public static TagWriteScope ForRelease(ReleaseId release, string title) =>
        new(release, null, title);

    public static TagWriteScope ForArtist(ArtistId artist, string name) =>
        new(null, artist, name);
}

public sealed record TagWriteOutcome(TagWriteStartStatus Status, string? JobId);

public enum TagWriteStartStatus
{
    Started,
    AlreadyRunning,
}

public sealed record TagWriteProgress(
    string JobId,
    string Scope,
    int Processed,
    int Total,
    string? CurrentFile);

/// <param name="Unchanged">Files that already said what the catalogue says. Read, never opened for writing.</param>
/// <param name="Refused">
/// Files left alone because <c>Fonoteca:AllowFileMutation</c> is off. The
/// ordinary answer under the default configuration, and not a failure: the plan
/// was computed and journalled, so the run is a complete dry run.
/// </param>
/// <param name="Unsupported">Containers with nowhere to put a custom field — DSD, mostly.</param>
/// <param name="Skipped">Files that were not on disk. Left alone; an unmounted volume is not an empty library.</param>
public sealed record TagWriteSummary(
    string JobId,
    string Scope,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset CompletedAtUtc,
    long DurationMilliseconds,
    int Examined,
    int Written,
    int Unchanged,
    int Refused,
    int Unsupported,
    int Failed,
    int Skipped,
    bool Cancelled);
