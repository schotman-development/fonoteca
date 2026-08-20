using System.Globalization;
using Fonoteca.Api.Configuration;
using Fonoteca.Api.Endpoints;
using Fonoteca.Api.Logging;
using Fonoteca.Data;
using Fonoteca.Domain.Abstractions;
using Fonoteca.Domain.Catalogue;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Fonoteca.Api.Library;

/// <summary>
/// Fills the candidate caches for the questions already on the worklist, so
/// opening one is a row read rather than a minute of provider requests.
/// </summary>
/// <remarks>
/// Both candidate endpoints cache what they assemble, and both fill that cache
/// on the first click — which makes the second visit free and leaves the first
/// one exactly as slow as it ever was. Measured against the live library that is
/// <b>24 seconds</b> for a recording question (one AcoustID turn plus six of the
/// heaviest MusicBrainz lookups this application makes) and over two minutes for
/// a large component. A person working a seven-hundred-row worklist pays that on
/// every row, in front of the screen, one at a time.
///
/// So the waiting is moved off the click. Nothing here assembles anything: it
/// calls the same two endpoint handlers a browser would, in the order the screen
/// lists them, and they do their own caching exactly as before. That is the
/// whole design — a warmer that built its own document would be a second copy of
/// two hundred lines of ranking and shaping, free to drift from the one a person
/// actually reads.
///
/// <b>It yields to the passes rather than taking the gate.</b> A full sweep is
/// hours of gated requests and holding <see cref="LibraryWorkGate"/> for that
/// long would refuse every pass and every decision meanwhile. What it must not
/// do is compete for turns at the rate limit with a pass that is running — the
/// lesson the MusicBrainz health probe already paid for — so it checks between
/// items and stops for the rest of the cycle as soon as one starts.
///
/// <b>Repeating the cycle costs nothing once it is warm.</b> Every item is put
/// to the endpoint again on the next sweep, and an item whose document is still
/// fresh comes back from the cache in milliseconds without touching a provider.
/// That is what makes this converge without tracking its own progress: newly
/// refused files are picked up by the next sweep, and everything else is a
/// handful of database reads.
/// </remarks>
public sealed class CandidateWarmService(
    IServiceScopeFactory scopeFactory,
    LibraryWorkGate gate,
    LibraryScanService scans,
    IOptions<FonotecaOptions> options,
    ILogger<CandidateWarmService> logger) : BackgroundService
{
    /// <summary>How long after startup the first sweep begins.</summary>
    /// <remarks>
    /// Long enough to be behind migrations, the first page load and the
    /// identification pass a scan may have started, all of which want the
    /// database and the rate limit more than this does.
    /// </remarks>
    private static readonly TimeSpan FirstSweep = TimeSpan.FromSeconds(30);

    /// <summary>How long between sweeps.</summary>
    /// <remarks>
    /// The interval a newly refused file waits before its answer is ready, and
    /// on a warm library the cost of a sweep is one query per kind and a cache
    /// hit per question. Not a schedule anybody depends on: a person who opens
    /// a cold question still gets the live answer, slowly, as they always did.
    /// </remarks>
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(15);

    /// <summary>
    /// Consecutive provider failures that end a sweep.
    /// </summary>
    /// <remarks>
    /// A missing API key, a blocked address and a mirror that is down all look
    /// the same from here and all fail on every item. Three is enough to tell
    /// that from one recording MusicBrainz happens to have merged away.
    /// </remarks>
    private const int GiveUpAfter = 3;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.WarmCandidates) return;

        var wait = FirstSweep;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(wait, stoppingToken).ConfigureAwait(false);
                await SweepAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // Shutdown. Every item was committed as it was assembled, so
                // there is nothing to finish and nothing to roll back.
                return;
            }
            catch (Exception error)
            {
                // Nothing here may end the process. An unhandled exception out
                // of a BackgroundService stops the host by default — so a
                // transient database error on the sweep's own
                // queries, fifteen minutes into an otherwise healthy run, would
                // take the API down to save a cache nobody is waiting for. It
                // waits and tries again instead.
                Log.CandidateWarmSweepFailed(logger, error);
            }

            wait = Interval;
        }
    }

    /// <summary>
    /// One pass over the open worklist. Returns how many documents were built.
    /// </summary>
    /// <remarks>
    /// Public because it is the unit a test can run: the background loop is a
    /// delay and this is the work.
    /// </remarks>
    public async Task<int> SweepAsync(CancellationToken cancellationToken)
    {
        var warmed = 0;
        var failures = 0;

        var scope = scopeFactory.CreateAsyncScope();
        List<MediaFileId> files;
        List<DateTimeOffset> components;

        await using (scope.ConfigureAwait(false))
        {
            var db = scope.ServiceProvider.GetRequiredService<FonotecaDbContext>();

            // The two refusals the candidate endpoints answer, and no others.
            // `Unknown` is audio AcoustID has never heard and `NoRecording` is a
            // cluster MusicBrainz links nothing to, so warming either would
            // spend a turn to reconfirm an emptiness the catalogue already
            // holds. A file with no stored fingerprint has no question to put.
            files = await db.MediaFiles
                .AsNoTracking()
                .Where(file => (file.AcoustIdOutcome == AcoustIdOutcome.Ambiguous
                        || file.AcoustIdOutcome == AcoustIdOutcome.BelowThreshold)
                    && file.Fingerprint != null
                    && file.FingerprintDuration != null)
                .OrderBy(file => file.Path)
                .Select(file => file.Id)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            // Largest first, which is the worklist's own order: the component
            // with forty files hanging on it is the one a person opens first and
            // the one that takes longest to gather. `NoCandidate` is left out
            // for the reason the screen leaves it out — MusicBrainz holds no
            // release with the recording on it, so the browse that would
            // recover the candidates is the one already known to be empty.
            components = await db.MediaFiles
                .AsNoTracking()
                .Where(file => file.ReleaseLookupUtc != null
                    && file.ReleaseDecidedUtc == null
                    && file.AttributionOutcome == ReleaseAttributionOutcome.NoConfidentFit

                    // The endpoint's own condition: a component is recovered
                    // from files whose recording MusicBrainz can be asked about,
                    // and one made only of unlinked files answers 404. Without
                    // this the sweep reads that as a provider failure and three
                    // of them in a row end every sweep at the same place.
                    && file.Recording!.Mbid != null)
                .GroupBy(file => file.ReleaseLookupUtc!.Value)
                .Select(group => new { Stamp = group.Key, Files = group.Count() })
                .OrderByDescending(group => group.Files)
                .ThenBy(group => group.Stamp)
                .Select(group => group.Stamp)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
        }

        // The album questions first, because that is the order the worklist is
        // in — the attribution reasons sort above the file-level ones however
        // many of each there are — and because every early return below happens
        // mid-loop. Warming the cheap file questions first would leave the
        // expensive ones a person sees at the top of the screen permanently
        // behind them on a library where a pass runs often.
        foreach (var component in components)
        {
            if (Busy || failures >= GiveUpAfter) return Done(warmed, failures);

            var built = await WarmAsync(
                (services, token) => ComponentAsync(component, services, token),
                cancellationToken).ConfigureAwait(false);

            Count(built, ref warmed, ref failures);
        }

        foreach (var file in files)
        {
            if (Busy || failures >= GiveUpAfter) return Done(warmed, failures);

            var built = await WarmAsync(
                (services, token) => RecordingAsync(file, services, token),
                cancellationToken).ConfigureAwait(false);

            Count(built, ref warmed, ref failures);
        }

        return Done(warmed, failures);
    }

    /// <summary>
    /// Whether something with a better claim on the library is running.
    /// </summary>
    /// <remarks>
    /// The gate covers the three passes; the scan has never been on it and
    /// guards itself with a private flag, so it is asked separately. It is the
    /// one that matters most here — a scan that decides a file changed clears
    /// the very columns this fills, and a sweep's read-modify-write spans a full
    /// AcoustID turn plus up to six MusicBrainz lookups, which is long enough
    /// for a document computed from audio that is gone to land after the clear.
    /// </remarks>
    private bool Busy => gate.IsBusy || scans.IsRunning;

    /// <summary>The recording question, as the endpoint answers it.</summary>
    private static async Task<IResult> RecordingAsync(
        MediaFileId file,
        IServiceProvider services,
        CancellationToken cancellationToken)
    {
        var answer = await CatalogueEndpoints
            .GetRecordingCandidates(
                file.Value,
                services.GetRequiredService<FonotecaDbContext>(),
                services.GetRequiredService<IAcoustIdLookup>(),
                services.GetRequiredService<IMusicBrainzCatalogue>(),
                services.GetRequiredService<IClock>(),
                cancellationToken)
            .ConfigureAwait(false);

        return answer.Result;
    }

    /// <summary>The album question, as the endpoint answers it.</summary>
    /// <remarks>
    /// The stamp goes back to a string here for the reason it is one on the
    /// wire: <c>UtcTicks</c> is the component's identity and
    /// <see cref="CatalogueEndpoints"/> is the one place it becomes a number.
    /// </remarks>
    private static async Task<IResult> ComponentAsync(
        DateTimeOffset component,
        IServiceProvider services,
        CancellationToken cancellationToken)
    {
        var answer = await CatalogueEndpoints
            .GetComponentCandidates(
                component.UtcTicks.ToString(CultureInfo.InvariantCulture),
                services.GetRequiredService<FonotecaDbContext>(),
                services.GetRequiredService<IMusicBrainzCatalogue>(),
                services.GetRequiredService<IOptions<FonotecaOptions>>(),
                services.GetRequiredService<IClock>(),
                cancellationToken)
            .ConfigureAwait(false);

        return answer.Result;
    }

    private int Done(int warmed, int failures)
    {
        if (warmed > 0 || failures > 0) Log.CandidatesWarmed(logger, warmed, failures);

        return warmed;
    }

    /// <summary>One item, in its own scope, never able to end the sweep by throwing.</summary>
    /// <remarks>
    /// A scope per item for the reason the passes take one per file: the
    /// endpoints commit what they assemble, and a shared context would carry one
    /// item's tracked rows into the next hundred.
    /// </remarks>
    private async Task<IResult?> WarmAsync(
        Func<IServiceProvider, CancellationToken, Task<IResult>> work,
        CancellationToken cancellationToken)
    {
        var scope = scopeFactory.CreateAsyncScope();

        await using (scope.ConfigureAwait(false))
        {
            try
            {
                return await work(scope.ServiceProvider, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception error)
            {
                // Nothing a person is waiting for. One unreadable row must not
                // stop the other six hundred being made fast.
                Log.CandidateWarmFailed(logger, error);
                return null;
            }
        }
    }

    /// <summary>
    /// What the endpoint's own answer says about whether work was done.
    /// </summary>
    /// <remarks>
    /// <c>FromCache</c> is the endpoint telling us it spent nothing, which is
    /// the ordinary answer on a warm library and the reason a sweep can be
    /// repeated forever.
    /// </remarks>
    private static void Count(IResult? result, ref int warmed, ref int failures)
    {
        switch (result)
        {
            // Every candidate nameless is MusicBrainz refusing one lookup at a
            // time. The endpoint stores that on purpose — rows without titles
            // are still the real candidate set, and a person who thinks it went
            // badly has `?refresh=true` — but unattended it is an outage
            // written across the whole worklist and believed for a week, with
            // AcoustID still answering so nothing else notices. Counted as a
            // failure, three of them end the sweep.
            case Ok<RecordingCandidatesResponse> { Value: { FromCache: false } document }
                when document.Candidates.Count > 0
                    && document.Candidates.All(row => row.Title is null):
                failures++;
                break;

            case Ok<RecordingCandidatesResponse> { Value.FromCache: false }:
            case Ok<ComponentCandidatesResponse> { Value.FromCache: false }:
                warmed++;
                failures = 0;
                break;

            case Ok<RecordingCandidatesResponse>:
            case Ok<ComponentCandidatesResponse>:
                failures = 0;
                break;

            default:
                failures++;
                break;
        }
    }
}
