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
/// <see cref="OpenRangeAsync"/> belongs to playback and still throws rather than
/// offering a plausible-looking implementation that has never had a byte through
/// it. <see cref="OpenForReplaceAsync"/> is live as of the identification pass,
/// and it is the only thing here that can modify a library file — always through
/// a temporary sibling and an atomic swap, never in place. Whether a caller may
/// use it at all is <c>Fonoteca:AllowFileMutation</c>, decided one layer up in
/// <c>Fonoteca.Tagging</c>, because this class has no opinion about policy.
/// </remarks>
public sealed class FileSystemAudioFileStore : IAudioFileStore
{
    /// <summary>
    /// 64 KiB. Large enough that hashing a FLAC is not syscall-bound, small
    /// enough that a scan running at ScanConcurrency does not hold megabytes of
    /// buffers.
    /// </summary>
    private const int ReadBufferBytes = 64 * 1024;

    /// <summary>
    /// Marks a file as ours, so a sweep cannot delete somebody else's <c>.tmp</c>.
    /// </summary>
    private const string StagingInfix = ".fonoteca-";

    /// <summary>Matches <see cref="StagingInfix"/>; the leading dot is part of the name.</summary>
    private const string StagingSearchPattern = "*" + StagingInfix + "*.tmp";

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

    /// <summary>
    /// The absolute path, for the external tools that will not take a stream.
    /// </summary>
    /// <remarks>
    /// The one sanctioned way out of the library-relative world, and it exists
    /// for exactly two callers: <c>fpcalc</c> and <c>ffprobe</c> are subprocesses
    /// that open files themselves (ADR 0001). Everything else uses
    /// <see cref="OpenReadAsync"/>.
    ///
    /// It goes through the same containment check as every other path here
    /// rather than being a public <c>Path.Combine(store.Root, …)</c> at four call
    /// sites — that version is the one where somebody eventually forgets.
    /// </remarks>
    /// <exception cref="UnauthorizedAccessException">The path escapes the root.</exception>
    public string AbsolutePathFor(LibraryPath path) => Resolve(path);

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

    /// <summary>
    /// Opens a temporary sibling to write, swapped over the original only on commit.
    /// </summary>
    /// <remarks>
    /// ADR 0002's "never in place", implemented. Four decisions, each of which
    /// the obvious version gets wrong:
    ///
    /// <b>The staging file is a sibling, never in <c>/tmp</c>.</b>
    /// <c>File.Move</c> is <c>rename(2)</c> — atomic — only within one
    /// filesystem. Across devices it silently degrades to copy-then-delete,
    /// which is neither atomic nor cheap when the file is 454 MB, and a library
    /// on its own mount is the normal case rather than the exception.
    ///
    /// <b>The name starts with a dot and ends in <c>.tmp</c>.</b> The dot makes
    /// .NET report <c>FileAttributes.Hidden</c> on Unix, which the walk's
    /// <c>AttributesToSkip</c> already excludes, and <c>tmp</c> is not an audio
    /// extension. So a leftover from a killed process cannot enter the catalogue
    /// — excluded twice, by two mechanisms that were both already there.
    ///
    /// <b>Committing flushes to disk before the rename.</b> The rename is atomic
    /// for the <i>directory entry</i> and says nothing about whether the staged
    /// bytes reached the platter. Without the flush, power loss just after the
    /// swap leaves the entry pointing at a file that is partly zeroes: original
    /// gone, replacement not durable. Full correctness would also fsync the
    /// containing directory, which .NET cannot do portably — a known limit,
    /// recorded rather than papered over.
    ///
    /// <b>Committing copies the original's permission bits forward.</b>
    /// <c>File.Move</c> keeps the <i>staging</i> file's mode, so without this
    /// every file the tagger touches quietly acquires the process umask. On a
    /// library shared with a group that is a real regression, and nobody would
    /// attribute it to a tag write.
    /// </remarks>
    /// <exception cref="FileNotFoundException">There is nothing to replace.</exception>
    /// <exception cref="IOException">The volume has no room for a second copy.</exception>
    public Task<IStagedWrite> OpenForReplaceAsync(
        LibraryPath path,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var target = Resolve(path);
        var info = new FileInfo(target);

        if (!info.Exists)
        {
            throw new FileNotFoundException(
                $"Cannot replace '{path.Value}' because it does not exist.", target);
        }

        EnsureRoomFor(info.DirectoryName ?? info.FullName, info.Length, info.Name);

        return Task.FromResult<IStagedWrite>(Stage(target, inheritModeFrom: target));
    }

