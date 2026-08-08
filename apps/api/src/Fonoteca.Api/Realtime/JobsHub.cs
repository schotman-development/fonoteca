using Microsoft.AspNetCore.SignalR;

namespace Fonoteca.Api.Realtime;

/// <summary>
/// Pushes background-work state to connected clients.
/// </summary>
/// <remarks>
/// A full library scan runs for hours. Polling for its progress is both laggy
/// and wasteful, so progress, completion and failure are pushed instead.
/// SignalR handles reconnection and transport fallback, which matters because
/// the UI is often left open across sleep/wake.
///
/// The hub is a transport, not a source of truth: a client that reconnects
/// re-reads current state from the REST endpoints rather than assuming it
/// caught every message.
/// </remarks>
public sealed class JobsHub : Hub<IJobsClient>
{
    public const string Route = "/hubs/jobs";
}

/// <summary>
/// The strongly-typed client contract. Method names here are the wire protocol,
/// so renaming one is a breaking change for the web app.
/// </summary>
public interface IJobsClient
{
    Task JobProgress(JobProgressMessage message);

    /// <summary>Liveness signal, so the UI can distinguish "idle" from "disconnected".</summary>
    Task Heartbeat(HeartbeatMessage message);
}

public sealed record JobProgressMessage(
    string JobId,
    string Kind,
    string State,
    int Processed,
    int? Total,
    string? CurrentItem,
    DateTimeOffset AtUtc);

public sealed record HeartbeatMessage(DateTimeOffset AtUtc, int ActiveJobs);
