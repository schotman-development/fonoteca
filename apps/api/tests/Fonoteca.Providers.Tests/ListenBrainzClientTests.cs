using System.Net;
using System.Text;
using System.Text.Json;
using Fonoteca.Domain.Abstractions;
using Fonoteca.Domain.Catalogue;
using Fonoteca.Fixtures;
using Fonoteca.Providers.ListenBrainz;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Polly;

namespace Fonoteca.Providers.Tests;

/// <summary>ListenBrainz's API, built through its real DI registration and pointed at a stub socket.</summary>
public sealed class ListenBrainzClientTests : IDisposable
{
    private static readonly Mbid Recording = new(Guid.Parse("11111111-1111-4111-8111-111111111111"));

    private readonly List<ServiceProvider> _providers = [];

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ATokenIsAskedAboutWithItsHeaderAndARefusedOneIsNoUser()
    {
        var (client, stub) = Build(request => request.Headers("Authorization").SingleOrDefault() == "Token good"
            ? Json(HttpStatusCode.OK, """{"code":200,"message":"Token valid.","valid":true,"user_name":"lb-me"}""")
            : Json(HttpStatusCode.OK, """{"code":200,"message":"Token invalid.","valid":false}"""));

        Assert.Equal("lb-me", await client.UserOfAsync("good", Token));
        Assert.Null(await client.UserOfAsync("bad", Token));
        Assert.All(stub.Requests, request => Assert.Equal("/1/validate-token", request.Uri.AbsolutePath));
        Assert.StartsWith("Fonoteca/0.1", stub.Requests[0].UserAgent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AListensPageIsPagedByMaxTsAndMappedMbidsWin()
    {
        var (client, stub) = Build(_ => Json(HttpStatusCode.OK, """
            {"payload":{"count":2,"user_id":"lb-me","listens":[
              {"listened_at":1700000800,"track_metadata":{"artist_name":"Wiener Philharmoniker","track_name":"Allegro","release_name":"Symphonies",
                "additional_info":{"recording_mbid":"22222222-2222-4222-8222-222222222222"},
                "mbid_mapping":{"recording_mbid":"11111111-1111-4111-8111-111111111111"}}},
              {"listened_at":1700000700,"track_metadata":{"artist_name":"Somebody","track_name":"Something","release_name":""}}
            ]}}
            """));

        var page = await client.ListensAsync("lb me", DateTimeOffset.FromUnixTimeSeconds(1_700_000_900), Token);

        Assert.Equal(
            [
                new HeardListen(DateTimeOffset.FromUnixTimeSeconds(1_700_000_800), "Wiener Philharmoniker", "Allegro", "Symphonies", Recording),
                new HeardListen(DateTimeOffset.FromUnixTimeSeconds(1_700_000_700), "Somebody", "Something", null, null),
            ],
            page);

        var asked = Assert.Single(stub.Requests).Uri;
        Assert.Equal("/1/user/lb%20me/listens", asked.AbsolutePath);
        Assert.Equal("?count=1000&max_ts=1700000900", asked.Query);
    }

    [Fact]
    public async Task OneListenIsSingleSeveralImportAndOneWithNoTimePlayingNow()
    {
        var (client, stub) = Build(_ => Json(HttpStatusCode.OK, """{"status":"ok"}"""));
        var heard = new ListenToSubmit(DateTimeOffset.FromUnixTimeSeconds(1_700_000_000), "Wiener Philharmoniker", "Allegro", "Symphonies", Recording, 1, 420);

        await client.SubmitAsync("good", [heard], Token);
        await client.SubmitAsync("good", [heard, heard with { ListenedAt = DateTimeOffset.FromUnixTimeSeconds(1_700_000_500) }], Token);
        await client.SubmitAsync("good", [heard with { ListenedAt = null, Release = null }], Token);

        var bodies = stub.Requests.Select(request => JsonDocument.Parse(request.Body).RootElement).ToList();
        Assert.Equal(["single", "import", "playing_now"], bodies.Select(body => body.GetProperty("listen_type").GetString()));

        var single = bodies[0].GetProperty("payload")[0];
        Assert.Equal(1_700_000_000, single.GetProperty("listened_at").GetInt64());
        var track = single.GetProperty("track_metadata");
        Assert.Equal(("Wiener Philharmoniker", "Allegro", "Symphonies"), (track.GetProperty("artist_name").GetString(), track.GetProperty("track_name").GetString(), track.GetProperty("release_name").GetString()));
        var info = track.GetProperty("additional_info");
        Assert.Equal(Recording.ToString(), info.GetProperty("recording_mbid").GetString());
        Assert.Equal(1, info.GetProperty("tracknumber").GetInt32());
        Assert.Equal(420_000, info.GetProperty("duration_ms").GetInt32());

        var now = bodies[2].GetProperty("payload")[0];
        Assert.False(now.TryGetProperty("listened_at", out _));
        Assert.False(now.GetProperty("track_metadata").TryGetProperty("release_name", out _));
        Assert.All(stub.Requests, request => Assert.Equal("Token good", request.Headers("Authorization").Single()));
    }

    [Fact]
    public async Task ARefusedSubmissionIsARejectionNotAnOutage()
    {
        var (client, _) = Build(_ => Json(HttpStatusCode.BadRequest, """{"code":400,"error":"Value for key listened_at is too low."}"""));
        var heard = new ListenToSubmit(DateTimeOffset.FromUnixTimeSeconds(1), "A", "B", null, null, null, null);

        var refused = await Assert.ThrowsAsync<ProviderRejectedException>(() => client.SubmitAsync("good", [heard], Token));
        Assert.Contains("too low", refused.Message, StringComparison.Ordinal);
    }

    public void Dispose()
    {
        foreach (var provider in _providers) provider.Dispose();
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private (IListenBrainz Client, StubHttpHandler Stub) Build(Func<RecordedRequest, HttpResponseMessage> respond)
    {
        var stub = new StubHttpHandler(respond);
        var services = new ServiceCollection();

        services.AddListenBrainz(options =>
        {
            options.MinimumRequestInterval = TimeSpan.FromMilliseconds(1);
            options.Contact = "https://example.test/contact";
        });

        services.AddHttpClient(ListenBrainzOptions.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => stub);

        services.ConfigureAll<HttpStandardResilienceOptions>(options =>
        {
            options.Retry.MaxRetryAttempts = 1;
            options.Retry.Delay = TimeSpan.FromMilliseconds(1);
            options.Retry.BackoffType = DelayBackoffType.Constant;
            options.Retry.UseJitter = false;
        });

        var provider = services.BuildServiceProvider();
        _providers.Add(provider);

        return (provider.GetRequiredService<IListenBrainz>(), stub);
    }
}
