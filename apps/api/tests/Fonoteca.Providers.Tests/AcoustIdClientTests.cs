using Fonoteca.Fixtures;
using System.Diagnostics;
using System.Net;
using Fonoteca.Domain.Abstractions;
using Fonoteca.Domain.Identification;
using Fonoteca.Providers.AcoustId;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Polly;

namespace Fonoteca.Providers.Tests;

/// <summary>
/// The AcoustID adapter, built through its real DI registration and pointed at
/// a stub socket.
/// </summary>
public sealed class AcoustIdClientTests : IDisposable
{
    private const string ApiKey = "test-key";
    private const string EmptyAnswer = """{"status":"ok","results":[]}""";

    /// <summary>Short enough to keep the suite quick, long enough to be measurable.</summary>
    private static readonly TimeSpan Gate = TimeSpan.FromMilliseconds(60);

    private static readonly AudioFingerprint Fingerprint =
        new("AQABz0qUkZK4oOfhL-CPc4e5C_wW2H2QH9uDL4cvoT8UNQ", TimeSpan.FromSeconds(641));

    /// <summary>
    /// A real answer's shape: two clusters, the same recording behind both.
    /// </summary>
    private const string TwoClusters = """
        {
          "status": "ok",
          "results": [
            {
              "id": "9ff43b6a-4f16-427c-93c2-92307ca505e0",
              "score": 0.87,
              "recordings": [{ "id": "cd2e7c47-16f5-46c6-a37c-a1eb7bf599ff", "sources": 653 }]
            },
            {
              "id": "be6fa248-97d1-4a18-845b-7c6a070b764c",
              "score": 1.0,
              "recordings": [{ "id": "cd2e7c47-16f5-46c6-a37c-a1eb7bf599ff", "sources": 25 }]
            }
          ]
        }
        """;

    private readonly List<ServiceProvider> _providers = [];

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task TheFingerprintIsPostedAsAGzippedForm()
    {
        var (lookup, stub) = Build(_ => Ok(EmptyAnswer));

        await lookup.LookupAsync(Fingerprint, Token);

        var request = Assert.Single(stub.Requests);

        // POST, not GET. A full-length fingerprint in a query string is a URL
        // long enough for something in the middle to truncate it.
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.EndsWith("/v2/lookup", request.Uri.AbsolutePath, StringComparison.Ordinal);
        Assert.True(request.IsGzipped, "AcoustID documents compressed POSTs as preferred");

        var form = request.Form();

        Assert.Equal(ApiKey, form["client"]);
        Assert.Equal("json", form["format"]);
        Assert.Equal("641", form["duration"]);
        Assert.Equal(Fingerprint.Value, form["fingerprint"]);

        // Identifiers, not metadata: the titles AcoustID would also return are
        // a stale mirror of MusicBrainz.
        Assert.Equal("recordingids sources", form["meta"]);
    }

    [Theory]
    [InlineData(240.4, "240")]
    [InlineData(240.5, "241")]
    [InlineData(240.7, "241")]
    public async Task DurationIsRoundedToWholeSeconds(double seconds, string expected)
    {
        var (lookup, stub) = Build(_ => Ok(EmptyAnswer));

        await lookup.LookupAsync(
            Fingerprint with { Duration = TimeSpan.FromSeconds(seconds) }, Token);

        Assert.Equal(expected, Assert.Single(stub.Requests).Form()["duration"]);
    }

    [Fact]
    public async Task ClustersComeBackBestScoreFirst()
    {
        var (lookup, _) = Build(_ => Ok(TwoClusters));

        var matches = await lookup.LookupAsync(Fingerprint, Token);

        Assert.Equal(2, matches.Count);
        Assert.Equal(1.0, matches[0].Score);
        Assert.Equal(0.87, matches[1].Score);
        Assert.Equal(Guid.Parse("be6fa248-97d1-4a18-845b-7c6a070b764c"), matches[0].AcoustId);
        Assert.Equal(25, Assert.Single(matches[0].Recordings).Sources);
    }

    /// <summary>
    /// The shape a naive client gets wrong. Both clusters point at the same
    /// recording, so this is one candidate with 678 submissions behind it, not
    /// two answers to choose between.
    /// </summary>
    [Fact]
    public async Task TheSameRecordingUnderTwoClustersIsOneCandidate()
    {
        var (lookup, _) = Build(_ => Ok(TwoClusters));

        var candidate = Assert.Single(
            RecordingCandidates.From(await lookup.LookupAsync(Fingerprint, Token)));

        Assert.Equal(1.0, candidate.Score);
        Assert.Equal(678, candidate.Sources);
    }

    [Fact]
    public async Task AClusterWithNoMusicBrainzLinkIsStillAMatch()
    {
        var (lookup, _) = Build(_ => Ok("""
            {
              "status": "ok",
              "results": [{ "id": "9ff43b6a-4f16-427c-93c2-92307ca505e0", "score": 0.99 }]
            }
            """));

        var matches = await lookup.LookupAsync(Fingerprint, Token);

        // "AcoustID knows this audio, nobody has tagged it" is a different fact
        // from "nothing matched", and only one of the two is worth submitting to.
        Assert.Empty(Assert.Single(matches).Recordings);
    }

