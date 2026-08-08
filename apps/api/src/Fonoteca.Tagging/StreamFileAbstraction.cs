using TagLibFile = TagLib.File;

namespace Fonoteca.Tagging;

/// <summary>
/// Lets TagLib# read a stream it did not open.
/// </summary>
/// <remarks>
/// TagLib#'s ordinary entry point takes a path, which this project deliberately
/// does not have — everything arrives through <see cref="Fonoteca.Domain.Abstractions.IAudioFileStore"/>
/// so that the library root stays the adapter's secret and containment is
/// checked in one place. <c>IFileAbstraction</c> is TagLib#'s own seam for
/// exactly this.
///
/// <b>The stream is not closed here.</b> TagLib# calls
/// <see cref="CloseStream"/> when it is finished, but the stream belongs to
/// whoever opened it, and a verification read that closed the caller's handle
/// would break the write path in a way that only shows up under load. Closing is
/// the caller's <c>await using</c>, as it is everywhere else.
/// </remarks>
internal sealed class StreamFileAbstraction(string name, Stream stream) : TagLibFile.IFileAbstraction
{
    public string Name => name;

    public Stream ReadStream => stream;

    /// <summary>Never written to. This project's TagLib# is a reader, by decision.</summary>
    public Stream WriteStream =>
        throw new NotSupportedException(
            "TagLib# does not write in Fonoteca. ATL.NET performs writes and TagLib# verifies "
            + "them; see docs/adr/0002.");

    public void CloseStream(Stream toClose)
    {
        // Deliberately nothing. See the remarks: the stream is the caller's.
        // Rewound instead, so a second reading of the same handle starts where
        // the first one did.
        if (toClose?.CanSeek == true) toClose.Position = 0;
    }
}
