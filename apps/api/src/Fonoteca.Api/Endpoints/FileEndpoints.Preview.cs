using Fonoteca.Domain.Abstractions;
using Fonoteca.Domain.Catalogue;
using Fonoteca.Ingest;
using Fonoteca.Tagging;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Fonoteca.Api.Endpoints;

/// <summary>
/// Showing a person what a file is, rather than telling them what it is called.
/// </summary>
/// <remarks>
/// The listing answers "what is here". These three answer "what is <i>this</i>",
/// and between them they are what makes the screen a file browser rather than a
/// table of names.
///
/// <list type="bullet">
/// <item><b>content</b> — the bytes, ranged. An <c>&lt;img&gt;</c>, an
/// <c>&lt;audio&gt;</c> and a <c>fetch</c> of the first 8 KB of a rip log are
/// all this one endpoint.</item>
/// <item><b>detail</b> — what a decoder and a tag library make of one audio
/// file, <b>by path</b>. That is the point: an album uploaded four seconds ago
/// has no catalogue row and no id, and it is exactly the file somebody wants to
/// look at.</item>
/// <item><b>art</b> — a picture of an entry. A file's embedded cover, or a
/// folder's own <c>cover.jpg</c>.</item>
/// </list>
///
/// <b>Serving bytes out of the library is the one genuinely new risk in this
/// feature</b>, and it is answered in one place: <see cref="FilePreview"/> is an
/// allowlist, so a media type is never guessed from an extension. Anything it
/// does not name is <c>application/octet-stream</c> sent as an attachment, and
/// every response carries <c>nosniff</c> so a browser cannot decide otherwise.
/// An <c>.html</c> dropped in a music folder is a download, not a page on this
/// application's origin.
///
/// <b>Range support is ASP.NET's, not ours.</b> <c>PhysicalFile</c> with
/// <c>enableRangeProcessing</c> handles <c>Range</c>, <c>If-Range</c> and the
/// 206 itself, which is what lets an <c>&lt;audio&gt;</c> element seek into the
/// middle of a 400 MB FLAC without reading the first 399. Note what this does
/// <i>not</i> do: <c>IAudioFileStore.OpenRangeAsync</c> is still unimplemented
/// and still has no caller. It was written for this and turned out not to be
/// needed — the framework already does it, over a path we have already
/// contained.
/// </remarks>
public static partial class FileEndpoints
{
    /// <summary>
    /// What a folder's own cover is called, in the order the answer is taken.
    /// </summary>
    /// <remarks>
    /// Ordered rather than sorted: on a library where both exist, `cover.jpg` is
    /// the front and `folder.jpg` is what a Windows media player wrote, so the
    /// first is the better answer. `backdrop` and `logo` are deliberately absent
    /// — they sit beside these in this very library and neither is the album.
    /// </remarks>
    private static readonly string[] FolderArtNames =
        ["cover", "folder", "front", "album", "albumart"];

