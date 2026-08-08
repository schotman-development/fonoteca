namespace Fonoteca.Domain.Abstractions;

/// <summary>
/// Access to the bytes of the library.
/// </summary>
/// <remarks>
/// Deliberately hands out <b>seekable streams</b> rather than path strings.
///
/// Tag reading and integrity checks are indifferent to the difference, but
/// HTTP range-serving audio is not — and that is exactly what a future playback
/// context needs. Returning a stream now costs nothing and avoids revisiting
/// every call site later. It also keeps the door open to a non-POSIX backing
/// store without the domain noticing.
///
/// Note the asymmetry: reads are ordinary, writes go through
/// <see cref="OpenForReplaceAsync"/> and are staged then swapped, because a
/// partially-written file in a music library is data loss.
/// </remarks>
public interface IAudioFileStore
{
    /// <summary>Open for reading. The stream must support seeking.</summary>
    Task<Stream> OpenReadAsync(LibraryPath path, CancellationToken cancellationToken = default);

    /// <summary>Read a byte range. For range requests; avoids buffering whole files.</summary>
    Task<Stream> OpenRangeAsync(
        LibraryPath path,
        long offset,
        long? length,
        CancellationToken cancellationToken = default);

    Task<FileFacts?> StatAsync(LibraryPath path, CancellationToken cancellationToken = default);

    Task<bool> ExistsAsync(LibraryPath path, CancellationToken cancellationToken = default);

    IAsyncEnumerable<LibraryPath> EnumerateAsync(
        LibraryPath root,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Begin a replace. Writes land in a temporary sibling; the original is only
    /// swapped when <see cref="IStagedWrite.CommitAsync"/> succeeds, and is left
    /// untouched if the handle is disposed without committing.
    /// </summary>
    Task<IStagedWrite> OpenForReplaceAsync(
        LibraryPath path,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// A staged, atomic file replacement. Dispose without committing to abandon it;
/// the original file is never modified in place.
/// </summary>
public interface IStagedWrite : IAsyncDisposable
{
    /// <summary>The temporary file being written. Same filesystem as the target, so the swap is atomic.</summary>
    LibraryPath StagingPath { get; }

    Stream Content { get; }

    /// <summary>Atomically move staging over the target. Only call after verification passes.</summary>
    Task CommitAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// A library-relative path.
/// </summary>
/// <remarks>
/// A wrapper rather than a raw string so that a path cannot be confused with a
/// title, an artist name or an absolute host path. Resolution against the
/// configured library root happens in the adapter, which is also where
/// traversal outside the root is rejected.
/// </remarks>
public readonly record struct LibraryPath(string Value)
{
    public override string ToString() => Value;
}

/// <summary>Cheap filesystem facts, used for change detection before any file is opened.</summary>
public sealed record FileFacts(long SizeBytes, DateTimeOffset LastModifiedUtc);
