using System.Diagnostics.CodeAnalysis;

namespace Fonoteca.Data;

/// <summary>
/// Where a listener's client left off: OpenSubsonic's <c>savePlayQueue</c>
/// and <c>getPlayQueue</c>.
/// </summary>
/// <remarks>
/// ADR 0012. One per account, the protocol's model, and private to it by a
/// query filter like the hearts'. <see cref="Songs"/> are media file ids in
/// the client's order, repeats kept, with no foreign key: a song the library
/// has lost since stays here and is left out when the queue is read, so a
/// save never fails on a song trashed while it was queued.
/// </remarks>
[SuppressMessage(
    "Naming",
    "CA1711:Identifiers should not have incorrect suffix",
    Justification =
        "The protocol's own name for it, savePlayQueue and getPlayQueue's playQueue; it is " +
        "one row, never a collection type, and a name without the word would hide what a " +
        "client calls it.")]
public sealed class PlayQueue
{
    public required Guid AccountId { get; init; }

    public required List<Guid> Songs { get; init; }

    /// <summary>The song playing, as its place in <see cref="Songs"/>, so a song queued twice is the one it was.</summary>
    public required int Current { get; init; }

    /// <summary>Milliseconds into the song playing.</summary>
    public required long PositionMs { get; init; }

    public required DateTimeOffset ChangedUtc { get; init; }

    /// <summary>The client that saved it, as it names itself in <c>c</c>.</summary>
    public required string ChangedBy { get; init; }
}
