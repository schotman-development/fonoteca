using System.Net;
using System.Net.Sockets;
using System.Text;
using Fonoteca.Domain.Abstractions;
using Fonoteca.Domain.Catalogue;
using Fonoteca.Fixtures;
using Fonoteca.Providers.Radio;
using Fonoteca.Providers.RadioBrowser;
using Fonoteca.Providers.Wikidata;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Polly;

namespace Fonoteca.Providers.Tests;

/// <summary>
/// A station's logo from its home page, Wikidata and radio-browser, in that
/// order, through the real registrations with every socket stubbed but the
/// one test that proves the home page client's own.
/// </summary>
public sealed class StationLogosTests : IDisposable
{
    private const string Radio5Head = """
        <meta content="https://www.nporadio5.nl/images/og_image.jpg" property="og:image" data-next-head=""/>
        <link href="/apple-touch-icon.png" rel="apple-touch-icon" sizes="180x180" data-next-head=""/>
        <link href="/favicon-32x32.png" rel="icon" sizes="32x32" type="image/png" data-next-head=""/>
        """;

    private const string SmallHead = """<link href="/favicon-32x32.png" rel="icon" sizes="32x32" type="image/png"/>""";

    /// <summary>What the query service answered for NPO Radio 5's site on 2026-10-23.</summary>
    private const string Radio5Logo = """
        {"head":{"vars":["logo"]},"results":{"bindings":[{"logo":{"type":"uri","value":"http://commons.wikimedia.org/wiki/Special:FilePath/NPO%20Radio%205%20logo%202016.svg"}}]}}
        """;

    private const string NoLogo = """{"head":{"vars":["logo"]},"results":{"bindings":[]}}""";

    private static readonly RadioStation Radio5 = new("NPO Radio 5", "http://icecast.omroep.nl/radio5-bb-mp3", "https://www.nporadio5.nl/", null);

    private readonly List<ServiceProvider> _providers = [];

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ALargeIconOnTheHomePageAnswersBeforeAnyOtherSourceIsAsked()
    {
        var (logos, requests) = Build(page: _ => Html(Radio5Head));

        Assert.Equal("https://www.nporadio5.nl/apple-touch-icon.png", await logos.FindAsync(Radio5, Token));
        Assert.Equal(["https://www.nporadio5.nl/"], requests().Select(request => request.Uri.AbsoluteUri));
    }

    [Fact]
    public async Task WithoutOneWikidataIsAskedByTheStreamAndEveryWayOfWritingTheSite()
    {
        var (logos, requests) = Build(page: _ => Html(SmallHead), wikidata: _ => Json(Radio5Logo));

        Assert.Equal(
            "https://commons.wikimedia.org/wiki/Special:FilePath/NPO%20Radio%205%20logo%202016.svg?width=500",
            await logos.FindAsync(Radio5, Token));

        var query = Encoding.UTF8.GetString(Assert.Single(requests(), request => request.Uri.Host == "query.wikidata.org").Body);
        Assert.Contains("wdt:P963", query, StringComparison.Ordinal);
        Assert.Contains("<http://icecast.omroep.nl/radio5-bb-mp3>", query, StringComparison.Ordinal);
        Assert.Contains("<https://www.nporadio5.nl/>", query, StringComparison.Ordinal);
        Assert.Contains("<http://nporadio5.nl>", query, StringComparison.Ordinal);
        Assert.Equal(8, WikidataStationLogos.Sites(Radio5.HomePageUrl!).Count);
        Assert.DoesNotContain(requests(), request => request.Uri.Host == "all.api.radio-browser.info");
    }

    /// <summary>A sister channel's page is not its network's site.</summary>
    [Fact]
    public void ASitesRootIsNeverAskedForOneOfItsPages()
    {
        var sites = WikidataStationLogos.Sites("http://www.skyradio.nl/player/sky-radio-80s");

        Assert.Contains("https://skyradio.nl/player/sky-radio-80s/", sites);
        Assert.DoesNotContain(sites, site => site.TrimEnd('/').EndsWith("skyradio.nl", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ThenRadioBrowserThenThePagesSmallIconsThenAFaviconItAnswersFor()
    {
        var (byRadioBrowser, _) = Build(page: _ => Html(SmallHead), radioBrowser: _ => Json(Fixture("radiobrowser-byname-npo-radio-5.json")));
        Assert.Equal("http://www.radio5.nl/apple-touch-icon.png", await byRadioBrowser.FindAsync(Radio5, Token));

        var (bySmall, _) = Build(page: _ => Html(SmallHead));
        Assert.Equal("https://www.nporadio5.nl/favicon-32x32.png", await bySmall.FindAsync(Radio5, Token));

        var (byFavicon, asked) = Build(page: request => request.Uri.AbsolutePath == "/favicon.ico"
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([0, 0, 1, 0]) { Headers = { ContentType = new("image/x-icon") } } }
            : Html("<title>Radio 5</title>"));
        Assert.Equal("https://www.nporadio5.nl/favicon.ico", await byFavicon.FindAsync(Radio5, Token));
        Assert.Equal("/favicon.ico", asked().Last(request => request.Uri.Host == "www.nporadio5.nl").Uri.AbsolutePath);

        var (none, _) = Build(page: request => request.Uri.AbsolutePath == "/favicon.ico"
            ? new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("<h1>Not found</h1>", Encoding.UTF8, "text/html") }
            : Html("<title>Radio 5</title>"));
        Assert.Null(await none.FindAsync(Radio5, Token));
    }

    /// <summary>A home page that is the stream, a page that is down and a query service that fails are each passed over.</summary>
    [Fact]
    public async Task ASourceThatCannotAnswerIsPassedOver()
    {
        var (stream, _) = Build(
            page: _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[4096]) { Headers = { ContentType = new("audio/mpeg") } } },
            radioBrowser: _ => Json(Fixture("radiobrowser-byname-npo-radio-5.json")));
        Assert.Equal("http://www.radio5.nl/apple-touch-icon.png", await stream.FindAsync(Radio5, Token));

