using Fonoteca.Api.Library;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Fonoteca.Api.Endpoints;

/// <summary>
/// The library as a directory tree.
/// </summary>
/// <remarks>
/// Four routes, and the shape of them is the point: a GET that reads the disk,
/// and three POSTs that each name one act on it. There is no
/// <c>DELETE /api/files</c> — trashing is a move, and calling it a delete would
/// be the one place in this application where the wire says something the
/// implementation does not do.
///
/// Failures come back as problem documents rather than as an <c>applied: false</c>
/// a client has to remember to check. The exception is a refusal the service
/// makes on purpose — a folder that already exists, a pass holding the gate —
/// which is 409: the request was well-formed and the answer is "not now" or "not
/// like that", and both are things to show a person rather than to retry.
/// </remarks>
public static partial class FileEndpoints
{
    public static IEndpointRouteBuilder MapFileEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/files").WithTags("Files");

        group.MapGet("/", ListFolder)
            .WithName("ListLibraryFolder")
            .WithSummary("One directory of the library, from disk, annotated from the catalogue.")
            .WithDescription(
                "Lists what is actually on disk — including files nothing has scanned and files "
                + "that are not audio at all — and puts the catalogue's own counts on each row: "
                + "how many audio files it holds beneath, how many are identified, and how many "
                + "are filed under an album.");

        group.MapPost("/trash", TrashEntries)
            .WithName("TrashLibraryEntries")
            .WithSummary("Move files or folders out of the library, into the trash.")
            .WithDescription(
                "Nothing is deleted: entries move to Fonoteca:TrashPath under a timestamp, keeping "
                + "their library-relative layout, so undoing a wrong click is a mv. Their "
                + "catalogue rows are removed in the same operation, because this process moved "
                + "the files itself and does not have to infer their absence from a scan.")
            .ProducesProblem(StatusCodes.Status409Conflict);

        group.MapPost("/move", MoveEntry)
            .WithName("MoveLibraryEntry")
            .WithSummary("Rename or move one file or folder within the library.")
            .WithDescription(
                "The catalogue rows move with it. A rename changes no bytes, so nothing derived "
                + "is cleared — which is the whole reason this is an endpoint rather than a mv and "
                + "a rescan, since a scan reads a rename as a deletion and an arrival and discards "
                + "every AcoustID, recording link and album decision beneath the folder.")
            .ProducesProblem(StatusCodes.Status409Conflict);

