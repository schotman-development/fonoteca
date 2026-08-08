using System.IO.Enumeration;
using System.Runtime.CompilerServices;
using Fonoteca.Domain.Abstractions;

namespace Fonoteca.Ingest;

/// <summary>
/// <see cref="IAudioFileStore"/> over an ordinary directory tree.
/// </summary>
/// <remarks>
/// Every path crossing this boundary is <b>library-relative</b>. The absolute
/// root exists only inside this class, which is what lets the catalogue store
/// portable paths — remounting a library at a different point must not
/// invalidate 100,000 rows — and gives traversal outside the root exactly one
/// place to be rejected.
///
/// Only the read half is live. <see cref="OpenRangeAsync"/> belongs to playback
/// and <see cref="OpenForReplaceAsync"/> to the verified tag-write path;
/// neither exists yet, and both throw rather than offering a plausible-looking
/// implementation that has never had a byte through it. Nothing in this
/// application writes to an audio file today, and that stays true here.
/// </remarks>
public sealed class FileSystemAudioFileStore : IAudioFileStore
{
    /// <summary>
    /// 64 KiB. Large enough that hashing a FLAC is not syscall-bound, small
    /// enough that a scan running at ScanConcurrency does not hold megabytes of
    /// buffers.
    /// </summary>
    private const int ReadBufferBytes = 64 * 1024;

    private readonly string _root;

