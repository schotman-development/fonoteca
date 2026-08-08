using System.Diagnostics;
using System.Net;
using Fonoteca.Domain.Abstractions;
using Microsoft.Extensions.Options;
using Polly;

namespace Fonoteca.Providers.MusicBrainz;

/// <summary>Whether the configured MusicBrainz server is answering, and how it is configured.</summary>
public enum MusicBrainzReachability
{
    /// <summary>No request was sent, because one would be refused locally anyway.</summary>
    NotConfigured,

    /// <summary>The server answered.</summary>
    Reachable,

    /// <summary>Nothing answered, or what answered said "not now" — down, rate-limited, timed out.</summary>
    Unreachable,

    /// <summary>Something answered and refused. A configuration problem, not a transient one.</summary>
    Rejected,
}

/// <summary>A point-in-time reading of the MusicBrainz seam.</summary>
/// <param name="Server">The configured server.</param>
/// <param name="IsOfficialServer">Whether that is the public instance, which is rate-limited.</param>
/// <param name="MinimumRequestInterval">The gate's interval. Zero means ungated.</param>
/// <param name="ContactConfigured">Whether a contact is set. Without one, lookups are refused locally.</param>
/// <param name="Reachability">What the probe found.</param>
/// <param name="Latency">
/// Wall-clock for the whole attempt — time queued at the rate gate and any
/// retries included, because that is what a caller would actually wait. Only
/// meaningful alongside <see cref="MusicBrainzReachability.Reachable"/>; on a
/// failure it measures how long giving up took. Null when nothing was sent.
/// </param>
/// <param name="Detail">Why, when it is not simply reachable.</param>
/// <param name="CheckedAt">When the probe ran — which, because results are cached, is not when you asked.</param>
public sealed record MusicBrainzHealth(
    Uri Server,
    bool IsOfficialServer,
    TimeSpan MinimumRequestInterval,
    bool ContactConfigured,
    MusicBrainzReachability Reachability,
    TimeSpan? Latency,
    string? Detail,
    DateTimeOffset CheckedAt);

