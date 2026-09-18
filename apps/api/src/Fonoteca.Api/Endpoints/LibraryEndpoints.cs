using Fonoteca.Api.Configuration;
using Fonoteca.Api.Library;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Options;

namespace Fonoteca.Api.Endpoints;

/// <summary>
/// The library on disk, and what the catalogue knows about it.
/// </summary>
/// <remarks>
/// Same shape as <see cref="SystemEndpoints"/>: one static class per
/// capability, one <c>MapXEndpoints</c> extension, registered from Program.
///
/// Two operations, one resource each: a POST that changes it and a GET that
/// reads it.
///
/// <b>Scan answers when it has finished. Identify answers immediately.</b> That
/// asymmetry is the honest one rather than an inconsistency. A scan is seconds
/// of walking and stat'ing, so the response can be the result; identification
/// opens every file, spawns a subprocess for each and then queues behind
/// AcoustID's three-per-second limit, so the response is a job id and the work
/// reports on <c>JobsHub</c>.
///
/// They are separate endpoints, not phases of one, because their risk profiles
/// are opposite: the scan reads no file contents and modifies nothing, while
/// identification rewrites the user's files. Fused, there would be no way to run
/// the safe one alone. <c>Fonoteca:IdentifyAfterScan</c> chains them instead, so
/// scanning still leads to identifying without the two being welded together.
/// </remarks>
public static class LibraryEndpoints
{
    public static IEndpointRouteBuilder MapLibraryEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/library").WithTags("Library");

        group.MapPost("/scan", ScanLibrary)
            .WithName("ScanLibrary")
            .WithSummary("Reconcile the catalogue's file list with the files on disk.")
            .WithDescription(
                "Walks the library root, adding files that are new, updating those whose size or "
                + "modification time changed, and removing rows for files that are gone. Reads no "
                + "file contents and modifies no file.")
            // Declared, not inferred: a Results<Ok<T>, ProblemHttpResult> union
            // tells the generator that a problem is possible but not which
            // status codes it uses, and an undeclared 409 is a client that
            // treats "already running" as an unexpected failure.
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        group.MapGet("/scan", GetLibraryScanStatus)
            .WithName("GetLibraryScanStatus")
            .WithSummary("Whether a scan is running, and what the last one found.");

        group.MapPost("/identify", StartIdentification)
            .WithName("StartLibraryIdentification")
            .WithSummary("Fingerprint the files with no AcoustID and look them up.")
            .WithDescription(
                "Returns immediately with a job id; progress arrives on the jobs hub. This pass "
                + "opens every file that has not been identified, runs fpcalc on it and asks "
                + "AcoustID, which enforces three requests a second — so a first pass over a large "
                + "library is a matter of tens of minutes, not seconds. Tags are only written when "
                + "Fonoteca:AllowFileMutation is enabled; with it off the pass still fingerprints "
                + "and identifies everything, so enabling it and running again costs no further "
                + "lookups.")
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        group.MapGet("/identify", GetIdentificationStatus)
            .WithName("GetLibraryIdentificationStatus")
            .WithSummary("How many files still have no AcoustID, and how the last pass went.");

        group.MapDelete("/identify", CancelIdentification)
            .WithName("CancelLibraryIdentification")
            .WithSummary("Ask the running pass to stop after the file it is on.")
            .ProducesProblem(StatusCodes.Status409Conflict);

        group.MapPost("/enrich", StartEnrichment)
            .WithName("StartLibraryEnrichment")
            .WithSummary("Ask MusicBrainz what the identified files are, and file them under artists.")
            .WithDescription(
                "Returns immediately with a job id; progress arrives on the jobs hub. Takes each "
                + "identified file's stored fingerprint back to AcoustID for the MusicBrainz "
                + "recording it names, then fetches that recording and its work — so a first pass "
                + "over a large library is tens of minutes, paced by AcoustID's three requests a "
                + "second. Opens no file and modifies none, so it runs even with the library "
                + "volume unmounted.")
            .ProducesProblem(StatusCodes.Status409Conflict);

        group.MapGet("/enrich", GetEnrichmentStatus)
            .WithName("GetLibraryEnrichmentStatus")
            .WithSummary("How many identified files have no recording yet, and how the last pass went.");

        group.MapDelete("/enrich", CancelEnrichment)
            .WithName("CancelLibraryEnrichment")
            .WithSummary("Ask the running pass to stop after the file it is on.")
            .ProducesProblem(StatusCodes.Status409Conflict);

