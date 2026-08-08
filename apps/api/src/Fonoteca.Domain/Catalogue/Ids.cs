namespace Fonoteca.Domain.Catalogue;

/// <summary>
/// Strongly-typed identifiers.
/// </summary>
/// <remarks>
/// Eight entity types all keyed by <c>Guid</c> is eight chances to pass the
/// wrong one. These wrappers make that a compile error instead of a query that
/// silently returns nothing.
///
/// All use <c>Guid.CreateVersion7</c>, which is time-ordered. That matters at
/// this scale: random v4 keys scatter inserts across the whole B-tree, and a
/// 100k-file scan inserting v4 primary keys spends much of its time on page
/// splits. v7 keeps inserts at the right-hand edge of the index.
/// </remarks>
public readonly record struct WorkId(Guid Value)
{
    public static WorkId New() => new(Guid.CreateVersion7());
    public override string ToString() => Value.ToString();
}

public readonly record struct RecordingId(Guid Value)
{
    public static RecordingId New() => new(Guid.CreateVersion7());
    public override string ToString() => Value.ToString();
}

public readonly record struct ReleaseGroupId(Guid Value)
{
    public static ReleaseGroupId New() => new(Guid.CreateVersion7());
    public override string ToString() => Value.ToString();
}

public readonly record struct ReleaseId(Guid Value)
{
    public static ReleaseId New() => new(Guid.CreateVersion7());
    public override string ToString() => Value.ToString();
}

public readonly record struct TrackId(Guid Value)
{
    public static TrackId New() => new(Guid.CreateVersion7());
    public override string ToString() => Value.ToString();
}

public readonly record struct MediaFileId(Guid Value)
{
    public static MediaFileId New() => new(Guid.CreateVersion7());
    public override string ToString() => Value.ToString();
}

public readonly record struct ArtistId(Guid Value)
{
    public static ArtistId New() => new(Guid.CreateVersion7());
    public override string ToString() => Value.ToString();
}

/// <summary>
/// A MusicBrainz identifier. Distinct from our own ids: it is assigned
/// externally, may be absent, and is the join key back to MusicBrainz data.
/// </summary>
public readonly record struct Mbid(Guid Value)
{
    public override string ToString() => Value.ToString();
}
