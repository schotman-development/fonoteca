using Fonoteca.Domain.Catalogue;

namespace Fonoteca.Data;

/// <summary>
/// A listener's own label for songs: Fonoteca's <c>fonotecaTags</c> Subsonic extension.
/// </summary>
/// <remarks>
/// ADR 0012. Its account's, and nobody else sees it, an admin included (the
/// owner's choice), by a query filter like the hearts'. A tag is a label and
/// a parent at once: <see cref="ParentId"/> nests it under another at any
/// depth, and its songs are its own and every tag's below it. Sibling names
/// are unique ignoring case; the index holds them unique as written, which is
/// what a double tap sends. Deleting a tag deletes every tag below it, by the
/// database's cascade; the songs stay.
/// </remarks>
public sealed class Tag
{
    public const int NameLength = 200;

    public required Guid Id { get; init; }

    public required Guid AccountId { get; init; }

    public required string Name { get; set; }

    public Guid? ParentId { get; set; }
}

/// <summary>
/// A tag on a song: on its recording, so every copy has it, or on the file
/// itself while no recording is known.
/// </summary>
/// <remarks>
/// As a heart is (<see cref="Love"/>, the owner's choice): exactly one of the
/// two is set, and a copy tagged as itself before it was identified is its
/// song's tag once it is. Goes when the song goes, except a recording a merge
/// or a download's catch-up folds away, whose files carry it
/// (<c>CatalogueWriter.CarryLovesAsync</c>).
/// </remarks>
public sealed class SongTag
{
    public required Guid Id { get; init; }

    public required Guid TagId { get; init; }

    public Tag? Tag { get; init; }

    public RecordingId? RecordingId { get; set; }

    public MediaFileId? MediaFileId { get; init; }
}