    [Fact]
    public async Task NothingMatchingIsAnEmptyListRatherThanAnError()
    {
        var (lookup, _) = Build(_ => Ok(EmptyAnswer));

        Assert.Empty(await lookup.LookupAsync(Fingerprint, Token));
    }

    /// <summary>
    /// The failure every new installation hits, and the one that must never be
    /// retried: 100,000 files each retrying a bad key is hundreds of thousands
    /// of requests that cannot possibly succeed.
    /// </summary>
    [Fact]
    public async Task AnInvalidKeyIsRefusedOnceAndNotRetried()
    {
        var (lookup, stub) = Build(_ => StubHttpHandler.Json(
            HttpStatusCode.BadRequest,
            """{"error": {"code": 4, "message": "invalid API key"}, "status": "error"}"""));

        var failure = await Assert.ThrowsAsync<ProviderRejectedException>(
            async () => await lookup.LookupAsync(Fingerprint, Token));

        Assert.Equal("AcoustID", failure.Provider);
        Assert.Contains("invalid API key", failure.Message, StringComparison.Ordinal);
        Assert.Single(stub.Requests);
    }

    [Fact]
    public async Task AMissingKeyIsRefusedWithoutAskingTheService()
    {
        var (lookup, stub) = Build(
            _ => Ok(EmptyAnswer),
            options => options.ApiKey = string.Empty);

        var failure = await Assert.ThrowsAsync<ProviderRejectedException>(
            async () => await lookup.LookupAsync(Fingerprint, Token));

        // Names the setting rather than repeating the service's "invalid API
        // key", which reads like a wrong key rather than no key at all.
        Assert.Contains("Fonoteca:AcoustIdApiKey", failure.Message, StringComparison.Ordinal);
        Assert.Empty(stub.Requests);
    }

    /// <summary>
    /// Two claims in one test because they are the same claim: a 5xx is
    /// retried, and the retry goes through the gate.
    /// </summary>
    /// <remarks>
    /// Handler order is what is really being checked. Registered the other way
    /// round, the retries would leave the instant the first attempt failed —
    /// the precise burst that turns a temporary fault into a blocked address.
    /// </remarks>
    [Fact]
    public async Task ServerErrorsAreRetriedThroughTheGateAndThenReportedAsUnavailable()
    {
        var (lookup, stub) = Build(_ => StubHttpHandler.Json(
            HttpStatusCode.ServiceUnavailable, """{"status":"error"}"""));

        var started = Stopwatch.GetTimestamp();

        var failure = await Assert.ThrowsAsync<ProviderUnavailableException>(
            async () => await lookup.LookupAsync(Fingerprint, Token));

        var elapsed = Stopwatch.GetElapsedTime(started);

        Assert.Equal("AcoustID", failure.Provider);

        // Build() cuts the retries to one: the attempt, plus one retry.
        Assert.Equal(2, stub.Requests.Count);

        Assert.True(
            elapsed >= Gate,
            $"the retry should have waited at the gate; the call took {elapsed.TotalMilliseconds}ms");
    }

    [Fact]
    public async Task ANonJsonErrorPageIsNotMistakenForAnAnswer()
    {
        var (lookup, _) = Build(_ =>
            new HttpResponseMessage(HttpStatusCode.Forbidden)
            {
                Content = new StringContent("<html>blocked by proxy</html>"),
            });

        var failure = await Assert.ThrowsAsync<ProviderRejectedException>(
            async () => await lookup.LookupAsync(Fingerprint, Token));

        Assert.Contains("no error document", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnEmptyFingerprintIsACallerBug()
    {
        var (lookup, stub) = Build(_ => Ok(EmptyAnswer));

        await Assert.ThrowsAsync<ArgumentException>(
            async () => await lookup.LookupAsync(Fingerprint with { Value = "  " }, Token));

        Assert.Empty(stub.Requests);
    }

    public void Dispose()
    {
        foreach (var provider in _providers) provider.Dispose();
    }

    private static HttpResponseMessage Ok(string body) =>
        StubHttpHandler.Json(HttpStatusCode.OK, body);

    /// <summary>
    /// The real registration, with the socket replaced and the retry backoff
    /// collapsed so the suite does not spend half a minute asleep.
    /// </summary>
    private (IAcoustIdLookup Lookup, StubHttpHandler Stub) Build(
        Func<RecordedRequest, HttpResponseMessage> respond,
        Action<AcoustIdOptions>? configure = null)
    {
        var stub = new StubHttpHandler(respond);
        var services = new ServiceCollection();

        services.AddAcoustId(options =>
        {
            options.ApiKey = ApiKey;
            options.MinimumRequestInterval = Gate;
            configure?.Invoke(options);
        });

        // Re-opening the same named client appends configuration, and the last
        // primary handler wins — so everything AddAcoustId put in the pipeline
        // above it is still in place.
        services.AddHttpClient(AcoustIdOptions.HttpClientName)
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

        return (provider.GetRequiredService<IAcoustIdLookup>(), stub);
    }
}
