using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Fonoteca.Integration.Tests;

/// <summary>
/// The system endpoints through the real host, real routing and real serialisation.
/// </summary>
/// <remarks>
/// Asserted against the raw JSON rather than a deserialised record, because
/// what the web client consumes is the JSON. A record that round-trips
/// perfectly in C# and emits <c>"status": 2</c> would pass a typed assertion
/// and hand the browser a number where its generated union expects a name.
/// </remarks>
[Collection(nameof(PostgresCollection))]
public sealed class SystemEndpointTests(PostgresFixture postgres) : IAsyncLifetime
{
    private string _root = string.Empty;
    private WebApplicationFactory<Program>? _factory;

    public async ValueTask InitializeAsync()
    {
        var connectionString = await postgres.CreateDatabaseAsync(TestContext.Current.CancellationToken);
        _root = Directory.CreateTempSubdirectory("fonoteca-system-api-").FullName;

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:Fonoteca", connectionString);
            builder.UseSetting("Fonoteca:LibraryPath", _root);

            // The background warmer would put its own questions to the providers,
            // out of a thread nothing here waits for.
            builder.UseSetting("Fonoteca:WarmCandidates", "false");

            // No contact, so the probe answers from configuration alone and
            // this test never touches the network. That is also the state a
            // fresh checkout is in, which is the one worth pinning: the card
            // has to say something useful before anything is set up.
            builder.UseSetting("Fonoteca:MusicBrainzContact", string.Empty);
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
    public async Task MusicBrainzHealthReportsTheConfiguredServerWithoutCallingIt()
    {
        using var client = _factory!.CreateClient();

        using var response = await client.GetAsync(
            new Uri("/api/system/musicbrainz", UriKind.Relative),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var document = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var body = document.RootElement;

        // The name, not the ordinal. The generated TypeScript union is
        // "NotConfigured" | "Reachable" | "Unreachable" | "Rejected", and a
        // number here would type-check on both sides and match nothing.
        Assert.Equal("NotConfigured", body.GetProperty("status").GetString());

        Assert.False(body.GetProperty("contactConfigured").GetBoolean());
        Assert.True(body.GetProperty("isOfficialServer").GetBoolean());
        Assert.Contains(
            "musicbrainz.org",
            body.GetProperty("server").GetString() ?? "",
            StringComparison.Ordinal);

        // Numbers as numbers — see the NumberHandling note in Program.cs. If
        // this ever comes back as a string the generated client types every
        // count in the API as `string | number` again.
        Assert.Equal(JsonValueKind.Number, body.GetProperty("minimumRequestIntervalMs").ValueKind);
        Assert.Equal(1_000, body.GetProperty("minimumRequestIntervalMs").GetInt32());

        Assert.Equal(JsonValueKind.Null, body.GetProperty("latencyMs").ValueKind);
        Assert.Contains(
            "Fonoteca:MusicBrainzContact",
            body.GetProperty("detail").GetString() ?? "",
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task SystemInfoSerialisesCountsAsNumbers()
    {
        using var client = _factory!.CreateClient();

        using var response = await client.GetAsync(
            new Uri("/api/system/info", UriKind.Relative),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var document = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        var counts = document.RootElement.GetProperty("counts");
        Assert.Equal(JsonValueKind.Number, counts.GetProperty("files").ValueKind);
        Assert.Equal(JsonValueKind.Number, counts.GetProperty("recordings").ValueKind);
    }
}
