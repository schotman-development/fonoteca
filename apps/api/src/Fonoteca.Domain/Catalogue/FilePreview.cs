using System.Collections.Frozen;

namespace Fonoteca.Domain.Catalogue;

/// <summary>What, if anything, a browser can be shown of a file.</summary>
public enum PreviewKind
{
    /// <summary>Nothing. The bytes are still served, as an opaque download.</summary>
    None = 0,

    /// <summary>An <c>&lt;audio&gt;</c> element can play it.</summary>
    Audio = 1,

    /// <summary>An <c>&lt;img&gt;</c> element can draw it.</summary>
    Image = 2,

    /// <summary>It is prose, and the first few kilobytes are worth reading.</summary>
    Text = 3,
}

/// <summary>
/// Which extensions may be previewed, and as what.
/// </summary>
/// <remarks>
/// Pure, and in the domain for the reason <see cref="AudioFormats"/> is: it is a
/// rule about names, not an act on a filesystem.
///
/// <b>It is an allowlist, and that is the security control rather than a
/// convenience.</b> The file manager serves bytes out of a directory whose
/// contents nobody vetted, from the same origin as the application. A media type
/// guessed from an extension and echoed back is how a stray file in a music
/// library becomes script running on the page that lists it — so anything not
/// named here is <c>application/octet-stream</c>, and the endpoint sends it as
/// an attachment.
///
/// Two absences are deliberate and must stay absent:
///
/// <list type="bullet">
/// <item><b>No <c>text/html</c>.</b> An <c>.html</c> in the library is served as
/// a download, not rendered. There is no version of "preview the web page in the
/// music folder" worth the same-origin scripting it buys.</item>
/// <item><b>No <c>image/svg+xml</c>.</b> SVG is a document that can carry
/// script, so it is an image everywhere except in the way that matters here.
/// The raster formats below cannot execute anything.</item>
/// </list>
///
/// <b><see cref="PreviewKind.Audio"/> means a browser can play it, not that it
/// is audio.</b> Monkey's Audio, WavPack and DSD are audio to
/// <see cref="AudioFormats"/> and to every pass in this application, and no
/// browser decodes them. Filed as <see cref="PreviewKind.None"/> they offer no
/// player, which is the truth; the tags and the measured quality still show,
/// because those come from a decoder on the server rather than from the
/// browser.
/// </remarks>
public static class FilePreview
{
    private static readonly FrozenDictionary<string, FilePreviewType> Known =
        new Dictionary<string, FilePreviewType>(StringComparer.OrdinalIgnoreCase)
        {
            // Audio a browser decodes. Measured against what Chromium and
            // Firefox actually accept, not against what the container can hold.
            ["flac"] = new(PreviewKind.Audio, "audio/flac"),
            ["mp3"] = new(PreviewKind.Audio, "audio/mpeg"),
            ["m4a"] = new(PreviewKind.Audio, "audio/mp4"),
            ["m4b"] = new(PreviewKind.Audio, "audio/mp4"),
            ["aac"] = new(PreviewKind.Audio, "audio/aac"),
            ["ogg"] = new(PreviewKind.Audio, "audio/ogg"),
            ["oga"] = new(PreviewKind.Audio, "audio/ogg"),
            ["opus"] = new(PreviewKind.Audio, "audio/ogg"),
            ["wav"] = new(PreviewKind.Audio, "audio/wav"),
            ["aiff"] = new(PreviewKind.Audio, "audio/aiff"),
            ["aif"] = new(PreviewKind.Audio, "audio/aiff"),

            // Raster images only. See the remarks for why SVG is not here.
            ["jpg"] = new(PreviewKind.Image, "image/jpeg"),
            ["jpeg"] = new(PreviewKind.Image, "image/jpeg"),
            ["png"] = new(PreviewKind.Image, "image/png"),
            ["gif"] = new(PreviewKind.Image, "image/gif"),
            ["webp"] = new(PreviewKind.Image, "image/webp"),
            ["bmp"] = new(PreviewKind.Image, "image/bmp"),
            ["avif"] = new(PreviewKind.Image, "image/avif"),

            // What sits beside a rip: the log that proves it, the cue sheet that
            // describes it, the playlist that orders it. Always text/plain, never
            // the type the extension suggests.
            ["txt"] = new(PreviewKind.Text, "text/plain"),
            ["log"] = new(PreviewKind.Text, "text/plain"),
            ["cue"] = new(PreviewKind.Text, "text/plain"),
            ["m3u"] = new(PreviewKind.Text, "text/plain"),
            ["m3u8"] = new(PreviewKind.Text, "text/plain"),
            ["nfo"] = new(PreviewKind.Text, "text/plain"),
            ["md"] = new(PreviewKind.Text, "text/plain"),
            ["json"] = new(PreviewKind.Text, "text/plain"),
            ["xml"] = new(PreviewKind.Text, "text/plain"),
            ["ini"] = new(PreviewKind.Text, "text/plain"),
            ["accurip"] = new(PreviewKind.Text, "text/plain"),
            ["sfv"] = new(PreviewKind.Text, "text/plain"),
        }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    /// <summary>What may be shown of this path, and the media type to send.</summary>
    public static FilePreviewType Of(string? path)
    {
        if (string.IsNullOrEmpty(path)) return FilePreviewType.Opaque;

        var extension = Path.GetExtension(path);

        // A name that is all extension — ".flac" — is a dotfile, not a FLAC, and
        // Path.GetExtension answers ".flac" for it either way. Compared against
        // the file name rather than the whole path: measured against the length
        // of `path`, the rule held for ".flac" at the root and not for
        // "Brahms/.flac", which is the spelling anything real would have.
        if (extension.Length <= 1) return FilePreviewType.Opaque;
        if (extension.Length == Path.GetFileName(path.AsSpan()).Length) return FilePreviewType.Opaque;

        return Known.TryGetValue(extension[1..], out var type) ? type : FilePreviewType.Opaque;
    }