    public FileSystemAudioFileStore(string libraryRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(libraryRoot);

        // Normalised once: the traversal check below is a prefix comparison, and
        // a root with a trailing slash or a ".." in it would quietly defeat it.
        _root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(libraryRoot));
    }

    /// <summary>The absolute library root, as this process sees it.</summary>
    public string Root => _root;

    /// <summary>
    /// Whether the root is present. A missing root is an unmounted volume far
    /// more often than a deleted library, so callers check this before
    /// concluding that files have disappeared.
    /// </summary>
    public bool RootExists => Directory.Exists(_root);

    public IAsyncEnumerable<LibraryPath> EnumerateAsync(
        LibraryPath root,
        CancellationToken cancellationToken = default) =>
        EnumerateAsync(root, new LibraryWalkReport(), cancellationToken);

    /// <summary>
    /// The walk, reporting what it could not read into <paramref name="report"/>.
    /// </summary>
    /// <remarks>
    /// The overload exists because the interface cannot express the second
    /// return value, and a caller that reconciles a catalogue against this walk
    /// genuinely needs it: a directory the walk failed to open is
    /// indistinguishable, from the results alone, from a directory whose files
    /// were deleted. Discarding that distinction is how a permissions change
    /// silently deletes a third of the catalogue.
    /// </remarks>
    public async IAsyncEnumerable<LibraryPath> EnumerateAsync(
        LibraryPath root,
        LibraryWalkReport report,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(report);

        var absoluteRoot = Resolve(root);

        if (!Directory.Exists(absoluteRoot))
        {
            yield break;
        }

        // The walk itself is blocking syscalls with no asynchronous equivalent.
        // One hop onto the thread pool up front keeps a 100k-file enumeration
        // off whichever thread happened to call in; yielding per file would cost
        // a hundred thousand scheduler round trips to no benefit.
        await Task.Yield();

        using var walk = new LibraryTreeEnumerator(absoluteRoot, report);

        while (walk.MoveNext())
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return ToLibraryPath(walk.Current);
        }
    }

    public Task<FileFacts?> StatAsync(LibraryPath path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var info = new FileInfo(Resolve(path));

        // One stat call answers both questions: FileInfo caches its result, so
        // Exists, Length and LastWriteTimeUtc do not each hit the filesystem.
        return Task.FromResult<FileFacts?>(
            info.Exists ? new FileFacts(info.Length, new DateTimeOffset(info.LastWriteTimeUtc)) : null);
    }

    public Task<bool> ExistsAsync(LibraryPath path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(File.Exists(Resolve(path)));
    }

    public Task<Stream> OpenReadAsync(LibraryPath path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        Stream stream = new FileStream(
            Resolve(path),
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            ReadBufferBytes,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        return Task.FromResult(stream);
    }

    /// <summary>Not implemented: range reads arrive with playback.</summary>
    public Task<Stream> OpenRangeAsync(
        LibraryPath path,
        long offset,
        long? length,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException(
            "Range reads are not implemented. They exist for HTTP range-serving audio, " +
            "which has no caller yet.");

    /// <summary>Not implemented: writing to a library file needs the verified write path.</summary>
    public Task<IStagedWrite> OpenForReplaceAsync(
        LibraryPath path,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException(
            "No code path writes to an audio file yet. Staged replacement arrives with " +
            "Fonoteca.Tagging's verified write path, behind Fonoteca:AllowFileMutation.");

    /// <summary>Absolute path for a library-relative one, rejecting anything outside the root.</summary>
    private string Resolve(LibraryPath path)
    {
        var relative = path.Value ?? string.Empty;

        // Path.Combine silently discards the root when handed an absolute
        // second argument, so "/etc/passwd" would resolve to itself. The prefix
        // check below is what actually enforces containment; this is only the
        // normalisation that makes it meaningful.
        var absolute = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(Path.Combine(_root, relative)));

        var contained = absolute.Equals(_root, StringComparison.Ordinal)
            || absolute.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.Ordinal);

        if (!contained)
        {
            throw new UnauthorizedAccessException(
                $"Path '{relative}' resolves outside the library root.");
        }

        return absolute;
    }

    private LibraryPath ToLibraryPath(string absolute) =>
        new(Path.GetRelativePath(_root, absolute));

    /// <summary>
    /// The tree walk, with the two defaults that matter changed.
    /// </summary>
    /// <remarks>
    /// <b>It does not follow directory symlinks.</b> <c>Directory.EnumerateFiles</c>
    /// does, and <c>MaxRecursionDepth</c> is unbounded by default, so a library
    /// containing a link to one of its own ancestors — which "ln -s .. all" in a
    /// collection folder produces — walks
    /// <c>loop/loop/loop/…</c> forever, inserting a new catalogue row at every
    /// level until the disk fills. Verified on this host: the naive walk yielded
    /// the same three files sixty times before being cut off. Symlinked
    /// <i>files</i> are still catalogued; only recursion through a link is
    /// refused, since the files it leads to are either already reachable by
    /// their real path or outside the library entirely.
    ///
    /// <b>It counts what it could not read instead of ignoring it.</b>
    /// <c>IgnoreInaccessible = true</c> turns an unreadable directory into
    /// silence, and silence is exactly what the reconciler reads as "those files
    /// were deleted" — one <c>chmod</c> on a subtree and its whole branch of the
    /// catalogue is gone, along with every hash and fingerprint in it. With the
    /// flag off, <see cref="ContinueOnError"/> is invoked instead: the walk still
    /// continues past the bad directory, but it says so.
    /// </remarks>
    private sealed class LibraryTreeEnumerator(string root, LibraryWalkReport report)
        : FileSystemEnumerator<string>(root, WalkOptions)
    {
        private static readonly EnumerationOptions WalkOptions = new()
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = false,

            // Hidden and system entries are other tools' metadata — @eaDir,
            // .Trash-1000, Windows' System Volume Information.
            AttributesToSkip = FileAttributes.Hidden | FileAttributes.System,
        };

        protected override bool ShouldIncludeEntry(ref FileSystemEntry entry) => !entry.IsDirectory;

        protected override bool ShouldRecurseIntoEntry(ref FileSystemEntry entry) =>
            (entry.Attributes & FileAttributes.ReparsePoint) == 0;

        protected override string TransformEntry(ref FileSystemEntry entry) => entry.ToFullPath();

        protected override bool ContinueOnError(int error)
        {
            report.RecordUnreadableDirectory();

            // Keep going. One unreadable directory should not cost the operator
            // the other 99,000 files, and the count is what keeps the omission
            // from being mistaken for a deletion.
            return true;
        }
    }
}

/// <summary>What a walk could not see.</summary>
/// <remarks>
/// One number, and it only has to answer one question: is this walk a complete
/// picture of the library? A reconciler that deletes rows for files it did not
/// see must not act on an incomplete one.
/// </remarks>
public sealed class LibraryWalkReport
{
    public int UnreadableDirectories { get; private set; }

    public bool IsComplete => UnreadableDirectories == 0;

    internal void RecordUnreadableDirectory() => UnreadableDirectories++;
}
