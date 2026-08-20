using System.Net;
using System.Net.Http.Json;
using Fonoteca.Api.Endpoints;
using Fonoteca.Api.Library;
using Fonoteca.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;

namespace Fonoteca.Integration.Tests;

/// <summary>
/// The scan endpoints through the real host, real routing and real serialisation.
/// </summary>
/// <remarks>
/// <see cref="LibraryScanTests"/> proves the reconciliation is correct; this
/// proves it is reachable. The two failures it can catch that unit-level wiring
/// cannot are a handler that was never registered in Program, and a response
/// record that does not survive JSON — both of which look fine in C# right up
/// until the web app gets <c>undefined</c>.
/// </remarks>
[Collection(nameof(PostgresCollection))]
public sealed class LibraryScanEndpointTests(PostgresFixture postgres) : IAsyncLifetime
{
    private string _connectionString = string.Empty;
    private string _root = string.Empty;
    private WebApplicationFactory<Program>? _factory;

    public async ValueTask InitializeAsync()
    {
        _connectionString = await postgres.CreateDatabaseAsync(TestContext.Current.CancellationToken);
        _root = Directory.CreateTempSubdirectory("fonoteca-scan-api-").FullName;

        // The host migrates on boot, so the endpoint tests exercise that path
        // too rather than arriving at an already-prepared schema.
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:Fonoteca", _connectionString);
            builder.UseSetting("Fonoteca:LibraryPath", _root);

            // The background warmer would put its own questions to the providers,
            // out of a thread nothing here waits for.
            builder.UseSetting("Fonoteca:WarmCandidates", "false");
        });
    }

    public async ValueTask DisposeAsync()
    {
        if (_factory is not null)
        {
            await _factory.DisposeAsync();
        }

        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public async Task PostScanReturnsWhatItDid()
    {
        WriteFile("Miles Davis/01 So What.flac");
        WriteFile("Miles Davis/cover.jpg");

        using var client = _factory!.CreateClient();

        using var response = await client.PostAsync(
            new Uri("/api/library/scan", UriKind.Relative),
            content: null,
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var summary = await response.Content.ReadFromJsonAsync<LibraryScanSummary>(
            TestContext.Current.CancellationToken);

        Assert.NotNull(summary);
        Assert.Equal(1, summary.FilesSeen);
        Assert.Equal(1, summary.Added);
        Assert.Equal(0, summary.Removed);

        await using var db = PostgresFixture.CreateContext(_connectionString);
        Assert.Equal(1, await db.MediaFiles.CountAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task GetScanReportsIdleBeforeAnyScanAndTheSummaryAfterOne()
    {
        WriteFile("a.flac");

        using var client = _factory!.CreateClient();

        var before = await client.GetFromJsonAsync<LibraryScanStatusResponse>(
            new Uri("/api/library/scan", UriKind.Relative),
            TestContext.Current.CancellationToken);

        Assert.NotNull(before);
        Assert.False(before.Running);
        Assert.Null(before.LastCompleted);

        using var scan = await client.PostAsync(
            new Uri("/api/library/scan", UriKind.Relative),
            content: null,
            TestContext.Current.CancellationToken);
        scan.EnsureSuccessStatusCode();

        var after = await client.GetFromJsonAsync<LibraryScanStatusResponse>(
            new Uri("/api/library/scan", UriKind.Relative),
            TestContext.Current.CancellationToken);

        Assert.NotNull(after);
        Assert.False(after.Running);
        Assert.NotNull(after.LastCompleted);
        Assert.Equal(1, after.LastCompleted.Added);
    }

    /// <summary>
    /// A missing root is a mount that has not come back, not a bug — so it is a
    /// 503 the caller can sensibly retry, and the catalogue is left alone.
    /// </summary>
    [Fact]
    public async Task PostScanReturns503WhenTheLibraryRootIsMissing()
    {
        Directory.Delete(_root, recursive: true);

        using var client = _factory!.CreateClient();

        using var response = await client.PostAsync(
            new Uri("/api/library/scan", UriKind.Relative),
            content: null,
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);

        var problem = await response.Content.ReadAsStringAsync(
            TestContext.Current.CancellationToken);
        Assert.Contains("LibraryPath", problem, StringComparison.Ordinal);
    }

    private void WriteFile(string relativePath, string content = "not really audio")
    {
        var absolute = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(absolute)!);
        File.WriteAllText(absolute, content);
    }
}
