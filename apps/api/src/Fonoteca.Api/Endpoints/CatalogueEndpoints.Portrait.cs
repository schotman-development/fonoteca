using Fonoteca.Data;
using Fonoteca.Domain.Abstractions;
using Fonoteca.Domain.Catalogue;
using Fonoteca.Ingest;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Net.Http.Headers;

namespace Fonoteca.Api.Endpoints;

/*
 * An artist's pictures: where they are served from, and how a person replaces
 * one.
 *
 * Three sources, in one order, and the order is the whole design:
 *
 *   1. what a person uploaded  — bytes in `ArtistImages`
 *   2. what is on their shelf  — artist.* or backdrop.* beside their records
 *   3. what a provider found   — a redirect to PortraitUrl or BannerUrl
 *
 * The shelf outranks the provider because the shelf is what every other player
 * on the same disk reads, and a page disagreeing with the rest of the machine
 * about somebody's face is the complaint this whole feature began as. An upload
 * outranks the shelf because it is a person's answer and it is instant — the
 * tag write is what puts it on the shelf, and that is a button, not a
 * consequence.
 *
 * Two kinds, one handler each way. `ArtistImageKind` holds the only thing that
 * differs between them: which name a player looks for on the disk.
 */
public static partial class CatalogueEndpoints
{
    /// <summary>The largest picture a person may upload for an artist. The cover's own cap.</summary>
    private const int MaxArtistImageBytes = 10 * 1024 * 1024;

    private const string UploadDescription =
        "The request body is the image itself, its Content-Type one of JPEG, PNG, GIF, WebP, "
        + "BMP or AVIF — never SVG, which is a document that runs script. At most 10 MB. "
        + "Stored beside the provider's answer rather than over it, so deleting it puts the "
        + "provider's picture back. It reaches the library the next time tags are written "
        + "for this artist.";

    private const string DeleteDescription =
        "The provider's picture applies again. The copy already written onto the artist's "
        + "shelf is left where it is until tags are written again — this endpoint touches "
        + "the catalogue, never the library.";

    private static void MapPortraitEndpoints(IEndpointRouteBuilder group)
    {
        group.MapGet("/artists/{id:guid}/portrait", (
                Guid id,
                FonotecaDbContext db,
                FileSystemAudioFileStore store,
                HttpContext context,
                CancellationToken cancellationToken,
                int? width) =>
                GetArtistImage(
                    id, ArtistImageKind.Portrait, db, store, context, width, cancellationToken))
            .WithName("GetArtistPortrait")
            .WithSummary("The artist's picture, from the shelf where one exists.")
            .WithDescription(
                "An uploaded picture first, then `artist.*` beside the artist's own records, "
                + "then a redirect to whatever a provider found. The shelf outranks the "
                + "provider because it is what every other player reading this library shows, "
                + "and the tag write is what puts a picture there. Served under an ETag with "
                + "`no-cache`, so a changed picture shows on the next view.")
            .Produces(StatusCodes.Status200OK, contentType: "image/jpeg")
            .Produces(StatusCodes.Status302Found)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapGet("/artists/{id:guid}/banner", (
                Guid id,
                FonotecaDbContext db,
                FileSystemAudioFileStore store,
                HttpContext context,
                CancellationToken cancellationToken,
                int? width) =>
                GetArtistImage(
                    id, ArtistImageKind.Banner, db, store, context, width, cancellationToken))
            .WithName("GetArtistBanner")
            .WithSummary("The wide picture for the head of the artist's page.")
            .WithDescription(
                "The same three sources the portrait has, reading `backdrop.*` off the shelf. "
                + "Navidrome does not show artist banners at all; Jellyfin, Kodi and Plex do.")
            .Produces(StatusCodes.Status200OK, contentType: "image/jpeg")
            .Produces(StatusCodes.Status302Found)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPost("/artists/{id:guid}/portrait", (
                Guid id,
                FonotecaDbContext db,
                IClock clock,
                HttpContext context,
                CancellationToken cancellationToken) =>
                UploadArtistImage(
                    id, ArtistImageKind.Portrait, db, clock, context, cancellationToken))
            .WithName("UploadArtistPortrait")
            .WithSummary("Use an uploaded picture for this artist.")
            .WithDescription(UploadDescription)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPost("/artists/{id:guid}/banner", (
                Guid id,
                FonotecaDbContext db,
                IClock clock,
                HttpContext context,
                CancellationToken cancellationToken) =>
                UploadArtistImage(
                    id, ArtistImageKind.Banner, db, clock, context, cancellationToken))
            .WithName("UploadArtistBanner")
            .WithSummary("Use an uploaded picture as this artist's banner.")
            .WithDescription(UploadDescription)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapDelete("/artists/{id:guid}/portrait", (
                Guid id, FonotecaDbContext db, CancellationToken cancellationToken) =>
                DeleteArtistImage(id, ArtistImageKind.Portrait, db, cancellationToken))
            .WithName("DeleteArtistPortrait")
            .WithSummary("Stop using the uploaded picture for this artist.")
            .WithDescription(DeleteDescription)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapDelete("/artists/{id:guid}/banner", (
                Guid id, FonotecaDbContext db, CancellationToken cancellationToken) =>
                DeleteArtistImage(id, ArtistImageKind.Banner, db, cancellationToken))
            .WithName("DeleteArtistBanner")
            .WithSummary("Stop using the uploaded banner for this artist.")
            .WithDescription(DeleteDescription)
            .ProducesProblem(StatusCodes.Status404NotFound);
    }

