using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Fonoteca.Api.Configuration;
using Fonoteca.Api.Endpoints;
using Fonoteca.Api.Logging;
using Fonoteca.Api.Realtime;
using Fonoteca.Data;
using Fonoteca.Domain.Abstractions;
using Fonoteca.Domain.Catalogue;
using Fonoteca.Domain.Events;
using Fonoteca.Ingest;
using Fonoteca.Providers.Wikidata;
using Fonoteca.Tagging;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

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
/// <item><b>A file is on the worklist once its recording and its album are
/// known.</b> With a proven pressing it gets everything, the track number and the
/// release included. Held to its album alone (ADR 0013) it gets what is true of
/// the album — title, artist, album, album artist, year, the recording, artist,
/// album-artist, work and release-group MBIDs and the AcoustID — and no disc,
/// track total or release MBID, because
/// those belong to a pressing nobody proved. Its track number is its place in
/// the folder's settled order (<c>FolderPosition</c>) and its year the album's,
/// each only where the file carries none of its own — or carries the one this
/// pass filled in, which the journal says — and no track number beside a disc
/// number: a rip tagged disc 2 track 1 keeps that rather than becoming disc 2
/// track 14, and a 2008 reissue keeps its date rather than taking the album's
/// 1977.</item>
///
/// <item><b>The chosen sleeve goes out as a file beside the album, and its unit
/// is the folder rather than the file.</b> Everything else here is a tag, and a
/// cover is not one: it is written once per album folder, named for its own
/// image type, and the files themselves are not touched by it. See
/// <see cref="EnsureCoverAsync"/> for why that is the shape and not an embedded
/// picture.</item>
///
/// <item><b>The artist's photograph goes out the same way, onto the shelf named
/// for them.</b> A picture of a person is a fact about nothing in any file, so
/// the shelf is the artist's only where its name is one billed on the album in
/// it — or, from an artist's page, that artist's — because most of a
/// violinist's files sit on composers' shelves. See
/// <see cref="EnsurePortraitAsync"/>.</item>
/// </list>
/// </remarks>
public sealed partial class TagWriteService(
    LibraryWorkGate gate,
    IServiceScopeFactory scopeFactory,
    IHubContext<JobsHub, IJobsClient> hub,
    IHostApplicationLifetime lifetime,
    TagWriterOptions writerOptions,
    FileSystemAudioFileStore store,
    IOptions<FonotecaOptions> options,
    IHttpClientFactory clients,
    IClock clock,
    PlaylistFiles playlists,
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
    /// A person, not <c>SystemCallerContext.SystemId</c>, which is what the
    /// identification pass records for its own tag writes. The account that
    /// pressed the button where one did (<see cref="Start"/>); this constant
    /// only where nobody is known. The difference is the
    /// whole design: that pass runs because a scan finished, and this one runs
    /// because a person pressed a button. A journal that attributed both to the
    /// system would lose the only fact worth keeping about a run that rewrote
    /// eight thousand files.
    /// </remarks>
    private const string Actor = SingleUserCallerContext.OwnerId;

    private static readonly TimeSpan ProgressInterval = TimeSpan.FromMilliseconds(250);

    private CancellationTokenSource? _cancellation;
    private volatile TagWriteProgress? _progress;

    /// <summary>Who the running pass's journal says did it: the owner, or an agent saving tags.</summary>
    /// <remarks>A field rather than a parameter on every step, because the gate admits one run at a time.</remarks>
    private volatile string _actor = Actor;
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
    /// A recording and a proven pressing's track, or a recording and an album
    /// with no pressing, for the reason in the class remarks — a file filed under
    /// a pressing whose track it has lost is neither, and waits.
    /// </remarks>
    public static IQueryable<MediaFile> Answered(IQueryable<MediaFile> files) =>
        files.Where(file =>
            file.TagEditsJson != null
            || (file.RecordingId != null
                && ((file.ReleaseId != null && file.TrackId != null)
                    || (file.ReleaseId == null && file.ReleaseGroupId != null))));

    /// <summary>
    /// The files a run considers: the answered, and every analysed file, whose
    /// ReplayGain and DR are the audio's and need no identity.
    /// </summary>
    /// <remarks>
    /// One expression, used by the counts the buttons show and by the pass itself,
    /// because two copies of it would drift and the visible symptom is a button
    /// offering a number it then does not write. An analysed file with no answer
    /// is given its ReplayGain and DR alone (<see cref="PendingTagWrite.Answered"/>).
    /// </remarks>
    public static IQueryable<MediaFile> Writable(IQueryable<MediaFile> files)
    {
        var answered = Answered(files);

        return files.Where(file => file.MeasuredUtc != null || answered.Any(other => other.Id == file.Id));
    }

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
    /// <param name="actor">Who the journal says did it: the account that pressed the button.</param>
    public TagWriteOutcome Start(TagWriteScope scope, string actor = Actor)
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
                    _lastCompleted = await RunAsync(jobId, scope, cancellation.Token, actor).ConfigureAwait(false);
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

        if (scope.Folder is { } folder)
        {
            var prefix = folder + "/";
            return files.Where(file => file.Path.StartsWith(prefix));
        }

        if (scope.Album is { } album)
        {
            return files.Where(file => file.ReleaseGroupId == album);
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

    /// <param name="actor">Who the journal says did it.</param>
    /// <param name="correlation">The run's journal correlation, where the caller needs to know it.</param>
    private async Task<TagWriteSummary> RunAsync(
        string jobId,
        TagWriteScope scope,
        CancellationToken cancellationToken,
        string actor = Actor,
        string? correlation = null)
    {
        _actor = actor;

        try
        {
            return await PassAsync(jobId, scope, correlation ?? Guid.CreateVersion7().ToString("N")[..12], cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _actor = Actor;
        }
    }

    private async Task<TagWriteSummary> PassAsync(
        string jobId,
        TagWriteScope scope,
        string correlationId,
        CancellationToken cancellationToken)
    {
        var startedAt = clock.UtcNow;
        var elapsed = Stopwatch.StartNew();
        var counts = new Tally();
        var covers = new CoverRun(clock.UtcNow);
        var portraits = new PortraitRun(scope.Artist, clock.UtcNow);

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
                        await HandleAsync(
                                file, counts, covers, portraits, correlationId, cancellationToken)
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

        // After every file, because the unit is the folder and a folder's files
        // are spread across the pages. Not on a stopped run: half a library's
        // folders renamed is a library in two layouts.
        try
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                await RenameAsync(jobId, scope, counts, pending, correlationId, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Between folders. The folder being moved finished.
        }

        // Rule 6: refusing is an answer, and this one is otherwise invisible —
        // the run finishes clean, the panel says nothing, and no picture arrives.
        if (portraits is { Artist: { } named, Shelved: false } && portraits.Subjects.GetValueOrDefault(named) is not null)
        {
            Log.PortraitHasNoShelf(logger, scope.Label);
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
            CoversWritten: counts.CoversWritten,
            MotionWritten: counts.MotionWritten,
            BookletsWritten: counts.BookletsWritten,
            PortraitsWritten: counts.PortraitsWritten,
            Renamed: counts.Renamed,
            NotRenamed: counts.NotRenamed,
            Linked: counts.Linked,
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

            return await ProjectAsync(
                    db,
                    files.OrderBy(file => file.Id).Skip(offset).Take(PageSize),
                    cancellationToken)
                .ConfigureAwait(false);
        }
    }

    /// <summary>Files with everything a tag needs joined, as <see cref="ClaimAsync"/> reads a page.</summary>
    private static async Task<List<PendingTagWrite>> ProjectAsync(
        FonotecaDbContext db,
        IQueryable<MediaFile> files,
        CancellationToken cancellationToken)
    {
        var answered = Answered(db.MediaFiles.AsNoTracking());

        // A pressing's facts where one is filed, the album's where not: its
        // title and first-release year here, its billing line below.
        var page = await files
            .Select(file => new PendingTagWrite(
                file.Id,
                file.Path,
                file.ReleaseId,
                file.ReleaseGroupId,
                file.AcoustId,
                file.Track!.Title,
                file.Recording!.Title,
                file.Recording.Mbid,
                file.Track != null ? (int?)file.Track.Position : file.FolderPosition,
                file.Release!.TrackCount,
                (int?)file.Track!.DiscNumber,
                file.Release.DiscCount,
                file.Release != null ? file.Release.Title : file.ReleaseGroup!.Title,
                file.Release!.Mbid,
                file.ReleaseGroup!.Mbid,
                file.Release != null ? file.Release.ReleasedYear : file.ReleaseGroup!.FirstReleaseYear,
                file.Recording.Work!.Mbid,
                file.TagEditsJson,
                file.ReleaseGroup!.EditsJson,
                file.Release!.EditsJson,
                file.ReleaseGroup!.Genres,
                file.Recording.Genres,
                file.Recording.Isrcs,
                file.Recording.Lyrics!.Synced ?? file.Recording.Lyrics.Plain,
                file.Measurement,
                answered.Any(other => other.Id == file.Id),
                file.Recording.Contributions
                    .OrderBy(link => link.Position)
                    .Select(link => new PendingLink(
                        link.Type,
                        link.Attributes,
                        link.Name,
                        link.ArtistMbid,
                        db.Artists.Where(artist => artist.Mbid == link.ArtistMbid).Select(artist => artist.SortName).FirstOrDefault()))
                    .ToList(),
                file.Recording.Work!.Contributions
                    .OrderBy(link => link.Position)
                    .Select(link => new PendingLink(
                        link.Type,
                        link.Attributes,
                        link.Name,
                        link.ArtistMbid,
                        db.Artists.Where(artist => artist.Mbid == link.ArtistMbid).Select(artist => artist.SortName).FirstOrDefault()))
                    .ToList(),
                file.Recording.Credits
                    .OrderBy(credit => credit.Position)
                    .Select(credit => new PendingCredit(
                        credit.CreditedAs ?? credit.Artist!.Name,
                        credit.JoinPhrase,
                        credit.Artist!.Mbid,
                        credit.Artist!.Name,
                        credit.Artist!.LatinName,
                        credit.Artist!.SortName,
                        credit.Artist!.LinksJson))
                    .ToList(),
                file.Release!.Credits
                    .OrderBy(credit => credit.Position)
                    .Select(credit => new PendingCredit(
                        credit.CreditedAs ?? credit.Artist!.Name,
                        credit.JoinPhrase,
                        credit.Artist!.Mbid,
                        credit.Artist!.Name,
                        credit.Artist!.LatinName,
                        credit.Artist!.SortName,
                        null,
                        credit.ArtistId))
                    .ToList(),
                file.Fingerprint,
                file.TempoAndKey,
                file.Release != null
                    ? new PendingPressing(
                        file.Release.Label,
                        file.Release.CatalogNumber,
                        file.Release.Barcode,
                        file.Release.Country,
                        file.Release.Status,
                        file.Release.Asin,
                        file.Release.Script,
                        file.Track!.Mbid,
                        file.Track.MediumTitle,
                        file.Track.MediumFormat)
                    : null,
                file.ReleaseGroup != null
                    ? new PendingAlbum(file.ReleaseGroup.PrimaryType, file.ReleaseGroup.SecondaryTypes, file.ReleaseGroup.FirstReleaseYear)
                    : null,
                file.Recording.Work != null
                    ? new PendingWork(
                        file.Recording.Work.Title,
                        file.Recording.Work.Languages,
                        file.Recording.Work.Position,
                        file.Recording.Work.Parent!.Title,
                        file.Recording.Work.Parent!.PartCount)
                    : null))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var filed = await AlbumOnlyAsync(db, page, cancellationToken).ConfigureAwait(false);

        // Each file's album folder's ReplayGain and DR, which every file of
        // the folder carries alike — see AlbumLoudness.
        var albums = await AlbumLoudness.ForAsync(db, filed.Select(file => file.Path), cancellationToken).ConfigureAwait(false);

        return [.. filed.Select(file => file with { AlbumLoudness = albums.GetValueOrDefault(AlbumFolder.Of(file.Path)) })];
    }

    /// <summary>
    /// The values this pass put into a file's fields that are still its own, by
    /// field: a fill of an empty field, and each later write that replaced
    /// nothing but the value before it.
    /// </summary>
    private static async Task<Dictionary<string, string?>> FilledAsync(
        FonotecaDbContext db,
        MediaFileId file,
        CancellationToken cancellationToken)
    {
        var subject = file.ToString();

        // An undo's writes are links in the same chain: one that took a fill
        // back out leaves the field nobody's, and one that put an earlier fill
        // back leaves it ours again.
        string[] types = [EventPrefix + ".written", UndoPrefix + ".written"];

        var payloads = await db.DomainEvents
            .AsNoTracking()
            .Where(entry => entry.SubjectId == subject && types.Contains(entry.Type))
            .OrderBy(entry => entry.OccurredAtUtc)
            .Select(entry => entry.PayloadJson)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var ours = new Dictionary<string, string?>(StringComparer.Ordinal);

        foreach (var payload in payloads)
        {
            foreach (var change in JsonSerializer.Deserialize(payload, TaggingJson.Default.TagWritePayload)?.Changes ?? [])
            {
                if (change.Previous is null
                    || (ours.TryGetValue(change.Field, out var before) && before == change.Previous))
                {
                    ours[change.Field] = change.Written;
                }
                else
                {
                    ours.Remove(change.Field);
                }
            }
        }

        return ours;
    }

    /// <summary>
    /// The fields of a file whose correction another program's newer edit
    /// beats, as the container spells them, to the correction's key and the
    /// file's value.
    /// </summary>
    /// <remarks>
    /// Read off the journal: when each key's correction was last saved, and
    /// what this application last wrote into each field and when — a tag
    /// write's or an undo's, the chain <see cref="FilledAsync"/> reads too.
    /// The rule is <see cref="PersonTags.Superseded"/>.
    /// </remarks>
    private static async Task<Dictionary<string, (string Key, string? Value)>> SupersededAsync(
        FonotecaDbContext db,
        PendingTagWrite file,
        LibraryPath path,
        TagWritePlan plan,
        CancellationToken cancellationToken)
    {
        var subject = file.Id.ToString();
        string[] types = [EventPrefix + ".written", UndoPrefix + ".written", PersonSavedEvent];

        var entries = await db.DomainEvents
            .AsNoTracking()
            .Where(entry => entry.SubjectId == subject && types.Contains(entry.Type))
            .OrderBy(entry => entry.OccurredAtUtc)
            .Select(entry => new { entry.Type, entry.OccurredAtUtc, entry.PayloadJson })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var corrected = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
        var written = new Dictionary<string, (DateTimeOffset At, string? Value)>(StringComparer.Ordinal);

        foreach (var entry in entries)
        {
            if (entry.Type == PersonSavedEvent)
            {
                if (JsonSerializer.Deserialize<SavedPayload>(entry.PayloadJson) is not { } saved) continue;

                var was = PersonEdits.Read(saved.Before);
                var now = PersonEdits.Read(saved.After);

                foreach (var key in was.Keys.Union(now.Keys))
                {
                    if (was.TryGetValue(key, out var a) != now.TryGetValue(key, out var b) || a != b)
                    {
                        corrected[key] = entry.OccurredAtUtc;
                    }
                }

                continue;
            }

            foreach (var change in JsonSerializer.Deserialize(entry.PayloadJson, TaggingJson.Default.TagWritePayload)?.Changes ?? [])
            {
                written[change.Field] = (entry.OccurredAtUtc, change.Written);
            }
        }

        var superseded = new Dictionary<string, (string Key, string? Value)>(StringComparer.Ordinal);

        foreach (var key in file.Person.Keys)
        {
            if ((CatalogueTagFields.Spell(path, key) ?? CatalogueTagFields.Custom(path, key)) is not { } field) continue;

            var onFile = plan.Before.Find(field);
            var last = written.TryGetValue(field, out var write) ? write : ((DateTimeOffset At, string? Value)?)null;

            if (PersonTags.Superseded(
                corrected.TryGetValue(key, out var at) ? at : null,
                file.Person[key],
                last?.At,
                last?.Value,
                onFile))
            {
                superseded[field] = (key, onFile);
            }
        }

        return superseded;
    }

    /// <summary>
    /// A page's album-only files, given what the album page shows for their album.
    /// </summary>
    /// <remarks>
    /// The album artist is the display edition's billing line, the album's own
    /// where no edition is stored, and the year is <c>AlbumYear</c> — both the
    /// album page's rules, called rather than copied. The names are the tag writer's own
    /// (credited, else the artist's name, never the Latin alias), which is why
    /// only the display edition's id is taken from the page's rule. A person's
    /// edits to the album are not applied, as they are not for a pressing.
    /// </remarks>
    private static async Task<List<PendingTagWrite>> AlbumOnlyAsync(
        FonotecaDbContext db,
        List<PendingTagWrite> page,
        CancellationToken cancellationToken)
    {
        var albums = page
            .Where(row => row.ReleaseId is null && row.AlbumId is not null)
            .Select(row => row.AlbumId!.Value)
            .Distinct()
            .ToList();

        if (albums.Count == 0) return page;

        var editions = await CatalogueEndpoints.EditionFactsAsync(db, albums, cancellationToken).ConfigureAwait(false);
        var shown = editions.ToDictionary(album => album.Key, album => CatalogueEndpoints.DisplayEdition(album.Value)?.Id);

        var releaseKeys = shown.Values.Where(id => id is not null).ToList();
        var groupKeys = albums.Select(album => (ReleaseGroupId?)album).ToList();

        var billed = (await db.ArtistCredits
                .AsNoTracking()
                .Where(credit => releaseKeys.Contains(credit.ReleaseId) || groupKeys.Contains(credit.ReleaseGroupId))
                .OrderBy(credit => credit.Position)
                .Select(credit => new
                {
                    credit.ReleaseId,
                    credit.ReleaseGroupId,
                    Credit = new PendingCredit(
                        credit.CreditedAs ?? credit.Artist!.Name,
                        credit.JoinPhrase,
                        credit.Artist!.Mbid,
                        credit.Artist!.Name,
                        credit.Artist!.LatinName,
                        credit.Artist!.SortName,
                        null,
                        credit.ArtistId),
                })
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false));

        var byRelease = billed.Where(row => row.ReleaseId is not null).ToLookup(row => row.ReleaseId, row => row.Credit);
        var byGroup = billed.Where(row => row.ReleaseGroupId is not null).ToLookup(row => row.ReleaseGroupId, row => row.Credit);

        return [.. page.Select(row =>
        {
            if (row.ReleaseId is not null || row.AlbumId is not { } album) return row;

            var line = shown[album] is { } display && byRelease[display].Any()
                ? byRelease[display].ToList()
                : byGroup[album].ToList();

            return row with
            {
                Year = CatalogueEndpoints.AlbumYear(row.Year, editions[album]),
                ReleaseCredits = line,
                CoverReleaseId = shown[album],
            };
        })];
    }

    /// <summary>One file: plan it, write it, and record what the bytes became.</summary>
    private async Task HandleAsync(
        PendingTagWrite file,
        Tally counts,
        CoverRun covers,
        PortraitRun portraits,
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

            // A lyrics file beside the track under its name: its words go into
            // the tag where LRCLIB has none, and it goes to the trash once the
            // tag holds the words — see TrashLyricsAsync. A file the catalogue
            // has not answered takes a lyrics file's words, which are its own,
            // and not LRCLIB's, which are part of the answer it waits for.
            var sidecars = LyricsFiles(path);

            if (!file.Answered) file = file with { Lyrics = null };
            if (file.Lyrics is null && SidecarWords(sidecars) is { } words) file = file with { Lyrics = words };

            // A file one of the two libraries cannot parse throws out of here
            // rather than returning: they have to agree about what is in a file
            // before it is changed. The backstop in RunAsync counts it and
            // moves on.
            var person = file.Person;

            var plan = await writer
                .PlanAsync(path, CatalogueTags.For(file.Describe()), person, cancellationToken)
                .ConfigureAwait(false);

            // A field another program changed since a correction was written
            // keeps that edit, and the edit becomes the correction (the owner's
            // choice): the newer one stands. Nothing is written over it.
            if (plan is not null && person.Count > 0
                && await SupersededAsync(db, file, path, plan, cancellationToken).ConfigureAwait(false) is { Count: > 0 } superseded)
            {
                plan = plan with { Changes = [.. plan.Changes.Where(change => !superseded.ContainsKey(change.Field))] };

                var row = await db.MediaFiles
                    .FirstAsync(candidate => candidate.Id == file.Id, cancellationToken)
                    .ConfigureAwait(false);

                var corrections = new Dictionary<string, string?>(PersonEdits.Read(row.TagEditsJson), StringComparer.Ordinal);
                foreach (var (_, (key, value)) in superseded) corrections[key] = value;

                var before = row.TagEditsJson;
                row.TagEditsJson = PersonEdits.Write(corrections);

                await provider.GetRequiredService<IEventLog>()
                    .AppendAsync(
                        DomainEvent.Create(
                            PersonSupersededEvent,
                            TagWriter.FileSubject,
                            file.Id.ToString(),
                            _actor,
                            clock.UtcNow,
                            JsonSerializer.Serialize(new SavedPayload(before, row.TagEditsJson)),
                            correlationId),
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            // Held to its album alone, a file keeps the track number and the date
            // it already carries — both are its pressing's facts, and nothing here
            // knows that pressing — and is given ours only where it has none. Read
            // off the file itself, since a tag the pass could not read is still
            // there. A value this pass filled into an empty field is ours, not the
            // file's, and is updated when the folder's order or the album's year
            // moves; the journal is what tells the two apart. No track number is
            // written beside a disc number, or "disc 2" reads "track 14". A date
            // only TagLib# can read ("17/08/1959") is still the file's own.
            // A number or a year a person set — on the file, or the album's year —
            // is theirs, not the file's to keep.
            if (file.ReleaseId is null && plan is not null)
            {
                var track = CatalogueTagFields.Spell(path, CatalogueTags.TrackNumber);
                var year = CatalogueTagFields.Spell(path, CatalogueTags.Year);
                var disc = CatalogueTagFields.Spell(path, CatalogueTags.DiscNumber);
                var sided = disc is not null && !string.IsNullOrWhiteSpace(plan.Before.Find(disc));

                var dated = plan.Changes.Any(change => change.Field == year && string.IsNullOrWhiteSpace(change.From))
                    && (await provider.GetRequiredService<TagReader>()
                        .ReadWithVerifierAsync(path, cancellationToken: cancellationToken)
                        .ConfigureAwait(false)).RecordedDate is not null;

                var owned = plan.Changes.Any(change =>
                    (change.Field == track || change.Field == year) && !string.IsNullOrWhiteSpace(change.From));
                var filled = owned
                    ? await FilledAsync(db, file.Id, cancellationToken).ConfigureAwait(false)
                    : new Dictionary<string, string?>();

                bool Mine(TagFieldChange change) => file.Typed(change.Field);

                bool Theirs(TagFieldChange change) =>
                    !Mine(change)
                    && !string.IsNullOrWhiteSpace(change.From)
                    && !(filled.TryGetValue(change.Field, out var ours) && ours == change.From);

                plan = plan with
                {
                    Changes = [.. plan.Changes.Where(change => Mine(change)
                        || !((change.Field == year && (dated || Theirs(change)))
                            || (change.Field == track && (sided || Theirs(change)))))],
                };
            }

            var write = await writer
                .ApplyAsync(
                    plan, file.Id.ToString(), correlationId, _actor, EventPrefix, cancellationToken)
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

            if (sidecars.Count > 0
                && writerOptions.AllowFileMutation
                && write.Status is TagWriteStatus.Written or TagWriteStatus.NothingToDo)
            {
                await TrashLyricsAsync(file, sidecars, covers.Stamp, provider, correlationId, cancellationToken)
                    .ConfigureAwait(false);
            }

            // A file here for its ReplayGain and DR alone vouches for no album and no shelf.
            if (file.Answered)
            {
                // The album's sleeve, once per folder rather than once per file, and
                // inside this scope so its journal entry rides the save below.
                await EnsureCoverAsync(file, counts, covers, db, provider, correlationId, cancellationToken)
                    .ConfigureAwait(false);

                // Its motion artwork, the same way and for the same reasons.
                await EnsureMotionAsync(file, counts, covers, db, provider, correlationId, cancellationToken)
                    .ConfigureAwait(false);

                // Its booklets, the same way again.
                await EnsureBookletAsync(file, counts, covers, db, provider, correlationId, cancellationToken)
                    .ConfigureAwait(false);

                // The artist's photograph, once per shelf. Same scope, same reason.
                await EnsurePortraitAsync(
                        file, counts, portraits, db, provider, correlationId, cancellationToken)
                    .ConfigureAwait(false);
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

    /// <summary>
    /// The album's chosen sleeve, written once beside the music as <c>cover.*</c>.
    /// </summary>
    /// <remarks>
    /// <b>Why a file beside the album rather than a picture inside each one.</b>
    /// Every player that reads a library off a disk — Navidrome, Plex, Kodi,
    /// Jellyfin, foobar — looks for <c>cover.*</c> in the album's directory
    /// before it looks inside the audio, so one 100 KB file answers for the whole
    /// folder. Embedding the same image in a dozen FLACs means a dozen container
    /// rewrites through the verified write path, a dozen undo entries carrying
    /// the displaced artwork, and <c>TagSnapshot.PictureDigests</c> — which
    /// exists to prove a tag write did <i>not</i> disturb the pictures — taught
    /// to expect this one change. That is a pass of its own and this is not it.
    ///
    /// <b>Once per album, not once per file, and the album is
    /// <see cref="AlbumFolder"/>'s.</b> A cover is a fact about a folder, so the
    /// first file of each album does the work and the rest skip this entirely.
    /// The cut is the shared one rather than the file's own directory because a
    /// multi-disc rip must get <i>one</i> cover at the album root: what a player
    /// globs beside the audio is a separate and narrower setting — Navidrome's
    /// <c>DiscArtPriority</c> matches <c>disc*</c> and <c>cd*</c> and not
    /// <c>cover.*</c> — so a sleeve written into <c>CD 01</c> is found by
    /// nothing and the embedded picture wins, which is the failure this exists
    /// to end.
    ///
    /// <b>The bytes are the worklist, exactly as the diff is for tags.</b> A
    /// folder already holding this image and nothing else claiming the glob is
    /// read and left alone, so a second run costs one file read per album and
    /// writes nothing.
    ///
    /// <b>Every other <c>cover.*</c> there is displaced, never overwritten.</b>
    /// All of them, not just the name this sleeve would take: a PNG answered
    /// later by a JPEG otherwise leaves both, and a player's glob then has two
    /// matches with the stale one able to win. They go to
    /// <c>Fonoteca:TrashPath</c> under a stamped folder — the file manager's own
    /// convention, and the reason this is reversible. It cannot call
    /// <c>FileManagerService.TrashAsync</c> to do it: that takes
    /// <see cref="LibraryWorkGate"/>, which this pass is holding.
    ///
    /// <b>It never marks the file as failed, and the catch is on
    /// <see cref="Exception"/> for that reason.</b> A folder this process cannot
    /// write to is a cover problem, not a tagging one — and the caller has
    /// already committed the tags but not yet saved the row carrying the new
    /// size and mtime, so anything escaping here would cost that save and hand
    /// the next scan a file it reads as modified. Cancellation is the one thing
    /// let through: the save takes the same token and would throw on it anyway.
    /// </remarks>
    private async Task EnsureCoverAsync(
        PendingTagWrite file,
        Tally counts,
        CoverRun covers,
        FonotecaDbContext db,
        IServiceProvider provider,
        string correlationId,
        CancellationToken cancellationToken)
    {
        // The album, not the file's own directory: a disc folder is not an album
        // and a sleeve written into one is found by nothing.
        var folder = AlbumFolder.Of(file.Path);

        // The pressing's sleeve, else the one the album page draws. Before the
        // folder is claimed: a file with neither has no sleeve to write, and one
        // further down the folder still may.
        if ((file.ReleaseId ?? file.CoverReleaseId) is not { } releaseId) return;

        // The whole album behind the first file is answered by this line.
        if (!covers.Folders.Add(folder)) return;

        // Named for the log until the sleeve's own type names the file.
        var target = folder;

        try
        {
            if (!covers.Sleeves.TryGetValue(releaseId, out var sleeve))
            {
                // `Bytes != null` is the whole filter: a row with no bytes is a
                // stamp saying both sources were asked and neither answered.
                sleeve = await db.ReleaseCovers
                    .AsNoTracking()
                    .Where(cover => cover.ReleaseId == releaseId && cover.Bytes != null)
                    .Select(cover => new StoredCover(cover.Bytes!, cover.MediaType))
                    .FirstOrDefaultAsync(cancellationToken)
                    .ConfigureAwait(false);

                covers.Sleeves[releaseId] = sleeve;
            }

            if (sleeve is null) return;

            // An image type with no filename is one this application would refuse
            // to serve back, so it is not one to write into somebody's library.
            if (FilePreview.ImageExtensionFor(sleeve.MediaType) is not { } extension)
            {
                Log.CoverNotWritten(logger, folder, $"'{sleeve.MediaType}' is not an image with a name");
                return;
            }

            target = folder.Length == 0 ? $"cover.{extension}" : $"{folder}/cover.{extension}";

            // Inside the try: `Resolve` refuses a path reaching outside the root
            // or through a directory symlink, and that is a cover problem.
            var absolute = store.AbsolutePathFor(new LibraryPath(target));

            // Every image the folder already names `cover.*`, the way a player
            // globs for one — not only the name this sleeve would take.
            var directory = Path.GetDirectoryName(absolute)!;
            var rivals = Directory.Exists(directory)
                ? Directory.GetFiles(directory, "cover.*", CoverGlob)
                    .Where(path => FilePreview.Of(path).Kind == PreviewKind.Image)
                    .Order(StringComparer.Ordinal)
                    .ToArray()
                : [];

            // Already ours and alone. Nothing to do, and this is what makes
            // re-running the pass cheap.
            if (rivals is [var only]
                && string.Equals(only, absolute, StringComparison.Ordinal)
                && (await File.ReadAllBytesAsync(only, cancellationToken).ConfigureAwait(false))
                    .AsSpan().SequenceEqual(sleeve.Bytes))
            {
                return;
            }

            if (!writerOptions.AllowFileMutation)
            {
                Log.CoverNotWritten(logger, target, "file mutation is off");
                return;
            }

            var displaced = new List<string>();

            foreach (var rival in rivals)
            {
                var name = Path.GetFileName(rival);

                displaced.Add(Displace(
                    folder.Length == 0 ? name : $"{folder}/{name}",
                    rival,
                    covers.Stamp));
            }

            var staged = await store
                .OpenForCreateAsync(new LibraryPath(target), sleeve.Bytes.Length, cancellationToken)
                .ConfigureAwait(false);

            await using (staged.ConfigureAwait(false))
            {
                await staged.Content.WriteAsync(sleeve.Bytes, cancellationToken).ConfigureAwait(false);
                await staged.CommitAsync(cancellationToken).ConfigureAwait(false);
            }

            counts.CoversWritten++;
            Log.CoverWritten(
                logger,
                target,
                displaced.Count == 0 ? string.Empty : $", displacing {string.Join(", ", displaced)}");

            // Keyed by MediaFileId like every other entry this pass writes,
            // because a path is up to 4096 bytes and SubjectId is 200. The file
            // named is the one whose folder got the cover, which is also the one
            // whose save this entry rides on.
            var payload = JsonSerializer.Serialize(new
            {
                path = target,
                mediaType = sleeve.MediaType,
                bytes = sleeve.Bytes.Length,
                displaced = displaced.Count == 0 ? null : displaced,
            });

            await provider.GetRequiredService<IEventLog>()
                .AppendAsync(
                    DomainEvent.Create(
                        $"{EventPrefix}.cover",
                        TagWriter.FileSubject,
                        file.Id.ToString(),
                        _actor,
                        clock.UtcNow,
                        payload,
                        correlationId),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        // Not a cancellation: the caller's `SaveChanges` takes the same token
        // and would throw on it anyway, so swallowing it here buys nothing and
        // costs a warning line naming a cover problem that is a stopped pass.
        catch (Exception cause) when (cause is not OperationCanceledException)
        {
            Log.CoverNotWritten(logger, target, Because(cause));
        }
    }

    /// <summary>The largest lyrics file read, Navidrome's own limit for a lyrics tag.</summary>
    private const int MaxLyricsFileBytes = 1024 * 1024;

    /// <summary>
    /// The <c>.lrc</c> files beside a track under its name, as
    /// <c>MoveCompanionsAsync</c> finds a companion: same folder, same stem,
    /// and not a link.
    /// </summary>
    private List<(string Relative, string Absolute)> LyricsFiles(LibraryPath path)
    {
        try
        {
            var absolute = store.AbsolutePathFor(path);
            var directory = Path.GetDirectoryName(absolute)!;
            var stem = Path.GetFileNameWithoutExtension(absolute);

            if (!Directory.Exists(directory)) return [];

            return [.. Directory.EnumerateFiles(directory)
                .Where(candidate => Path.GetFileNameWithoutExtension(candidate) == stem
                    && Path.GetExtension(candidate).Equals(".lrc", StringComparison.OrdinalIgnoreCase)
                    && !IsLink(candidate)
                    && new FileInfo(candidate).Length <= MaxLyricsFileBytes)
                .Order(StringComparer.Ordinal)
                .Select(candidate => (Relative(store.Root, candidate), candidate))];
        }
        catch (Exception cause) when (cause is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Log.LyricsFileNotTrashed(logger, path.Value, Because(cause));
            return [];
        }
    }

    /// <summary>
    /// The words of the first lyrics file that holds any, as it holds them.
    /// </summary>
    /// <remarks>
    /// UTF-8, else Latin-1, as a playlist file is read. A file of one or two
    /// lines is the release group's advert, not lyrics (<see cref="LyricsText.IsLyrics"/>).
    /// </remarks>
    private static string? SidecarWords(List<(string Relative, string Absolute)> sidecars)
    {
        foreach (var (_, absolute) in sidecars)
        {
            string text;

            try
            {
                var bytes = File.ReadAllBytes(absolute);

                try
                {
                    text = new System.Text.UTF8Encoding(false, true).GetString(bytes);
                }
                catch (System.Text.DecoderFallbackException)
                {
                    text = System.Text.Encoding.Latin1.GetString(bytes);
                }
            }
            catch (Exception cause) when (cause is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            text = text.TrimStart('\uFEFF').Trim();

            if (LyricsText.IsLyrics(text)) return text;
        }

        return null;
    }

    /// <summary>
    /// The lyrics files beside a track, to the trash, once its tag holds its words.
    /// </summary>
    /// <remarks>
    /// <b>Every one, the owner's choice</b> — the words are in the file's own
    /// tag now, LRCLIB's or the lyrics file's own where LRCLIB had none, and an
    /// advert is nobody's words. Only after a write that left the tag as
    /// planned, so a refused write keeps both.
    ///
    /// <see cref="EnsureCoverAsync"/>'s rules for a file in the way: displaced
    /// to the run's stamped folder in the trash rather than deleted, journalled
    /// so Undo puts it back, and a failure the lyrics file's problem rather
    /// than the track's — the row's new size and mtime still have to be saved.
    /// </remarks>
    private async Task TrashLyricsAsync(
        PendingTagWrite file,
        List<(string Relative, string Absolute)> sidecars,
        string stamp,
        IServiceProvider provider,
        string correlationId,
        CancellationToken cancellationToken)
    {
        foreach (var (relative, absolute) in sidecars)
        {
            try
            {
                var trashed = Displace(relative, absolute, stamp);

                await provider.GetRequiredService<IEventLog>()
                    .AppendAsync(
                        DomainEvent.Create(
                            LyricsEvent,
                            TagWriter.FileSubject,
                            file.Id.ToString(),
                            _actor,
                            clock.UtcNow,
                            JsonSerializer.Serialize(new { path = relative, trashed }),
                            correlationId),
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception cause) when (cause is not OperationCanceledException)
            {
                Log.LyricsFileNotTrashed(logger, relative, Because(cause));
            }
        }
    }

    /// <summary>The name motion artwork is written under.</summary>
    /// <remarks>
    /// The name the most-used Apple Music downloader writes, so a player that
    /// learns to read one library's reads this one's. Never <c>cover.*</c>: a
    /// player globbing for the sleeve would find a video.
    /// </remarks>
    internal const string MotionName = "square_animated_artwork.mp4";

    /// <summary>
    /// The name the tall video was written under until it stopped being kept
    /// (ADR 0014, amended 2026-10-15).
    /// </summary>
    internal const string TallMotionName = "tall_animated_artwork.mp4";

    /// <summary>
    /// The album's motion artwork, written once beside the music as an MP4.
    /// </summary>
    /// <remarks>
    /// <see cref="EnsureCoverAsync"/>'s rules throughout — once per album folder,
    /// nothing with mutation off, whatever is in the way displaced to the trash
    /// rather than overwritten, journalled so Undo takes it back, and a failure
    /// a motion problem rather than a tagging one — with three differences.
    ///
    /// <b>It is the album's</b>, keyed on the release group rather than an
    /// edition, because the shop sells one video per record.
    ///
    /// <b>A video already there is recognised by its length</b>, the way Undo
    /// recognises a sleeve it wrote, rather than read and compared: a row holds
    /// a 25 MB video, and a second run over the library would otherwise read
    /// every one of them from the database and the disk to write nothing.
    ///
    /// <b>Any tall video in the folder is deleted for good</b>, not trashed:
    /// the owner chose to have every <see cref="TallMotionName"/> gone, the
    /// earlier tag writes' and anybody else's alike (ADR 0014, amended
    /// 2026-10-15). The journal records its path and length, in an entry with
    /// nothing written for Undo to take back and nothing displaced to return.
    /// </remarks>
    private async Task EnsureMotionAsync(
        PendingTagWrite file,
        Tally counts,
        CoverRun covers,
        FonotecaDbContext db,
        IServiceProvider provider,
        string correlationId,
        CancellationToken cancellationToken)
    {
        if (file.AlbumId is not { } album) return;

        var folder = AlbumFolder.Of(file.Path);

        if (!covers.MotionFolders.Add(folder)) return;

        string Target(string name) => folder.Length == 0 ? name : $"{folder}/{name}";

        // The sleeve's entry in every field, so Undo's sleeve step puts this
        // back the same way; keyed by the file whose folder got it.
        async Task JournalAsync(object entry) =>
            await provider.GetRequiredService<IEventLog>()
                .AppendAsync(
                    DomainEvent.Create(
                        MotionEvent,
                        TagWriter.FileSubject,
                        file.Id.ToString(),
                        _actor,
                        clock.UtcNow,
                        JsonSerializer.Serialize(entry),
                        correlationId),
                    cancellationToken)
                .ConfigureAwait(false);

        var target = folder;

        try
        {
            // Inside the try: `Resolve` refuses a path reaching outside the
            // root or through a directory symlink.
            target = Target(TallMotionName);
            var tall = store.AbsolutePathFor(new LibraryPath(target));

            if (writerOptions.AllowFileMutation && File.Exists(tall) && !IsLink(tall))
            {
                var gone = new FileInfo(tall).Length;

                File.Delete(tall);

                await JournalAsync(new { path = target, mediaType = "video/mp4", length = gone, deleted = true })
                    .ConfigureAwait(false);
            }

            var length = await db.AlbumMotions
                .AsNoTracking()
                .Where(motion => motion.ReleaseGroupId == album && motion.Square != null)
                .Select(motion => (int?)motion.Square!.Length)
                .FirstOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false);

            if (length is not { } size) return;

            target = Target(MotionName);
            var absolute = store.AbsolutePathFor(new LibraryPath(target));

            if (File.Exists(absolute) && !IsLink(absolute) && new FileInfo(absolute).Length == size) return;

            if (!writerOptions.AllowFileMutation)
            {
                Log.CoverNotWritten(logger, target, "file mutation is off");
                return;
            }

            var bytes = await db.AlbumMotions
                .AsNoTracking()
                .Where(motion => motion.ReleaseGroupId == album)
                .Select(motion => motion.Square)
                .FirstAsync(cancellationToken)
                .ConfigureAwait(false);

            if (bytes is null) return;

            List<string> displaced = File.Exists(absolute) ? [Displace(target, absolute, covers.Stamp)] : [];

            var staged = await store
                .OpenForCreateAsync(new LibraryPath(target), bytes.Length, cancellationToken)
                .ConfigureAwait(false);

            await using (staged.ConfigureAwait(false))
            {
                await staged.Content.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                await staged.CommitAsync(cancellationToken).ConfigureAwait(false);
            }

            Log.CoverWritten(
                logger,
                target,
                displaced.Count == 0 ? string.Empty : $", displacing {string.Join(", ", displaced)}");

            await JournalAsync(new
                {
                    path = target,
                    mediaType = "video/mp4",
                    bytes = bytes.Length,
                    displaced = displaced.Count == 0 ? null : displaced,
                })
                .ConfigureAwait(false);

            counts.MotionWritten++;
        }
        // Not a cancellation, for EnsureCoverAsync's reason.
        catch (Exception cause) when (cause is not OperationCanceledException)
        {
            Log.CoverNotWritten(logger, target, Because(cause));
        }
    }

    /// <summary>
    /// The names an album's booklet files are written under, in order; null
    /// for a type with no name.
    /// </summary>
    /// <remarks>
    /// The archive's pages numbered from <c>01</c> — three digits past 99, so
    /// they sort as they read — and the shop's digital booklet
    /// <c>booklet.pdf</c>, numbered only when it sells more than one. Never a
    /// name a player globs for the sleeve.
    /// </remarks>
    internal static string?[] BookletNames(IReadOnlyList<(string Source, string MediaType)> files)
    {
        var pages = files.Count(file => file.Source == AlbumBookletFile.Archive);
        var pdfs = files.Count - pages;
        var digits = pages > 99 ? "000" : "00";
        int page = 0, pdf = 0;

        return
        [
            .. files.Select(file =>
            {
                var extension = string.Equals(file.MediaType, "application/pdf", StringComparison.OrdinalIgnoreCase)
                    ? "pdf"
                    : FilePreview.ImageExtensionFor(file.MediaType);

                if (extension is null) return null;

                if (file.Source == AlbumBookletFile.Archive)
                {
                    return $"booklet-{(++page).ToString(digits, CultureInfo.InvariantCulture)}.{extension}";
                }

                return pdfs == 1 ? $"booklet.{extension}" : $"booklet-{++pdf}.{extension}";
            }),
        ];
    }

    /// <summary>
    /// The album's booklets, written once beside the music.
    /// </summary>
    /// <remarks>
    /// <see cref="EnsureMotionAsync"/>'s rules — once per album folder, nothing
    /// with mutation off, journalled so Undo takes it back with the sleeve, a
    /// file recognised as already written by its name and length, and a failure
    /// a booklet problem rather than a tagging one — with one difference, the
    /// owner's choice: <b>a booklet a person put there wins.</b> A
    /// <c>booklet*</c> file that is not one of these by name and length is
    /// theirs, and then nothing is written: not beside it, which would be the
    /// same booklet twice, and not over it. So nothing is displaced here.
    ///
    /// Known and accepted: a booklet written by an earlier run whose stored
    /// files have since been replaced by hand reads as a person's, and stops
    /// the album being written until it is moved out of the way.
    /// </remarks>
    private async Task EnsureBookletAsync(
        PendingTagWrite file,
        Tally counts,
        CoverRun covers,
        FonotecaDbContext db,
        IServiceProvider provider,
        string correlationId,
        CancellationToken cancellationToken)
    {
        if (file.AlbumId is not { } album) return;

        var folder = AlbumFolder.Of(file.Path);

        if (!covers.BookletFolders.Add(folder)) return;

        var target = folder;

        try
        {
            var stored = await db.AlbumBookletFiles
                .AsNoTracking()
                .Where(booklet => booklet.ReleaseGroupId == album)
                .OrderBy(booklet => booklet.Position)
                .Select(booklet => new { booklet.Position, booklet.Source, booklet.MediaType, booklet.Bytes.Length })
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            if (stored.Count == 0) return;

            var names = BookletNames([.. stored.Select(booklet => (booklet.Source, booklet.MediaType))]);

            var planned = stored
                .Select((booklet, index) => (booklet.Position, booklet.MediaType, booklet.Length, Name: names[index]))
                .Where(booklet => booklet.Name is not null)
                .ToList();

            if (planned.Count == 0) return;

            string Target(string name) => folder.Length == 0 ? name : $"{folder}/{name}";

            // Inside the try: `Resolve` refuses a path reaching outside the root
            // or through a directory symlink.
            var directory = Path.GetDirectoryName(store.AbsolutePathFor(new LibraryPath(Target(planned[0].Name!))))!;

            var ours = planned.ToDictionary(booklet => booklet.Name!, booklet => booklet.Length, StringComparer.OrdinalIgnoreCase);

            var present = Directory.Exists(directory)
                ? Directory.GetFiles(directory, "booklet*", CoverGlob)
                : [];

            foreach (var existing in present)
            {
                if (IsLink(existing)
                    || !ours.TryGetValue(Path.GetFileName(existing), out var length)
                    || new FileInfo(existing).Length != length)
                {
                    Log.CoverNotWritten(logger, Target(Path.GetFileName(existing)), "the folder has a booklet of its own");
                    return;
                }
            }

            var there = present.Select(Path.GetFileName).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var missing = planned.Where(booklet => !there.Contains(booklet.Name)).ToList();

            if (missing.Count == 0) return;

            if (!writerOptions.AllowFileMutation)
            {
                Log.CoverNotWritten(logger, Target(missing[0].Name!), "file mutation is off");
                return;
            }

            foreach (var booklet in missing)
            {
                target = Target(booklet.Name!);

                var bytes = await db.AlbumBookletFiles
                    .AsNoTracking()
                    .Where(row => row.ReleaseGroupId == album && row.Position == booklet.Position)
                    .Select(row => row.Bytes)
                    .FirstAsync(cancellationToken)
                    .ConfigureAwait(false);

                var staged = await store
                    .OpenForCreateAsync(new LibraryPath(target), bytes.Length, cancellationToken)
                    .ConfigureAwait(false);

                await using (staged.ConfigureAwait(false))
                {
                    await staged.Content.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                    await staged.CommitAsync(cancellationToken).ConfigureAwait(false);
                }

                Log.CoverWritten(logger, target, string.Empty);

                // The sleeve's entry in every field, so Undo's sleeve step takes
                // it back; keyed by the file whose folder got it.
                var payload = JsonSerializer.Serialize(new
                {
                    path = target,
                    mediaType = booklet.MediaType,
                    bytes = bytes.Length,
                    displaced = (string[]?)null,
                });

                await provider.GetRequiredService<IEventLog>()
                    .AppendAsync(
                        DomainEvent.Create(
                            BookletEvent,
                            TagWriter.FileSubject,
                            file.Id.ToString(),
                            _actor,
                            clock.UtcNow,
                            payload,
                            correlationId),
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            counts.BookletsWritten++;
        }
        // Not a cancellation, for EnsureCoverAsync's reason.
        catch (Exception cause) when (cause is not OperationCanceledException)
        {
            Log.CoverNotWritten(logger, target, Because(cause));
        }
    }

    /// <summary>
    /// <c>cover.*</c> the way a player looks for it: this folder only, and
    /// case-insensitively, because <c>Cover.JPG</c> wins the same glob on a
    /// case-sensitive disk and would be left sitting beside the new sleeve.
    /// </summary>
    private static readonly EnumerationOptions CoverGlob = new()
    {
        MatchCasing = MatchCasing.CaseInsensitive,
    };

    /// <summary>The largest portrait worth writing into a library. The cover upload's own cap.</summary>
    private const int MaxPortraitBytes = 10 * 1024 * 1024;

    /// <summary>
    /// How long one picture may take before the pass stops waiting for it.
    /// </summary>
    /// <remarks>
    /// Short, because a stalled CDN would otherwise hold up a run over eight
    /// thousand files for a photograph. Asked once per run, so the whole cost of
    /// getting this wrong is one artist's picture missing until the next run.
    /// </remarks>
    private static readonly TimeSpan PortraitTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The width a provider's picture is asked for at, for a shelf.
    /// </summary>
    /// <remarks>
    /// Through <see cref="PortraitRendition"/>, because an original is no size
    /// at all: one Commons original here is 33.7 MB, over
    /// <see cref="MaxPortraitBytes"/>, and 378 KB at 1280; Qobuz's
    /// <c>large</c> reaches 13.3 MB. 1280 is one of the widths Commons keeps.
    /// </remarks>
    internal const int ShelfWidth = 1280;

    /// <summary>
    /// The artist's photograph, written once beside their records as <c>artist.*</c>.
    /// </summary>
    /// <remarks>
    /// <b>Every button writes it, onto the shelf named for one of the album's
    /// billed artists</b> (the owner's choice, "artist pictures in artist
    /// folders"). A picture of a person is not a fact about any file, so the
    /// claim comes from two facts that are: the album's billing, and the name of
    /// the folder the album sits in, which the rename gives the first-billed
    /// artist. The artist page's button names its artist, and only that
    /// artist's shelf is considered.
    ///
    /// <b>The folder's name is checked, and the measurement is why.</b> An
    /// artist's files are not all on their shelf: 47 of Janine Jansen's 54
    /// files sit under <c>Johann Sebastian Bach</c> and <c>Antonio Vivaldi</c>,
    /// because a classical library files a performance under its composer.
    /// Writing her photograph into every folder her playing reaches would put a
    /// violinist's portrait on two dead composers' shelves. So the folder is
    /// taken only when its name <i>is</i> the artist, by
    /// <see cref="ArtistNameMatch"/> — the same rule that guards the one
    /// portrait source keyed on a name. A shelf no album on it bills by its name
    /// gets nothing, and the first album that does decides it.
    ///
    /// <b>What the catalogue shows is what goes on the shelf, so a picture
    /// already there is displaced.</b> The same rule a sleeve follows, and for
    /// the same reason: this application displays the artist's picture, a person
    /// can change which one it is, and a folder still holding the old one would
    /// make every other player on the disk disagree with the page that chose it.
    /// The alternative — leaving whatever is there — sounds safer and is not: it
    /// makes the folder a one-way door that no picked or uploaded portrait can
    /// ever reach.
    ///
    /// <b>Displaced, never overwritten.</b> Every <c>artist.*</c> the shelf
    /// holds goes to <c>Fonoteca:TrashPath</c> under the run's stamp — all of
    /// them, not only the name this picture would take, or a PNG answered later
    /// by a JPEG leaves two files for a player's glob to choose between. There
    /// is no undo journal carrying image bytes, so the trash is the undo.
    ///
    /// <b>The bytes are the worklist, which costs a request a picture.</b> A
    /// shelf already holding exactly this picture is left untouched — but
    /// proving that means having the picture to compare, so unlike a sleeve,
    /// which is read from the database for nothing, this is fetched before it
    /// can be skipped: once per picture per run, not per file, and only for a
    /// shelf that is the artist's. Over the library that is about two hundred.
    ///
    /// <b>The bytes come over the wire, which nothing else in this pass does.</b>
    /// <c>Artists.PortraitUrl</c> is a URL rather than stored bytes — the browser
    /// fetches it directly — so writing one into a library means downloading it
    /// here. One request per run, from the plain client rather than a provider's:
    /// these are CDN hosts (Qobuz, TheAudioDB, Wikimedia) serving a static image,
    /// not the rate-limited APIs the six <c>RequestGate</c>s exist to protect,
    /// and taking a provider's gate for a picture would queue behind
    /// identification's lookups for no reason.
    ///
    /// It never marks the file as failed, for <see cref="EnsureCoverAsync"/>'s
    /// reason exactly: the tags are already committed and the row carrying the
    /// new size is not yet saved.
    /// </remarks>
    private async Task EnsurePortraitAsync(
        PendingTagWrite file,
        Tally counts,
        PortraitRun portraits,
        FonotecaDbContext db,
        IServiceProvider provider,
        string correlationId,
        CancellationToken cancellationToken)
    {
        // The shelf the album sits on, which is a claim about whose it is and is
        // checked below. Empty for a file too shallow to have one.
        var folder = AlbumFolder.ParentOf(file.Path);

        if (folder.Length == 0 || portraits.Folders.Contains(folder)) return;

        // From an artist's page, theirs; otherwise whichever of the album's
        // billed artists the folder is named for. Bach's shelf is not Janine
        // Jansen's, however much of her playing sits on it.
        var owner = portraits.Artist
            ?? file.ReleaseCredits
                .FirstOrDefault(credit => credit.Artist is not null && ArtistShelf.IsNamedFor(folder, credit.Own, credit.Latin))
                ?.Artist;

        if (owner is not { } artistId) return;

        // Every album on the shelf behind the first one is answered by this
        // line — and both kinds are done inside this one visit, or the portrait
        // would take the gate and the banner would never be reached.
        portraits.Folders.Add(folder);

        PortraitSubject? subject;

        try
        {
            if (!portraits.Subjects.TryGetValue(artistId, out subject))
            {
                subject = await SubjectAsync(db, artistId, cancellationToken).ConfigureAwait(false);
                portraits.Subjects[artistId] = subject;
            }
        }
        catch (Exception cause)
            when (cause is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            Log.PortraitNotWritten(logger, folder, Because(cause));
            return;
        }

        if (subject is null || !subject.Owns(folder)) return;

        // The shelf was found and it is theirs, whatever happens below.
        portraits.Shelved = true;

        foreach (var kind in ArtistImageKind.All)
        {
            await WriteImageAsync(
                    kind, folder, file, counts, portraits, subject, provider, correlationId,
                    cancellationToken)
                .ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The artist, their two pictures, and which of each a person chose.
    /// </summary>
    /// <remarks>
    /// One query for both kinds and both sources. <b>The picked URL, not the raw
    /// column</b>: a person correcting a picture writes it into
    /// <c>EditsJson</c> beside the provider's answer rather than over it (rule
    /// 4), so a pass reading <c>PortraitUrl</c> alone writes the provider's
    /// picture onto the shelf and silently discards the correction. Applied in
    /// memory because <c>PersonEdits</c> parses JSON and EF cannot.
    /// </remarks>
    private static async Task<PortraitSubject?> SubjectAsync(
        FonotecaDbContext db,
        ArtistId artistId,
        CancellationToken cancellationToken)
    {
        var row = await db.Artists
            .AsNoTracking()
            .Where(artist => artist.Id == artistId)
            .Select(artist => new
            {
                artist.Name,
                artist.LatinName,
                artist.PortraitUrl,
                artist.BannerUrl,
                artist.EditsJson,
                Uploaded = db.ArtistImages
                    .Where(upload => upload.ArtistId == artist.Id)
                    .Select(upload => new { upload.Kind, upload.Bytes, upload.MediaType })
                    .ToList(),
            })
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        if (row is null) return null;

        var edits = PersonEdits.Read(row.EditsJson);

        ArtistPicture Picture(ArtistImageKind kind, string? provider)
        {
            var upload = row.Uploaded.Find(candidate => candidate.Kind == kind.Name);

            return new ArtistPicture(
                PersonEdits.Apply(edits, kind.Name, provider), upload?.Bytes, upload?.MediaType);
        }

        return new PortraitSubject(
            row.Name,
            row.LatinName,
            Picture(ArtistImageKind.Portrait, row.PortraitUrl),
            Picture(ArtistImageKind.Banner, row.BannerUrl));
    }

    /// <summary>
    /// One picture, written beside the artist's records under its own name.
    /// </summary>
    /// <remarks>
    /// Its own try, so a banner nobody can fetch does not cost the portrait
    /// beside it — and the filter is the token's rather than the exception's,
    /// for the reason spelled out on the catch itself.
    /// </remarks>
    private async Task WriteImageAsync(
        ArtistImageKind kind,
        string folder,
        PendingTagWrite file,
        Tally counts,
        PortraitRun portraits,
        PortraitSubject subject,
        IServiceProvider provider,
        string correlationId,
        CancellationToken cancellationToken)
    {
        var chosen = subject.For(kind);

        if (!chosen.Any) return;

        // Named for the log until the picture's own type names the file.
        var target = folder;

        try
        {
            if (!writerOptions.AllowFileMutation)
            {
                Log.PortraitNotWritten(logger, folder, "file mutation is off");
                return;
            }

            // A person's picture is already here; only a provider's has to be
            // fetched, and an artist with an upload costs no request at all.
            var picture = chosen.Uploaded
                ?? await PortraitAsync(portraits, PortraitRendition.Sized(chosen.Url!, ShelfWidth), cancellationToken)
                    .ConfigureAwait(false);

            if (picture is null) return;

            // An image type with no filename is one this application would refuse
            // to serve back, so it is not one to write into somebody's library.
            if (FilePreview.ImageExtensionFor(picture.MediaType) is not { } extension)
            {
                Log.PortraitNotWritten(
                    logger, folder, $"'{picture.MediaType}' is not an image with a name");
                return;
            }

            target = $"{folder}/{kind.FileName(extension)}";

            // Inside the try: `Resolve` refuses a path reaching outside the root
            // or through a directory symlink, and that is a picture problem.
            var absolute = store.AbsolutePathFor(new LibraryPath(target));

            // Every image the shelf already names for this kind, the way a
            // player globs for one — not only the name this picture would take,
            // or a PNG answered later by a JPEG leaves both and the stale one
            // can win.
            var directory = Path.GetDirectoryName(absolute)!;
            var rivals = Directory.Exists(directory)
                ? Directory.GetFiles(directory, kind.Glob, CoverGlob)
                    .Where(path => FilePreview.Of(path).Kind == PreviewKind.Image)
                    .Order(StringComparer.Ordinal)
                    .ToArray()
                : [];

            // Already this picture and alone. The bytes are the worklist here as
            // they are for a sleeve — the difference is only that these had to be
            // fetched to be compared, which is one request for the whole run.
            if (rivals is [var only]
                && string.Equals(only, absolute, StringComparison.Ordinal)
                && (await File.ReadAllBytesAsync(only, cancellationToken).ConfigureAwait(false))
                    .AsSpan().SequenceEqual(picture.Bytes))
            {
                return;
            }

            var displaced = new List<string>();

            foreach (var rival in rivals)
            {
                var name = Path.GetFileName(rival);

                displaced.Add(Displace($"{folder}/{name}", rival, portraits.Stamp));
            }

            var staged = await store
                .OpenForCreateAsync(new LibraryPath(target), picture.Bytes.Length, cancellationToken)
                .ConfigureAwait(false);

            await using (staged.ConfigureAwait(false))
            {
                await staged.Content.WriteAsync(picture.Bytes, cancellationToken).ConfigureAwait(false);
                await staged.CommitAsync(cancellationToken).ConfigureAwait(false);
            }

            counts.PortraitsWritten++;
            Log.PortraitWritten(
                logger,
                target,
                displaced.Count == 0 ? string.Empty : $", displacing {string.Join(", ", displaced)}");

            // Keyed by MediaFileId like every other entry this pass writes, and
            // riding the same save. The source is in the payload because it is
            // the only record of which provider's guess this was.
            var payload = JsonSerializer.Serialize(new
            {
                path = target,
                kind = kind.Name,
                mediaType = picture.MediaType,
                bytes = picture.Bytes.Length,
                source = chosen.Uploaded is null ? chosen.Url : "uploaded",
                displaced = displaced.Count == 0 ? null : displaced,
            });

            await provider.GetRequiredService<IEventLog>()
                .AppendAsync(
                    DomainEvent.Create(
                        $"{EventPrefix}.portrait",
                        TagWriter.FileSubject,
                        file.Id.ToString(),
                        _actor,
                        clock.UtcNow,
                        payload,
                        correlationId),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        // <b>Not "is not OperationCanceledException", which is what
        // <see cref="EnsureCoverAsync"/> can afford and this cannot.</b> That
        // one does no network I/O, so the only cancellation it can see is the
        // pass being stopped. This one holds an <c>HttpClient</c> whose
        // <see cref="PortraitTimeout"/> throws <c>TaskCanceledException</c> — an
        // <c>OperationCanceledException</c> — with the caller's token untouched,
        // so the narrower filter let a slow CDN out of here and the damage was
        // three deep: the caller's `SaveChanges` never ran, leaving the row
        // holding the old size against a file whose tags had already been
        // written (rule 2, and the next scan then discards the catalogue on it);
        // the per-file backstop rethrows a cancellation rather than counting it;
        // and `RunAsync` swallowed it into a summary reporting a clean finish,
        // because `Cancelled` reads the token, which nobody had cancelled. What
        // separates the two is the token, so that is what the filter asks about.
        catch (Exception cause)
            when (cause is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            Log.PortraitNotWritten(logger, target, Because(cause));
        }
    }

    /// <summary>
    /// The picture itself, downloaded once however many shelves ask for it.
    /// </summary>
    /// <remarks>
    /// <b>A failed fetch is an answer for the rest of the run.</b> The flag goes
    /// up before the request rather than after it, so a CDN that is down costs
    /// one attempt and not one per folder.
    ///
    /// <c>MaxResponseContentBufferSize</c> is the cap, so a host answering with
    /// something enormous throws out of the read rather than being held in memory
    /// first and measured after.
    /// </remarks>
    private async Task<FetchedImage?> PortraitAsync(
        PortraitRun portraits,
        string url,
        CancellationToken cancellationToken)
    {
        if (portraits.Fetched.TryGetValue(url, out var already)) return already;

        portraits.Fetched[url] = null;

        var (fetched, refused) = await DownloadPortraitAsync(clients, url, cancellationToken).ConfigureAwait(false);

        if (refused is not null) Log.PortraitNotWritten(logger, url, refused);

        portraits.Fetched[url] = fetched;

        return fetched;
    }

    /// <summary>
    /// A provider's picture, or why it was refused. Throws what the request
    /// throws, a timeout included.
    /// </summary>
    /// <remarks>
    /// Shared with Subsonic's <c>getCoverArt</c>, which serves the same
    /// pictures, so one allowlist, cap and timeout hold for both.
    /// </remarks>
    internal static async Task<(FetchedImage? Picture, string? Refused)> DownloadPortraitAsync(
        IHttpClientFactory clients,
        string url,
        CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var address)
            || (address.Scheme != Uri.UriSchemeHttps && address.Scheme != Uri.UriSchemeHttp))
        {
            return (null, "the stored portrait is not an http address");
        }

        var http = clients.CreateClient(WikidataOptions.PicturesClientName);

        http.Timeout = PortraitTimeout;
        http.MaxResponseContentBufferSize = MaxPortraitBytes;

        using var response = await http.GetAsync(address, cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            return (null, $"the picture answered {(int)response.StatusCode}");
        }

        var mediaType = response.Content.Headers.ContentType?.MediaType;

        // The allowlist the upload path holds every cover to, for the same
        // reason: no text/html and no image/svg+xml, both documents that run
        // script, and these bytes go into the library and out to clients.
        if (!FilePreview.IsSafeImageMediaType(mediaType))
        {
            return (null, $"it answered with {mediaType ?? "no type"}");
        }

        return (
            new FetchedImage(
                await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false),
                mediaType),
            null);
    }

    /// <summary>
    /// Moves a cover that is in the way into the trash, and says where it went.
    /// </summary>
    /// <remarks>
    /// The same layout <c>FileManagerService.TrashAsync</c> writes — a stamped
    /// folder holding the library-relative path — so one run's displaced sleeves
    /// land together and can be put back by hand. The stamp is the run's, not
    /// each file's, for exactly that reason.
    /// </remarks>
    private string Displace(string relative, string absolute, string stamp)
    {
        var destination = Path.Combine(
            FileManagerService.TrashRoot(options.Value),
            stamp,
            relative.Replace('/', Path.DirectorySeparatorChar));

        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

        // The log line first: a crash mid-move leaves something naming what was
        // being displaced, which is the only thing that would explain a missing
        // cover afterwards.
        Log.FilesTrashing(logger, relative, destination);
        File.Move(absolute, destination);

        return destination;
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

        /// <summary>Album folders that got a sleeve. Counted per folder, unlike everything above.</summary>
        public int CoversWritten;

        /// <summary>Album folders that got motion artwork, either shape or both. Per folder too.</summary>
        public int MotionWritten;

        /// <summary>Album folders that got booklets. Per folder too.</summary>
        public int BookletsWritten;

        /// <summary>Artist folders that got a photograph. Per folder too, and there is rarely more than one.</summary>
        public int PortraitsWritten;

        /// <summary>Files that moved to the name the pattern gives them, with their folder or alone.</summary>
        public int Renamed;

        /// <summary>Files left where they were because the name was taken or a fact was missing.</summary>
        public int NotRenamed;

        /// <summary>Links made in a collaborator's folder. Per album, like the covers.</summary>
        public int Linked;
    }

    /// <summary>
    /// What one run has already answered about covers.
    /// </summary>
    /// <remarks>
    /// Two caches with the same purpose: a folder is asked once however many
    /// files it holds, and a release is read out of the database once however
    /// many folders it spans. A null value in <see cref="Sleeves"/> is an answer
    /// — "this release has no stored cover" — and not a missing entry, or a
    /// library of albums nobody has chosen a sleeve for queries once per file.
    ///
    /// Per run rather than per pass: a cover chosen between two runs has to be
    /// picked up by the second one.
    /// </remarks>
    private sealed class CoverRun(DateTimeOffset startedAt)
    {
        /// <summary>One stamp for the whole run, so displaced sleeves land together.</summary>
        public string Stamp { get; } =
            startedAt.ToString("yyyy-MM-dd HHmmss", CultureInfo.InvariantCulture);

        public HashSet<string> Folders { get; } = new(StringComparer.Ordinal);

        public Dictionary<ReleaseId, StoredCover?> Sleeves { get; } = [];

        /// <summary>Album folders whose motion artwork this run has already considered.</summary>
        public HashSet<string> MotionFolders { get; } = new(StringComparer.Ordinal);

        /// <summary>Album folders whose booklets this run has already considered.</summary>
        public HashSet<string> BookletFolders { get; } = new(StringComparer.Ordinal);
    }

    /// <summary>A chosen cover as the catalogue holds it.</summary>
    private sealed record StoredCover(byte[] Bytes, string? MediaType);

    /// <summary>
    /// What one run has already answered about the artist's photograph.
    /// </summary>
    /// <remarks>
    /// Three answers cached rather than two, because unlike a sleeve the picture
    /// is not in the database: each artist is read once, each image is
    /// downloaded once, and each shelf is decided once. <see cref="Subjects"/>
    /// and <see cref="Fetched"/> hold a null for "asked and got nothing", so
    /// that and "not looked yet" stay different — a null without them is a
    /// query per file.
    /// </remarks>
    private sealed class PortraitRun(ArtistId? artist, DateTimeOffset startedAt)
    {
        /// <summary>Whose page the button was on, or null for the other two scopes.</summary>
        public ArtistId? Artist { get; } = artist;

        /// <summary>One stamp for the whole run, so a displaced picture lands with the sleeves.</summary>
        public string Stamp { get; } =
            startedAt.ToString("yyyy-MM-dd HHmmss", CultureInfo.InvariantCulture);

        /// <summary>Whether any folder in this run turned out to be an artist's own.</summary>
        public bool Shelved { get; set; }

        /// <summary>The shelves already given an owner this run.</summary>
        public HashSet<string> Folders { get; } = new(StringComparer.Ordinal);

        public Dictionary<ArtistId, PortraitSubject?> Subjects { get; } = [];

        /// <summary>
        /// What each URL answered with.
        /// </summary>
        /// <remarks>
        /// Present-with-null is "asked and got nothing": a CDN that is down has
        /// to cost one attempt for the picture, not one per shelf.
        /// </remarks>
        public Dictionary<string, FetchedImage?> Fetched { get; } = new(StringComparer.Ordinal);
    }

    /// <summary>
    /// The artist a portrait would be written for, and which picture is theirs.
    /// </summary>
    /// <remarks>
    /// <b>An upload outranks whatever a provider found</b>, and it is why this
    /// carries bytes as well as a URL: rule 4 again, a person's answer and a
    /// rule's answer being different facts. The uploaded bytes are already in
    /// the database, so a run that has one downloads nothing at all.
    /// </remarks>
    /// <summary>
    /// The artist a picture would be written for, and which picture is theirs.
    /// </summary>
    /// <remarks>
    /// <b>An upload outranks whatever a provider found</b>, and it is why this
    /// carries bytes as well as a URL: rule 4 again, a person's answer and a
    /// rule's answer being different facts. The uploaded bytes are already in
    /// the database, so a run that has one downloads nothing at all.
    /// </remarks>
    private sealed record PortraitSubject(
        string Name,
        string? LatinName,
        ArtistPicture Portrait,
        ArtistPicture Banner)
    {
        /// <summary>Whether a shelf is named for this artist.</summary>
        public bool Owns(string folder) => ArtistShelf.IsNamedFor(folder, Name, LatinName);

        public ArtistPicture For(ArtistImageKind kind) =>
            kind == ArtistImageKind.Banner ? Banner : Portrait;
    }

    /// <summary>One of an artist's pictures, from both sources at once.</summary>
    private sealed record ArtistPicture(string? Url, byte[]? Bytes, string? MediaType)
    {
        /// <summary>The picture a person handed us, or null to go and fetch one.</summary>
        public FetchedImage? Uploaded =>
            Bytes is { Length: > 0 } bytes ? new FetchedImage(bytes, MediaType) : null;

        /// <summary>Whether either source has anything to write.</summary>
        public bool Any => Uploaded is not null || Url is { Length: > 0 };
    }

    /// <summary>A picture downloaded for this run, with the type the host called it.</summary>
    internal sealed record FetchedImage(byte[] Bytes, string? MediaType);

    /// <param name="Name">As billed: what the tags print.</param>
    /// <param name="Own">The artist's own name, whatever the billing: what a folder is named for.</param>
    /// <param name="Latin">The displayed alias, only to recognise a folder named by it.</param>
    /// <param name="Sort">The artist's sort name: "Davis, Miles".</param>
    /// <param name="Links">The artist's pages elsewhere, read only for the recording's billing — see <see cref="ArtistLinks"/>.</param>
    /// <param name="Artist">The artist's row, on the album's billing only: whose shelf a folder named for them is.</param>
    private sealed record PendingCredit(string Name, string? JoinPhrase, Mbid? Mbid, string Own, string? Latin, string? Sort, string? Links, ArtistId? Artist = null);

    /// <summary>One link MusicBrainz states on the recording or its work, as <see cref="CreditTags"/> reads it.</summary>
    /// <param name="Sort">The sort name of the artist row the link's MBID names, where there is one.</param>
    private sealed record PendingLink(string Type, string? Attributes, string Name, Mbid? Mbid, string? Sort);

    /// <summary>The facts of the pressing a file is filed under, and of its place there.</summary>
    private sealed record PendingPressing(
        string? Label,
        string? CatalogNumber,
        string? Barcode,
        string? Country,
        string? Status,
        string? Asin,
        string? Script,
        Mbid? TrackMbid,
        string? MediumTitle,
        string? MediumFormat);

    /// <summary>The album's own facts, whatever pressing is filed.</summary>
    private sealed record PendingAlbum(string? PrimaryType, string? SecondaryTypes, int? FirstReleaseYear);

    /// <summary>The recording's work, and the whole it is a part of.</summary>
    private sealed record PendingWork(string Title, string? Languages, int? Position, string? ParentTitle, int? PartCount);

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
        ReleaseId? ReleaseId,
        ReleaseGroupId? AlbumId,
        AcoustId? AcoustId,
        string? TrackTitle,
        string? RecordingTitle,
        Mbid? RecordingMbid,
        int? TrackNumber,
        int? TrackTotal,
        int? DiscNumber,
        int? DiscCount,
        string? AlbumTitle,
        Mbid? ReleaseMbid,
        Mbid? ReleaseGroupMbid,
        int? Year,
        Mbid? WorkMbid,
        string? TagEditsJson,
        string? AlbumEditsJson,
        string? PressingEditsJson,
        string? AlbumGenres,
        string? RecordingGenres,
        string? Isrcs,
        string? Lyrics,
        AudioMeasurement? Measurement,
        bool Answered,
        List<PendingLink> RecordingLinks,
        List<PendingLink> WorkLinks,
        List<PendingCredit> RecordingCredits,
        List<PendingCredit> ReleaseCredits,
        string? Fingerprint,
        TempoAndKey? TempoAndKey,
        PendingPressing? Pressing,
        PendingAlbum? AlbumFacts,
        PendingWork? WorkFacts)
    {
        /// <summary>The tags a person set on the file, over everything else.</summary>
        public IReadOnlyDictionary<string, string?> Person => PersonEdits.Read(TagEditsJson);

        /// <summary>
        /// The billing line a person gave the album, which is then the album
        /// artist and the folder it is shelved under, as typed.
        /// </summary>
        public string? AlbumArtistEdit => PersonEdits.Read(AlbumEditsJson).GetValueOrDefault("credit");

        /// <summary>
        /// Whether a person set this field: on the file, or — its year — on the
        /// album or pressing the file is held to.
        /// </summary>
        public bool Typed(string field) =>
            Person.ContainsKey(field)
            || (field == CatalogueTags.Year
                && PersonEdits.Read(ReleaseId is null ? AlbumEditsJson : PressingEditsJson)
                    .ContainsKey(ReleaseId is null ? "firstReleaseYear" : "releasedYear"));

        /// <summary>The tags the file is to carry: the catalogue's, a person's over them.</summary>
        public Dictionary<string, string?> Tags()
        {
            var tags = CatalogueTags.For(Describe()).ToDictionary(tag => tag.Key, string? (tag) => tag.Value, StringComparer.Ordinal);

            foreach (var (field, value) in Person) tags[field] = value;

            return tags;
        }

        /// <summary>
        /// Where no pressing is filed, the album's display edition: the one whose
        /// sleeve the album page draws, and so the one written beside the folder.
        /// </summary>
        public ReleaseId? CoverReleaseId { get; init; }

        /// <summary>The album folder's ReplayGain and DR, or null where it has no album folder.</summary>
        public AlbumLoudness.Figures? AlbumLoudness { get; init; }

        /// <remarks>
        /// A file the catalogue has not answered is on the worklist for its
        /// ReplayGain and DR, and is given nothing else of the catalogue's: what it
        /// would otherwise carry is the partial answer it is waiting to complete.
        /// Its <see cref="Lyrics"/> are a lyrics file's beside it, never LRCLIB's.
        /// </remarks>
        public CatalogueTagSource Describe() => Answered ? Catalogued() : new()
        {
            TrackGain = AudioAnalysis.TrackGain(Measurement),
            TrackPeak = AudioAnalysis.TrackPeak(Measurement),
            AlbumGain = AlbumLoudness?.ReplayGain?.Gain,
            AlbumPeak = AlbumLoudness?.ReplayGain?.Peak,
            DynamicRange = AudioAnalysis.DynamicRange(Measurement),
            AlbumDynamicRange = AlbumLoudness?.DynamicRange,
            Lyrics = Lyrics,
            AcoustIdFingerprint = Fingerprint,
            TrackRange = Measurement?.LoudnessRangeLu,
            TempoAndKey = TempoAndKey,
        };

        private CatalogueTagSource Catalogued() => Movements(new()
        {
            TrackTitle = TrackTitle,
            RecordingTitle = RecordingTitle,
            RecordingMbid = RecordingMbid,
            TrackNumber = TrackNumber,
            TrackTotal = TrackTotal,
            DiscNumber = DiscNumber,
            DiscTotal = DiscCount,

            // A person's corrections to the album: its title and billing line
            // for every file of it, its year where the file is held to the
            // album, the pressing's year where it is filed under a pressing.
            AlbumTitle = PersonEdits.Apply(PersonEdits.Read(AlbumEditsJson), "title", AlbumTitle),
            ReleaseMbid = ReleaseMbid,
            ReleaseGroupMbid = ReleaseGroupMbid,
            Year = Edited(
                ReleaseId is null ? AlbumEditsJson : PressingEditsJson,
                ReleaseId is null ? "firstReleaseYear" : "releasedYear",
                Year),
            WorkMbid = WorkMbid,
            AcoustId = AcoustId,
            ArtistCredit = Line(RecordingCredits),
            ArtistMbids = Mbids(RecordingCredits),
            AlbumArtistCredit = AlbumArtistEdit ?? Line(ReleaseCredits),
            AlbumArtistMbids = Mbids(ReleaseCredits),
            Genres = FileGenres.Of(AlbumGenres, RecordingGenres),
            Isrcs = FileGenres.Split(Isrcs),
            Credits = CreditTags.From(RecordingLinks.Concat(WorkLinks).Select(link => (link.Type, link.Attributes, link.Name))),
            TrackGain = AudioAnalysis.TrackGain(Measurement),
            TrackPeak = AudioAnalysis.TrackPeak(Measurement),
            AlbumGain = AlbumLoudness?.ReplayGain?.Gain,
            AlbumPeak = AlbumLoudness?.ReplayGain?.Peak,
            DynamicRange = AudioAnalysis.DynamicRange(Measurement),
            AlbumDynamicRange = AlbumLoudness?.DynamicRange,
            Lyrics = Lyrics,
            AcoustIdFingerprint = Fingerprint,
            TrackRange = Measurement?.LoudnessRangeLu,
            TempoAndKey = TempoAndKey,

            // A person's own artist or album artist leaves the catalogue's
            // spellings of that billing out, rather than contradict it.
            ArtistSort = Person.ContainsKey(CatalogueTags.Artist) ? null : SortLine(RecordingCredits),
            Artists = Person.ContainsKey(CatalogueTags.Artist) ? [] : [.. RecordingCredits.Select(credit => credit.Name)],
            AlbumArtistSort = AlbumArtistTyped ? null : SortLine(ReleaseCredits),
            AlbumArtists = AlbumArtistTyped ? [] : [.. ReleaseCredits.Select(credit => credit.Name)],
            ComposerSorts = ComposerLinks.All(link => link.Sort is { Length: > 0 }) ? [.. ComposerLinks.Select(link => link.Sort!)] : [],
            ComposerMbids = [.. ComposerLinks.Where(link => link.Mbid is not null).Select(link => link.Mbid!.Value).Distinct()],
            Website = RecordingCredits.FirstOrDefault() is { } first ? ArtistLinks.Homepage(first.Links) : null,
            Compilation = Mbids(ReleaseCredits).Contains(CatalogueTags.VariousArtists),
            ReleaseTypes = AlbumFacts is null ? [] : [.. new[] { AlbumFacts.PrimaryType }.OfType<string>().Concat(FileGenres.Split(AlbumFacts.SecondaryTypes))],
            OriginalYear = AlbumFacts?.FirstReleaseYear,
            Languages = FileGenres.Split(WorkFacts?.Languages),

            // The pressing's, which a file held to its album alone has none of.
            Label = Pressing?.Label,
            CatalogNumber = Pressing?.CatalogNumber,
            Barcode = Pressing?.Barcode,
            ReleaseCountry = Pressing?.Country,
            ReleaseStatus = Pressing?.Status,
            Asin = Pressing?.Asin,
            Script = Pressing?.Script,
            ReleaseTrackMbid = Pressing?.TrackMbid,
            DiscSubtitle = Pressing?.MediumTitle,
            Media = Pressing?.MediumFormat,
        });

        private bool AlbumArtistTyped => AlbumArtistEdit is not null || Person.ContainsKey(CatalogueTags.AlbumArtist);

        /// <summary>The composers each once by name, as <see cref="CreditTags"/> lists them.</summary>
        private List<PendingLink> ComposerLinks =>
            [.. RecordingLinks.Concat(WorkLinks)
                .Where(link => link.Type.Equals("composer", StringComparison.OrdinalIgnoreCase))
                .DistinctBy(link => link.Name, StringComparer.Ordinal)];

        /// <summary>
        /// The work, and where it is a movement of a whole whose order is known,
        /// the whole as the work and the movement beside it.
        /// </summary>
        private CatalogueTagSource Movements(CatalogueTagSource source) =>
            WorkFacts switch
            {
                null => source,
                { ParentTitle: { } whole, Position: > 0 } work => source with
                {
                    Work = whole,
                    MovementName = WorkTags.MovementName(work.Title, whole),
                    Movement = work.Position,
                    MovementTotal = work.PartCount,
                },
                var work => source with { Work = work.Title },
            };

        /// <summary>The billing line in sort names, or null where any artist on it has none.</summary>
        private static string? SortLine(List<PendingCredit> credits) =>
            credits.Count > 0 && credits.All(credit => credit.Sort is { Length: > 0 })
                ? CatalogueEndpoints.CreditLine(credits.Select(credit => (credit.Sort!, credit.JoinPhrase)))
                : null;

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

        private static int? Edited(string? edits, string field, int? stated) =>
            PersonEdits.Read(edits).TryGetValue(field, out var value)
                ? int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var year) ? year : null
                : stated;

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
public sealed record TagWriteScope(ReleaseGroupId? Album, ArtistId? Artist, string Label, string? Folder = null)
{
    public static TagWriteScope Library => new(null, null, "the whole library");

    /// <summary>One album folder: what saving its tags in the editor writes.</summary>
    public static TagWriteScope ForFolder(string folder) => new(null, null, folder, folder);

    public static TagWriteScope ForAlbum(ReleaseGroupId album, string title) =>
        new(album, null, title);

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
/// <param name="CoversWritten">
/// Album folders that got the catalogue's chosen sleeve written beside the music
/// as <c>cover.*</c>, named for the image's own type. <b>Counted per album,
/// unlike every other number here</b>
/// — a twelve-track album is twelve examined files and one cover — so it is
/// deliberately not part of any total.
/// </param>
/// <param name="PortraitsWritten">
/// Artist folders that got the artist's photograph written beside their records
/// as <c>artist.*</c>. <b>Per folder like the covers, and all but always one or
/// zero</b>: only the artist button writes these, and it writes to the one shelf
/// named for that artist.
/// </param>
/// <param name="MotionWritten">
/// Album folders that got their motion artwork written beside the music, as
/// <c>square_animated_artwork.mp4</c>. Per folder like the covers.
/// </param>
/// <param name="BookletsWritten">
/// Album folders that got their booklets written beside the music, as
/// <c>booklet-01.jpg</c> onwards and <c>booklet.pdf</c>. Per folder like the covers.
/// </param>
/// <param name="Renamed">
/// Files moved to where <c>Fonoteca:FileNaming</c> puts them, with their album
/// folder or on their own. With <c>Fonoteca:AllowFileMutation</c> off, the files
/// that would have moved.
/// </param>
/// <param name="NotRenamed">
/// Files left where they were: the name was taken, or the catalogue lacks a fact
/// the pattern needs. The log names each one.
/// </param>
/// <param name="Linked">
/// Links made in a collaborator's folder to an album filed under its
/// first-billed artist. Per album, like the covers.
/// </param>
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
    int CoversWritten,
    int MotionWritten,
    int BookletsWritten,
    int PortraitsWritten,
    int Renamed,
    int NotRenamed,
    int Linked,
    bool Cancelled);
