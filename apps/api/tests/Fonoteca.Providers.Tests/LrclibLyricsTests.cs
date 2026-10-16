using System.Net;
using System.Text;
using System.Web;
using Fonoteca.Domain.Abstractions;
using Fonoteca.Fixtures;
using Fonoteca.Providers.Lrclib;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Options;
using Polly;

namespace Fonoteca.Providers.Tests;

/// <summary>
/// The LRCLIB lyrics source, built through its real DI registration and
/// pointed at a stub socket, answering with what LRCLIB answered.
/// </summary>
public sealed class LrclibLyricsTests : IDisposable
{
    private static readonly TimeSpan Gate = TimeSpan.FromMilliseconds(20);

    private static readonly SongToFind AmazingGrace = new(
        "Amazing Grace", "Judy Collins", null, "Both Sides Now - The Very Best Of", TimeSpan.FromSeconds(234.4));

    private readonly List<ServiceProvider> _providers = [];

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task TheFirstAskIsByAlbumAndLengthAndKeepsBothTexts()
    {
        var (lyrics, stub) = Build(_ => Json(HttpStatusCode.OK, Fixture("lrclib-get-amazing-grace.json")));

        var found = await lyrics.FindAsync(AmazingGrace, Token);

        Assert.NotNull(found);
        Assert.Equal(34509973, found.Id);
        Assert.Equal("album", found.MatchedBy);
        Assert.StartsWith("[00:00.78] Amazing grace, how sweet the sound\n", found.Synced, StringComparison.Ordinal);
        Assert.StartsWith("Amazing grace, how sweet the sound\n", found.Plain, StringComparison.Ordinal);
        Assert.False(found.Instrumental);

        var query = HttpUtility.ParseQueryString(Assert.Single(stub.Requests).Uri.Query);
        Assert.Equal("Amazing Grace", query["track_name"]);
        Assert.Equal("Judy Collins", query["artist_name"]);
        Assert.Equal("Both Sides Now - The Very Best Of", query["album_name"]);
        Assert.Equal("234.4", query["duration"]);
        Assert.StartsWith("Fonoteca/0.1", Assert.Single(stub.Requests).UserAgent, StringComparison.Ordinal);
    }

    /// <summary>
    /// A compilation's album names nothing; the same song by the same artist at
    /// the same length is found on any album, and then by the first artist.
    /// </summary>
    [Fact]
    public async Task AMissWidensToAnyAlbumThenTheFirstArtistEveryAskHeldToTheLength()
    {
        var (byAlbum, _) = Build(request =>
            HttpUtility.ParseQueryString(request.Uri.Query)["album_name"] is not null
                ? Json(HttpStatusCode.NotFound, Fixture("lrclib-get-not-found.json"))
                : Json(HttpStatusCode.OK, Fixture("lrclib-get-amazing-grace.json")));

        Assert.Equal("any album", (await byAlbum.FindAsync(AmazingGrace, Token))?.MatchedBy);

        var (byFirst, _) = Build(request =>
            HttpUtility.ParseQueryString(request.Uri.Query)["artist_name"] == "Judy Collins"
                ? Json(HttpStatusCode.OK, Fixture("lrclib-get-amazing-grace.json"))
                : Json(HttpStatusCode.NotFound, Fixture("lrclib-get-not-found.json")));

        var found = await byFirst.FindAsync(AmazingGrace with { Artist = "Judy Collins & Friends", FirstArtist = "Judy Collins" }, Token);

        Assert.Equal("first artist", found?.MatchedBy);

        var (none, misses) = Build(_ => Json(HttpStatusCode.NotFound, Fixture("lrclib-get-not-found.json")));

        Assert.Null(await none.FindAsync(AmazingGrace with { Artist = "Judy Collins & Friends", FirstArtist = "Judy Collins" }, Token));

        var asks = misses.Requests.Select(request => HttpUtility.ParseQueryString(request.Uri.Query)).ToList();
        Assert.Equal(3, asks.Count);
        Assert.Equal(["Both Sides Now - The Very Best Of", null, null], asks.Select(ask => ask["album_name"]));
        Assert.Equal(["Judy Collins & Friends", "Judy Collins & Friends", "Judy Collins"], asks.Select(ask => ask["artist_name"]));
        Assert.All(asks, ask => Assert.Equal("234.4", ask["duration"]));
    }