        var (down, _) = Build(
            page: _ => throw new HttpRequestException("Connection refused"),
            wikidata: _ => new HttpResponseMessage(HttpStatusCode.InternalServerError),
            radioBrowser: _ => Json(Fixture("radiobrowser-byname-npo-radio-5.json")));
        Assert.Equal("http://www.radio5.nl/apple-touch-icon.png", await down.FindAsync(Radio5, Token));

        var (nothing, requests) = Build(
            page: _ => throw new HttpRequestException("Connection refused"),
            wikidata: _ => new HttpResponseMessage(HttpStatusCode.InternalServerError),
            radioBrowser: _ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        Assert.Null(await nothing.FindAsync(Radio5, Token));
        Assert.DoesNotContain(requests(), request => request.Uri.AbsolutePath == "/favicon.ico");
    }

    /// <summary>The real client, socket and all: a home page on this machine is never connected to.</summary>
    [Fact]
    public async Task TheHomePageClientConnectsToNothingButThePublicInternet()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var services = new ServiceCollection();
        services.AddStationLogos(options => options.Contact = "https://example.test/contact");
        using var provider = services.BuildServiceProvider();
        var homepages = provider.GetRequiredService<StationHomepages>();

        await Assert.ThrowsAsync<ProviderUnavailableException>(() => homepages.IconsAsync($"http://127.0.0.1:{port}/", Token));
        await Assert.ThrowsAsync<ProviderUnavailableException>(() => homepages.IconsAsync($"http://localhost:{port}/", Token));
        Assert.False(listener.Pending());
    }

    [Theory]
    [InlineData("127.0.0.1", false)]
    [InlineData("10.1.2.3", false)]
    [InlineData("172.16.0.1", false)]
    [InlineData("172.32.0.1", true)]
    [InlineData("192.168.1.1", false)]
    [InlineData("169.254.169.254", false)]
    [InlineData("100.64.0.1", false)]
    [InlineData("100.127.255.254", false)]
    [InlineData("100.128.0.1", true)]
    [InlineData("0.0.0.0", false)]
    [InlineData("224.0.0.1", false)]
    [InlineData("::1", false)]
    [InlineData("fe80::1", false)]
    [InlineData("fd00::1", false)]
    [InlineData("::ffff:192.168.1.1", false)]
    [InlineData("91.98.4.78", true)]
    [InlineData("2a01:4f8:1c1d:699::1", true)]
    public void OnlyPublicAddressesArePublic(string address, bool expected) =>
        Assert.Equal(expected, StationHomepages.IsPublic(IPAddress.Parse(address)));

    public void Dispose()
    {
        foreach (var provider in _providers) provider.Dispose();
    }

    private static HttpResponseMessage Html(string head) =>
        new(HttpStatusCode.OK) { Content = new StringContent($"<!doctype html><html><head>{head}</head><body></body></html>", Encoding.UTF8, "text/html") };

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static string Fixture(string name)
    {
        var assembly = typeof(StationLogosTests).Assembly;

        using var stream = assembly.GetManifestResourceStream($"{assembly.GetName().Name}.Responses.{name}")
            ?? throw new InvalidOperationException($"Missing embedded fixture '{name}'.");

        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>The logos with each source's socket stubbed; one with no answer given has nothing to say.</summary>
    private (IStationLogos Logos, Func<IReadOnlyList<RecordedRequest>> Requests) Build(
        Func<RecordedRequest, HttpResponseMessage> page,
        Func<RecordedRequest, HttpResponseMessage>? wikidata = null,
        Func<RecordedRequest, HttpResponseMessage>? radioBrowser = null)
    {
        var stubs = new[]
        {
            (Name: StationHomepages.HttpClientName, Stub: new StubHttpHandler(page)),
            (Name: WikidataOptions.HttpClientName, Stub: new StubHttpHandler(wikidata ?? (_ => Json(NoLogo)))),
            (Name: RadioBrowserOptions.HttpClientName, Stub: new StubHttpHandler(radioBrowser ?? (_ => Json("[]")))),
        };

        var services = new ServiceCollection();
        services.AddLogging();

        services.AddWikidata(options =>
        {
            options.Contact = "https://example.test/contact";
            options.MinimumRequestInterval = TimeSpan.FromMilliseconds(20);
        });

        services.AddStationLogos(options =>
        {
            options.Contact = "https://example.test/contact";
            options.MinimumRequestInterval = TimeSpan.FromMilliseconds(20);
        });

        foreach (var (name, stub) in stubs) services.AddHttpClient(name).ConfigurePrimaryHttpMessageHandler(() => stub);

        services.ConfigureAll<HttpStandardResilienceOptions>(options =>
        {
            options.Retry.MaxRetryAttempts = 1;
            options.Retry.Delay = TimeSpan.FromMilliseconds(1);
            options.Retry.BackoffType = DelayBackoffType.Constant;
            options.Retry.UseJitter = false;
        });

        var provider = services.BuildServiceProvider();
        _providers.Add(provider);

        return (provider.GetRequiredService<IStationLogos>(), () => [.. stubs.SelectMany(each => each.Stub.Requests)]);
    }
}
