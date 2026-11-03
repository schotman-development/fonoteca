using Fonoteca.Api.Library;
using Fonoteca.Data;
using Fonoteca.Domain.Abstractions;
using Fonoteca.Domain.Catalogue;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace Fonoteca.Api.Endpoints;

/*
 * A person's answer to which work a recording performs, where MusicBrainz
 * links none or the wrong one (`WorkSources`): a MusicBrainz work, written as
 * the recording stage writes the one MusicBrainz links, which no enrichment
 * run replaces.
 */
public static partial class CatalogueEndpoints
{
    private static void MapRecordingWorkEndpoints(IEndpointRouteBuilder group)
    {
        group.MapPut("/recordings/{id:guid}/work", SetRecordingWork)
            .RequireRole(AccountRoles.CoverArt)
            .WithName("SetRecordingWork")
            .WithSummary("Say which MusicBrainz work a recording performs, over MusicBrainz's own link.")
            .WithDescription(
                "The work by its MusicBrainz id: a movement where the recording is one. MusicBrainz is "
                + "asked for it, so the piece it is part of, its place there and its composers come with "
                + "it, and no enrichment run replaces it; the files get it at the next tag write. Null "
                + "takes a person's link back, and MusicBrainz and Apple Music Classical are asked again "
                + "on the next enrichment run.")
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);
    }

    internal static async Task<Results<Ok<RecordingWorkResponse>, ProblemHttpResult>> SetRecordingWork(
        Guid id,
        RecordingWorkRequest request,
        FonotecaDbContext db,
        IMusicBrainzCatalogue musicBrainz,
        ICallerContext caller,
        IClock clock,
        CancellationToken cancellationToken)
    {
        var recordingId = new RecordingId(id);

        var recording = await db.Recordings
            .FirstOrDefaultAsync(row => row.Id == recordingId, cancellationToken)
            .ConfigureAwait(false);

        if (recording is null)
        {
            return TypedResults.Problem(
                title: "No such recording",
                detail: $"The catalogue has no recording with id {id}.",
                statusCode: StatusCodes.Status404NotFound);
        }

        // Discarded: only an enrichment run's summary counts the artists touched.
        var writer = new CatalogueWriter(db, new HashSet<Mbid>(), clock);
        var previous = recording.WorkId;

        if (request.Work is { } mbid)
        {
            MusicBrainzWork? work;

            try
            {
                work = await musicBrainz.GetWorkAsync(new Mbid(mbid), cancellationToken).ConfigureAwait(false);
            }
            catch (ProviderException error)
            {
                return TypedResults.Problem(
                    title: "MusicBrainz did not answer",
                    detail: error.Message,
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }

            if (work is null)
            {
                return TypedResults.Problem(
                    title: "No such work",
                    detail: $"MusicBrainz has no work with id {mbid}.",
                    statusCode: StatusCodes.Status404NotFound);
            }

            var row = await writer.UpsertWorkAsync(work, cancellationToken).ConfigureAwait(false);
            await writer.ApplyWritersAsync(row, work, cancellationToken).ConfigureAwait(false);

            recording.WorkId = row.Id;
            recording.WorkSource = caller is AgentCallerContext ? WorkSources.Agent : WorkSources.Person;
        }
        else if (recording.WorkSource is not null)
        {
            recording.WorkId = null;
            recording.WorkSource = null;
            recording.DescribedUtc = null;

            // Apple's piece went when the link replaced it, and Apple is asked
            // about a record it carries only once.
            foreach (var album in await db.ReleaseGroups
                .Where(album => album.Files.Any(file => file.RecordingId == recordingId))
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false))
            {
                album.ClassicalLookupUtc = null;
            }
        }

        await writer
            .DropUnperformedAsync([(previous == recording.WorkId ? null : previous, recording.WorkId)], cancellationToken)
            .ConfigureAwait(false);

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        var performed = await db.Works
            .AsNoTracking()
            .Where(work => work.Id == recording.WorkId)
            .Select(work => new { work.Mbid, work.Title })
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        return TypedResults.Ok(new RecordingWorkResponse(
            recording.WorkId?.Value,
            performed?.Mbid?.Value,
            performed?.Title,
            recording.WorkSource));
    }
}

/// <param name="Work">The MusicBrainz id of the work the recording performs; null takes a person's link back.</param>
public sealed record RecordingWorkRequest(Guid? Work);

/// <param name="Work">The catalogue's id for the work the recording performs now, or null.</param>
/// <param name="Mbid">That work's MusicBrainz id, or null for one of Apple's pieces.</param>
/// <param name="Title">That work's title.</param>
/// <param name="Source">Who linked it: "person", "agent", or null for MusicBrainz or Apple.</param>
public sealed record RecordingWorkResponse(Guid? Work, Guid? Mbid, string? Title, string? Source);
