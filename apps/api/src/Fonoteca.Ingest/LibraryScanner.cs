using System.Runtime.CompilerServices;
using Fonoteca.Domain.Abstractions;
using Fonoteca.Domain.Catalogue;

namespace Fonoteca.Ingest;

/// <summary>
/// The walk: every audio file in the library, with the facts that are free to
/// obtain.
/// </summary>
/// <remarks>
/// Size and modification time only. No file is opened, nothing is hashed,
/// decoded, probed or fingerprinted — those are separate passes over a
/// catalogue that already exists, and keeping them out of the walk is what
/// makes a full pass over 100,000 files take seconds rather than hours.
///
/// That split is also what makes the walk safely repeatable: it costs two
/// syscalls per file and touches nothing, so running it on a schedule, on
/// demand, or twice by accident is uninteresting.
///
/// A streamed <c>IAsyncEnumerable</c> rather than a list, because a library
/// this size should never be materialised in memory just to be counted.
/// </remarks>
public sealed class LibraryScanner(FileSystemAudioFileStore files)
{
    /// <summary>
    /// Every audio file under the library root, streamed.
    /// </summary>
    /// <remarks>
    /// Takes the concrete store rather than <see cref="IAudioFileStore"/>
    /// because it needs the walk to report what it could not read, and that is a
    /// property of walking a filesystem rather than of storing bytes. The
    /// interface stays what the domain depends on for reading a file; this is
    /// the one caller that needs to know how trustworthy the listing was.
    /// </remarks>
    public async IAsyncEnumerable<ScannedFile> EnumerateAsync(
        LibraryWalkReport report,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var root = new LibraryPath(string.Empty);

        var paths = files.EnumerateAsync(root, report, cancellationToken).ConfigureAwait(false);

        await foreach (var path in paths)
        {
            if (!AudioFormats.IsAudioFile(path)) continue;

            var facts = await files.StatAsync(path, cancellationToken).ConfigureAwait(false);

            // Null means the file went away between being listed and being
            // stat'd. On a library being written to by other tools that is
            // ordinary, and the next scan will agree it is gone.
            if (facts is null) continue;

            yield return new ScannedFile(path, facts.SizeBytes, facts.LastModifiedUtc);
        }
    }
}

/// <summary>One audio file as the walk found it.</summary>
public sealed record ScannedFile(LibraryPath Path, long SizeBytes, DateTimeOffset LastModifiedUtc);
