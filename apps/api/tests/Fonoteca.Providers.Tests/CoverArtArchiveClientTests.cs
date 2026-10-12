using System.Net;
using System.Net.Http.Headers;
using Fonoteca.Domain.Abstractions;
using Fonoteca.Domain.Catalogue;
using Fonoteca.Fixtures;
using Fonoteca.Providers.CoverArt;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Polly;

namespace Fonoteca.Providers.Tests;

/// <summary>
/// The archive's cover download, through its real registration and a stub socket.
/// </summary>
public sealed class CoverArtArchiveClientTests : IDisposable
{
    private static readonly Mbid Release = new(Guid.Parse("f333a3df-77d1-4d68-bcc8-85fdffd1f3a6"));

    private readonly List<ServiceProvider> _providers = [];

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>
    /// A cover is the 1200px rendition, because it is also the album's one
    /// cover.jpg; the 500px where the archive has no 1200px copy.
    /// </summary>
    [Fact]
    public async Task ACoverIsThe1200pxRenditionAndThe500pxWhereThatIsMissing()
    {
        var (archive, stub) = Build(_ => Jpeg());

        await archive.DownloadAsync(Release, 22600067618, Token);

        Assert.EndsWith("/22600067618-1200.jpg", Assert.Single(stub.Requests).Uri.AbsolutePath, StringComparison.Ordinal);

        var (older, fell) = Build(request => request.Uri.AbsolutePath.EndsWith("-1200.jpg", StringComparison.Ordinal)
            ? new HttpResponseMessage(HttpStatusCode.NotFound)
            : Jpeg());

        Assert.Equal("image/jpeg", (await older.DownloadAsync(Release, 22600067618, Token)).MediaType);
        Assert.EndsWith("/22600067618-500.jpg", fell.Requests[^1].Uri.AbsolutePath, StringComparison.Ordinal);
    }

    /// <summary>
    /// Only a missing 1200px copy falls back: a server error is an outage, or a
    /// passing one would store a 500px cover that is never asked about again.
    /// </summary>
    [Fact]
    public async Task AServerErrorOnThe1200pxCopyIsAnOutageNotAFallback()
    {
        var (archive, stub) = Build(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));

        await Assert.ThrowsAsync<ProviderUnavailableException>(() => archive.DownloadAsync(Release, 22600067618, Token));
        Assert.DoesNotContain(stub.Requests, request => request.Uri.AbsolutePath.EndsWith("-500.jpg", StringComparison.Ordinal));
    }

    public void Dispose()
    {
        foreach (var provider in _providers) provider.Dispose();
    }

    private static HttpResponseMessage Jpeg() =>
        new(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent([0xFF, 0xD8, 0xFF, 0xE0])
            {
                Headers = { ContentType = new MediaTypeHeaderValue("image/jpeg") },
            },
        };

    private (ICoverArtArchive Archive, StubHttpHandler Stub) Build(Func<RecordedRequest, HttpResponseMessage> respond)
    {
        var stub = new StubHttpHandler(respond);
        var services = new ServiceCollection();

        services.AddLogging();
        services.AddCoverArtArchive(options => options.Contact = "test@example.org");
        services.AddHttpClient(CoverArtArchiveOptions.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => stub);

        services.ConfigureAll<HttpStandardResilienceOptions>(options =>
        {
            options.Retry.MaxRetryAttempts = 1;
            options.Retry.Delay = TimeSpan.FromMilliseconds(1);
            options.Retry.BackoffType = DelayBackoffType.Constant;
            options.Retry.UseJitter = false;
        });

        var provider = services.BuildServiceProvider();
        _providers.Add(provider);

        return (provider.GetRequiredService<ICoverArtArchive>(), stub);
    }
}
