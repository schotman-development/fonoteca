using Fonoteca.Api.Endpoints;
using Fonoteca.Data;
using Fonoteca.Domain.Abstractions;
using Fonoteca.Ingest;
using Fonoteca.Providers.Qobuz;
using Fonoteca.Tagging;
using Microsoft.EntityFrameworkCore;

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
    /// folder's own <c>cover.jpg</c>.
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
                return await CatalogueEndpoints
                    .GetReleaseCover(display.Id.Value, db, archive, shop, store, describer, clock, http, cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        var path = await PathOfAsync(db, id, cancellationToken).ConfigureAwait(false)
            ?? (album is { } held ? await FirstFileOfAsync(db, held, cancellationToken).ConfigureAwait(false) : null);

        if (path is null) return SubsonicResult.Error(70, "No such cover art.");

        return await FileEndpoints
            .GetArt(store, describer, http, path, cancellationToken)
            .ConfigureAwait(false);
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
