using Fonoteca.Domain.Catalogue;

namespace Fonoteca.Data;

/// <summary>
/// One time a listener played a song: a Subsonic <c>scrobble</c> with
/// <c>submission=true</c>, or a listen their ListenBrainz account held when it
/// was connected (client issue #67).
/// </summary>
/// <remarks>
/// <b>One per account per second</b> (<see cref="PlayedUtc"/>, whole seconds,
/// unique with the account): nobody starts two songs in one second, so a play
/// sent again — a client's queue of offline plays retried, a listen
/// ListenBrainz also holds — is the same play and counts once.
///
/// <b>What was played is the file, read through to what it is now.</b> A play
/// counts on the recording and album its file holds when it is read, so an
/// identification or a re-filing since carries it along; where the file is
/// gone (an upgrade, the trash) on the <see cref="RecordingId"/> and
/// <see cref="ReleaseGroupId"/> it held then, which a merge carries
/// (<c>CatalogueWriter.CarryLovesAsync</c>). Every reference sets null rather
/// than cascading: a play is a listener's history, and nothing in the library
/// moving takes it away.
/// </remarks>
public sealed class Play
{
    public required Guid Id { get; init; }

    public required Guid AccountId { get; init; }

    /// <summary>When the play began, in whole seconds, as ListenBrainz keys a listen.</summary>
    public required DateTimeOffset PlayedUtc { get; init; }

    public MediaFileId? MediaFileId { get; set; }

    public MediaFile? MediaFile { get; set; }

    /// <summary>The file's recording at the time, for when the file is gone.</summary>
    public RecordingId? RecordingId { get; set; }

    /// <summary>The file's album at the time, for when the file is gone.</summary>
    public ReleaseGroupId? ReleaseGroupId { get; set; }

    /// <summary>When ListenBrainz took it, or held it already; null while it is still to be sent.</summary>
    public DateTimeOffset? ForwardedUtc { get; set; }
}
