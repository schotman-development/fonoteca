using Fonoteca.Api.Library;
using Fonoteca.Data;
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
        group.MapPost("/releases/{id:guid}/tags", WriteReleaseTags)
            .WithName("WriteReleaseTags")
            .WithSummary("Write what the catalogue knows about this album into its files.")
            .WithDescription(
                "Returns immediately with a job id; progress arrives on the jobs hub. Writes "
                + "title, artist, album, album artist, track and disc numbers, the year and every "
                + "MusicBrainz identifier into each file that has a recording, a track and a "
                + "release. Never automatic. Each file is rendered to a staged sibling, read back "
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
    }

    private static async Task<Results<Accepted<TagWriteStartedResponse>, ProblemHttpResult>>
        WriteReleaseTags(
            Guid id,
            FonotecaDbContext db,
            TagWriteService tags,
            CancellationToken cancellationToken)
    {
        var releaseId = new ReleaseId(id);

        var title = await db.Releases
            .Where(release => release.Id == releaseId)
            .Select(release => release.Title)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        if (title is null)
        {
            return TypedResults.Problem(
                title: "No such release",
                detail: $"The catalogue has no release with id {id}.",
                statusCode: StatusCodes.Status404NotFound);
        }

        return await StartAsync(
            tags, TagWriteScope.ForRelease(releaseId, title), cancellationToken).ConfigureAwait(false);
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
