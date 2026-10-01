using System.Net;
using System.Text;
using Fonoteca.Domain.Abstractions;
using Fonoteca.Domain.Catalogue;
using Fonoteca.Fixtures;
using Fonoteca.Providers.Qobuz;
using Microsoft.Extensions.DependencyInjection;

namespace Fonoteca.Providers.Tests;

/// <summary>
/// The Qobuz discography source, built through its real DI registration and
/// pointed at a stub socket.
/// </summary>
public sealed class QobuzReleaseDiscoveryTests : IDisposable
{
    private static readonly Mbid SamaraJoy = new(new Guid("5a2f2d1e-4c1b-4f6e-9a55-0c9f1b8e7d21"));

    private readonly List<ServiceProvider> _providers = [];

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>
    /// Each product keeps the quality the shop states for it.
    /// </summary>
    /// <remarks>
    /// The two products are Samara Joy's live album as Qobuz list it, one
    /// record under two ids and two barcodes. The quality is what picks which
    /// of them the missing shelf shows.
    /// </remarks>
    [Fact]
    public async Task EachProductKeepsTheQualityTheShopStates()
    {
        var discovery = Build(request => Ok(
            request.Uri.ToString().Contains("artist/search", StringComparison.Ordinal)
                ? """{ "artists": { "items": [ { "name": "Samara Joy", "id": 8133288, "albums_count": 2 } ] } }"""
                : """
                  { "albums": { "total": 2, "items": [
                    { "id": "a369xj1anvuyy", "title": "Live At The Blue Note L.A.", "upc": "0881061520641",
                      "hires": true, "maximum_bit_depth": 24, "maximum_sampling_rate": 96 },
                    { "id": "vu15v9oe6901f", "title": "Live At The Blue Note L.A.", "upc": "0881061520627",
                      "hires": false, "maximum_bit_depth": 16, "maximum_sampling_rate": 44.1 } ] } }
                  """));

        var found = await discovery.FindAsync(new ArtistToPicture(SamaraJoy, "Samara Joy"), Token);

        Assert.Equal(
            new (bool?, int?, double?)[] { (true, 24, 96), (false, 16, 44.1) },
            found.Releases.Select(release =>
                (release.HiRes, release.MaximumBitDepth, release.MaximumSamplingRate)));
    }

    public void Dispose()
    {
        foreach (var provider in _providers) provider.Dispose();
    }

    private static HttpResponseMessage Ok(string body) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };

    private IReleaseDiscovery Build(Func<RecordedRequest, HttpResponseMessage> respond)
    {
        var stub = new StubHttpHandler(respond);
        var services = new ServiceCollection();

        services.AddQobuz(options =>
        {
            options.AppId = "test-app";
            options.UserAuthToken = "test-token";
            options.MinimumRequestInterval = TimeSpan.FromMilliseconds(20);
        });

        services.AddHttpClient(QobuzOptions.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(() => stub);

        var provider = services.BuildServiceProvider();
        _providers.Add(provider);

        return provider.GetRequiredKeyedService<IReleaseDiscovery>(ReleaseDiscoverySources.Qobuz);
    }
}
