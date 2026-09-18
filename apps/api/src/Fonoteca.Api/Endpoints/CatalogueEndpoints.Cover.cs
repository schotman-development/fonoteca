using Fonoteca.Data;
using Fonoteca.Domain.Abstractions;
using Fonoteca.Domain.Catalogue;
using Fonoteca.Providers.Qobuz;
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
///
/// <b>Two sources, in that order, and the order is the whole safety argument.</b>
/// The archive is keyed on the release's own MusicBrainz id and cannot be wrong
/// about which record it is showing. Qobuz is asked only where that produced
/// nothing, and is keyed on a barcode where there is one and on a matched title
/// otherwise — see <c>QobuzCovers</c>, which refuses rather than guesses.
///
/// <b>Nothing found is not a final answer.</b> It is remembered for
/// <see cref="CoverRetryAfter"/> and then asked again, because a sleeve reaching
/// either source after the first view is exactly the thing a stored "no" would
/// hide forever.
/// </remarks>
public static partial class CatalogueEndpoints
{
    /// <summary>The largest picture a person may upload.</summary>
    private const int MaxCoverBytes = 10 * 1024 * 1024;

    /// <summary>
    /// How long "nobody has a picture of this" is believed.
    /// </summary>
    /// <remarks>
    /// <b>A week, which is the same week the candidate caches are believed
    /// for</b>, and chosen against the same two costs pulling opposite ways. Too
    /// short and every browse of a shelf with a hundred coverless albums is a
    /// hundred searches against a paid subscription; too long and an album
    /// whose sleeve was added the day after somebody looked at it stays a
    /// monogram until they think to go and ask.
    ///
    /// <b>Only a row with no bytes expires.</b> A cover that exists is never
    /// re-asked, from any source — see <c>ReleaseCover</c>.
    /// </remarks>
    private static readonly TimeSpan CoverRetryAfter = TimeSpan.FromDays(7);