    /// <summary>
    /// The picture, or somewhere else to get it.
    /// </summary>
    /// <remarks>
    /// <b>A redirect rather than a proxy for the provider's own URL.</b> The
    /// page then has one <c>src</c> whatever the answer turns out to be, which
    /// is what lets the shelf take over silently the day a picture lands on it;
    /// proxying instead would mean this application fetching and holding a copy
    /// of every picture in the catalogue to serve pixels a browser can fetch
    /// itself.
    ///
    /// <b>The ETag is the source's own stamp, and the two sources cannot
    /// collide</b> — an upload's is <c>SavedUtc</c>, a shelf file's is its
    /// mtime and length — because the answer changes whole when the source
    /// does, and <c>no-cache</c> makes the browser revalidate every time.
    /// </remarks>
    private static async Task<Results<FileContentHttpResult, RedirectHttpResult, ProblemHttpResult>>
        GetArtistImage(
            Guid id,
            ArtistImageKind kind,
            FonotecaDbContext db,
            FileSystemAudioFileStore store,
            HttpContext context,
            int? width,
            CancellationToken cancellationToken)
    {
        var artistId = new ArtistId(id);

        var artist = await db.Artists
            .AsNoTracking()
            .Where(row => row.Id == artistId)
            .Select(row => new
            {
                row.Name,
                row.LatinName,
                row.PortraitUrl,
                row.BannerUrl,
                row.EditsJson,
            })
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        if (artist is null) return NoSuchArtist(id);

        context.Response.Headers.CacheControl = "no-cache";
        context.Response.Headers.XContentTypeOptions = "nosniff";

        var uploaded = await db.ArtistImages
            .AsNoTracking()
            .FirstOrDefaultAsync(
                row => row.ArtistId == artistId && row.Kind == kind.Name, cancellationToken)
            .ConfigureAwait(false);

        if (uploaded is not null)
        {
            return TypedResults.File(
                uploaded.Bytes,
                uploaded.MediaType,
                lastModified: uploaded.SavedUtc,
                entityTag: new EntityTagHeaderValue($"\"{uploaded.SavedUtc.UtcTicks}\""));
        }

        if (OnShelf(store, kind, artist.Name, artist.LatinName) is { } shelved)
        {
            var facts = new FileInfo(shelved);

            return TypedResults.File(
                await File.ReadAllBytesAsync(shelved, cancellationToken).ConfigureAwait(false),
                FilePreview.Of(shelved).MediaType,
                lastModified: facts.LastWriteTimeUtc,
                entityTag: new EntityTagHeaderValue(
                    $"\"{facts.LastWriteTimeUtc.Ticks:x}-{facts.Length:x}\""));
        }

        // <b>A person's pick, not the raw column.</b> The profile editor writes
        // a corrected picture into `EditsJson` beside the provider's answer
        // rather than over it (rule 4), so reading the column alone makes
        // picking a different one do nothing at all — which is exactly what it
        // did until this line existed.
        var picked = PersonEdits.Apply(
            PersonEdits.Read(artist.EditsJson),
            kind.Name,
            kind == ArtistImageKind.Banner ? artist.BannerUrl : artist.PortraitUrl);

        // Sized on the way out rather than in the browser, because which
        // rendition exists is the provider's business and `PortraitRendition`
        // is where that is written down. A grid of unsized Qobuz `large` files
        // was measured at 39.7 MB drawn into 112px circles.
        if (picked is { Length: > 0 } url)
        {
            return TypedResults.Redirect(
                PortraitRendition.Sized(url, width ?? PortraitRendition.DefaultWidth));
        }

        return TypedResults.Problem(
            title: "No picture",
            detail: $"Nothing has found a {kind.Name} for this artist, and none has been uploaded.",
            statusCode: StatusCodes.Status404NotFound);
    }