        group.MapPost("/probe", StartProbe)
            .WithName("StartLibraryProbe")
            .WithSummary("Measure every file: codec, depth, rate, and whether it still decodes.")
            .WithDescription(
                "Returns immediately with a job id; progress arrives on the jobs hub. Decodes each "
                + "file rather than reading its header, which is what lets it answer both what the "
                + "audio is and whether it is intact — measured at 0.63s a file, so roughly twenty "
                + "minutes for eight thousand at the default concurrency. Fills MediaFile.Quality, "
                + "which is what the upgrade list needs to see a CD-quality rip against a hi-res "
                + "master. Writes no file and needs the library volume mounted.")
            .ProducesProblem(StatusCodes.Status409Conflict);

        group.MapGet("/probe", GetProbeStatus)
            .WithName("GetLibraryProbeStatus")
            .WithSummary("How many files have never been measured, and what the last pass found.");

        group.MapDelete("/probe", CancelProbe)
            .WithName("CancelLibraryProbe")
            .WithSummary("Ask the running pass to stop after the page it is on.")
            .ProducesProblem(StatusCodes.Status409Conflict);

        group.MapPost("/attribute", StartAttribution)
            .WithName("StartReleaseAttribution")
            .WithSummary("Work out which album each identified file came from.")
            .WithDescription(
                "Returns immediately with a job id; progress arrives on the jobs hub. Decides files "
                + "in sets rather than one at a time — a single file cannot name its release, since "
                + "one recording appears on the album, on compilations and on every regional "
                + "pressing. The set is one album folder: the folder's boundary is taken as the "
                + "grouping, its name is never read, and the release is decided from the audio. "
                + "Opens no file and modifies none.")
            .ProducesProblem(StatusCodes.Status409Conflict);

        group.MapGet("/attribute", GetAttributionStatus)
            .WithName("GetReleaseAttributionStatus")
            .WithSummary("How many files have no album yet, and how the last pass went.");

        group.MapDelete("/attribute", CancelAttribution)
            .WithName("CancelReleaseAttribution")
            .WithSummary("Ask the running pass to stop after the set it is on.")
            .ProducesProblem(StatusCodes.Status409Conflict);

        group.MapPost("/tags", StartTagWrite)
            .WithName("StartLibraryTagWrite")
            .WithSummary("Write everything the catalogue knows back into the library's files.")
            .WithDescription(
                "Returns immediately with a job id; progress arrives on the jobs hub. The last "
                + "step of the chain and the only one that is never automatic: scan, identify, "
                + "enrich and attribute all write to a database, and this is what makes their "
                + "answers portable. Writes title, artist, album, album artist, track and disc "
                + "numbers, the year and every MusicBrainz identifier into each file that has a "
                + "recording, a track and a release. Each file is rendered to a staged sibling, "
                + "read back by two independent tag libraries and length-checked before the swap, "
                + "and the previous values are journalled. With Fonoteca:AllowFileMutation off "
                + "the whole run happens except the write. Same pass as the per-album and "
                + "per-artist buttons, with no scope.")
            .ProducesProblem(StatusCodes.Status409Conflict);

        group.MapGet("/tags", GetTagWriteStatus)
            .WithName("GetLibraryTagWriteStatus")
            .WithSummary("Whether a tag write is running, how many files it could cover, and how the last one went.");

        group.MapDelete("/tags", CancelTagWrite)
            .WithName("CancelLibraryTagWrite")
            .WithSummary("Ask the running tag write to stop after the file it is on.")
            .ProducesProblem(StatusCodes.Status409Conflict);

