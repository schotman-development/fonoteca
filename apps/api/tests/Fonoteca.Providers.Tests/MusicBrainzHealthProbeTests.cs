using Fonoteca.Fixtures;
using System.Net;
using Fonoteca.Domain.Abstractions;
using Fonoteca.Providers.MusicBrainz;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Polly;

namespace Fonoteca.Providers.Tests;

/// <summary>
/// The health probe behind the web client's MusicBrainz card.
/// </summary>
/// <remarks>
/// Two things are worth testing here and they are not the obvious one. That a
/// 200 means "reachable" is arithmetic. What matters is that the probe cannot
/// be turned into a denial of service against the very work it reports on — it
/// shares a one-request-per-second gate with identification — and that a server
/// which is perfectly healthy but unusable is reported as unusable rather than
/// green.
/// </remarks>
public sealed class MusicBrainzHealthProbeTests : IDisposable
{
    private const string Contact = "https://example.invalid/fonoteca";

    private readonly List<ServiceProvider> _providers = [];

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AnAnsweringServerIsReachableAndTimed()
    {
        var (probe, _, stub) = Build(_ => StubHttpHandler.Json(HttpStatusCode.OK, "{}"));

        var health = await probe.CheckAsync(Token);

        Assert.Equal(MusicBrainzReachability.Reachable, health.Reachability);
        Assert.NotNull(health.Latency);
        Assert.Null(health.Detail);
        Assert.Single(stub.Requests);
    }

    /// <summary>
    /// The cheapest question WS/2 answers: one artist, no <c>inc</c>.
    /// </summary>
    /// <remarks>
    /// A recording lookup with releases and media attached was measured at 10.3
    /// seconds against the public instance. Asking that every thirty seconds to
    /// draw a green badge would cost more than the work it is reporting on.
    /// </remarks>
    [Fact]
    public async Task TheProbeAsksTheCheapestQuestionThereIs()
    {
        var (probe, _, stub) = Build(_ => StubHttpHandler.Json(HttpStatusCode.OK, "{}"));

        await probe.CheckAsync(Token);

        var request = Assert.Single(stub.Requests);

        Assert.Equal("/ws/2/artist/" + MusicBrainzHealthProbe.ProbeArtistId, request.Uri.AbsolutePath);
        Assert.DoesNotContain("inc=", request.Uri.Query, StringComparison.Ordinal);
    }

    /// <summary>
    /// The failure that looks like success. The server is up; every lookup is
    /// still refused before it is sent, so the card must not go green.
    /// </summary>
    [Fact]
    public async Task WithoutAContactNothingIsSentAndNothingIsGreen()
    {
        var (probe, _, stub) = Build(
            _ => StubHttpHandler.Json(HttpStatusCode.OK, "{}"),
            options => options.Contact = string.Empty);

        var health = await probe.CheckAsync(Token);

        Assert.Equal(MusicBrainzReachability.NotConfigured, health.Reachability);
        Assert.False(health.ContactConfigured);
        Assert.Null(health.Latency);
        Assert.Contains("Fonoteca:MusicBrainzContact", health.Detail ?? "", StringComparison.Ordinal);
        Assert.Empty(stub.Requests);
    }

    [Fact]
    public async Task AnOverloadedServerIsUnreachableRatherThanRejected()
    {
        var (probe, _, stub) = Build(_ =>
            StubHttpHandler.Json(HttpStatusCode.ServiceUnavailable, "{}"));

        var health = await probe.CheckAsync(Token);

        Assert.Equal(MusicBrainzReachability.Unreachable, health.Reachability);
        Assert.Contains("503", health.Detail ?? "", StringComparison.Ordinal);

        // Went through the same resilience pipeline as a real lookup, which is
        // the only reason the reading means anything.
        Assert.Equal(2, stub.Requests.Count);
    }

    [Fact]
    public async Task ARefusalIsNotMistakenForAnOutage()
    {
        var (probe, _, stub) = Build(_ => StubHttpHandler.Json(HttpStatusCode.BadRequest, "{}"));

        var health = await probe.CheckAsync(Token);

        // Distinct from Unreachable on purpose: waiting fixes an outage, and
        // does nothing at all for a 400.
        Assert.Equal(MusicBrainzReachability.Rejected, health.Reachability);
        Assert.Single(stub.Requests);
    }

    [Fact]
    public async Task AnUnreachableHostIsReportedRatherThanThrown()
    {
        var (probe, _, _) = Build(_ => throw new HttpRequestException("Connection refused"));

        var health = await probe.CheckAsync(Token);

        Assert.Equal(MusicBrainzReachability.Unreachable, health.Reachability);
        Assert.Contains("Connection refused", health.Detail ?? "", StringComparison.Ordinal);
    }