        group.MapPost("/upload", UploadFile)
            .WithName("UploadLibraryFile")
            .WithSummary("Write one file into the library at the given path.")
            .WithDescription(
                "The request body is the file itself — no multipart, so nothing is buffered to a "
                + "temporary copy on the way in and an album-sized file costs one write. Upload "
                + "one file per request; the catalogue learns about them at the next scan. "
                + "'folder' is a folder that already exists and is taken as it is; 'name' is the "
                + "file's own path — one segment from a file picker, 'Album/CD1/01.flac' from a "
                + "directory one — and is sanitised, because it is the only half the browser "
                + "invented.")
            .Accepts<Stream>("application/octet-stream")
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status400BadRequest);

        MapPreview(group);

        return app;
    }

    private static async Task<Results<Ok<FolderListing>, ProblemHttpResult>> ListFolder(
        FileManagerService files,
        CancellationToken cancellationToken,
        string? path = null)
    {
        try
        {
            return TypedResults.Ok(
                await files.ListAsync(path, cancellationToken).ConfigureAwait(false));
        }
        catch (UnauthorizedAccessException)
        {
            // The store's containment check. A path outside the root is a
            // request that should never have been made, not a server fault.
            return Outside(path);
        }
    }

    /// <remarks>
    /// The null checks are not ceremony. A body of <c>{}</c> deserialises every
    /// member to null, which reaches the service and throws — a 500 for a
    /// malformed request, on the endpoint that moves albums.
    /// </remarks>
    private static Task<Results<Ok<FileOperation>, ProblemHttpResult>> TrashEntries(
        FileManagerService files,
        TrashRequest request,
        CancellationToken cancellationToken) =>
        request?.Paths is null
            ? Task.FromResult<Results<Ok<FileOperation>, ProblemHttpResult>>(Malformed("paths"))
            : RunAsync(() => files.TrashAsync(request.Paths, cancellationToken));

    private static Task<Results<Ok<FileOperation>, ProblemHttpResult>> MoveEntry(
        FileManagerService files,
        MoveRequest request,
        CancellationToken cancellationToken) =>
        request?.From is null || request.To is null
            ? Task.FromResult<Results<Ok<FileOperation>, ProblemHttpResult>>(Malformed("from and to"))
            : RunAsync(() => files.MoveAsync(request.From, request.To, cancellationToken));

    /// <summary>
    /// The upload, streamed straight out of the request body.
    /// </summary>
    /// <remarks>
    /// <b>Not a form.</b> A multipart upload bound to <c>IFormFile</c> buffers
    /// anything over 64 KB to a temporary file first, which for an album is
    /// writing every byte twice — and on a host where <c>/tmp</c> is tmpfs, into
    /// RAM. The largest FLAC in the target library is 475 MB. The body is the
    /// file, the path is a query parameter, and
    /// <c>content.CopyToAsync(staged)</c> is the whole implementation.
    ///
    /// Kestrel's 30 MB default body limit is lifted for this request only, on
    /// the feature rather than globally: nothing else here accepts a body worth
    /// more than a few kilobytes, and an unbounded limit on the endpoints that
    /// take JSON is a way to be handed a gigabyte of it.
    /// </remarks>
    private static async Task<Results<Ok<FileOperation>, ProblemHttpResult>> UploadFile(
        FileManagerService files,
        HttpContext context,
        string folder,
        string name,
        CancellationToken cancellationToken)
    {
        var limit = context.Features.Get<IHttpMaxRequestBodySizeFeature>();

        if (limit is { IsReadOnly: false }) limit.MaxRequestBodySize = null;

        try
        {
            return TypedResults.Ok(await files
                .SaveAsync(
                    folder,
                    name,
                    context.Request.Body,
                    context.Request.ContentLength,
                    cancellationToken)
                .ConfigureAwait(false));
        }
        catch (UnauthorizedAccessException)
        {
            return Outside(folder);
        }
        catch (IOException cause)
        {
            // Already there, or the volume is full. Both are the caller's to
            // resolve and neither is a fault in this process.
            return TypedResults.Problem(
                title: "The file was not written",
                detail: cause.Message,
                statusCode: StatusCodes.Status409Conflict);
        }
    }

    /// <summary>
    /// Runs an operation, turning its refusal into a 409 and the store's
    /// containment check into a 400.
    /// </summary>
    private static async Task<Results<Ok<FileOperation>, ProblemHttpResult>> RunAsync(
        Func<Task<FileOperation>> operation)
    {
        try
        {
            var result = await operation().ConfigureAwait(false);

            return result.Applied
                ? TypedResults.Ok(result)
                : TypedResults.Problem(
                    title: "Nothing was moved",
                    detail: result.Detail,
                    statusCode: StatusCodes.Status409Conflict);
        }
        catch (UnauthorizedAccessException)
        {
            return Outside(null);
        }
        catch (IOException cause)
        {
            // A rename across filesystems is the one worth naming: the trash
            // defaults to a sibling of the library so the move is a rename, and
            // pointing it at another volume makes every trash throw here rather
            // than silently copying an album.
            return TypedResults.Problem(
                title: "The move failed",
                detail: cause.Message,
                statusCode: StatusCodes.Status409Conflict);
        }
    }

    /// <remarks>
    /// The path is optional because <see cref="RunAsync"/> does not have one —
    /// it runs an operation over a list. Interpolating a null there produced
    /// <c>'' resolves outside the library root</c>, which reads as a bug in the
    /// application rather than as a rejected request.
    /// </remarks>
    private static ProblemHttpResult Outside(string? path) =>
        TypedResults.Problem(
            title: "That path is not in the library",
            detail: path is null
                ? "One of those paths resolves outside the configured library root."
                : $"'{path}' resolves outside the configured library root.",
            statusCode: StatusCodes.Status400BadRequest);

    private static ProblemHttpResult Malformed(string fields) =>
        TypedResults.Problem(
            title: "The request is incomplete",
            detail: $"'{fields}' must be present.",
            statusCode: StatusCodes.Status400BadRequest);
}

/// <param name="Paths">Library-relative paths. Files or folders, mixed freely.</param>
public sealed record TrashRequest(IReadOnlyList<string> Paths);

/// <param name="To">
/// The entry's new library-relative path, not its new parent — so a rename and a
/// move are one operation rather than two that differ only in which half of the
/// path changed.
/// </param>
public sealed record MoveRequest(string From, string To);