        return app;
    }

    /// <summary>
    /// The library-wide tag write.
    /// </summary>
    /// <remarks>
    /// No 503 arm, unlike the scan: the pass stats each file and leaves the ones
    /// that are not there alone, so an unmounted volume produces a run that
    /// writes nothing rather than a refusal. That is the same posture the probe
    /// takes and for the same reason — refusing to start needs certainty this
    /// endpoint does not have.
    /// </remarks>
    private static Task<Results<Accepted<TagWriteStartedResponse>, ProblemHttpResult>> StartTagWrite(
        TagWriteService tags,
        CancellationToken cancellationToken) =>
        CatalogueEndpoints.StartAsync(tags, TagWriteScope.Library, cancellationToken);

    internal static async Task<Ok<TagWriteStatusResponse>> GetTagWriteStatus(
        TagWriteService tags,
        CancellationToken cancellationToken)
    {
        var progress = tags.Progress;

        return TypedResults.Ok(new TagWriteStatusResponse(
            Running: tags.IsRunning,
            JobId: progress?.JobId,
            Scope: progress?.Scope,
            Processed: progress?.Processed ?? 0,
            Total: progress?.Total ?? 0,
            CurrentFile: progress?.CurrentFile,

            // The library-wide count, whatever scope is running: this is the
            // number the dashboard card is about, and a card that changed its
            // meaning while an album-scoped run was in flight would be unreadable.
            Files: await tags.CountAsync(TagWriteScope.Library, cancellationToken).ConfigureAwait(false),
            WillWrite: tags.MutationAllowed,
            LastError: tags.LastError,
            LastCompleted: tags.LastCompleted));
    }

    private static Results<Accepted, ProblemHttpResult> CancelTagWrite(TagWriteService tags) =>
        tags.Cancel()
            ? TypedResults.Accepted("/api/library/tags")
            : TypedResults.Problem(
                title: "Nothing to cancel",
                detail: "No tag write is running.",
                statusCode: StatusCodes.Status409Conflict);

    internal static async Task<Results<Ok<LibraryScanSummary>, ProblemHttpResult>> ScanLibrary(
        LibraryScanService scans,
        IdentificationService identification,
        IOptions<FonotecaOptions> options,
        CancellationToken cancellationToken)
    {
        var outcome = await scans.ScanAsync(cancellationToken).ConfigureAwait(false);

        // "During scanning, identify the files that have no AcoustID yet." The
        // scan itself stays synchronous and honest — it is seconds of walking —
        // and hands off to the pass that is not. Started after the scan releases
        // the gate, and only when the scan actually did something, so a rescan of
        // an identified library does not keep launching passes with no work.
        if (options.Value.IdentifyAfterScan && outcome.Status == LibraryScanStatus.Completed)
        {
            identification.Start();
        }

        return outcome switch
        {
            { Status: LibraryScanStatus.Completed, Summary: { } summary } =>
                TypedResults.Ok(summary),

            { Status: LibraryScanStatus.AlreadyRunning } => TypedResults.Problem(
                title: "A scan is already running",
                detail: "Only one scan runs at a time. Poll GET /api/library/scan for its result.",
                statusCode: StatusCodes.Status409Conflict),

            // 503, not 500: the configured root is missing, which is a mount
            // that has not come back rather than a bug, and it is very likely
            // to be true again in a minute.
            _ => TypedResults.Problem(
                title: "The library root is not available",
                detail: "The configured Fonoteca:LibraryPath does not exist. "
                    + "Nothing was read, and the catalogue was left untouched.",
                statusCode: StatusCodes.Status503ServiceUnavailable),
        };
    }

    internal static Ok<LibraryScanStatusResponse> GetLibraryScanStatus(LibraryScanService scans) =>
        TypedResults.Ok(new LibraryScanStatusResponse(scans.IsRunning, scans.LastCompleted));

    /// <summary>
    /// Starts the identification pass. 202, because it will not be finished for
    /// a long time.
    /// </summary>
    /// <remarks>
    /// A separate endpoint from the scan rather than a phase of it. The two have
    /// opposite risk profiles — the scan opens no file and modifies none, while
    /// this spawns a subprocess per file, holds a third party's rate limit for
    /// most of an hour and rewrites the user's files. Fused together, there would
    /// be no way to run the safe one without the dangerous one, which is the
    /// opposite of the posture ADR 0002 takes. Chained instead, by
    /// <c>Fonoteca:IdentifyAfterScan</c>, so scanning still leads to identifying.
    /// </remarks>
    internal static async Task<Results<Accepted<IdentificationStartedResponse>, ProblemHttpResult>>
        StartIdentification(IdentificationService identification, CancellationToken cancellationToken)
    {
        var pending = await identification.CountPendingAsync(cancellationToken).ConfigureAwait(false);
        var outcome = identification.Start();

        return outcome switch
        {
            { Status: IdentificationStatus.Started, JobId: { } jobId } => TypedResults.Accepted(
                "/api/library/identify",
                new IdentificationStartedResponse(jobId, pending)),

            { Status: IdentificationStatus.AlreadyRunning } => TypedResults.Problem(
                title: "The library is already busy",
                detail: "A scan or an identification pass is running. Only one at a time touches "
                    + "the catalogue. Poll GET /api/library/identify.",
                statusCode: StatusCodes.Status409Conflict),

            _ => TypedResults.Problem(
                title: "The library root is not available",
                detail: "The configured Fonoteca:LibraryPath does not exist. Nothing was read.",
                statusCode: StatusCodes.Status503ServiceUnavailable),
        };
    }

    internal static async Task<Ok<IdentificationStatusResponse>> GetIdentificationStatus(
        IdentificationService identification,
        CancellationToken cancellationToken)
    {
        var progress = identification.Progress;

        return TypedResults.Ok(new IdentificationStatusResponse(
            Running: identification.IsRunning,
            JobId: progress?.JobId,
            Processed: progress?.Processed ?? 0,
            Total: progress?.Total ?? 0,
            CurrentFile: progress?.CurrentFile,
            Pending: await identification.CountPendingAsync(cancellationToken).ConfigureAwait(false),
            WritesTags: identification.WritesTags,
            LastCompleted: identification.LastCompleted));
    }

    internal static Results<Accepted, ProblemHttpResult> CancelIdentification(
        IdentificationService identification) =>
        identification.Cancel()
            ? TypedResults.Accepted("/api/library/identify")
            : TypedResults.Problem(
                title: "Nothing to cancel",
                detail: "No identification pass is running.",
                statusCode: StatusCodes.Status409Conflict);

    /// <summary>
    /// Starts the enrichment pass. A third endpoint rather than a phase of
    /// identification, and for a reason that is not symmetry.
    /// </summary>
    /// <remarks>
    /// Identification writes to the user's files; this only reads the catalogue
    /// and talks to two web services. Keeping them apart means the safe half can
    /// be re-run after a rule change without the dangerous half running again —
    /// and a library whose volume is unmounted can still be enriched, because
    /// every fingerprint it needs is already stored.
    ///
    /// No 503 arm: unlike the other two, this pass never touches the library
    /// root, so its absence is not a reason to refuse.
    /// </remarks>
    internal static async Task<Results<Accepted<EnrichmentStartedResponse>, ProblemHttpResult>>
        StartEnrichment(EnrichmentService enrichment, CancellationToken cancellationToken)
    {
        var pending = (await enrichment.CountPendingAsync(cancellationToken).ConfigureAwait(false))
            .Total;

        var outcome = enrichment.Start();

        return outcome switch
        {
            { Status: EnrichmentStatus.Started, JobId: { } jobId } => TypedResults.Accepted(
                "/api/library/enrich",
                new EnrichmentStartedResponse(jobId, pending)),

            _ => TypedResults.Problem(
                title: "The library is already busy",
                detail: "A scan, an identification pass or an enrichment pass is running. Only one "
                    + "at a time touches the catalogue. Poll GET /api/library/enrich.",
                statusCode: StatusCodes.Status409Conflict),
        };
    }

    internal static async Task<Ok<EnrichmentStatusResponse>> GetEnrichmentStatus(
        EnrichmentService enrichment,
        CancellationToken cancellationToken)
    {
        var progress = enrichment.Progress;

        var pending = await enrichment.CountPendingAsync(cancellationToken).ConfigureAwait(false);

        return TypedResults.Ok(new EnrichmentStatusResponse(
            Running: enrichment.IsRunning,
            JobId: progress?.JobId,
            Processed: progress?.Processed ?? 0,
            Total: progress?.Total ?? 0,
            CurrentFile: progress?.CurrentFile,
            Pending: pending.Total,
            PendingFiles: pending.Files,
            PendingArtists: pending.Artists,
            PendingPortraits: pending.Portraits,
            PendingDiscographies: pending.Discographies,
            LastCompleted: enrichment.LastCompleted));
    }

    internal static Results<Accepted, ProblemHttpResult> CancelEnrichment(EnrichmentService enrichment) =>
        enrichment.Cancel()
            ? TypedResults.Accepted("/api/library/enrich")
            : TypedResults.Problem(
                title: "Nothing to cancel",
                detail: "No enrichment pass is running.",
                statusCode: StatusCodes.Status409Conflict);

    /// <summary>
    /// Starts the pass that measures the library.
    /// </summary>
    /// <remarks>
    /// Unlike enrichment and attribution this one <i>does</i> open every file, so
    /// an unmounted volume makes it fail on the first row rather than run
    /// harmlessly. It is not refused up front for that: the probe reports a
    /// missing file as unreadable, and refusing on a directory check would be a
    /// second, worse copy of the same test.
    /// </remarks>
    internal static async Task<Results<Accepted<ProbeStartedResponse>, ProblemHttpResult>>
        StartProbe(ProbeService probes, CancellationToken cancellationToken)
    {
        var pending = await probes.CountPendingAsync(cancellationToken).ConfigureAwait(false);
        var outcome = probes.Start();

        return outcome switch
        {
            { Status: ProbeStatus.Started, JobId: { } jobId } => TypedResults.Accepted(
                "/api/library/probe",
                new ProbeStartedResponse(jobId, pending)),

            _ => TypedResults.Problem(
                title: "The library is already busy",
                detail: "A scan or another pass is running. Only one at a time touches the "
                    + "catalogue. Poll GET /api/library/probe.",
                statusCode: StatusCodes.Status409Conflict),
        };
    }

    internal static async Task<Ok<ProbeStatusResponse>> GetProbeStatus(
        ProbeService probes,
        CancellationToken cancellationToken)
    {
        var progress = probes.Progress;

        return TypedResults.Ok(new ProbeStatusResponse(
            Running: probes.IsRunning,
            JobId: progress?.JobId,
            Processed: progress?.Processed ?? 0,
            Total: progress?.Total ?? 0,
            CurrentFile: progress?.CurrentFile,

            // Pending comes out of the coverage read rather than a second query
            // beside it: two reads of a moving library disagree, and the card
            // shows both numbers.
            Coverage: await probes.CoverageAsync(cancellationToken).ConfigureAwait(false),
            LastError: probes.LastError,
            LastCompleted: probes.LastCompleted));
    }

    internal static Results<Accepted, ProblemHttpResult> CancelProbe(ProbeService probes) =>
        probes.Cancel()
            ? TypedResults.Accepted("/api/library/probe")
            : TypedResults.Problem(
                title: "Nothing to cancel",
                detail: "No probe pass is running.",
                statusCode: StatusCodes.Status409Conflict);

    /// <summary>
    /// Starts the attribution pass, which needs enrichment to have run first.
    /// </summary>
    /// <remarks>
    /// Like enrichment, no 503 arm: it reads fingerprint durations and recording
    /// links out of the catalogue and never opens a file, so an unmounted library
    /// volume is no reason to refuse.
    /// </remarks>
    internal static async Task<Results<Accepted<AttributionStartedResponse>, ProblemHttpResult>>
        StartAttribution(ReleaseAttributionService attribution, CancellationToken cancellationToken)
    {
        var pending = await attribution.CountPendingAsync(cancellationToken).ConfigureAwait(false);
        var outcome = attribution.Start();

        return outcome switch
        {
            { Status: AttributionStatus.Started, JobId: { } jobId } => TypedResults.Accepted(
                "/api/library/attribute",
                new AttributionStartedResponse(jobId, pending)),

            _ => TypedResults.Problem(
                title: "The library is already busy",
                detail: "A scan, an identification, an enrichment or an attribution pass is "
                    + "running. Only one at a time touches the catalogue. Poll "
                    + "GET /api/library/attribute.",
                statusCode: StatusCodes.Status409Conflict),
        };
    }

    internal static async Task<Ok<AttributionStatusResponse>> GetAttributionStatus(
        ReleaseAttributionService attribution,
        CancellationToken cancellationToken)
    {
        var progress = attribution.Progress;

        return TypedResults.Ok(new AttributionStatusResponse(
            Running: attribution.IsRunning,
            JobId: progress?.JobId,
            Processed: progress?.Processed ?? 0,
            Total: progress?.Total ?? 0,
            CurrentFile: progress?.Current,
            Pending: await attribution.CountPendingAsync(cancellationToken).ConfigureAwait(false),
            LastCompleted: attribution.LastCompleted));
    }

    internal static Results<Accepted, ProblemHttpResult> CancelAttribution(
        ReleaseAttributionService attribution) =>
        attribution.Cancel()
            ? TypedResults.Accepted("/api/library/attribute")
            : TypedResults.Problem(
                title: "Nothing to cancel",
                detail: "No attribution pass is running.",
                statusCode: StatusCodes.Status409Conflict);
}

