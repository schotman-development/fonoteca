using System.Globalization;
using Fonoteca.Api.Endpoints;
using Fonoteca.Data;
using Fonoteca.Domain.Abstractions;
using Fonoteca.Ingest;
using Fonoteca.Providers.Qobuz;
using Fonoteca.Tagging;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Net.Http.Headers;

namespace Fonoteca.Api.Subsonic;

/// <summary>
/// The bytes: one song, and one picture of an album.
/// </summary>
/// <remarks>
/// <b>Nothing here serves a file itself.</b> Both routes resolve an id to a
/// library-relative path and hand it to <c>FileEndpoints.GetContent</c>, which
/// already answers <c>Range</c>, <c>If-Range</c> and 206 through
/// <c>PhysicalFile</c>, already resolves the path through
/// <c>FileSystemAudioFileStore</c> — containment check, symlinked-directory
/// refusal and all — and already takes its media type from the
/// <c>FilePreview</c> allowlist rather than from the extension.
///
/// That is the <c>/mcp</c> rule and it matters more here than there. The rule
/// that would drift if this were copied is the one deciding whether a path
/// escapes the library root, and <i>lexical containment is not containment</i>
/// is a lesson this repository has already paid for once.
///
/// <b>No transcoding.</b> <c>maxBitRate</c> and <c>format</c> are accepted and
/// ignored and the bytes go out as they are. ffmpeg is already a dependency, so
/// the upgrade is a pipe on the day somebody is measured to need it; over a LAN,
/// what these clients want is the FLAC.
/// </remarks>
public static partial class SubsonicEndpoints
{
    private static Task<IResult> Stream(
        HttpContext http,
        FileSystemAudioFileStore store,
        FonotecaDbContext db,
        CancellationToken cancellationToken) =>
        Serve(http, store, db, cancellationToken);

    /// <summary>
    /// The same bytes, and deliberately the same handler.
    /// </summary>
    /// <remarks>
    /// The protocol distinguishes them so a server can transcode one and not the
    /// other. This one transcodes neither, so a difference between them would be
    /// a difference with nothing behind it.
    /// </remarks>
    private static Task<IResult> Download(
        HttpContext http,
        FileSystemAudioFileStore store,
        FonotecaDbContext db,
        CancellationToken cancellationToken) =>
        Serve(http, store, db, cancellationToken);

    private static async Task<IResult> Serve(
        HttpContext http,
        FileSystemAudioFileStore store,
        FonotecaDbContext db,
        CancellationToken cancellationToken)
    {
        var path = await PathOfAsync(db, Text(http, "id"), cancellationToken).ConfigureAwait(false);

        if (path is null) return SubsonicResult.Error(70, "No such song.");

        return FileEndpoints.GetContent(store, http, path);
    }

