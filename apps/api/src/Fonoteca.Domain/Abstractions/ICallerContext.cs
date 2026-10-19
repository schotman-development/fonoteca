namespace Fonoteca.Domain.Abstractions;

/// <summary>
/// Who is asking, and from where.
/// </summary>
/// <remarks>
/// A request acts as the account signed in to it (ADR 0018); the
/// implementation that reads it is the API's <c>AccountCallerContext</c>, since
/// this assembly knows nothing of HTTP. Every audit entry, undo journal row and
/// domain event names it — and a future playback context needs to know which
/// device is asking, so that "who is playing what, where" has somewhere to
/// hang.
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

/// <summary>
/// A person with no account: what work started without one is attributed to,
/// and the person the tests act as. No request resolves it since accounts
/// arrived (ADR 0018).
/// </summary>
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

/// <summary>
/// An agent acting through <c>/mcp</c>, on the owner's approval of each call.
/// </summary>
/// <remarks>
/// Not the owner. Approving a tool call is a different act from reading the
/// candidates and listening to the file, and the decision endpoints record the
/// difference in the outcome they write as well as in the event log's actor.
/// </remarks>
public sealed class AgentCallerContext : ICallerContext
{
    public const string AgentId = "agent";

    public string ActorId => AgentId;
    public string ActorName => "agent";
    public string? DeviceId => null;
}
