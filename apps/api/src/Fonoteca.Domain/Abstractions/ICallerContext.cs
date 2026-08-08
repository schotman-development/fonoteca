namespace Fonoteca.Domain.Abstractions;

/// <summary>
/// Who is asking, and from where.
/// </summary>
/// <remarks>
/// Fonoteca is single-user today, so <see cref="SingleUserCallerContext"/>
/// returns a constant. The seam exists anyway because every audit entry, undo
/// journal row and domain event needs a subject to attribute — and because a
/// future playback context needs to know which device is asking, so that
/// "who is playing what, where" has somewhere to hang.
///
/// Threading an identity through afterwards is a wide, boring refactor touching
/// every call site. Declaring it now costs one interface.
/// </remarks>
public interface ICallerContext
{
    /// <summary>Stable identifier for the acting principal.</summary>
    string ActorId { get; }

    /// <summary>Human-readable label, recorded in audit entries.</summary>
    string ActorName { get; }

    /// <summary>
    /// The device or session acting, when one is meaningful. Null for
    /// background work. Reserved for playback zones.
    /// </summary>
    string? DeviceId { get; }
}

/// <summary>The single-user implementation. Replace, do not extend, when users arrive.</summary>
public sealed class SingleUserCallerContext : ICallerContext
{
    public const string OwnerId = "owner";

    public string ActorId => OwnerId;
    public string ActorName => "owner";
    public string? DeviceId => null;
}

/// <summary>Background workers act as the system, not as the owner.</summary>
public sealed class SystemCallerContext : ICallerContext
{
    public const string SystemId = "system";

    public string ActorId => SystemId;
    public string ActorName => "system";
    public string? DeviceId => null;
}
