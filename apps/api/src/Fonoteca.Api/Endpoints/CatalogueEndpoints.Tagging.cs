using Fonoteca.Api.Library;
using Fonoteca.Data;
using Fonoteca.Domain.Abstractions;
using Fonoteca.Domain.Catalogue;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace Fonoteca.Api.Endpoints;

/// <summary>
/// Writing the catalogue back into the files, one album or one artist at a time.
/// </summary>
/// <remarks>
/// <b>The same pass as <c>POST /api/library/tags</c>, narrowed.</b> Three
/// buttons, one <see cref="TagWriteService"/>: the work is identical and only
/// the worklist differs, so a scoped run takes the same gate, reports on the
/// same hub channel and is watched through the same status endpoint. Three
/// separate implementations would be three places for the "record the file's
/// new size" line to be missing from.
///
/// <b>Why these two scopes and not others.</b> An album is the unit a person
/// actually thinks in — they have just corrected one on the matching screen and
/// want it written down — and an artist is the unit for the case where a whole
/// discography was enriched at once. The library-wide button is the one that
/// exists because the other two do not scale to a first run.
///
/// <b>Nothing here writes on its own.</b> No pass chains into this, no timer
/// reaches it, and <c>Fonoteca:AllowFileMutation</c> still has to be on for a
/// byte to change. That is two locks on the only operation in this application
/// that a rescan cannot undo.
/// </remarks>
public static partial class CatalogueEndpoints
{
    private static void MapTaggingEndpoints(IEndpointRouteBuilder group)
    {
        group.MapPost("/albums/{id:guid}/tags", WriteAlbumTags)
            .WithName("WriteAlbumTags")
            .WithSummary("Write what the catalogue knows about this album into its files.")
            .WithDescription(
                "Returns immediately with a job id; progress arrives on the jobs hub. Writes "
                + "title, artist, album, album artist, track and disc numbers, the year and every "
                + "MusicBrainz identifier into each file with a proven pressing; a file held to "
                + "its album alone gets the album's facts and no disc, track total or release "
                + "MBID, and keeps any track number and date of its own. Never automatic. Each file is rendered to a staged sibling, read back "
                + "by two independent tag libraries and length-checked before the swap, and the "
                + "previous values are journalled. With Fonoteca:AllowFileMutation off the whole "
                + "run happens except the write.")
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        group.MapPost("/artists/{id:guid}/tags", WriteArtistTags)
            .WithName("WriteArtistTags")
            .WithSummary("Write what the catalogue knows about this artist's tracks into their files.")
            .WithDescription(
                "The album endpoint's rule over every recording this artist is responsible for — "
                + "billed, linked as conductor or ensemble, or a writer of the work — which is the "
                + "same set the artist page lists. Returns immediately with a job id. Never "
                + "automatic.")
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        group.MapGet("/folders/tags", GetFolderTags)
            .WithName("GetFolderTags")
            .WithSummary("What an album folder's files carry, what the catalogue would write, and what a person set.")
            .WithDescription(
                "One row per file and one cell per field the editor sets: the value the tag write "
                + "would put in the file now (a person's, else the catalogue's, else the file's "
                + "own), the catalogue's, the file's, and whether a person set it. Where the folder "
                + "is one album, `album` holds its title, artist and year, which are corrected for "
                + "the whole album.")
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPost("/folders/tags", SaveFolderTags)
            .WithName("SaveFolderTags")
            .WithSummary("Set, take out or forget a person's tags in an album folder, and write the folder.")
            .WithDescription(
                "Each change names a file, or none for the album's own title, artist or year where "
                + "the folder is one album. A value sets the field, a null value takes it out of the "
                + "file, and `reset` forgets the person's value. The corrections are stored, then "
                + "the folder is written and renamed as the tag write does, at once; a person's "
                + "value wins over the catalogue's and over any tagger that changed the file since. "
                + "One Undo press takes the whole save back.")
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status409Conflict);

        group.MapGet("/folders/undo", FolderUndoState)
            .WithName("GetFolderUndo")
            .WithSummary("The edit an undo of this album folder would reverse, if any.")
            .WithDescription(
                "`edit` is null when nothing written to the folder's files is left to undo, or when "
                + "`folder` is not an album folder. `willWrite` is Fonoteca:AllowFileMutation; with "
                + "it off an undo is refused, as every file write is.");

