using Fonoteca.Api.Logging;

namespace Fonoteca.Api.Acquisition;

/// <summary>
/// Asks MusicBrainz, once a day, about the downloads still filed as the shop
/// described them — each album at most weekly (ADR 0011, the owner's choice).
/// </summary>
/// <remarks>
/// The first sweep waits ten minutes after start, so a restart in the middle
/// of work does not open with a burst at the rate gate.
/// </remarks>
public sealed class ProviderCatchUpService(
    MusicBrainzCatchUp catchUp,
    ILogger<ProviderCatchUpService> logger) : BackgroundService
{
    private static readonly TimeSpan FirstSweep = TimeSpan.FromMinutes(10);

    private static readonly TimeSpan Interval = TimeSpan.FromDays(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var wait = FirstSweep;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(wait, stoppingToken).ConfigureAwait(false);

                var (albums, recordings) = await catchUp.SweepAsync(stoppingToken).ConfigureAwait(false);

                if (albums + recordings > 0) Log.CaughtUp(logger, albums, recordings);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception error)
            {
                // An exception out of a BackgroundService stops the host. Nothing
                // here is worth that: the next sweep asks again.
                Log.CatchUpSweepFailed(logger, error);
            }

            wait = Interval;
        }
    }
}