/// <summary>Current scan state. <c>LastCompleted</c> is null until one has run.</summary>
public sealed record LibraryScanStatusResponse(bool Running, LibraryScanSummary? LastCompleted);

/// <summary>A pass was accepted, with the size of the job it took on.</summary>
public sealed record IdentificationStartedResponse(string JobId, int Pending);

/// <summary>
/// Everything the identification card needs, in one read.
/// </summary>
/// <remarks>
/// The hub carries progress, but a client that reconnects has to be able to
/// re-read the current state rather than assume it caught every message — which
/// is what <c>JobsHub</c>'s own documentation says the hub is for. This is that
/// read.
/// </remarks>
public sealed record IdentificationStatusResponse(
    bool Running,
    string? JobId,
    int Processed,
    int Total,
    string? CurrentFile,

    /// <summary>Files that have never been asked about.</summary>
    int Pending,

    /// <summary>Whether Fonoteca:AllowFileMutation lets this pass write anything.</summary>
    bool WritesTags,

    IdentificationSummary? LastCompleted);

/// <summary>An enrichment pass was accepted, with the size of the job it took on.</summary>
public sealed record EnrichmentStartedResponse(string JobId, int Pending);

public sealed record ProbeStartedResponse(string JobId, int Pending);

/// <param name="Coverage">What the library measures to, across every pass that has run.</param>
/// <param name="LastError">
/// Why the last pass stopped without finishing. Null on the ordinary path — and
/// the difference between a button that failed and a button that did nothing,
/// which for this pass is usually ffprobe missing from PATH.
/// </param>
public sealed record ProbeStatusResponse(
    bool Running,
    string? JobId,
    int Processed,
    int Total,
    string? CurrentFile,
    ProbeCoverage Coverage,
    string? LastError,
    ProbeSummary? LastCompleted);

