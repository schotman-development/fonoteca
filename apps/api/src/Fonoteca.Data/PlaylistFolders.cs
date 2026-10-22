namespace Fonoteca.Data;

/// <summary>
/// A folder in a listener's own tree of playlists: OpenSubsonic's
/// <c>playlistFolders</c>.
/// </summary>
/// <remarks>
/// ADR 0012. Private to its account, by a query filter like the hearts', as
/// the extension requires, and nested at any depth by <see cref="ParentId"/>.
/// Deleted only when empty, so the cascade on the parent is met only by an
/// account being removed.
/// </remarks>
public sealed class PlaylistFolder
{
    public const int NameLength = 200;

    public required Guid Id { get; init; }

    public required Guid AccountId { get; init; }

    public required string Name { get; set; }

    public Guid? ParentId { get; set; }

    /// <summary>Its place among its siblings, folders and playlists alike: the client's number, kept as sent.</summary>
    public int? SortOrder { get; set; }
}

/// <summary>Where one account keeps one playlist in its tree.</summary>
/// <remarks>
/// Beside the playlist, never in it: playlists are everyone's (ADR 0018) and
/// a static one is its file. Keyed on the catalogue's own id for it — a static
/// playlist's file name, which a rename carries it to, or a smart playlist's
/// id. A playlist with none is at the root, unordered.
/// </remarks>
public sealed class PlaylistPlacement
{
    public required Guid Id { get; init; }

    public required Guid AccountId { get; init; }

    public required string Playlist { get; set; }

    public Guid? FolderId { get; set; }

    public int? SortOrder { get; set; }
}