    private static void MapCoverEndpoints(IEndpointRouteBuilder group)
    {
        group.MapGet("/releases/{id:guid}/cover", GetReleaseCover)
            .WithName("GetReleaseCover")
            .WithSummary("The album's cover, as stored in the catalogue.")
            .WithDescription(
                "The first request for an album with no stored cover fetches the Cover Art "
                + "Archive's front at 500px and keeps it, so the archive is asked once per album. "
                + "Where the archive holds no front, Qobuz is asked for the same record — by "
                + "barcode where there is one, by a matched title and artist otherwise — and "
                + "refuses rather than guesses. An album neither has a picture of answers 404 and "
                + "is remembered as such for a week, then asked about again, so a sleeve either "
                + "gains later is still picked up. Served under an ETag with `no-cache`, so a "
                + "changed cover shows on the next view and an unchanged one costs a 304.")
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
        QobuzCovers shop,
        IClock clock,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        var releaseId = new ReleaseId(id);
        var now = Now(clock);

        var cover = await db.ReleaseCovers
            .AsNoTracking()
            .FirstOrDefaultAsync(row => row.ReleaseId == releaseId, cancellationToken)
            .ConfigureAwait(false);

        // A row with bytes is the answer, whoever found them. A row without is
        // only how long ago nobody had a picture, and it runs out.
        if (cover is null || (cover.Bytes is null && now - cover.SavedUtc >= CoverRetryAfter))
        {
            var release = await db.Releases
                .Where(row => row.Id == releaseId)
                .Select(row => new
                {
                    row.Mbid,
                    row.Title,
                    row.ReleasedYear,
                    row.Barcode,

                    // The billed line, as every other screen renders it — the
                    // name a shop prints on the same record.
                    Artists = row.Credits
                        .OrderBy(credit => credit.Position)
                        .Select(credit => new
                        {
                            credit.CreditedAs,
                            credit.JoinPhrase,
                            Name = credit.Artist!.LatinName ?? credit.Artist!.Name,
                        })
                        .ToList(),
                })
                .FirstOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false);

            if (release is null) return NoCover();

            try
            {
                cover =
                    await ArchiveCoverAsync(archive, releaseId, release.Mbid, clock, cancellationToken)
                        .ConfigureAwait(false)
                    ?? await ShopCoverAsync(
                            shop,
                            releaseId,
                            release.Title,
                            CreditLine(release.Artists.Select(a => (a.CreditedAs ?? a.Name, a.JoinPhrase))),
                            release.ReleasedYear,
                            release.Barcode,
                            clock,
                            cancellationToken)
                        .ConfigureAwait(false)
                    ?? new ReleaseCover { ReleaseId = releaseId, SavedUtc = now };
            }
            catch (ProviderException cause)
            {
                // Not stored, from either source: an outage is not an answer, and
                // a stored one would be believed for a week.
                return ArchiveUnavailable(cause);
            }

            await StoreFoundCoverAsync(db, cover, cancellationToken).ConfigureAwait(false);
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
            .Select(row => new { row.ArchiveImageId, row.QobuzAlbumId, Stored = row.Bytes != null })
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
            chosen is { Stored: true, ArchiveImageId: null, QobuzAlbumId: null },
            chosen?.QobuzAlbumId,
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

    /// <summary>The archive's own front, or nothing.</summary>
    /// <remarks>
    /// <b>First, and the only source keyed on an identifier.</b> An album with
    /// no MusicBrainz id skips straight past it: the archive is indexed by
    /// release mbid and has no other way in.
    /// </remarks>
    private static async Task<ReleaseCover?> ArchiveCoverAsync(
        ICoverArtArchive archive,
        ReleaseId releaseId,
        Mbid? mbid,
        IClock clock,
        CancellationToken cancellationToken)
    {
        if (mbid is not { } release) return null;

        var images = await archive.ListAsync(release, cancellationToken).ConfigureAwait(false);

        return images.FirstOrDefault(image => image.Front) is { } front
            ? await DownloadCoverAsync(archive, releaseId, release, front.Id, clock, cancellationToken)
                .ConfigureAwait(false)
            : null;
    }

    /// <summary>The shop's picture of the same record, or nothing.</summary>
    /// <remarks>
    /// Second, never first, and unreached for five albums in six — see the
    /// type's own remarks for why a matched title is allowed to choose a cover
    /// when it is not allowed to choose a portrait.
    /// </remarks>
    private static async Task<ReleaseCover?> ShopCoverAsync(
        QobuzCovers shop,
        ReleaseId releaseId,
        string title,
        string? artist,
        int? year,
        string? barcode,
        IClock clock,
        CancellationToken cancellationToken)
    {
        var found = await shop.FindAsync(title, artist, year, barcode, cancellationToken)
            .ConfigureAwait(false);

        return found is null
            ? null
            : new ReleaseCover
            {
                ReleaseId = releaseId,
                Bytes = found.Bytes,
                MediaType = found.MediaType,
                QobuzAlbumId = found.AlbumId,
                SavedUtc = Now(clock),
            };
    }

    /// <summary>
    /// Writes down what the sources answered, without overwriting a picture.
    /// </summary>
    /// <remarks>
    /// <b>An upsert rather than an insert, because the row may already exist</b>
    /// — that is what a week-old "nobody has one" is — and guarded on
    /// <c>Bytes IS NULL</c>, because between this request reading that row and
    /// writing this one somebody may have uploaded or chosen a cover. A rule's
    /// answer must not land on top of a person's; the guard is where
    /// <c>ChooseReleaseCover</c>'s unconditional upsert gets to win a race it
    /// did not know it was in.
    /// </remarks>
    private static async Task StoreFoundCoverAsync(
        FonotecaDbContext db,
        ReleaseCover cover,
        CancellationToken cancellationToken)
    {
        await db.Database
            .ExecuteSqlAsync(
                $"""
                INSERT INTO "ReleaseCovers"
                    ("ReleaseId", "Bytes", "MediaType", "ArchiveImageId", "QobuzAlbumId", "SavedUtc")
                VALUES ({cover.ReleaseId.Value}, {cover.Bytes}, {cover.MediaType},
                    {cover.ArchiveImageId}, {cover.QobuzAlbumId}, {cover.SavedUtc})
                ON CONFLICT ("ReleaseId") DO UPDATE SET
                    "Bytes" = excluded."Bytes",
                    "MediaType" = excluded."MediaType",
                    "ArchiveImageId" = excluded."ArchiveImageId",
                    "QobuzAlbumId" = excluded."QobuzAlbumId",
                    "SavedUtc" = excluded."SavedUtc"
                WHERE "ReleaseCovers"."Bytes" IS NULL
                """,
                cancellationToken)
            .ConfigureAwait(false);
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
                INSERT INTO "ReleaseCovers"
                    ("ReleaseId", "Bytes", "MediaType", "ArchiveImageId", "QobuzAlbumId", "SavedUtc")
                VALUES ({cover.ReleaseId.Value}, {cover.Bytes}, {cover.MediaType},
                    {cover.ArchiveImageId}, {cover.QobuzAlbumId}, {cover.SavedUtc})
                ON CONFLICT ("ReleaseId") DO UPDATE SET
                    "Bytes" = excluded."Bytes",
                    "MediaType" = excluded."MediaType",
                    "ArchiveImageId" = excluded."ArchiveImageId",
                    "QobuzAlbumId" = excluded."QobuzAlbumId",
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

    /// <summary>
    /// One of the two sources did not answer.
    /// </summary>
    /// <remarks>
    /// Named for the provider that failed rather than for the archive, since
    /// either can be the one that did — and the title is what reaches a person
    /// looking at why a tile is empty.
    /// </remarks>
    private static ProblemHttpResult ArchiveUnavailable(ProviderException cause) =>
        TypedResults.Problem(
            title: $"{cause.Provider} did not answer",
            detail: cause.Message,
            statusCode: StatusCodes.Status503ServiceUnavailable);
}

/// <param name="Mbid">Null for an album with no MusicBrainz id; <c>Images</c> is then empty.</param>
/// <param name="Chosen">The archive image currently stored, if the stored cover is one.</param>
/// <param name="Uploaded">
/// Whether the stored cover is a person's upload — which now means neither
/// source found it, rather than merely "not the archive's". A cover the shop
/// supplied is nobody's upload, and a screen saying otherwise tells somebody
/// they did something they did not do.
/// </param>
/// <param name="QobuzAlbum">Which Qobuz album supplied it, when one did.</param>
public sealed record ReleaseCoverOptions(
    Guid? Mbid,
    long? Chosen,
    bool Uploaded,
    string? QobuzAlbum,
    IReadOnlyList<ReleaseCoverOption> Images);

/// <param name="Front">Whether the archive itself calls this the front.</param>
public sealed record ReleaseCoverOption(
    long Id,
    bool Front,
    IReadOnlyList<string> Types,
    string? Comment);