    private static void MapPreview(RouteGroupBuilder group)
    {
        group.MapGet("/content", GetContent)
            .WithName("GetLibraryFileContent")
            .WithSummary("The bytes of one file, with range support.")
            .WithDescription(
                "What an <img>, an <audio> or a partial fetch of a rip log reads. Range requests "
                + "are handled, so seeking into a large FLAC does not read the part before it.\n\n"
                + "**The media type comes from an allowlist, never from the extension itself.** "
                + "Anything not on it is served as application/octet-stream and as an attachment, "
                + "and every response carries X-Content-Type-Options: nosniff — an .html in a "
                + "music folder is a download, not a page on this origin.")
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapGet("/detail", GetDetail)
            .WithName("GetLibraryFileDetail")
            .WithSummary("What one file says about itself, by path.")
            .WithDescription(
                "The decoder's measurements and the file's own tags, for any audio file in the "
                + "library — including one no scan has seen yet, which is why this is keyed on a "
                + "path rather than on a media file id.\n\n"
                + "**It cannot fail on the file.** A truncated FLAC, an unmounted volume and a "
                + "tag parser that dereferences null all come back as a reading with a note in "
                + "it, because a screen that 500s on the file you opened it for is worse than one "
                + "that says the decoder would not read it.")
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapGet("/art", GetArt)
            .WithName("GetLibraryEntryArt")
            .WithSummary("A picture of one entry: a file's embedded cover, or a folder's own.")
            .WithDescription(
                "For an audio file, the front cover stored inside it, served unchanged. For a "
                + "directory, the first of cover/folder/front/album it holds.\n\n"
                + "**404 is the ordinary answer, not an error.** Plenty of files carry no "
                + "picture and plenty of folders hold none, and the screen draws a monogram for "
                + "both.")
            .Produces(
                StatusCodes.Status200OK,
                contentType: "image/jpeg",
                additionalContentTypes: "image/png")
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound);
    }

    private static Results<PhysicalFileHttpResult, NotFound, ProblemHttpResult> GetContent(
        FileSystemAudioFileStore store,
        HttpContext context,
        string path)
    {
        string absolute;

        try
        {
            absolute = store.AbsolutePathFor(new LibraryPath(path));
        }
        catch (UnauthorizedAccessException)
        {
            return Outside(path);
        }

        var info = new FileInfo(absolute);

        // Directories are listed, not served. Without this a request for one
        // reaches PhysicalFile and comes back as an unhandled IO failure.
        if (!info.Exists) return TypedResults.NotFound();

        var type = FilePreview.Of(path);

        NoSniff(context);

        return TypedResults.PhysicalFile(
            absolute,
            type.MediaType,
            // A download name is what turns Content-Disposition into
            // "attachment", so it is set for exactly the types this application
            // refuses to vouch for and left null for the ones it renders.
            fileDownloadName: type.Kind == PreviewKind.None ? info.Name : null,
            lastModified: new DateTimeOffset(info.LastWriteTimeUtc),
            entityTag: null,
            enableRangeProcessing: true);
    }

    private static async Task<Results<Ok<FilePreviewResponse>, NotFound, ProblemHttpResult>> GetDetail(
        FileSystemAudioFileStore store,
        AudioFileDescriber describer,
        string path,
        CancellationToken cancellationToken)
    {
        string absolute;

        try
        {
            absolute = store.AbsolutePathFor(new LibraryPath(path));
        }
        catch (UnauthorizedAccessException)
        {
            return Outside(path);
        }

        var info = new FileInfo(absolute);

        if (!info.Exists) return TypedResults.NotFound();

        var type = FilePreview.Of(path);
        var isAudio = AudioFormats.IsAudioFile(path);

        // Only audio is described, and only audio costs a subprocess. An image
        // or a log needs its size and its media type, both of which are already
        // in hand.
        var reading = isAudio
            ? await describer.DescribeAsync(new LibraryPath(path), cancellationToken)
                .ConfigureAwait(false)
            : null;

        return TypedResults.Ok(new FilePreviewResponse(
            Path: path,
            Name: info.Name,
            SizeBytes: info.Length,
            ModifiedUtc: new DateTimeOffset(info.LastWriteTimeUtc),
            Kind: type.Kind.ToString(),
            MediaType: type.MediaType,
            IsAudio: isAudio,
            DurationSeconds: reading?.Duration?.TotalSeconds,
            DecodedCleanly: reading?.DecodedCleanly,
            Audio: reading?.Quality is { } quality
                ? new AudioQualityRow(
                    quality.Codec,
                    (int)(quality.BitrateBps / 1000),
                    quality.SampleRateHz,
                    quality.BitDepth,
                    quality.Channels,
                    quality.IsLossless,
                    quality.Tier.ToString())
                : null,
            Tags: reading is null
                ? []
                : [.. reading.Tags.Select(tag => new FileTagRow(tag.Name, tag.Value))],
            Note: reading?.Note));
    }

    private static async Task<Results<FileStreamHttpResult, FileContentHttpResult, NotFound, ProblemHttpResult>> GetArt(
        FileSystemAudioFileStore store,
        AudioFileDescriber describer,
        HttpContext context,
        string path,
        CancellationToken cancellationToken)
    {
        string absolute;

        try
        {
            absolute = store.AbsolutePathFor(new LibraryPath(path));
        }
        catch (UnauthorizedAccessException)
        {
            return Outside(path);
        }

        NoSniff(context);

        // A folder's picture is a file sitting in it. An audio file's is inside
        // it. Same question, two entirely different reads.
        if (Directory.Exists(absolute))
        {
            var cover = FolderArt(absolute);

            if (cover is not null)
            {
                var coverType = FilePreview.Of(cover);

                return TypedResults.File(
                    File.OpenRead(cover),
                    coverType.MediaType,
                    lastModified: new DateTimeOffset(File.GetLastWriteTimeUtc(cover)),
                    entityTag: null);
            }

            // No cover file, so ask the music. Most rips carry the front cover
            // inside every track and never write it beside them — measured on
            // this library, where `Violin Concertos (2001)` has no cover.jpg and
            // every FLAC in it holds the sleeve. Without this the commonest
            // album folder on the screen draws a monogram.
            //
            // One file opened, on a click, for the folder somebody selected. It
            // is not on the listing path: a row does not ask for a picture until
            // it is the row being looked at.
            var first = FirstAudioIn(absolute);

            if (first is null) return TypedResults.NotFound();

            // Composed from the folder the caller already named rather than by
            // asking the store to un-resolve an absolute path — the relative
            // path is the thing that came in, so it is the thing to build on.
            var trimmed = path.Trim('/');
            var name = Path.GetFileName(first);

            var embedded = await describer
                .ReadArtworkAsync(
                    new LibraryPath(trimmed.Length == 0 ? name : $"{trimmed}/{name}"),
                    cancellationToken)
                .ConfigureAwait(false);

            return embedded is null
                ? TypedResults.NotFound()
                : TypedResults.File(embedded.Bytes, embedded.MimeType);
        }

        if (!File.Exists(absolute) || !AudioFormats.IsAudioFile(path))
        {
            return TypedResults.NotFound();
        }

        var artwork = await describer.ReadArtworkAsync(new LibraryPath(path), cancellationToken)
            .ConfigureAwait(false);

        return artwork is null
            ? TypedResults.NotFound()
            : TypedResults.File(artwork.Bytes, artwork.MimeType);
    }

    /// <summary>The first recognised cover image sitting directly in a folder.</summary>
    /// <remarks>
    /// One shallow enumeration, matched case-insensitively against
    /// <see cref="FolderArtNames"/> and filtered through the same allowlist
    /// everything else here uses — so a <c>cover.svg</c> is not a folder's
    /// picture, for the reason <see cref="FilePreview"/> gives.
    /// </remarks>
    private static string? FolderArt(string directory)
    {
        foreach (var name in FolderArtNames)
        {
            foreach (var candidate in SafeEnumerate(directory, name + ".*"))
            {
                if (FilePreview.Of(candidate).Kind == PreviewKind.Image) return candidate;
            }
        }

        return null;
    }

    /// <summary>
    /// The first audio file sitting directly in a folder, by name.
    /// </summary>
    /// <remarks>
    /// By name rather than by whatever the filesystem returns first, so the
    /// picture a folder shows does not change between two requests for it. On an
    /// album that is track 1, which is also the one most likely to carry the
    /// front cover rather than a booklet page.
    /// </remarks>
    private static string? FirstAudioIn(string directory) =>
        SafeEnumerate(directory, "*")
            .Where(candidate => AudioFormats.IsAudioFile(candidate))
            .OrderBy(candidate => candidate, StringComparer.Ordinal)
            .FirstOrDefault();

    /// <summary>
    /// A directory read that answers "nothing" rather than throwing.
    /// </summary>
    /// <remarks>
    /// An unmounted volume or a directory this process cannot read is a folder
    /// with no picture as far as this screen is concerned. Nothing here is worth
    /// a 500.
    /// </remarks>
    private static IEnumerable<string> SafeEnumerate(string directory, string pattern)
    {
        try
        {
            // Case-insensitively, which on Linux is not the default and which
            // FolderArt's remarks claimed without it: six album folders in the
            // target library spell it `Front.jpg`, and they were falling through
            // to the embedded cover rather than to the file sitting next to them.
            return Directory.EnumerateFiles(directory, pattern, new EnumerationOptions
            {
                MatchCasing = MatchCasing.CaseInsensitive,
                IgnoreInaccessible = true,
            });
        }
        catch (IOException)
        {
            return [];
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>
    /// Forbids the browser from second-guessing the media type above.
    /// </summary>
    /// <remarks>
    /// The allowlist decides what a file is claimed to be; without this a
    /// browser may sniff an <c>application/octet-stream</c> and decide it is
    /// HTML after all, which hands back the whole reason the allowlist exists.
    /// </remarks>
    private static void NoSniff(HttpContext context) =>
        context.Response.Headers.XContentTypeOptions = "nosniff";
}

/// <param name="Kind">
/// <c>None</c>, <c>Audio</c>, <c>Image</c> or <c>Text</c> — what the client may
/// show, decided by the same allowlist that decides what is served.
/// </param>
/// <param name="DecodedCleanly">
/// Null when nothing was decoded. False means the decoder objected to these
/// bytes while reading them — often the most useful thing on the screen.
/// </param>
public sealed record FilePreviewResponse(
    string Path,
    string Name,
    long SizeBytes,
    DateTimeOffset ModifiedUtc,
    string Kind,
    string MediaType,
    bool IsAudio,
    double? DurationSeconds,
    bool? DecodedCleanly,
    AudioQualityRow? Audio,
    IReadOnlyList<FileTagRow> Tags,
    string? Note);
