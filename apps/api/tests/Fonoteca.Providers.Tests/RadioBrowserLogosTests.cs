using System.Net;
using System.Text;
using System.Web;
using Fonoteca.Domain.Abstractions;
using Fonoteca.Fixtures;
using Fonoteca.Providers.RadioBrowser;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Polly;

namespace Fonoteca.Providers.Tests;

/// <summary>
/// The radio-browser logo lookup, built through its real DI registration and
/// pointed at a stub socket, answering with what radio-browser answered.
/// </summary>
public sealed class RadioBrowserLogosTests : IDisposable
{
    private readonly List<ServiceProvider> _providers = [];

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task TheStreamIsAskedForFirstOnEveryServerAtOnce()
    {
        var (logos, stub) = Build(_ => Json(HttpStatusCode.OK, Fixture("radiobrowser-byurl-radio5-sb-aac.json")));

        var logo = await logos.FindAsync("NPO Radio 5", "https://icecast.omroep.nl/radio5-sb-aac", Token);

        Assert.Equal("https://www.nporadio5.nl/apple-touch-icon.png", logo);

        var request = Assert.Single(stub.Requests);
        Assert.Equal("all.api.radio-browser.info", request.Uri.Host);
        Assert.Equal("/json/stations/byurl", request.Uri.AbsolutePath);
        Assert.Equal("https://icecast.omroep.nl/radio5-sb-aac", HttpUtility.ParseQueryString(request.Uri.Query)["url"]);
        Assert.StartsWith("Fonoteca/0.1", request.UserAgent, StringComparison.Ordinal);
    }

    /// <summary>
    /// Four stations are called NPO Radio 5 and one plays this stream; its
    /// logo is the old http one, which no other hit has.
    /// </summary>
    [Fact]
    public async Task AStreamNoStationWasAddedWithIsFoundByNameAndHeldToTheStream()
    {
        var (logos, stub) = Build(request => request.Uri.AbsolutePath.EndsWith("/byurl", StringComparison.Ordinal)
            ? Json(HttpStatusCode.OK, "[]")
            : Json(HttpStatusCode.OK, Fixture("radiobrowser-byname-npo-radio-5.json")));

        var logo = await logos.FindAsync("NPO Radio 5", "http://icecast.omroep.nl/radio5-bb-mp3", Token);

        Assert.Equal("http://www.radio5.nl/apple-touch-icon.png", logo);
        Assert.Equal(2, stub.Requests.Count);
        Assert.Equal("/json/stations/byname/NPO%20Radio%205", stub.Requests[1].Uri.AbsolutePath);
        Assert.Equal("100", HttpUtility.ParseQueryString(stub.Requests[1].Uri.Query)["limit"]);
    }

    /// <summary>A station added with a playlist's address is found by the stream that playlist resolves to.</summary>
    [Fact]
    public async Task TheResolvedStreamCountsAsTheStream()
    {
        var (logos, _) = Build(request => request.Uri.AbsolutePath.EndsWith("/byurl", StringComparison.Ordinal)
            ? Json(HttpStatusCode.OK, "[]")
            : Json(HttpStatusCode.OK, """[{"url":"https://example.org/radio5.m3u","url_resolved":"https://example.org/radio5.mp3","favicon":"https://example.org/radio5.png"}]"""));

        Assert.Equal("https://example.org/radio5.png", await logos.FindAsync("Radio 5", "https://example.org/radio5.mp3", Token));
    }

    [Fact]
    public async Task ANameWhoseStationsPlayOtherStreamsIsNoAnswer()
    {
        var (logos, _) = Build(request => request.Uri.AbsolutePath.EndsWith("/byurl", StringComparison.Ordinal)
            ? Json(HttpStatusCode.OK, "[]")
            : Json(HttpStatusCode.OK, Fixture("radiobrowser-byname-npo-radio-5.json")));

        Assert.Null(await logos.FindAsync("NPO Radio 5", "https://example.org/radio5", Token));
    }

    /// <summary>A server too busy to answer has said nothing about the station.</summary>
    [Fact]
    public async Task ABusyServerIsAnOutageNotAMiss()
    {
        var (logos, _) = Build(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));

        await Assert.ThrowsAsync<ProviderUnavailableException>(() => logos.FindAsync("NPO Radio 5", "https://icecast.omroep.nl/radio5-sb-aac", Token));
    }

    public void Dispose()
    {
        foreach (var provider in _providers) provider.Dispose();
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static string Fixture(string name)
    {
        var assembly = typeof(RadioBrowserLogosTests).Assembly;
        var resource = $"{assembly.GetName().Name}.Responses.{name}";

        using var stream = assembly.GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException($"Missing embedded fixture '{resource}'.");

        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private (RadioBrowserLogos Logos, StubHttpHandler Stub) Build(Func<RecordedRequest, HttpResponseMessage> respond)
    {
        var stub = new StubHttpHandler(respond);
        var services = new ServiceCollection();

        services.AddStationLogos(options =>
        {
            options.MinimumRequestInterval = TimeSpan.FromMilliseconds(20);
            options.Contact = "https://example.test/contact";
        });

        services.AddHttpClient(RadioBrowserOptions.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(() => stub);

        services.ConfigureAll<HttpStandardResilienceOptions>(options =>
        {
            options.Retry.Delay = TimeSpan.FromMilliseconds(1);
            options.Retry.BackoffType = DelayBackoffType.Constant;
            options.Retry.UseJitter = false;
        });

        var provider = services.BuildServiceProvider();
        _providers.Add(provider);

        return (provider.GetRequiredService<RadioBrowserLogos>(), stub);
    }
}
