using Fonoteca.Domain.Abstractions;

namespace Fonoteca.Tagging;

/// <summary>
/// A file whose tag block could not be read.
/// </summary>
/// <remarks>
/// Tag parsers are hostile-input parsers, and the input is whatever a decade of
/// other people's tools left behind. They do not restrict themselves to tidy
/// exceptions: ATL raises a <see cref="NullReferenceException"/> from
/// <c>EmbeddedPictures</c> on a FLAC that carries a prepended ID3v2 header, and
/// TagLib# raises <c>CorruptFileException</c> on some of the same files. Neither
/// type says anything useful about the file, and neither is something a caller
/// can reasonably be asked to catch by name.
///
/// So the failure gets one name, and one meaning: <b>this file's tags are not
/// legible, therefore this file must not be written to.</b> That is the
/// conservative reading and the only safe one — a library that cannot parse a
/// file reliably enough to describe it cannot be trusted to rewrite it and
/// preserve what was there. The verification in ADR 0002 depends on the "before"
/// reading being true; when it is not available, there is nothing to verify
/// against.
///
/// Worth knowing: on these files ATL reports zero embedded pictures where
/// TagLib# finds one. Had the read merely been made tolerant — pictures
/// defaulted to none — the artwork check would have compared nothing to nothing,
/// agreed, and let a write through that could silently drop the cover art.
/// Failing loudly here is what keeps that check meaningful.
/// </remarks>
public sealed class TagReadFailedException : Exception
{
    public TagReadFailedException(LibraryPath path, string library, Exception cause)
        : base($"{library} could not read the tags in '{path.Value}': {cause.Message}", cause)
    {
        Path = path;
        Library = library;
        CauseType = cause?.GetType().Name ?? string.Empty;
    }

    public TagReadFailedException()
    {
    }

    public TagReadFailedException(string message)
        : base(message)
    {
    }

    public TagReadFailedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>The file that could not be read.</summary>
    public LibraryPath Path { get; }

    /// <summary>Which of the two libraries gave up — ATL or TagLib#.</summary>
    public string Library { get; } = string.Empty;

    /// <summary>
    /// The underlying exception's type name, captured here rather than read back
    /// from <see cref="Exception.InnerException"/> at the logging call site,
    /// where it would be evaluated for every file whether or not the log level
    /// admits it (CA1873).
    /// </summary>
    public string CauseType { get; } = string.Empty;
}
