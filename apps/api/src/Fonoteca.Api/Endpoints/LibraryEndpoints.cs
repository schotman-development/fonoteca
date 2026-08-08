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

        return app;
    }

    private static async Task<Results<Ok<LibraryScanSummary>, ProblemHttpResult>> ScanLibrary(
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

    private static Ok<LibraryScanStatusResponse> GetLibraryScanStatus(LibraryScanService scans) =>
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
    private static async Task<Results<Accepted<IdentificationStartedResponse>, ProblemHttpResult>>
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

    private static async Task<Ok<IdentificationStatusResponse>> GetIdentificationStatus(
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

    private static Results<Accepted, ProblemHttpResult> CancelIdentification(
        IdentificationService identification) =>
        identification.Cancel()
            ? TypedResults.Accepted("/api/library/identify")
            : TypedResults.Problem(
                title: "Nothing to cancel",
                detail: "No identification pass is running.",
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
