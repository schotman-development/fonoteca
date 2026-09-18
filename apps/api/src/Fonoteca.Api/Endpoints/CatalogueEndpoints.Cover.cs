using Fonoteca.Data;
using Fonoteca.Domain.Abstractions;
using Fonoteca.Domain.Catalogue;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Net.Http.Headers;

namespace Fonoteca.Api.Endpoints;

/// <summary>
/// An album's cover, stored in the catalogue and chosen by a person when the
/// archive's own choice is wrong.
/// </summary>
/// <remarks>
/// <b>Fetched once, then served from here.</b> The first request for a release
/// with no stored cover takes the archive's front and keeps it; every later
/// one is a row read, revalidated by ETag. Choosing another archive image or
/// uploading one replaces the row.
///
/// <b>The archive's front is only the default.</b> It is the first image typed
/// <c>Front</c>, which on <i>Back to Tennessee</i> is a 2:1 fold-out spread
/// ahead of the square sleeve.
/// </remarks>
public static partial class CatalogueEndpoints
{
    /// <summary>The largest picture a person may upload.</summary>
    private const int MaxCoverBytes = 10 * 1024 * 1024;

    private static void MapCoverEndpoints(IEndpointRouteBuilder group)
    {
        group.MapGet("/releases/{id:guid}/cover", GetReleaseCover)
            .WithName("GetReleaseCover")
            .WithSummary("The album's cover, as stored in the catalogue.")
            .WithDescription(
                "The first request for an album with no stored cover fetches the Cover Art "
                + "Archive's front at 500px and keeps it, so the archive is asked once per album. "
                + "An album the archive holds no front for is remembered as such and answers 404 "
                + "without asking again. Served under an ETag with `no-cache`, so a changed cover "
                + "shows on the next view and an unchanged one costs a 304.")
            .Produces(StatusCodes.Status200OK, contentType: "image/jpeg")
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        group.MapGet("/releases/{id:guid}/cover/options", GetReleaseCoverOptions)
            .WithName("GetReleaseCoverOptions")
            .WithSummary("Every image the Cover Art Archive holds for this album, to choose from.")
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        group.MapPost("/releases/{id:guid}/cover/archive/{imageId:long}", ChooseReleaseCover)
            .WithName("ChooseReleaseCover")
            .WithSummary("Use this Cover Art Archive image as the album's cover.")
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        group.MapPost("/releases/{id:guid}/cover", UploadReleaseCover)
            .WithName("UploadReleaseCover")
            .WithSummary("Use an uploaded picture as the album's cover.")
            .WithDescription(
                "The request body is the image itself, its Content-Type one of JPEG, PNG, GIF, "
                + "WebP, BMP or AVIF — never SVG, which is a document that runs script. At most "
                + "10 MB.")
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound);
    }

    private static async Task<Results<FileContentHttpResult, ProblemHttpResult>> GetReleaseCover(
        Guid id,
        FonotecaDbContext db,
        ICoverArtArchive archive,
        IClock clock,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        var releaseId = new ReleaseId(id);

        var cover = await db.ReleaseCovers
            .AsNoTracking()
            .FirstOrDefaultAsync(row => row.ReleaseId == releaseId, cancellationToken)
            .ConfigureAwait(false);

        if (cover is null)
        {
            var mbid = await db.Releases
                .Where(release => release.Id == releaseId)
                .Select(release => release.Mbid)
                .FirstOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false);

            if (mbid is null) return NoCover();

            try
            {
                var images = await archive.ListAsync(mbid.Value, cancellationToken)
                    .ConfigureAwait(false);

                var front = images.FirstOrDefault(image => image.Front);

                cover = front is null
                    ? new ReleaseCover { ReleaseId = releaseId, SavedUtc = Now(clock) }
                    : await DownloadCoverAsync(archive, releaseId, mbid.Value, front.Id, clock, cancellationToken)
                        .ConfigureAwait(false);
            }
            catch (ProviderUnavailableException cause)
            {
                // Not stored: an outage is not an answer, and the next view retries.
                return ArchiveUnavailable(cause);
            }

            db.ReleaseCovers.Add(cover);

            try
            {
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (DbUpdateException)
            {
                // A second tile for the same album got there first. Its row is as
                // good as this one, and this request still has the bytes in hand.
            }
        }

        if (cover.Bytes is null || cover.MediaType is null) return NoCover();

        context.Response.Headers.CacheControl = "no-cache";
        context.Response.Headers.XContentTypeOptions = "nosniff";

        return TypedResults.File(
            cover.Bytes,
            cover.MediaType,
            lastModified: cover.SavedUtc,
            entityTag: new EntityTagHeaderValue($"\"{cover.SavedUtc.UtcTicks}\""));
    }

    private static async Task<Results<Ok<ReleaseCoverOptions>, ProblemHttpResult>>
        GetReleaseCoverOptions(
            Guid id,
            FonotecaDbContext db,
            ICoverArtArchive archive,
            CancellationToken cancellationToken)
    {
        var releaseId = new ReleaseId(id);

        var release = await db.Releases
            .Where(row => row.Id == releaseId)
            .Select(row => new { row.Mbid })
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        if (release is null) return NoSuchRelease(id);

        var chosen = await db.ReleaseCovers
            .Where(row => row.ReleaseId == releaseId)
            .Select(row => new { row.ArchiveImageId, Stored = row.Bytes != null })
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        IReadOnlyList<CoverArtImage> images = [];

        if (release.Mbid is { } mbid)
        {
            try
            {
                images = await archive.ListAsync(mbid, cancellationToken).ConfigureAwait(false);
            }
            catch (ProviderUnavailableException cause)
            {
                return ArchiveUnavailable(cause);
            }
        }

        return TypedResults.Ok(new ReleaseCoverOptions(
            release.Mbid?.Value,
            chosen?.ArchiveImageId,
            chosen is { Stored: true, ArchiveImageId: null },
            images
                .Select(image => new ReleaseCoverOption(
                    image.Id,
                    image.Front,
                    image.Types,
                    image.Comment))
                .ToList()));
    }

    private static async Task<Results<NoContent, ProblemHttpResult>> ChooseReleaseCover(
        Guid id,
        long imageId,
        FonotecaDbContext db,
        ICoverArtArchive archive,
        IClock clock,
        CancellationToken cancellationToken)
    {
        var releaseId = new ReleaseId(id);

        var mbid = await db.Releases
            .Where(release => release.Id == releaseId)
            .Select(release => new { release.Mbid })
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        if (mbid is null) return NoSuchRelease(id);

        if (mbid.Mbid is null)
        {
            return TypedResults.Problem(
                title: "Not a MusicBrainz release",
                detail: "This album has no MusicBrainz id, so the Cover Art Archive holds nothing for it.",
                statusCode: StatusCodes.Status404NotFound);
        }

        // Only an image the archive lists against this release. Downloading any
        // id would let a request store a picture of some other album here.
        IReadOnlyList<CoverArtImage> images;
        ReleaseCover cover;

        try
        {
            images = await archive.ListAsync(mbid.Mbid.Value, cancellationToken).ConfigureAwait(false);

            if (images.All(image => image.Id != imageId))
            {
                return TypedResults.Problem(
                    title: "No such image",
                    detail: $"The Cover Art Archive lists no image {imageId} for this release.",
                    statusCode: StatusCodes.Status404NotFound);
            }

            cover = await DownloadCoverAsync(
                    archive, releaseId, mbid.Mbid.Value, imageId, clock, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (ProviderUnavailableException cause)
        {
            return ArchiveUnavailable(cause);
        }

        await ReplaceCoverAsync(db, cover, cancellationToken).ConfigureAwait(false);

        return TypedResults.NoContent();
    }

    /// <summary>The picture, read straight out of the request body.</summary>
    /// <remarks>
    /// Not a form, for <c>UploadLibraryFile</c>'s reason. The media type is the
    /// declared one, checked against the raster allowlist; a body that lies about
    /// it is served under the type it claimed, with <c>nosniff</c>, and draws as
    /// a broken image rather than as anything else.
    /// </remarks>
    private static async Task<Results<NoContent, ProblemHttpResult>> UploadReleaseCover(
        Guid id,
        FonotecaDbContext db,
        IClock clock,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        var releaseId = new ReleaseId(id);

        if (!await db.Releases.AnyAsync(release => release.Id == releaseId, cancellationToken)
                .ConfigureAwait(false))
        {
            return NoSuchRelease(id);
        }

        var mediaType = context.Request.ContentType is { } header
            && MediaTypeHeaderValue.TryParse(header, out var parsed)
                ? parsed.MediaType.Value
                : null;

        if (!FilePreview.IsSafeImageMediaType(mediaType))
        {
            return TypedResults.Problem(
                title: "Not a picture this application will serve",
                detail: "Upload a JPEG, PNG, GIF, WebP, BMP or AVIF image.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        using var buffer = new MemoryStream();
        var chunk = new byte[81_920];
        int read;

        while ((read = await context.Request.Body.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > MaxCoverBytes)
            {
                return TypedResults.Problem(
                    title: "The picture is too large",
                    detail: $"A cover may be at most {MaxCoverBytes / 1024 / 1024} MB.",
                    statusCode: StatusCodes.Status400BadRequest);
            }

            buffer.Write(chunk, 0, read);
        }

        if (buffer.Length == 0)
        {
            return TypedResults.Problem(
                title: "No picture",
                detail: "The request body was empty.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        await ReplaceCoverAsync(
                db,
                new ReleaseCover
                {
                    ReleaseId = releaseId,
                    Bytes = buffer.ToArray(),
                    MediaType = mediaType,
                    SavedUtc = Now(clock),
                },
                cancellationToken)
            .ConfigureAwait(false);

        return TypedResults.NoContent();
    }

    private static async Task<ReleaseCover> DownloadCoverAsync(
        ICoverArtArchive archive,
        ReleaseId releaseId,
        Mbid mbid,
        long imageId,
        IClock clock,
        CancellationToken cancellationToken)
    {
        var image = await archive.DownloadAsync(mbid, imageId, cancellationToken).ConfigureAwait(false);

        // The archive's own type, held to the same allowlist as an upload. A
        // redirect landing somewhere unexpected must not become a stored page.
        if (!FilePreview.IsSafeImageMediaType(image.MediaType))
        {
            throw new ProviderUnavailableException(
                "Cover Art Archive",
                $"Cover Art Archive answered with {image.MediaType}, not a picture.");
        }

        return new ReleaseCover
        {
            ReleaseId = releaseId,
            Bytes = image.Bytes,
            MediaType = image.MediaType,
            ArchiveImageId = imageId,
            SavedUtc = Now(clock),
        };
    }

    private static async Task ReplaceCoverAsync(
        FonotecaDbContext db,
        ReleaseCover cover,
        CancellationToken cancellationToken)
    {
        // One statement, so a first view inserting the archive's default in the
        // same moment cannot collide with this on the key and lose the choice.
        await db.Database
            .ExecuteSqlAsync(
                $"""
                INSERT INTO "ReleaseCovers" ("ReleaseId", "Bytes", "MediaType", "ArchiveImageId", "SavedUtc")
                VALUES ({cover.ReleaseId.Value}, {cover.Bytes}, {cover.MediaType}, {cover.ArchiveImageId}, {cover.SavedUtc})
                ON CONFLICT ("ReleaseId") DO UPDATE SET
                    "Bytes" = excluded."Bytes",
                    "MediaType" = excluded."MediaType",
                    "ArchiveImageId" = excluded."ArchiveImageId",
                    "SavedUtc" = excluded."SavedUtc"
                """,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static DateTimeOffset Now(IClock clock) => StoreTime.ToStorePrecision(clock.UtcNow);

    private static ProblemHttpResult NoCover() =>
        TypedResults.Problem(
            title: "No cover",
            detail: "The catalogue holds no cover for this album.",
            statusCode: StatusCodes.Status404NotFound);

    private static ProblemHttpResult NoSuchRelease(Guid id) =>
        TypedResults.Problem(
            title: "No such release",
            detail: $"The catalogue has no release with id {id}.",
            statusCode: StatusCodes.Status404NotFound);

    private static ProblemHttpResult ArchiveUnavailable(ProviderUnavailableException cause) =>
        TypedResults.Problem(
            title: "The Cover Art Archive did not answer",
            detail: cause.Message,
            statusCode: StatusCodes.Status503ServiceUnavailable);
}

/// <param name="Mbid">Null for an album with no MusicBrainz id; <c>Images</c> is then empty.</param>
/// <param name="Chosen">The archive image currently stored, if the stored cover is one.</param>
/// <param name="Uploaded">Whether the stored cover is a person's upload.</param>
public sealed record ReleaseCoverOptions(
    Guid? Mbid,
    long? Chosen,
    bool Uploaded,
    IReadOnlyList<ReleaseCoverOption> Images);

/// <param name="Front">Whether the archive itself calls this the front.</param>
public sealed record ReleaseCoverOption(
    long Id,
    bool Front,
    IReadOnlyList<string> Types,
    string? Comment);