    /// <summary>
    /// Begin a write to a path that does not exist yet, staged the same way.
    /// </summary>
    /// <remarks>
    /// The upload half of the file manager, and it is
    /// <see cref="OpenForReplaceAsync"/> minus the one thing that made that
    /// method refuse: a target to replace. Everything else is shared rather than
    /// copied — the sibling staging file, the retried CreateNew, the flush to
    /// disk before the rename — because a second copy of that sequence is how one
    /// of them ends up without the flush.
    ///
    /// Staging matters more here than it looks. An upload arrives over a socket
    /// that can close mid-album, and a half-written FLAC left at its final name
    /// is a file the next scan catalogues, fingerprints and files under an
    /// artist. Written to a hidden sibling it is invisible to the walk twice
    /// over, and abandoning it is a delete rather than a repair.
    ///
    /// Containing directories are created, because the point of an upload is
    /// that the album is not there yet.
    /// </remarks>
    /// <param name="expectedBytes">
    /// What the caller expects to write, for the free-space check. Zero skips
    /// it — a chunked upload does not always know.
    /// </param>
    /// <exception cref="IOException">Something is already at that path.</exception>
    public Task<IStagedWrite> OpenForCreateAsync(
        LibraryPath path,
        long expectedBytes = 0,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var target = Resolve(path);

        // Never an overwrite. Replacing a file is OpenForReplaceAsync's job and
        // it verifies what it wrote; silently accepting one here would make an
        // upload with a colliding name destroy the copy already held.
        if (File.Exists(target) || Directory.Exists(target))
        {
            throw new IOException($"'{path.Value}' already exists.");
        }

        var directory = Path.GetDirectoryName(target)
            ?? throw new IOException($"'{target}' has no containing directory.");

        Directory.CreateDirectory(directory);

        if (expectedBytes > 0)
        {
            EnsureRoomFor(directory, expectedBytes, Path.GetFileName(target));
        }

        return Task.FromResult<IStagedWrite>(Stage(target, inheritModeFrom: null));
    }

    /// <summary>The staging file, and the retry that keeps two writers apart.</summary>
    /// <param name="inheritModeFrom">
    /// The file whose permission bits the commit should copy forward, or null
    /// when nothing is being replaced and the process umask is the right answer.
    /// </param>
    private StagedFileWrite Stage(string target, string? inheritModeFrom)
    {
        var directory = Path.GetDirectoryName(target)
            ?? throw new IOException($"'{target}' has no containing directory.");

        var name = Path.GetFileName(target);

        // CreateNew, retried: two passes over one library must not be able to
        // pick the same staging name, and a collision should cost a retry rather
        // than someone else's data.
        for (var attempt = 0; attempt < 8; attempt++)
        {
            var staging = Path.Combine(directory, StagingNameFor(name));

            FileStream content;

            try
            {
                content = new FileStream(
                    staging,
                    FileMode.CreateNew,
                    FileAccess.ReadWrite,
                    // Readable while we hold it, because verification opens the
                    // staged file by path to read it back — with both libraries,
                    // independently, which is the whole point. Exclusive here
                    // would mean verifying from the same handle that wrote it,
                    // and a check that shares its author's state is not a check.
                    FileShare.Read,
                    ReadBufferBytes,
                    FileOptions.Asynchronous);
            }
            catch (IOException) when (File.Exists(staging))
            {
                continue;
            }

            return new StagedFileWrite(
                content, staging, target, ToLibraryPath(staging), inheritModeFrom);
        }

        throw new IOException($"Could not create a staging file next to '{target}'.");
    }

    /// <summary>
    /// Removes the directories a move emptied, and stops at the library root.
    /// </summary>
    /// <remarks>
    /// Here rather than at either call site because it is the one function in
    /// this feature that can delete something nobody named. Two copies of it is
    /// how one of them ends up with the obvious guard — <c>current.Length &gt;
    /// root.Length</c> and a bare <c>StartsWith</c> — which is neither a
    /// containment check nor a boundary: <c>root/.</c> is longer than
    /// <c>root</c>, starts with it, and exists, so the loop deletes the library
    /// root; and <c>/mnt/music-replaced</c> starts with <c>/mnt/music</c>.
    ///
    /// An album folder holding nothing but the cover art it came with is not
    /// empty, so it stays — which is right: those bytes were not moved anywhere
    /// and deleting them is not this operation's business.
    /// </remarks>
    public void PruneEmptyDirectories(LibraryPath directory)
    {
        // Resolved, so a caller cannot ask this to walk up from outside the root
        // — and compared against the root plus a separator, so the root itself
        // is never a candidate.
        var boundary = _root + Path.DirectorySeparatorChar;
        var current = Resolve(directory);

        while (current.StartsWith(boundary, StringComparison.Ordinal)
            && Directory.Exists(current)
            && !Directory.EnumerateFileSystemEntries(current).Any())
        {
            var parent = Path.GetDirectoryName(current);

            Directory.Delete(current);

            if (parent is null) return;

            current = Path.TrimEndingDirectorySeparator(parent);
        }
    }

