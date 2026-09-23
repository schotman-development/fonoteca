namespace Fonoteca.Domain.Catalogue;

/// <summary>
/// Whether a folder in the library is one artist's own shelf.
/// </summary>
/// <remarks>
/// <b>The one claim about a library's layout that nothing else here makes.</b>
/// <see cref="AlbumFolder"/> believes a library about which files belong
/// together, which is nearly always right. This asks whose a directory
/// <i>is</i>, and the honest answer for most of them is "nobody's in
/// particular": a shelf holding a record is routinely not the artist's own,
/// because a classical library files a performance under its composer. Measured
/// on this library, 47 of Janine Jansen's 54 files sit under <c>Johann Sebastian
/// Bach</c> and <c>Antonio Vivaldi</c>.
///
/// <b>So the only shelf claimed is one named for the artist</b>, and the
/// comparison is <see cref="ArtistNameMatch.Normalise"/>'s rather than string
/// equality, because what a ripper wrote on a disk and what MusicBrainz prints
/// differ in spacing, case and punctuation far more often than in words.
/// Measured over the followed artists, 27 of 28 have a folder that answers to
/// this; the twenty-eighth has no folder at all, his music being filed under
/// the band, and correctly gets nothing.
///
/// <b>Asked in both directions, which is why it is here and not at either call
/// site.</b> The tag write starts from a file's path and asks whether that
/// shelf is the artist's; the portrait endpoints start from an artist and go
/// looking for one. Two copies of this comparison would drift, and the visible
/// symptom would be a page showing a picture the pass refuses to write — or
/// worse, a pass writing one the page cannot find.
///
/// A rule, not I/O: strings in, a bool out, testable without a filesystem.
/// </remarks>
public static class ArtistShelf
{
    /// <summary>What a shelf is called, whatever holds it.</summary>
    /// <remarks>
    /// Every player that reads a library off a disk looks for this beside an
    /// artist's records — Navidrome's <c>ArtistArtPriority</c> puts
    /// <c>artist.*</c> ahead of album art and ahead of every external agent,
    /// and Jellyfin, Kodi and Plex all read the same name. The extension is the
    /// image's own, so the glob is what a player uses rather than a fixed name.
    /// </remarks>
    public const string PortraitGlob = "artist.*";

    /// <summary>Whether <paramref name="folder"/> is this artist's own shelf.</summary>
    /// <param name="folder">
    /// A library-relative folder, or the bare directory name. The leaf is what
    /// is compared, so either works and a deeper library cannot break it.
    /// </param>
    /// <param name="name">The artist's name as the catalogue holds it.</param>
    /// <param name="latinName">
    /// MusicBrainz's own English alias, compared as well because a library on a
    /// western disk is far likelier to be filed under it — a folder called
    /// <c>Joe Hisaishi</c> beside a row whose name is <c>久石譲</c>.
    /// </param>
    public static bool IsNamedFor(string? folder, string? name, string? latinName)
    {
        if (string.IsNullOrWhiteSpace(folder)) return false;

        var leaf = folder.Replace('\\', '/').TrimEnd('/');
        leaf = leaf[(leaf.LastIndexOf('/') + 1)..];

        var named = ArtistNameMatch.Normalise(leaf);

        // <b>A name of nothing but punctuation folds to an empty string, and
        // "!!!" is a real band.</b> Compared through the normaliser these would
        // match every other punctuation-only shelf — "..." is an album title
        // here — so the fold is abandoned rather than trusted, and the two are
        // compared as they are written. Trimmed and case-insensitively, because
        // that much a filesystem can differ by on its own.
        return named.Length == 0
            ? Same(leaf, name, StringComparison.OrdinalIgnoreCase)
                || Same(leaf, latinName, StringComparison.OrdinalIgnoreCase)
            : Same(named, ArtistNameMatch.Normalise(name ?? string.Empty), StringComparison.Ordinal)
                || Same(named, ArtistNameMatch.Normalise(latinName ?? string.Empty), StringComparison.Ordinal);
    }

    private static bool Same(string folder, string? candidate, StringComparison how) =>
        candidate is { Length: > 0 } && folder.Trim().Equals(candidate.Trim(), how);
}
