using Fonoteca.Domain.Catalogue;

namespace Fonoteca.Domain.Tests;

/// <summary>
/// The allowlist behind the file manager's previews.
/// </summary>
/// <remarks>
/// These are security tests wearing a formatting test's clothes. The endpoint
/// serves bytes from a directory nobody vetted, on the application's own origin,
/// and this table is the only thing deciding what a browser is told they are.
/// </remarks>
public sealed class FilePreviewTests
{
    [Theory]
    [InlineData("Brahms/01 Allegro.flac", PreviewKind.Audio, "audio/flac")]
    [InlineData("Brahms/01 Allegro.MP3", PreviewKind.Audio, "audio/mpeg")]
    [InlineData("Brahms/cover.jpg", PreviewKind.Image, "image/jpeg")]
    [InlineData("Brahms/rip.log", PreviewKind.Text, "text/plain")]
    [InlineData("Brahms/album.cue", PreviewKind.Text, "text/plain")]
    public void AKnownExtensionIsPreviewableAsItsOwnKind(
        string path, PreviewKind kind, string mediaType)
    {
        var type = FilePreview.Of(path);

        Assert.Equal(kind, type.Kind);
        Assert.Equal(mediaType, type.MediaType);
    }

    /// <summary>
    /// The one that matters. Both of these are documents that run script, and
    /// serving either with its own media type from this origin is a cross-site
    /// scripting hole opened by dropping a file into a music folder.
    /// </summary>
    [Theory]
    [InlineData("Brahms/notes.html")]
    [InlineData("Brahms/notes.htm")]
    [InlineData("Brahms/cover.svg")]
    [InlineData("Brahms/sleeve.xhtml")]
    public void ADocumentThatCouldRunScriptIsNeverPreviewable(string path)
    {
        var type = FilePreview.Of(path);

        Assert.Equal(PreviewKind.None, type.Kind);
        Assert.Equal("application/octet-stream", type.MediaType);
    }

    /// <summary>
    /// Audio to every pass in this application, and undecodable by any browser.
    /// Offering a player for these is offering a control that cannot work.
    /// </summary>
    [Theory]
    [InlineData("Brahms/01.ape")]
    [InlineData("Brahms/01.wv")]
    [InlineData("Brahms/01.dsf")]
    [InlineData("Brahms/01.wma")]
    public void AudioNoBrowserDecodesOffersNoPlayer(string path)
    {
        Assert.Equal(PreviewKind.None, FilePreview.Of(path).Kind);

        // Still audio to the rest of the application, which is what keeps its
        // tags and its measured quality on the screen.
        Assert.True(AudioFormats.IsAudioFile(path));
    }

    [Fact]
    public void AnUnknownExtensionIsOpaqueRatherThanGuessedAt() =>
        Assert.Equal(FilePreviewType.Opaque, FilePreview.Of("Brahms/backup.tar.zst"));

    [Fact]
    public void AFileWithNoExtensionIsOpaque() =>
        Assert.Equal(FilePreviewType.Opaque, FilePreview.Of("Brahms/README"));

    /// <summary>A name that is nothing but an extension is a dotfile.</summary>
    /// <remarks>
    /// Both spellings, because the rule compared the extension against the whole
    /// path and so held only at the library root — where a dotfile is the least
    /// likely to be.
    /// </remarks>
    [Theory]
    [InlineData(".flac")]
    [InlineData("Brahms/.flac")]
    [InlineData("Brahms/Symphony 1/.jpg")]
    public void ADotfileIsNotItsOwnExtension(string path) =>
        Assert.Equal(FilePreviewType.Opaque, FilePreview.Of(path));

    /// <summary>
    /// The art path's question: a media type a tagger wrote inside a file.
    /// </summary>
    /// <remarks>
    /// <c>StartsWith("image/")</c> was the check, and it passes
    /// <c>image/svg+xml</c> — a document that runs script, echoed back from this
    /// application's own origin because of one PICTURE block in a FLAC.
    /// </remarks>
    [Theory]
    [InlineData("image/svg+xml")]
    [InlineData("image/svg+xml; charset=utf-8")]
    [InlineData("text/html")]
    [InlineData("image/")]
    [InlineData("")]
    [InlineData(null)]
    public void ADeclaredImageTypeThatIsNotARasterImageIsRefused(string? declared) =>
        Assert.False(FilePreview.IsSafeImageMediaType(declared));

    [Theory]
    [InlineData("image/jpeg")]
    [InlineData("IMAGE/PNG")]
    [InlineData("image/webp")]
    public void ADeclaredRasterImageTypeIsAllowed(string declared) =>
        Assert.True(FilePreview.IsSafeImageMediaType(declared));

    [Fact]
    public void NoEntryClaimsAMediaTypeThatRendersAsADocument() =>
        Assert.DoesNotContain(
            FilePreview.Extensions,
            extension => FilePreview.Of($"x.{extension}").MediaType
                is "text/html" or "application/xhtml+xml" or "image/svg+xml");
}