    /// <summary>
    /// A picture of an album, an artist's record, or a folder.
    /// </summary>
    /// <remarks>
    /// One endpoint over three existing answers, which is why the ids carry a
    /// prefix at all. An album — or a song filed under one — is the stored
    /// <c>ReleaseCover</c>, fetched once and served under an ETag. Anything else
    /// is a path, and a path's picture is the file's embedded cover or the
    /// folder's own <c>cover.jpg</c>. Each comes no larger than the <c>size</c>
    /// asked for (<see cref="SizedAsync"/>).
    ///
    /// <b>A refusal here is a problem document, not a Subsonic error.</b> The
    /// protocol says an error is XML even on a binary endpoint, and clients do
    /// not read it: every one of them treats a non-image as "no cover" and draws
    /// its placeholder. Answering in the envelope would cost a translation layer
    /// to change nothing a person sees.
    /// </remarks>
    private static async Task<IResult> GetCoverArt(
        HttpContext http,
        FonotecaDbContext db,
        FileSystemAudioFileStore store,
        AudioFileDescriber describer,
        ICoverArtArchive archive,
        QobuzCovers shop,
        IClock clock,
        FfmpegCoverScaler scaler,
        CancellationToken cancellationToken)
    {
        var id = Text(http, "id");

        var album = SubsonicIds.AsAlbum(id)
            ?? await AlbumOfAsync(db, SubsonicIds.AsSong(id), cancellationToken)
                .ConfigureAwait(false);

        // The sleeve of the edition that stands for the album, as on the album
        // page; an album with no edition stored falls through to its own files.
        if (album is { } group)
        {
            var editions = await CatalogueEndpoints
                .EditionFactsAsync(db, [group], cancellationToken)
                .ConfigureAwait(false);

            if (CatalogueEndpoints.DisplayEdition(editions[group]) is { } display)
            {
                var cover = await CatalogueEndpoints
                    .GetReleaseCover(display.Id.Value, db, archive, shop, store, describer, clock, http, cancellationToken)
                    .ConfigureAwait(false);

                return await SizedAsync(http, scaler, cover, cancellationToken).ConfigureAwait(false);
            }
        }

        var path = await PathOfAsync(db, id, cancellationToken).ConfigureAwait(false)
            ?? (album is { } held ? await FirstFileOfAsync(db, held, cancellationToken).ConfigureAwait(false) : null);

        if (path is null) return SubsonicResult.Error(70, "No such cover art.");

        var art = await FileEndpoints
            .GetArt(store, describer, http, path, cancellationToken)
            .ConfigureAwait(false);

        return await SizedAsync(http, scaler, art, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The cover no larger than the <c>size</c> the client asked for.
    /// </summary>
    /// <remarks>
    /// A client asks for a size so it need not decode pixels it throws away: a
    /// page of album tiles drawing 1200px archive fronts at 160px spends its
    /// time decoding, and a picture shrunk that far by a renderer looks grainier
    /// than one scaled once, properly. The scaled copy goes out under the
    /// original's ETag and date, which still say when this picture last changed.
    /// </remarks>
    private static async Task<IResult> SizedAsync(
        HttpContext http,
        FfmpegCoverScaler scaler,
        IResult cover,
        CancellationToken cancellationToken)
    {
        if (!int.TryParse(Text(http, "size"), NumberStyles.None, CultureInfo.InvariantCulture, out var size)
            || size <= 0)
        {
            return cover;
        }

        switch (cover is INestedHttpResult nested ? nested.Result : cover)
        {
            case FileContentHttpResult content:
            {
                var scaled = await scaler
                    .ScaleAsync(content.FileContents.ToArray(), size, cancellationToken)
                    .ConfigureAwait(false);

                return scaled is null
                    ? cover
                    : TypedResults.File(scaled, "image/jpeg", lastModified: content.LastModified, entityTag: content.EntityTag);
            }

            case FileStreamHttpResult stream:
            {
                byte[] picture;

                await using (stream.FileStream.ConfigureAwait(false))
                {
                    using var copy = new MemoryStream();
                    await stream.FileStream.CopyToAsync(copy, cancellationToken).ConfigureAwait(false);
                    picture = copy.ToArray();
                }

                var scaled = await scaler.ScaleAsync(picture, size, cancellationToken).ConfigureAwait(false);

                // Read now, so even a picture served as it is goes out as bytes.
                return TypedResults.File(
                    scaled ?? picture,
                    scaled is null ? stream.ContentType : "image/jpeg",
                    lastModified: stream.LastModified,
                    entityTag: stream.EntityTag);
            }

            default:
                return cover;
        }
    }

    /// <summary>
    /// An album's motion artwork, as an animated WebP no larger than <c>size</c>.
    /// </summary>
    /// <remarks>
    /// Fonoteca's own extension, <c>fonotecaAlbumMotion</c>: the protocol has no
    /// motion artwork, and the MP4 the shop sells needs a video decoder where an
    /// animated picture needs only an image one. The square video, the one kept
    /// (ADR 0014), converted once per size and kept by the covers' scaler, full
    /// length and never larger than the video. An album with none, or one ffmpeg
    /// could not convert, answers as a missing cover does.
    ///
    /// ponytail: the video is read from the database on every request, since
    /// the kept copy is named by its bytes — about 25 MB, a few tens of
    /// milliseconds locally. Name the copy by the row's <c>SavedUtc</c> if that
    /// read is ever measured to matter.
    /// </remarks>
    private static async Task<IResult> GetAlbumMotion(
        HttpContext http,
        FonotecaDbContext db,
        FfmpegCoverScaler scaler,
        CancellationToken cancellationToken)
    {
        var motion = SubsonicIds.AsAlbum(Text(http, "id")) is { } album
            ? await db.AlbumMotions
                .AsNoTracking()
                .Where(row => row.ReleaseGroupId == album && row.Square != null)
                .Select(row => new { row.Square, row.SavedUtc })
                .FirstOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false)
            : null;

        if (motion?.Square is not { } video) return SubsonicResult.Error(70, "No such album motion.");

        var size = int.TryParse(Text(http, "size"), NumberStyles.None, CultureInfo.InvariantCulture, out var asked)
            ? asked
            : 0;

        var animated = await scaler.AnimateAsync(video, size, cancellationToken).ConfigureAwait(false);

        if (animated is null) return SubsonicResult.Error(70, "No such album motion.");

        return TypedResults.File(
            animated,
            "image/webp",
            lastModified: motion.SavedUtc,
            entityTag: new EntityTagHeaderValue($"\"{motion.SavedUtc.UtcTicks}\""));
    }

    /// <summary>
    /// The library-relative path behind a song id, whichever kind it is.
    /// </summary>
    /// <remarks>
    /// A <c>tr-</c> id is looked up, because a file's path moves and its row is
    /// what follows it. A <c>fo-</c> id carries the path already, and is checked
    /// no further here — <c>GetContent</c> resolves it through the store, which
    /// is where containment is decided for every caller.
    /// </remarks>
    private static async Task<string?> PathOfAsync(
        FonotecaDbContext db,
        string? id,
        CancellationToken cancellationToken)
    {
        if (SubsonicIds.AsSong(id) is { } fileId)
        {
            return await db.MediaFiles
                .AsNoTracking()
                .Where(file => file.Id == fileId)
                .Select(file => file.Path)
                .FirstOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false);
        }

        return SubsonicIds.AsPath(id);
    }

    private static async Task<Domain.Catalogue.ReleaseGroupId?> AlbumOfAsync(
        FonotecaDbContext db,
        Domain.Catalogue.MediaFileId? file,
        CancellationToken cancellationToken)
    {
        if (file is not { } fileId) return null;

        return await db.MediaFiles
            .AsNoTracking()
            .Where(row => row.Id == fileId)
            .Select(row => row.ReleaseGroupId)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>The album's first file by path, whose own embedded picture stands in for a sleeve.</summary>
    private static async Task<string?> FirstFileOfAsync(
        FonotecaDbContext db,
        Domain.Catalogue.ReleaseGroupId album,
        CancellationToken cancellationToken) =>
        await db.MediaFiles
            .AsNoTracking()
            .Where(row => row.ReleaseGroupId == album)
            .OrderBy(row => row.Path)
            .Select(row => row.Path)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
}
