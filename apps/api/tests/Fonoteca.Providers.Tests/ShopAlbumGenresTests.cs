using System.Net;
using System.Text;
using Fonoteca.Domain.Abstractions;
using Fonoteca.Fixtures;
using Fonoteca.Providers.AppleMusic;
using Fonoteca.Providers.Qobuz;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Polly;

namespace Fonoteca.Providers.Tests;

/// <summary>
/// An album's genre from Apple Music, and from Qobuz where Apple has none,
/// built through the real registrations and pointed at a stub socket
/// answering with the recorded responses the motion art and booklet tests use.
/// </summary>
public sealed class ShopAlbumGenresTests : IDisposable
{
    private const string BewitchedBarcode = "197189040030";

    private const long BewitchedAppleId = 1690607869;

    private static readonly TimeSpan Gate = TimeSpan.FromMilliseconds(1);

    private readonly List<ServiceProvider> _providers = [];

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static AlbumToFind Bewitched => new("Bewitched", "Laufey", ["Laufey"], 2023, BewitchedBarcode, []);

    private static AlbumToFind Goldberg => new("Bach: Goldberg Variationen", "Andreas Staier", ["Andreas Staier"], 2010, null, []);

    /// <summary>Apple's record, recognised by its barcode, is asked for its genre, and Qobuz is never asked.</summary>
    [Fact]
    public async Task ApplesGenreIsTakenFirst()
    {
        var (genres, stub) = Build(Recorded);

        var found = await genres.FindAsync(Bewitched, Token);

        Assert.NotNull(found);
        Assert.Equal(["jazz"], found.Genres);
        Assert.Equal("Apple Music", found.Source);
        Assert.Equal("barcode", found.MatchedBy);
        Assert.Contains(stub.Requests, request => request.Uri.Query.Contains($"id={BewitchedAppleId}&", StringComparison.Ordinal));
        Assert.DoesNotContain(stub.Requests, request => request.Uri.Host == "www.qobuz.com");
    }

    /// <summary>A record Apple does not carry is Qobuz's, read by its genre's ids.</summary>
    [Fact]
    public async Task QobuzAnswersWhereAppleHasNoRecord()
    {
        var (genres, _) = Build(Recorded);

        var found = await genres.FindAsync(Goldberg, Token);

        Assert.NotNull(found);
        Assert.Equal(["classical"], found.Genres);
        Assert.Equal("Qobuz", found.Source);
    }

    /// <summary>Neither shop's record surely being this one is no answer, and an outage is not one either.</summary>
    [Fact]
    public async Task NoRecordIsNullAndAnOutageThrows()
    {
        var (genres, _) = Build(Recorded);

        Assert.Null(await genres.FindAsync(new AlbumToFind("Nobody Sells This", "Nobody", ["Nobody"], 1999, null, []), Token));

        var (down, _) = Build(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));

        await Assert.ThrowsAsync<ProviderUnavailableException>(() => down.FindAsync(Bewitched, Token));
    }

    public void Dispose()
    {
        foreach (var provider in _providers) provider.Dispose();
    }

    /// <summary>Apple's lookups and search for <i>Bewitched</i>, and Qobuz's search for the Goldberg Variations.</summary>
    private static HttpResponseMessage Recorded(RecordedRequest request)
    {
        var uri = request.Uri;

        if (uri.Host == "itunes.apple.com" && uri.AbsolutePath == "/lookup")
        {
            var bewitched = uri.Query.Contains($"upc={BewitchedBarcode}&", StringComparison.Ordinal)
                || uri.Query.Contains($"id={BewitchedAppleId}&", StringComparison.Ordinal);

            return Json(Fixture(bewitched ? "apple-lookup-bewitched.json" : "apple-lookup-none.json"));
        }

        if (uri.Host == "itunes.apple.com" && uri.AbsolutePath == "/search")
        {
            return Json(Fixture(uri.Query.Contains("Bewitched", StringComparison.Ordinal)
                ? "apple-search-bewitched.json"
                : "apple-lookup-none.json"));
        }

        if (uri.AbsolutePath.EndsWith("/album/search", StringComparison.Ordinal))
        {
            return uri.Query.Contains("Goldberg", StringComparison.Ordinal)
                ? Json(Fixture("qobuz-search-goldberg.json"))
                : Json("""{"albums":{"items":[]}}""");
        }

        return new HttpResponseMessage(HttpStatusCode.NotFound);
    }

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static string Fixture(string name)
    {
        var assembly = typeof(ShopAlbumGenresTests).Assembly;
        var resource = $"{assembly.GetName().Name}.Responses.{name}";

        using var stream = assembly.GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException($"Missing embedded fixture '{resource}'.");

        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private (IAlbumGenres Genres, StubHttpHandler Stub) Build(Func<RecordedRequest, HttpResponseMessage> respond)
    {
        var stub = new StubHttpHandler(respond);
        var services = new ServiceCollection();

        services.AddLogging();
        services.AddAppleMusic(options =>
        {
            options.MinimumRequestInterval = Gate;
            options.PageRequestInterval = Gate;
        });
        services.AddQobuz(options =>
        {
            options.AppId = "test-app";
            options.UserAuthToken = "test-token";
            options.MinimumRequestInterval = Gate;
        });
        services.AddAlbumGenres();

        foreach (var name in new[] { AppleMusicOptions.HttpClientName, QobuzOptions.HttpClientName })
        {
            services.AddHttpClient(name).ConfigurePrimaryHttpMessageHandler(() => stub);
        }

        services.ConfigureAll<HttpStandardResilienceOptions>(options =>
        {
            options.Retry.MaxRetryAttempts = 1;
            options.Retry.Delay = TimeSpan.FromMilliseconds(1);
            options.Retry.BackoffType = DelayBackoffType.Constant;
            options.Retry.UseJitter = false;
        });

        var provider = services.BuildServiceProvider();
        _providers.Add(provider);

        return (provider.GetRequiredService<IAlbumGenres>(), stub);
    }
}