    /// <summary>The recognised extensions, without leading dots. For diagnostics and tests.</summary>
    public static IReadOnlyCollection<string> Extensions => Known.Keys;

    /// <summary>
    /// Whether a media type somebody else declared may be echoed back as an image.
    /// </summary>
    /// <remarks>
    /// <b>For art that came out of a tag rather than off a filesystem.</b> The
    /// extension allowlist above cannot help there — an embedded cover has no
    /// filename, only a MIME string a tagger wrote, and that string is as
    /// untrusted as anything else inside a file this application did not create.
    ///
    /// The check it replaced was <c>StartsWith("image/")</c>, which passes
    /// <c>image/svg+xml</c>: a document that runs script, served from this
    /// application's origin, by putting one PICTURE block in a FLAC. Demonstrated
    /// against a live server before this existed. <c>nosniff</c> is no defence —
    /// it stops a browser guessing a type, not honouring the one it was given.
    ///
    /// So the answer is the same raster set the rest of this class allows, asked
    /// the other way round.
    /// </remarks>
    public static bool IsSafeImageMediaType(string? mediaType) =>
        mediaType is { Length: > 0 }
        && Known.Values.Any(type =>
            type.Kind == PreviewKind.Image
            && type.MediaType.Equals(mediaType, StringComparison.OrdinalIgnoreCase));
}

/// <param name="MediaType">
/// What to put in <c>Content-Type</c>. Never inferred at the endpoint: an
/// extension this class does not name is opaque, and opaque means a download.
/// </param>
public readonly record struct FilePreviewType(PreviewKind Kind, string MediaType)
{
    /// <summary>Bytes with no claim made about them.</summary>
    public static FilePreviewType Opaque => new(PreviewKind.None, "application/octet-stream");
}
