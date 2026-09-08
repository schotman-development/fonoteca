namespace Fonoteca.Domain.Catalogue;

/// <summary>
/// Which immediate child of a folder a library path belongs to.
/// </summary>
/// <remarks>
/// The rule behind the file manager's listing, and pure for the reason
/// <see cref="AlbumFolder"/> is: it is arithmetic on a name, not an act on a
/// filesystem, and the two cases that matter are the ones a disk cannot show
/// you in a test.
///
/// <b>The prefix carries a trailing slash.</b> Without it <c>Brahms</c> also
/// matches <c>Brahms Live</c>, and the counts on the folder row silently
/// include an album that is not in it — the same lesson
/// <c>AlbumReplacementService</c> and the held-track lookup have both already
/// paid for.
///
/// <b>The root is the empty string, not "/".</b> Catalogue paths are
/// library-relative and carry no leading separator, so the root's prefix has to
/// be empty rather than a separator that would match nothing.
///
/// Ordinal, matching the scan's own <c>StringComparer.Ordinal</c> over the same
/// column. Two files differing only in case are two files on the filesystem
/// this runs against, and folding them here would merge two folder rows into a
/// count that describes neither.
/// </remarks>
public static class FolderRollup
{
    /// <summary>
    /// The child of <paramref name="folder"/> that <paramref name="path"/> sits
    /// under, or null when it sits somewhere else entirely.
    /// </summary>
    /// <param name="folder">Library-relative folder; empty for the root.</param>
    /// <param name="path">A library-relative file path, '/' separated.</param>
    public static FolderChild? Under(string folder, string path)
    {
        if (string.IsNullOrEmpty(path)) return null;

        var trimmed = folder.Trim('/');
        var prefix = trimmed.Length == 0 ? string.Empty : trimmed + "/";

        if (!path.StartsWith(prefix, StringComparison.Ordinal)) return null;

        var rest = path[prefix.Length..];

        // The folder itself, which is not one of its own children.
        if (rest.Length == 0) return null;

        var separator = rest.IndexOf('/', StringComparison.Ordinal);

        return separator < 0
            ? new FolderChild(rest, IsDirectory: false)
            : new FolderChild(rest[..separator], IsDirectory: true);
    }
}

/// <param name="IsDirectory">
/// True when the path continues below this name. A directory here means "the
/// catalogue holds something underneath it", which is not the same as a
/// directory on disk — an empty folder has no paths and so no child row.
/// </param>
public readonly record struct FolderChild(string Name, bool IsDirectory);