/// <summary>
/// Asks the configured MusicBrainz server whether it is there.
/// </summary>
/// <remarks>
/// Deliberately not an <c>IHealthCheck</c> on <c>/health</c>. That endpoint
/// answers "should this process keep serving traffic", and the honest answer
/// does not change when MusicBrainz is down: scanning and browsing work
/// perfectly well without it. Wiring it in would turn somebody else's outage
/// into this application's restart loop.
///
/// It is also not in <c>Fonoteca.Domain</c>. Whether a remote host is up is an
/// operational fact, not a rule about music — the domain has no opinion to
/// express about it, and giving it one would be inventing an abstraction to
/// satisfy a layering diagram.
///
/// The probe goes through the same named client as real lookups, so it inherits
/// the rate gate, the retries and the User-Agent. That is the point: it
/// measures the path callers actually take, not a parallel one that might be
/// healthy while the real one is not.
/// </remarks>
public sealed class MusicBrainzHealthProbe(
    IHttpClientFactory clients,
    IOptions<MusicBrainzOptions> options,
    IClock clock) : IDisposable
{
    /// <summary>
    /// An artist to ask about — one that has existed since 2003 and is not going anywhere.
    /// </summary>
    /// <remarks>
    /// An artist rather than a recording, with no <c>inc</c> parameters, because
    /// this is the cheapest useful question WS/2 answers: one row and no joins.
    /// A mirror serves it from the same tables and the same indexes as a real
    /// lookup, so a green light here means the database is imported and not
    /// merely that a web server is listening.
    /// </remarks>
    public const string ProbeArtistId = "6d7b7cd4-254b-4c25-83f6-dd20f98ceacd";

    /// <summary>
    /// How long a reading stays good enough to hand out again.
    /// </summary>
    /// <remarks>
    /// The gate is shared with identification work, so an uncached probe does
    /// not merely cost a request — it costs a *turn*, delaying a real lookup by
    /// up to the interval. A browser tab left open on the health card must not
    /// be able to halve the throughput of a scan, so the cache is not an
    /// optimisation here, it is the thing that makes the display safe to poll.
    /// </remarks>
    public static readonly TimeSpan CacheDuration = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How long to wait before calling it down.
    /// </summary>
    /// <remarks>
    /// Measured, not guessed: pointed at a mirror that was not yet serving, the
    /// probe took 10.8 seconds to report "connection refused", because the
    /// standard resilience pipeline treats a refused connection as transient
    /// and retried it with backoff. That is correct for a lookup, which would
    /// rather wait than fail, and wrong for a display, which is holding an HTTP
    /// request open and a spinner on screen while it happens.
    ///
    /// The pipeline still applies — the gate especially, since a probe that
    /// jumped the queue would be measuring a path no real caller takes — but
    /// the answer is bounded. A server that cannot return one artist row in
    /// five seconds is not healthy in any sense the card is claiming.
    /// </remarks>
    public static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(5);

    private readonly SemaphoreSlim _turn = new(1, 1);
    private MusicBrainzHealth? _cached;

    public async ValueTask<MusicBrainzHealth> CheckAsync(CancellationToken cancellationToken = default)
    {
        if (IsFresh(_cached)) return _cached;

        await _turn.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Re-checked inside the gate: several callers can arrive together,
            // and only the first should spend a request on it.
            if (IsFresh(_cached)) return _cached;

            _cached = await ProbeAsync(cancellationToken).ConfigureAwait(false);
            return _cached;
        }
        finally
        {
            _turn.Release();
        }
    }

    private bool IsFresh([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] MusicBrainzHealth? reading) =>
        reading is not null && clock.UtcNow - reading.CheckedAt < CacheDuration;

    private async Task<MusicBrainzHealth> ProbeAsync(CancellationToken cancellationToken)
    {
        var config = options.Value;

        MusicBrainzHealth Reading(MusicBrainzReachability reachability, TimeSpan? latency, string? detail) =>
            new(config.Server,
                config.IsOfficialServer,
                config.MinimumRequestInterval,
                !string.IsNullOrWhiteSpace(config.Contact),
                reachability,
                latency,
                detail,
                clock.UtcNow);

        if (string.IsNullOrWhiteSpace(config.Contact))
        {
            // The same refusal MusicBrainzCatalogue makes, reported rather than
            // thrown. Sending an unidentified request to discover it is exactly
            // what gets an address blocked — and against a mirror it would
            // succeed, which would be worse: a green light on a seam that
            // refuses every real lookup.
            return Reading(
                MusicBrainzReachability.NotConfigured,
                latency: null,
                "Fonoteca:MusicBrainzContact is not set, so lookups are refused before they are sent.");
        }

        var client = clients.CreateClient(MusicBrainzOptions.HttpClientName);
        var url = new Uri(config.Server, $"/ws/2/artist/{ProbeArtistId}?fmt=json");

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(ProbeTimeout);

        var started = Stopwatch.GetTimestamp();

        try
        {
            using var response = await client
                .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, deadline.Token)
                .ConfigureAwait(false);

            var latency = Stopwatch.GetElapsedTime(started);

            if (response.IsSuccessStatusCode)
            {
                return Reading(MusicBrainzReachability.Reachable, latency, detail: null);
            }

            var status = (int)response.StatusCode;
            var transient = response.StatusCode is HttpStatusCode.TooManyRequests
                or HttpStatusCode.RequestTimeout || status >= 500;

            return Reading(
                transient ? MusicBrainzReachability.Unreachable : MusicBrainzReachability.Rejected,
                latency,
                $"HTTP {status} {response.ReasonPhrase}".TrimEnd());
        }
        catch (HttpRequestException failure)
        {
            return Reading(
                MusicBrainzReachability.Unreachable,
                Stopwatch.GetElapsedTime(started),
                failure.Message);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Either the pipeline's own timeout or ProbeTimeout above. Both
            // mean the same thing to whoever is looking at the card.
            return Reading(
                MusicBrainzReachability.Unreachable,
                Stopwatch.GetElapsedTime(started),
                $"No answer within {ProbeTimeout.TotalSeconds:0.#}s.");
        }
        catch (ExecutionRejectedException failure)
        {
            // The circuit breaker is open, or the total-request budget ran out.
            // Reported as unreachable rather than swallowed: from the caller's
            // side that is exactly what it is.
            return Reading(
                MusicBrainzReachability.Unreachable,
                Stopwatch.GetElapsedTime(started),
                $"Blocked by the resilience pipeline: {failure.GetType().Name}.");
        }
    }

    public void Dispose() => _turn.Dispose();
}
