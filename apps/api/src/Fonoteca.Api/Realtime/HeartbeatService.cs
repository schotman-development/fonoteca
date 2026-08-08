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
            // No job queue is wired yet, so ActiveJobs is always zero. Once
            // IJobQueue has an implementation this reads from it.
            var message = new HeartbeatMessage(clock.UtcNow, ActiveJobs: 0);
            await hub.Clients.All.Heartbeat(message).ConfigureAwait(false);
        }
    }
}