    /// <summary>
    /// Bounded, so a down server cannot hold the card's request open.
    /// </summary>
    /// <remarks>
    /// Regression: pointed at a mirror that was not yet listening, the probe
    /// took 10.8 seconds to answer, because the resilience pipeline retried a
    /// refused connection with backoff. Correct for a lookup; useless for a
    /// display.
    /// </remarks>
    [Fact]
    public async Task ASilentServerIsGivenUpOnRatherThanWaitedFor()
    {
        // A server that accepts the connection and then says nothing. Honours
        // cancellation, so the test ends when the probe gives up rather than a
        // minute later.
        var (probe, _, _) = BuildHanging();

        // Reaching the next line at all is the assertion: the stub never
        // answers, so without a bound this hangs forever. Deliberately not
        // asserting elapsed time — an upper bound on a loaded machine is a
        // flake, and it would add nothing, since the alternative to "gave up"
        // is "still waiting" rather than "waited slightly too long".
        var health = await probe.CheckAsync(Token);

        Assert.Equal(MusicBrainzReachability.Unreachable, health.Reachability);
        Assert.Contains("No answer within", health.Detail ?? "", StringComparison.Ordinal);
    }

    /// <summary>
    /// The one that stops a browser tab from halving a scan's throughput.
    /// </summary>
    [Fact]
    public async Task ReadingsAreCachedSoAPolledDisplayCannotStarveTheGate()
    {
        var (probe, clock, stub) = Build(_ => StubHttpHandler.Json(HttpStatusCode.OK, "{}"));

        var first = await probe.CheckAsync(Token);
        clock.Advance(MusicBrainzHealthProbe.CacheDuration - TimeSpan.FromSeconds(1));
        var second = await probe.CheckAsync(Token);

        Assert.Single(stub.Requests);

        // And says so honestly: the timestamp is when the reading was taken,
        // not when it was handed out.
        Assert.Equal(first.CheckedAt, second.CheckedAt);
    }

    [Fact]
    public async Task AStaleReadingIsReplaced()
    {
        var (probe, clock, stub) = Build(_ => StubHttpHandler.Json(HttpStatusCode.OK, "{}"));

        await probe.CheckAsync(Token);
        clock.Advance(MusicBrainzHealthProbe.CacheDuration + TimeSpan.FromSeconds(1));
        await probe.CheckAsync(Token);

        Assert.Equal(2, stub.Requests.Count);
    }

    /// <summary>
    /// Concurrent callers collapse onto one request rather than each spending a
    /// turn at the gate.
    /// </summary>
    [Fact]
    public async Task SimultaneousCallersShareOneReading()
    {
        var (probe, _, stub) = Build(_ => StubHttpHandler.Json(HttpStatusCode.OK, "{}"));

        var readings = await Task
            .WhenAll(Enumerable.Range(0, 8).Select(_ => probe.CheckAsync(Token).AsTask()))
            .ConfigureAwait(false);

        Assert.Single(stub.Requests);
        Assert.Single(readings.Select(r => r.CheckedAt).Distinct());
    }

    [Fact]
    public async Task TheReadingCarriesTheConfigurationTheCardDisplays()
    {
        var (probe, _, _) = Build(
            _ => StubHttpHandler.Json(HttpStatusCode.OK, "{}"),
            options =>
            {
                options.Server = new Uri("http://mirror.lan:5000");
                options.MinimumRequestInterval = TimeSpan.Zero;
            });

        var health = await probe.CheckAsync(Token);

        Assert.Equal(new Uri("http://mirror.lan:5000"), health.Server);
        Assert.False(health.IsOfficialServer);
        Assert.Equal(TimeSpan.Zero, health.MinimumRequestInterval);
        Assert.True(health.ContactConfigured);
    }

    public void Dispose()
    {
        foreach (var provider in _providers) provider.Dispose();
    }

    private (MusicBrainzHealthProbe Probe, FakeClock Clock, StubHttpHandler Stub) BuildHanging() =>
        Build(new StubHttpHandler(async (_, cancellationToken) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            return StubHttpHandler.Json(HttpStatusCode.OK, "{}");
        }));

    private (MusicBrainzHealthProbe Probe, FakeClock Clock, StubHttpHandler Stub) Build(
        Func<RecordedRequest, HttpResponseMessage> respond,
        Action<MusicBrainzOptions>? configure = null) =>
        Build(new StubHttpHandler(respond), configure);

    private (MusicBrainzHealthProbe Probe, FakeClock Clock, StubHttpHandler Stub) Build(
        StubHttpHandler stub,
        Action<MusicBrainzOptions>? configure = null)
    {
        var clock = new FakeClock();
        var services = new ServiceCollection();

        services.AddSingleton<IClock>(clock);

        services.AddMusicBrainz(options =>
        {
            options.Contact = Contact;
            options.MinimumRequestInterval = TimeSpan.FromMilliseconds(20);
            configure?.Invoke(options);
        });

        services.AddHttpClient(MusicBrainzOptions.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(() => stub);

        services.ConfigureAll<HttpStandardResilienceOptions>(options =>
        {
            options.Retry.MaxRetryAttempts = 1;
            options.Retry.Delay = TimeSpan.FromMilliseconds(1);
            options.Retry.BackoffType = DelayBackoffType.Constant;
            options.Retry.UseJitter = false;
        });

        var provider = services.BuildServiceProvider();
        _providers.Add(provider);

        return (provider.GetRequiredService<MusicBrainzHealthProbe>(), clock, stub);
    }

    /// <summary>A clock that only moves when told to, so cache expiry is a test rather than a wait.</summary>
    private sealed class FakeClock : IClock
    {
        public DateTimeOffset UtcNow { get; private set; } =
            new(2026, 8, 8, 12, 0, 0, TimeSpan.Zero);

        public void Advance(TimeSpan by) => UtcNow += by;
    }
}
