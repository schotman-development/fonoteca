using Fonoteca.Api.Library;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Fonoteca.Api.Endpoints;

/// <summary>
/// The library on disk, and what the catalogue knows about it.
/// </summary>
/// <remarks>
/// Same shape as <see cref="SystemEndpoints"/>: one static class per
/// capability, one <c>MapXEndpoints</c> extension, registered from Program.
///
/// Scan is a POST to <c>/api/library/scan</c> and its status is a GET on the
/// same path — same resource, one verb that changes it and one that reads it.
/// It responds when the scan has finished rather than immediately, which is
/// honest while a scan is seconds of walking and stat'ing. It stops being
/// honest the moment a pass opens files; that version returns 202 with a job
/// id, and this pair of handlers is where that change lands.
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

        return app;
    }

    private static async Task<Results<Ok<LibraryScanSummary>, ProblemHttpResult>> ScanLibrary(
        LibraryScanService scans,
        CancellationToken cancellationToken)
    {
        var outcome = await scans.ScanAsync(cancellationToken).ConfigureAwait(false);

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
}

/// <summary>Current scan state. <c>LastCompleted</c> is null until one has run.</summary>
public sealed record LibraryScanStatusResponse(bool Running, LibraryScanSummary? LastCompleted);
