namespace Fonoteca.Domain.Catalogue;

/// <summary>
/// The pictures an artist has, and what a library calls each one on disk.
/// </summary>
/// <remarks>
/// <b>Two kinds, one mechanism.</b> Both are stored the same way, uploaded the
/// same way, served the same way and written beside the artist's records the
/// same way; all that differs is which file name a player looks for. Keeping
/// that difference in one place is the point — the alternative is the same five
/// steps written twice, with the file name as the only line that varies.
///
/// <b>The names are somebody else's conventions, not ours.</b>
/// <list type="bullet">
/// <item><c>artist.*</c> is what Navidrome's <c>ArtistArtPriority</c> looks for
/// first, ahead of album art and ahead of every external agent, and what
/// Jellyfin, Kodi and Plex all read as the artist's picture.</item>
/// <item><c>backdrop.*</c> is the wide one. Kodi and Jellyfin also read
/// <c>fanart.*</c> and Plex prefers this spelling — it was chosen because this
/// library already holds 123 of them, so there is one banner per artist rather
/// than two. <b>The consequence is stated rather than avoided:</b> whatever
/// wrote those 123 writes this name too, so each tool's run displaces the
/// other's file to the trash. Nothing is lost, but the file changes hands.
/// Navidrome does not show artist banners at all; this one is for the others.
/// </item>
/// </list>
///
/// <see cref="Name"/> is what the catalogue stores and what a route says, so it
/// is lower-case and stable; <see cref="FileStem"/> is what the disk says.
/// </remarks>
public sealed record ArtistImageKind(string Name, string FileStem)
{
    /// <summary>A photograph of the artist. The one every player reads.</summary>
    public static readonly ArtistImageKind Portrait = new("portrait", "artist");

    /// <summary>A wide photograph for the head of their page.</summary>
    public static readonly ArtistImageKind Banner = new("banner", "backdrop");

    /// <summary>Both, in the order a run writes them.</summary>
    public static readonly IReadOnlyList<ArtistImageKind> All = [Portrait, Banner];

    /// <summary>What a player globs for beside the music, extension and all.</summary>
    public string Glob => $"{FileStem}.*";

    /// <summary>The file this picture would be, in a folder, as an image type's extension.</summary>
    public string FileName(string extension) => $"{FileStem}.{extension}";

    /// <summary>The kind that answers to this name, or null.</summary>
    public static ArtistImageKind? ByName(string? name) =>
        All.FirstOrDefault(kind =>
            string.Equals(kind.Name, name, StringComparison.OrdinalIgnoreCase));

    public override string ToString() => Name;
}