/// <summary>Everything the enrichment card needs, in one read.</summary>
public sealed record EnrichmentStatusResponse(
    bool Running,
    string? JobId,
    int Processed,
    int Total,
    string? CurrentFile,

    /// <summary>
    /// Everything left to ask about — the sum of the two below.
    /// </summary>
    /// <remarks>
    /// Still here, and still the total, because "is there anything to do" is
    /// what the button's enable rule asks and that question did not change when
    /// the pass grew an artist stage. What did change is that the total is no
    /// longer describable in one noun, which is why the parts travel with it.
    /// </remarks>
    int Pending,

    /// <summary>Identified files that have never been asked what they are.</summary>
    int PendingFiles,

    /// <summary>Artists MusicBrainz has never been asked to describe.</summary>
    int PendingArtists,

    /// <summary>
    /// Artists nobody has looked for a picture of.
    /// </summary>
    /// <remarks>
    /// The third noun, and it is on the wire for the reason the second one is:
    /// the moment a stage joins <see cref="Pending"/>, a total the panel cannot
    /// break down is a total it renders as a sentence about the wrong work. It
    /// is also the one that will usually be non-zero alone — every artist in
    /// this catalogue was described before pictures existed.
    /// </remarks>
    int PendingPortraits,

    /// <summary>
    /// Artists nobody has asked what they released.
    /// </summary>
    /// <remarks>
    /// The fourth noun, and no longer the small one. While this was the followed
    /// set it was single figures beside three counts in the thousands; it is now
    /// every artist with an MBID that has never been browsed, because a
    /// discography nobody fetched is one nobody can find an album in. On a
    /// catalogue this stage has not run over it is the largest number here and
    /// the slowest work behind it — a gated browse each, plus the shops.
    /// Separate from <see cref="PendingArtists"/> for the same reason the
    /// portraits are: same rows, different work, and one number would quote the
    /// wrong wait.
    /// </remarks>
    int PendingDiscographies,

    EnrichmentSummary? LastCompleted);

/// <summary>An attribution pass was accepted, with the size of the job it took on.</summary>
public sealed record AttributionStartedResponse(string JobId, int Pending);

/// <summary>Everything the attribution card needs, in one read.</summary>
public sealed record AttributionStatusResponse(
    bool Running,
    string? JobId,
    int Processed,
    int Total,
    string? CurrentFile,

    /// <summary>Identified files that have never been asked which album they came from.</summary>
    int Pending,

    AttributionSummary? LastCompleted);

/// <param name="Files">How many files in the library hold a complete catalogue answer.</param>
/// <param name="WillWrite">Whether <c>Fonoteca:AllowFileMutation</c> is on.</param>
/// <param name="Scope">What the running pass covers, in words. Null when nothing is running.</param>
public sealed record TagWriteStatusResponse(
    bool Running,
    string? JobId,
    string? Scope,
    int Processed,
    int Total,
    string? CurrentFile,
    int Files,
    bool WillWrite,
    string? LastError,
    TagWriteSummary? LastCompleted);