    /// <summary>
    /// Deletes staging files left behind by a process that was killed mid-write.
    /// </summary>
    /// <remarks>
    /// The catalogue cannot see them, so this is about disk rather than
    /// correctness — but a 454 MB orphan per interrupted run adds up. Age-gated,
    /// because a staging file belonging to a write happening <i>right now</i>
    /// looks exactly like an abandoned one.
    /// </remarks>
    /// <returns>How many were removed.</returns>
    public int RemoveStaleStagingFiles(TimeSpan minimumAge, DateTimeOffset now)
    {
        if (!RootExists) return 0;

        var cutoff = now - minimumAge;
        var removed = 0;

        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.System,
        };

        foreach (var candidate in Directory.EnumerateFiles(_root, StagingSearchPattern, options))
        {
            try
            {
                var info = new FileInfo(candidate);
                if (!info.Exists || new DateTimeOffset(info.LastWriteTimeUtc) > cutoff) continue;

                File.Delete(candidate);
                removed++;
            }
            catch (IOException)
            {
                // In use, or gone already. Either way, not ours to force.
            }
            catch (UnauthorizedAccessException)
            {
                // Someone else's file in our library. Leave it.
            }
        }

        return removed;
    }

    /// <summary>
    /// A staging name, random rather than time-ordered.
    /// </summary>
    /// <remarks>
    /// The one place in this codebase that must <b>not</b> use
    /// <c>Guid.CreateVersion7()</c>. Version 7 is time-ordered — the leading hex
    /// digits are a millisecond timestamp — so truncating one to eight
    /// characters gives every id minted in the same millisecond an identical
    /// prefix. The retry below then regenerates the same colliding name until it
    /// gives up, and two writes in one millisecond fail. Version 4 is random,
    /// which is the only property a temporary filename wants.
    /// </remarks>
    private static string StagingNameFor(string fileName) =>
        $".{fileName}{StagingInfix}{Guid.NewGuid().ToString("N")[..8]}.tmp";

    /// <summary>
    /// Refuses a write the volume cannot hold, rather than filling it.
    /// </summary>
    /// <remarks>
    /// The original is never at risk — it is untouched until the rename — so the
    /// worst case without this is a failed write and an orphan. The check turns
    /// that into a clean refusal naming the file. It is advisory: if the free
    /// space cannot be determined, that is not a reason to refuse.
    /// </remarks>
    private static void EnsureRoomFor(string directory, long bytes, string name)
    {
        long available;

        try
        {
            available = new DriveInfo(directory).AvailableFreeSpace;
        }
        catch (ArgumentException)
        {
            return;
        }
        catch (IOException)
        {
            return;
        }
        catch (UnauthorizedAccessException)
        {
            return;
        }

        // A tenth over, because the rewritten file can be marginally larger than
        // the original and a volume this close to full has other problems.
        var needed = (long)(bytes * 1.1);

        if (available < needed)
        {
            throw new IOException(
                $"Writing '{name}' needs about {needed / 1024 / 1024} MB of free space; "
                + $"{available / 1024 / 1024} MB is available.");
        }
    }

    /// <summary>One staged replacement. Not committed until it is, and never in place.</summary>
    private sealed class StagedFileWrite(
        FileStream content,
        string stagingAbsolute,
        string targetAbsolute,
        LibraryPath stagingRelative,
        string? inheritModeFrom) : IStagedWrite
    {
        private bool _committed;

        public LibraryPath StagingPath => stagingRelative;

        public Stream Content => content;

        public async Task CommitAsync(CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(_committed, this);

            await content.FlushAsync(cancellationToken).ConfigureAwait(false);

            // To the device, not just out of our buffers. See the remarks on
            // OpenForReplaceAsync for why this is the difference between an
            // atomic swap and a plausible-looking one.
            content.Flush(flushToDisk: true);
            await content.DisposeAsync().ConfigureAwait(false);

            // Nothing to inherit from on a create, where the target is the file
            // about to exist for the first time.
            if (inheritModeFrom is not null) CopyPermissions(inheritModeFrom, stagingAbsolute);

            // The whole point: one syscall, and afterwards the name refers to
            // either the old file or the new one. Never to neither.
            File.Move(stagingAbsolute, targetAbsolute, overwrite: true);

            _committed = true;
        }

        public async ValueTask DisposeAsync()
        {
            if (_committed) return;

            await content.DisposeAsync().ConfigureAwait(false);

            try
            {
                File.Delete(stagingAbsolute);
            }
            catch (IOException)
            {
                // Swept later. Dispose must not throw over a leftover.
            }
            catch (UnauthorizedAccessException)
            {
                // Likewise.
            }
        }

        private static void CopyPermissions(string from, string to)
        {
            if (OperatingSystem.IsWindows()) return;

            try
            {
                File.SetUnixFileMode(to, File.GetUnixFileMode(from));
            }
            catch (IOException)
            {
                // Ownership needs root and mode may be unsupported on the
                // filesystem. Neither is worth failing a verified write over.
            }
            catch (UnauthorizedAccessException)
            {
                // Likewise.
            }
        }
    }

    /// <summary>Absolute path for a library-relative one, rejecting anything outside the root.</summary>
    private string Resolve(LibraryPath path)
    {
        var relative = path.Value ?? string.Empty;
        string absolute;

        try
        {
            // Path.Combine silently discards the root when handed an absolute
            // second argument, so "/etc/passwd" would resolve to itself. The
            // prefix check below is what actually enforces containment; this is
            // only the normalisation that makes it meaningful.
            absolute = Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(Path.Combine(_root, relative)));
        }
        catch (ArgumentException)
        {
            // A null byte, which GetFullPath refuses outright. Caught here
            // because every caller already handles this exception as "not a path
            // in this library", and the alternative was a 500 with a stack trace
            // for a malformed query parameter.
            throw new UnauthorizedAccessException(
                $"Path '{relative}' is not a usable library path.");
        }

        var contained = absolute.Equals(_root, StringComparison.Ordinal)
            || absolute.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.Ordinal);

        if (!contained)
        {
            throw new UnauthorizedAccessException(
                $"Path '{relative}' resolves outside the library root.");
        }

        EnsureNoLinkedDirectory(absolute, relative);

        return absolute;
    }

    /// <summary>
    /// Refuses a path that reaches its target through a directory symlink.
    /// </summary>
    /// <remarks>
    /// <b>The containment check above is lexical, and a symlink is not.</b>
    /// <c>ln -s /etc secretdir</c> inside the library produces
    /// <c>music/secretdir/hostname</c>, which is under the root by every string
    /// comparison and is <c>/etc/hostname</c> on the disk. That was harmless for
    /// as long as nothing served bytes — the walk refuses to recurse through a
    /// link, so nothing behind one was ever catalogued — and stopped being
    /// harmless the moment a file manager could read and list arbitrary paths.
    /// Demonstrated: a listing of <c>/etc</c> and the contents of
    /// <c>/etc/hostname</c>, both through the library root.
    ///
    /// Here rather than at the three endpoints, because it is the same mistake
    /// the lexical check is already here to prevent and every caller resolves
    /// through this method.
    ///
    /// <b>Directories only.</b> A symlinked <i>file</i> is still allowed: the
    /// walk catalogues those deliberately, so refusing them here would take
    /// every one of them out of reach of the passes that already hold rows for
    /// them. It is the same policy <c>LibraryTreeEnumerator</c> applies, moved
    /// to the other side of the boundary.
    ///
    /// .NET has no <c>realpath</c>, so this walks the chain instead — a handful
    /// of attribute reads on a path that is about to be opened anyway.
    /// </remarks>
    private void EnsureNoLinkedDirectory(string absolute, string relative)
    {
        // The final component when it is itself a directory, then every
        // directory above it, stopping at the root.
        var current = Directory.Exists(absolute) ? absolute : Path.GetDirectoryName(absolute);

        while (current is not null
            && current.Length > _root.Length
            && current.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            FileAttributes attributes;

            try
            {
                attributes = File.GetAttributes(current);
            }
            catch (FileNotFoundException)
            {
                // Does not exist yet — an upload's destination folder. Nothing
                // to follow, so nothing to refuse.
                break;
            }
            catch (DirectoryNotFoundException)
            {
                break;
            }

            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new UnauthorizedAccessException(
                    $"Path '{relative}' reaches outside the library through a linked directory.");
            }

            current = Path.GetDirectoryName(current);
        }
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