    [Fact]
    public async Task AnInstrumentalIsAnAnswerWithNoWords()
    {
        var (lyrics, stub) = Build(_ => Json(HttpStatusCode.OK, Fixture("lrclib-get-classical-gas.json")));

        var found = await lyrics.FindAsync(new SongToFind("Classical Gas", "Mason Williams", null, "Superhits: 1968", TimeSpan.FromSeconds(185)), Token);

        Assert.True(found?.Instrumental);
        Assert.Null(found!.Synced);
        Assert.Null(found.Plain);
        Assert.Single(stub.Requests);
    }

    /// <summary>A server too busy to answer has said nothing about the song.</summary>
    [Fact]
    public async Task ABusyServerIsAnOutageNotAMiss()
    {
        var (lyrics, _) = Build(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));

        await Assert.ThrowsAsync<ProviderUnavailableException>(() => lyrics.FindAsync(AmazingGrace, Token));
    }

    /// <summary>LRCLIB's limiter turns asks away in runs, so a song is asked again through seven refusals.</summary>
    [Fact]
    public async Task ARunOfRefusalsIsWaitedOut()
    {
        var refused = 0;
        var (lyrics, stub) = Build(
            _ => refused++ < 7
                ? Json(HttpStatusCode.TooManyRequests, """{"message":"Too many requests. Please retry later.","name":"RateLimited","statusCode":429}""")
                : Json(HttpStatusCode.OK, Fixture("lrclib-get-amazing-grace.json")),
            retriesAsConfigured: true);

        Assert.NotNull(await lyrics.FindAsync(AmazingGrace, Token));
        Assert.Equal(8, stub.Requests.Count);
    }

    /// <summary>The waits are LRCLIB's own and fit inside the request's deadline, or the last retries end as a timeout.</summary>
    [Fact]
    public void TheWaitsBetweenRetriesFitTheRequestsDeadline()
    {
        var services = new ServiceCollection();
        services.AddLrclib(options => options.Contact = "https://example.test/contact");

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptionsMonitor<HttpStandardResilienceOptions>>()
            .Get($"{LrclibOptions.HttpClientName}-standard");

        Assert.False(options.Retry.ShouldRetryAfterHeader);
        Assert.True(options.Retry.MaxRetryAttempts * options.Retry.MaxDelay < options.TotalRequestTimeout.Timeout);
    }

    /// <summary>Longer than LRCLIB takes, so not asked about without the length that guards the match.</summary>
    [Fact]
    public async Task ATrackOverAnHourIsNotAsked()
    {
        var (lyrics, stub) = Build(_ => throw new InvalidOperationException("asked"));

        Assert.Null(await lyrics.FindAsync(AmazingGrace with { Duration = TimeSpan.FromMinutes(61) }, Token));
        Assert.Empty(stub.Requests);
    }

    public void Dispose()
    {
        foreach (var provider in _providers) provider.Dispose();
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static string Fixture(string name)
    {
        var assembly = typeof(LrclibLyricsTests).Assembly;
        var resource = $"{assembly.GetName().Name}.Responses.{name}";

        using var stream = assembly.GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException($"Missing embedded fixture '{resource}'.");

        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private (ISongLyrics Lyrics, StubHttpHandler Stub) Build(
        Func<RecordedRequest, HttpResponseMessage> respond,
        bool retriesAsConfigured = false)
    {
        var stub = new StubHttpHandler(respond);
        var services = new ServiceCollection();

        services.AddLrclib(options =>
        {
            options.MinimumRequestInterval = Gate;
            options.Contact = "https://example.test/contact";
        });

        services.AddHttpClient(LrclibOptions.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(() => stub);

        services.ConfigureAll<HttpStandardResilienceOptions>(options =>
        {
            if (!retriesAsConfigured) options.Retry.MaxRetryAttempts = 1;
            options.Retry.Delay = TimeSpan.FromMilliseconds(1);
            options.Retry.BackoffType = DelayBackoffType.Constant;
            options.Retry.UseJitter = false;
        });

        var provider = services.BuildServiceProvider();
        _providers.Add(provider);

        return (provider.GetRequiredService<ISongLyrics>(), stub);
    }
}
