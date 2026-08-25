using System.Net;
using System.Net.Http.Headers;
using Fonoteca.Domain.Abstractions;
using Fonoteca.Providers.AcoustId;
using Fonoteca.Providers.MusicBrainz;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Options;

namespace Fonoteca.Providers;

/// <summary>
/// Wiring for the external-service adapters.
/// </summary>
/// <remarks>
/// The registration lives here rather than in <c>Program.cs</c> because it is
/// not incidental: the order the message handlers go on, the decompression, the
/// handler lifetime and the timeouts are all part of not getting blocked, and
/// they belong next to the clients they protect.
///
/// Each method takes a delegate rather than an <c>IConfiguration</c> section.
/// This project does not know what a Fonoteca settings file looks like and
/// should not learn — the host maps its own options onto these.
/// </remarks>
public static class ProviderServiceCollectionExtensions
{
    /// <summary>Registers the AcoustID faces and the HTTP client behind them.</summary>
    public static IServiceCollection AddAcoustId(
        this IServiceCollection services,
        Action<AcoustIdOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        services.Configure(configure);

        services.AddKeyedSingleton(AcoustIdOptions.HttpClientName, (provider, _) =>
            new RequestGate(Options<AcoustIdOptions>(provider).MinimumRequestInterval));

        var client = services.AddHttpClient(AcoustIdOptions.HttpClientName, (provider, http) =>
        {
            var config = Options<AcoustIdOptions>(provider);

            http.BaseAddress = config.BaseAddress;
            http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        });

        // Responses are JSON full of base64 and UUIDs, which compresses to a
        // fraction of its size. The request half of the same trade is in
        // AcoustIdClient, which gzips the fingerprint it sends.
        client.ConfigurePrimaryHttpMessageHandler(static () => new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
        });

        AddResilienceThenGate(client, AcoustIdOptions.HttpClientName);

        // One instance behind both faces, resolved through the concrete type
        // rather than registered twice: two registrations would be two clients,
        // and the second would be a second reader of the same options with no
        // way to tell them apart in a log.
        services.AddSingleton<AcoustIdClient>();
        services.AddSingleton<IAcoustIdLookup>(provider => provider.GetRequiredService<AcoustIdClient>());
        services.AddSingleton<IAcoustIdSubmission>(
            provider => provider.GetRequiredService<AcoustIdClient>());

        return services;
    }

    /// <summary>Registers <see cref="IMusicBrainzCatalogue"/> and the HTTP client behind it.</summary>
    public static IServiceCollection AddMusicBrainz(
        this IServiceCollection services,
        Action<MusicBrainzOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        services.Configure(configure);

        services.AddKeyedSingleton(MusicBrainzOptions.HttpClientName, (provider, _) =>
            new RequestGate(Options<MusicBrainzOptions>(provider).MinimumRequestInterval));

        var client = services.AddHttpClient(MusicBrainzOptions.HttpClientName, (provider, http) =>
        {
            var config = Options<MusicBrainzOptions>(provider);

            // Set on the client, not per request, because the requests are
            // composed inside MetaBrainz.MusicBrainz. This is the one header
            // MusicBrainz will block an address over, so it must be impossible
            // for a code path to forget it.
            http.DefaultRequestHeaders.Add("User-Agent", MusicBrainzCatalogue.UserAgentFor(config));
        });

        client.ConfigurePrimaryHttpMessageHandler(static () => new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
        });

        // MusicBrainzCatalogue is a singleton and hands this client to a Query
        // that keeps it for the life of the process, so the factory's usual
        // two-minute handler rotation can never happen. Saying so is better
        // than leaving a handler marked expired and pinned forever: one fixed
        // host means there is no DNS change to rotate for.
        client.SetHandlerLifetime(Timeout.InfiniteTimeSpan);

        AddResilienceThenGate(client, MusicBrainzOptions.HttpClientName);

        services.AddSingleton<IMusicBrainzCatalogue, MusicBrainzCatalogue>();

        // Singleton because the cache is the point — see MusicBrainzHealthProbe.
        services.AddSingleton<MusicBrainzHealthProbe>();

        // TryAdd, so a host that already registered its own clock keeps it. The
        // probe needs one to expire its cache, and requiring the caller to
        // supply a clock before they can ask whether a server is up would be a
        // silly thing to make them discover at runtime.
        services.TryAddSingleton<IClock, SystemClock>();

        return services;
    }

    /// <summary>
    /// Retries on the outside, the rate gate on the inside.
    /// </summary>
    /// <remarks>
    /// The order is the whole point. Handlers run outermost-first in
    /// registration order, so putting the gate on last puts it <i>under</i> the
    /// resilience pipeline — which means a retry after a 503 waits its turn
    /// like any other request. Reversed, a failing service would be answered
    /// with three immediate retries, which is the precise traffic pattern that
    /// turns a temporary rate limit into a lasting block.
    ///
    /// The timeouts are raised from the standard 10s/30s for two reasons. The
    /// nesting is one: time spent queueing at the gate is inside the attempt,
    /// so a caller with four lookups in flight against a one-per-second service
    /// can legitimately sit there for four seconds before a byte moves. (Which
    /// does mean a caller must not fan out without bound — the queue has no
    /// limit, and the timeout is what would eventually notice.)
    ///
    /// The other is measured: a cold recording lookup against musicbrainz.org
    /// with releases, media and release groups included took 10.3 seconds, and
    /// would have been cancelled by the default. Their service is free, shared
    /// and occasionally slow; being patient with it is not optional.
    /// </remarks>
    private static void AddResilienceThenGate(IHttpClientBuilder client, string gateKey)
    {
        client.AddStandardResilienceHandler(options =>
        {
            options.AttemptTimeout.Timeout = TimeSpan.FromSeconds(30);
            options.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(90);

            // The pipeline validates that the breaker samples over at least
            // twice one attempt; raising the attempt timeout forces this up
            // with it.
            options.CircuitBreaker.SamplingDuration = TimeSpan.FromSeconds(60);
        });

        client.AddHttpMessageHandler(provider =>
            new RateLimitedHandler(provider.GetRequiredKeyedService<RequestGate>(gateKey)));
    }

    private static TOptions Options<TOptions>(IServiceProvider provider)
        where TOptions : class =>
        provider.GetRequiredService<IOptions<TOptions>>().Value;
}