    /// <summary>
    /// This artist's own shelf, and the picture of this kind on it, or null.
    /// </summary>
    /// <remarks>
    /// <b>The library root is listed rather than the artist's name being
    /// combined into a path</b>, because the folder is only ever matched
    /// through <see cref="ArtistShelf"/> — the same rule, asked the same way,
    /// as the tag write asks it of a file's own folder. A path built from a
    /// name would agree with that rule only where the spelling happens to match
    /// exactly, so the page would show a picture the pass refuses to write, or
    /// miss one it wrote.
    ///
    /// One <c>readdir</c> of the root per request, which the OS has cached; the
    /// alternative is a column holding a path, and a path in a column is a fact
    /// about the disk that nothing would keep true across a rename.
    /// </remarks>
    private static string? OnShelf(
        FileSystemAudioFileStore store,
        ArtistImageKind kind,
        string name,
        string? latinName)
    {
        if (!store.RootExists) return null;

        foreach (var directory in Directory.EnumerateDirectories(store.Root))
        {
            var leaf = Path.GetFileName(directory);

            if (!ArtistShelf.IsNamedFor(leaf, name, latinName)) continue;

            string shelf;

            try
            {
                // <b>Lexical containment is not containment, and this endpoint
                // reads bytes off a disk.</b> `ln -s /etc "Janine Jansen"`
                // inside the library is under the root by every string
                // comparison and somewhere else entirely on the disk, so the
                // matched folder is put through `Resolve` — which walks the
                // link chain in `EnsureNoLinkedDirectory` and refuses — rather
                // than being enumerated where it was found. The tag write
                // already goes through it; this went round it, and a shelf
                // pointing outside the library served its bytes.
                shelf = store.AbsolutePathFor(new LibraryPath(leaf));
            }
            catch (UnauthorizedAccessException)
            {
                // A shelf reached through a link is not this library's, so it
                // is not this artist's either. Fall through to the provider.
                return null;
            }

            return Directory
                .EnumerateFiles(shelf, kind.Glob, ShelfGlob)
                .Where(path => FilePreview.Of(path).Kind == PreviewKind.Image)
                .Order(StringComparer.Ordinal)
                .FirstOrDefault();
        }

        return null;
    }

    /// <summary>Case-insensitively, because <c>Artist.JPG</c> is the same claim.</summary>
    private static readonly EnumerationOptions ShelfGlob = new()
    {
        MatchCasing = MatchCasing.CaseInsensitive,
    };

    /// <remarks>
    /// The body is the image, as it is for a cover and for the same reason:
    /// <c>IFormFile</c> buffers over 64 KB to a temp file, which writes every
    /// byte twice and into RAM where <c>/tmp</c> is tmpfs. The type is the
    /// declared one, checked against the raster allowlist.
    /// </remarks>
    private static async Task<Results<NoContent, ProblemHttpResult>> UploadArtistImage(
        Guid id,
        ArtistImageKind kind,
        FonotecaDbContext db,
        IClock clock,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        var artistId = new ArtistId(id);

        if (!await db.Artists.AnyAsync(artist => artist.Id == artistId, cancellationToken)
                .ConfigureAwait(false))
        {
            return NoSuchArtist(id);
        }

        var mediaType = context.Request.ContentType is { } header
            && MediaTypeHeaderValue.TryParse(header, out var parsed)
                ? parsed.MediaType.Value
                : null;

        if (!FilePreview.IsSafeImageMediaType(mediaType) || mediaType is null)
        {
            return TypedResults.Problem(
                title: "Not a picture this application will serve",
                detail: "Upload a JPEG, PNG, GIF, WebP, BMP or AVIF image.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        using var buffer = new MemoryStream();
        var chunk = new byte[81_920];
        int read;

        while ((read = await context.Request.Body
                   .ReadAsync(chunk, cancellationToken)
                   .ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > MaxArtistImageBytes)
            {
                return TypedResults.Problem(
                    title: "The picture is too large",
                    detail: $"A picture may be at most {MaxArtistImageBytes / 1024 / 1024} MB.",
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

        var existing = await db.ArtistImages
            .FirstOrDefaultAsync(
                row => row.ArtistId == artistId && row.Kind == kind.Name, cancellationToken)
            .ConfigureAwait(false);

        if (existing is null)
        {
            db.ArtistImages.Add(new ArtistImage
            {
                ArtistId = artistId,
                Kind = kind.Name,
                Bytes = buffer.ToArray(),
                MediaType = mediaType,
                SavedUtc = Now(clock),
            });
        }
        else
        {
            existing.Bytes = buffer.ToArray();
            existing.MediaType = mediaType;
            existing.SavedUtc = Now(clock);
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return TypedResults.NoContent();
    }

    private static ProblemHttpResult NoSuchArtist(Guid id) =>
        TypedResults.Problem(
            title: "No such artist",
            detail: $"The catalogue has no artist with id {id}.",
            statusCode: StatusCodes.Status404NotFound);

    private static async Task<Results<NoContent, ProblemHttpResult>> DeleteArtistImage(
        Guid id,
        ArtistImageKind kind,
        FonotecaDbContext db,
        CancellationToken cancellationToken)
    {
        var artistId = new ArtistId(id);

        var uploaded = await db.ArtistImages
            .FirstOrDefaultAsync(
                row => row.ArtistId == artistId && row.Kind == kind.Name, cancellationToken)
            .ConfigureAwait(false);

        if (uploaded is null)
        {
            return await db.Artists.AnyAsync(artist => artist.Id == artistId, cancellationToken)
                .ConfigureAwait(false)
                ? TypedResults.NoContent()
                : NoSuchArtist(id);
        }

        db.ArtistImages.Remove(uploaded);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return TypedResults.NoContent();
    }
}
