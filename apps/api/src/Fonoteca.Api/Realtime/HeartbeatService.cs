using Fonoteca.Api.Library;
using Fonoteca.Api.Logging;
using Fonoteca.Domain.Abstractions;
using Microsoft.AspNetCore.SignalR;

namespace Fonoteca.Api.Realtime;

/// <summary>
/// Broadcasts a periodic heartbeat on <see cref="JobsHub"/>.
/// </summary>
/// <remarks>
/// Scaffold-level, and useful beyond proving the wiring: without a heartbeat a
/// client cannot tell "no jobs are running" from "the connection died quietly",
/// and those need different responses in the UI.
/// </remarks>
public sealed class HeartbeatService(
    IHubContext<JobsHub, IJobsClient> hub,
    LibraryWorkGate gate,
    IClock clock,
    ILogger<HeartbeatService> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Log.HeartbeatStarted(logger, Interval.TotalSeconds);

        using var timer = new PeriodicTimer(Interval);

        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            // Real, now that there is something to count. It is deliberately
            // not a queue depth: nothing queues here, one piece of library-wide
            // work runs at a time, so this is 0 or 1 and says which.
            var message = new HeartbeatMessage(clock.UtcNow, gate.IsBusy ? 1 : 0);
            await hub.Clients.All.Heartbeat(message).ConfigureAwait(false);
        }
    }
}