        group.MapPost("/folders/undo", UndoFolder)
            .WithName("UndoFolder")
            .WithSummary("Step an album folder back by one edit to its files.")
            .WithDescription(
                "The newest edit not yet undone: a tag write's tags, renames, links and sleeve "
                + "together, or identification's AcoustID. Tags go back to what they held before "
                + "it even where another tagger has changed them since; a move never goes over "
                + "anything, so a name since taken is reported and left. Undoing a tag write also "
                + "reopens the folder, so the next tag write leaves it alone until its album is "
                + "answered again. Press again to step further back.")
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);
    }

    internal static async Task<Ok<FolderUndoResponse>> FolderUndoState(
        string folder,
        TagWriteService tags,
        CancellationToken cancellationToken) =>
        TypedResults.Ok(new FolderUndoResponse(
            folder,
            tags.MutationAllowed,
            await tags.LastEditAsync(Unslashed(folder), cancellationToken).ConfigureAwait(false)));

    internal static async Task<Results<Ok<TagUndoResult>, ProblemHttpResult>> UndoFolder(
        FolderUndoRequest request,
        TagWriteService tags,
        ICallerContext caller,
        CancellationToken cancellationToken)
    {
        if (request is null || string.IsNullOrEmpty(request.Folder))
        {
            return TypedResults.Problem(
                title: "Nothing to undo",
                detail: "The request body must name a library-relative album `folder`.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var result = await tags.UndoAsync(Unslashed(request.Folder), caller, cancellationToken).ConfigureAwait(false);

        return result.Status switch
        {
            TagUndoStatus.Undone or TagUndoStatus.Incomplete => TypedResults.Ok(result),

            TagUndoStatus.NotOnDisk => TypedResults.Problem(
                title: "The folder is not on disk",
                detail: $"\u201c{request.Folder}\u201d is not there — the library may be unmounted. Nothing was touched.",
                statusCode: StatusCodes.Status409Conflict),

            TagUndoStatus.NotAnAlbumFolder => TypedResults.Problem(
                title: "Not an album folder",
                detail: $"\u201c{request.Folder}\u201d holds no files, or holds more than one album's. "
                    + "An undo steps back one album folder at a time.",
                statusCode: StatusCodes.Status400BadRequest),

            TagUndoStatus.NothingToUndo => TypedResults.Problem(
                title: "Nothing to undo",
                detail: $"Nothing written to the files in \u201c{request.Folder}\u201d is left to undo.",
                statusCode: StatusCodes.Status404NotFound),

            TagUndoStatus.MutationOff => TypedResults.Problem(
                title: "File writing is off",
                detail: "Fonoteca:AllowFileMutation is false, so no file may be changed, an undo included.",
                statusCode: StatusCodes.Status409Conflict),

            _ => TypedResults.Problem(
                title: "The library is busy",
                detail: "A scan or a pass is running. Undo once it has finished.",
                statusCode: StatusCodes.Status409Conflict),
        };
    }

    internal static async Task<Results<Ok<FolderTags>, ProblemHttpResult>> GetFolderTags(
        string folder,
        TagWriteService tags,
        CancellationToken cancellationToken) =>
        await tags.FolderTagsAsync(Unslashed(folder), cancellationToken).ConfigureAwait(false) is { } found
            ? TypedResults.Ok(found)
            : TypedResults.Problem(
                title: "Not an album folder",
                detail: $"\u201c{folder}\u201d holds no catalogued files, or holds more than one album's.",
                statusCode: StatusCodes.Status404NotFound);

    internal static async Task<Results<Ok<TagEditResult>, ProblemHttpResult>> SaveFolderTags(
        TagEditRequest request,
        TagWriteService tags,
        ICallerContext caller,
        CancellationToken cancellationToken)
    {
        if (request is null || string.IsNullOrEmpty(request.Folder) || request.Changes is null or { Count: 0 })
        {
            return TypedResults.Problem(
                title: "Nothing to save",
                detail: "The request body must name a library-relative album `folder` and at least one change.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var result = await tags.SaveTagsAsync(Unslashed(request.Folder), request.Changes, caller, cancellationToken)
            .ConfigureAwait(false);

        return result.Status switch
        {
            TagEditStatus.Saved => TypedResults.Ok(result),

            TagEditStatus.Invalid => TypedResults.Problem(
                title: "Not saved",
                detail: string.Join(" ", result.Problems),
                statusCode: StatusCodes.Status400BadRequest),

            TagEditStatus.NotAnAlbumFolder => TypedResults.Problem(
                title: "Not an album folder",
                detail: $"\u201c{request.Folder}\u201d holds no catalogued files, or holds more than one album's.",
                statusCode: StatusCodes.Status400BadRequest),

            TagEditStatus.NotOnDisk => TypedResults.Problem(
                title: "The folder is not on disk",
                detail: $"\u201c{request.Folder}\u201d is not there — the library may be unmounted. Nothing was saved.",
                statusCode: StatusCodes.Status409Conflict),

            TagEditStatus.MutationOff => TypedResults.Problem(
                title: "File writing is off",
                detail: "Fonoteca:AllowFileMutation is false, so no file may be changed and nothing was saved.",
                statusCode: StatusCodes.Status409Conflict),

            _ => TypedResults.Problem(
                title: "The library is busy",
                detail: "A scan or a pass is running. Save once it has finished.",
                statusCode: StatusCodes.Status409Conflict),
        };
    }

    /// <summary>Without a trailing slash; untrimmed otherwise, since a folder name may end in a space.</summary>
    private static string Unslashed(string folder) => folder.EndsWith('/') ? folder[..^1] : folder;

    private static async Task<Results<Accepted<TagWriteStartedResponse>, ProblemHttpResult>>
        WriteAlbumTags(
            Guid id,
            FonotecaDbContext db,
            TagWriteService tags,
            CancellationToken cancellationToken)
    {
        var groupId = new ReleaseGroupId(id);

        var title = await db.ReleaseGroups
            .Where(album => album.Id == groupId)
            .Select(album => album.Title)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        if (title is null)
        {
            return TypedResults.Problem(
                title: "No such album",
                detail: $"The catalogue has no album with id {id}.",
                statusCode: StatusCodes.Status404NotFound);
        }

        return await StartAsync(
            tags, TagWriteScope.ForAlbum(groupId, title), cancellationToken).ConfigureAwait(false);
    }

    private static async Task<Results<Accepted<TagWriteStartedResponse>, ProblemHttpResult>>
        WriteArtistTags(
            Guid id,
            FonotecaDbContext db,
            TagWriteService tags,
            CancellationToken cancellationToken)
    {
        var artistId = new ArtistId(id);

        var name = await db.Artists
            .Where(artist => artist.Id == artistId)
            .Select(artist => artist.Name)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        if (name is null)
        {
            return TypedResults.Problem(
                title: "No such artist",
                detail: $"The catalogue has no artist with id {id}.",
                statusCode: StatusCodes.Status404NotFound);
        }

        return await StartAsync(
            tags, TagWriteScope.ForArtist(artistId, name), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Counts the scope, then starts it. Shared by all three buttons.
    /// </summary>
    /// <remarks>
    /// The count comes back with the job id so the screen can say what it is
    /// about to do rather than only that it started. It is read <i>before</i> the
    /// pass takes the gate, which makes it a number from a moment ago rather than
    /// a promise — but a count taken after the start would race the pass's own
    /// first page and is no better.
    /// </remarks>
    internal static async Task<Results<Accepted<TagWriteStartedResponse>, ProblemHttpResult>>
        StartAsync(TagWriteService tags, TagWriteScope scope, CancellationToken cancellationToken)
    {
        var pending = await tags.CountAsync(scope, cancellationToken).ConfigureAwait(false);

        var outcome = tags.Start(scope);

        return outcome switch
        {
            { Status: TagWriteStartStatus.Started, JobId: { } jobId } => TypedResults.Accepted(
                "/api/library/tags",
                new TagWriteStartedResponse(jobId, scope.Label, pending, tags.MutationAllowed)),

            _ => TypedResults.Problem(
                title: "The library is already busy",
                detail: "A scan or another pass is running. Only one at a time touches the "
                    + "catalogue, and this one also rewrites files. Poll GET /api/library/tags.",
                statusCode: StatusCodes.Status409Conflict),
        };
    }
}

/// <param name="Files">How many files the scope holds a complete catalogue answer for.</param>
/// <param name="WillWrite">
/// Whether <c>Fonoteca:AllowFileMutation</c> is on.
/// </param>
/// <remarks>
/// <paramref name="WillWrite"/> is on the *start* response and not only on the
/// status, because it is the one thing a person needs told at the moment they
/// press the button: with the flag off the run is real, the journal fills up and
/// not one byte on disk changes. A screen that reported "done, 8,140 files" for
/// that would be lying by omission.
/// </remarks>
public sealed record TagWriteStartedResponse(
    string JobId,
    string Scope,
    int Files,
    bool WillWrite);

/// <param name="Folder">A library-relative album folder, as the album page lists it.</param>
public sealed record FolderUndoRequest(string Folder);

/// <param name="WillWrite">Whether <c>Fonoteca:AllowFileMutation</c> is on; with it off nothing may be undone.</param>
/// <param name="Edit">What an undo would reverse, or null when nothing is left to.</param>
public sealed record FolderUndoResponse(string Folder, bool WillWrite, FolderEdit? Edit);

/// <param name="Folder">A library-relative album folder.</param>
/// <param name="Changes">What to set, take out or forget, in order.</param>
public sealed record TagEditRequest(string Folder, IReadOnlyList<TagEdit> Changes);
