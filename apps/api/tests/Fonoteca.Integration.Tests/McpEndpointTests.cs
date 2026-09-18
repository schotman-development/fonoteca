using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Fonoteca.Domain.Abstractions;
using Fonoteca.Domain.Catalogue;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Fonoteca.Integration.Tests;

/// <summary>
/// The MCP endpoint through the real host, spoken to by the SDK's own client.
/// </summary>
/// <remarks>
/// Four things are under test, and each is a way the endpoint could be quietly
/// wrong: who can reach it, which tools it offers, whose name a decision goes
/// under, and whether a structured argument arrives whole. The last one is here
/// because the tools are thin — every rule is in a handler the HTTP tests
/// already cover — so the binding is most of what can break.
/// </remarks>
[Collection(nameof(PostgresCollection))]
public sealed class McpEndpointTests(PostgresFixture postgres) : IAsyncLifetime
{
    private const string Secret = "test-token";

    /// <summary>
    /// Every tool, and by omission every one that must not exist: nothing that
    /// writes tags in bulk, moves or trashes files, or buys anything.
    /// </summary>
    private static readonly string[] Tools =
    [
        "cancel_pass",
        "component_candidates",
        "decide_component",
        "decide_recording",
        "file_under_release",
        "folder_contents",
        "get_artist",
        "get_file",
        "get_release",
        "library_status",
        "list_artists",
        "list_folder",
        "list_releases",
        "mark_folder_unreleased",
        "musicbrainz_health",
        "open_questions",
        "recording_candidates",
        "release_slots",
        "reopen_folder",
        "search_releases",
        "start_pass",
    ];

    private string _connectionString = string.Empty;
    private string _root = string.Empty;
    private WebApplicationFactory<Program>? _factory;

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _connectionString = await postgres.CreateDatabaseAsync(Cancel);
        _root = Directory.CreateTempSubdirectory("fonoteca-mcp-").FullName;
        _factory = Factory(Secret);

        using var warm = _factory.CreateClient();
    }

    public async ValueTask DisposeAsync()
    {
        if (_factory is not null) await _factory.DisposeAsync();

        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task WithoutTheTokenNothingAnswers()
    {
        using var client = _factory!.CreateClient();

        using var bare = new HttpRequestMessage(HttpMethod.Post, "/mcp") { Content = Body() };
        using var none = await client.SendAsync(bare, Cancel);

        Assert.Equal(HttpStatusCode.Unauthorized, none.StatusCode);

        using var guessed = new HttpRequestMessage(HttpMethod.Post, "/mcp") { Content = Body() };
        guessed.Headers.Authorization = new("Bearer", "not-the-token");
        using var wrong = await client.SendAsync(guessed, Cancel);

        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
    }

    /// <summary>
    /// No token configured is no endpoint — including for a client that sends an
    /// empty bearer, which would otherwise compare equal to the empty setting.
    /// </summary>
    [Fact]
    public async Task WithNoTokenConfiguredTheEndpointIsNotThere()
    {
        await using var off = Factory(string.Empty);
        using var client = off.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp") { Content = Body() };
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer ");
        using var response = await client.SendAsync(request, Cancel);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task TheToolsAreTheListAndNothingElse()
    {
        await using var mcp = await ConnectAsync();

        var tools = await mcp.ListToolsAsync(cancellationToken: Cancel);

        Assert.Equal(Tools, tools.Select(tool => tool.Name).Order(StringComparer.Ordinal));
    }

    /// <summary>
    /// The point of <see cref="AgentCallerContext"/>: a folder dismissed through
    /// the endpoint is on record as the agent's, in the outcome and in the log.
    /// </summary>
    [Fact]
    public async Task ADecisionTakenThroughTheEndpointIsTheAgentsNotTheOwners()
    {
        MediaFileId id;

        await using (var db = PostgresFixture.CreateContext(_connectionString))
        {
            var open = new MediaFile
            {
                Id = MediaFileId.New(),
                Path = "Somebody/Mixtape/01 Side A.flac",
                SizeBytes = 9_000_000,
                LastModifiedUtc = DateTimeOffset.Parse("2026-01-01T00:00:00Z", CultureInfo.InvariantCulture),
                AcoustIdOutcome = AcoustIdOutcome.Unknown,
                AcoustIdCheckedUtc = DateTimeOffset.Parse("2026-02-01T10:00:00Z", CultureInfo.InvariantCulture),
            };

            id = open.Id;
            db.MediaFiles.Add(open);
            await db.SaveChangesAsync(Cancel);
        }

        await using var mcp = await ConnectAsync();

        var result = await mcp.CallToolAsync(
            "mark_folder_unreleased",
            new Dictionary<string, object?> { ["folder"] = "Somebody/Mixtape" },
            cancellationToken: Cancel);

        Assert.True(result.IsError is not true, Text(result));

        await using var check = PostgresFixture.CreateContext(_connectionString);

        var row = await check.MediaFiles.SingleAsync(file => file.Id == id, Cancel);

        Assert.Equal(AcoustIdOutcome.UnreleasedByAgent, row.AcoustIdOutcome);
        Assert.NotNull(row.IdentityDecidedUtc);

        var actors = await check.DomainEvents.Select(entry => entry.ActorId).Distinct().ToListAsync(Cancel);

        Assert.Equal([AgentCallerContext.AgentId], actors);
    }

    /// <summary>
    /// The seating arrives with all three fields bound.
    /// </summary>
    /// <remarks>
    /// Two distinct files on two distinct slots, neither file in the catalogue.
    /// Every field bound, the handler gets past both of its injectivity checks
    /// and answers that nothing in the set is open. A file id that failed to
    /// bind reads as the same empty guid twice and is refused as "named twice";
    /// a disc or position that failed to bind reads as the same slot twice.
    /// </remarks>
    [Fact]
    public async Task FilingPairsArriveWithEveryFieldBound()
    {
        await using var mcp = await ConnectAsync();

        using var pairs = JsonDocument.Parse(
            $$"""
            [
              { "file": "{{Guid.NewGuid()}}", "disc": 1, "position": 1 },
              { "file": "{{Guid.NewGuid()}}", "disc": 1, "position": 2 }
            ]
            """);

        var result = await mcp.CallToolAsync(
            "file_under_release",
            new Dictionary<string, object?>
            {
                ["release"] = Guid.NewGuid(),
                ["pairs"] = pairs.RootElement,
            },
            cancellationToken: Cancel);

        Assert.True(result.IsError);
        Assert.Contains("No open files in that set", Text(result), StringComparison.Ordinal);
    }

    private WebApplicationFactory<Program> Factory(string token) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:Fonoteca", _connectionString);
            builder.UseSetting("Fonoteca:LibraryPath", _root);
            builder.UseSetting("Fonoteca:WarmCandidates", "false");
            builder.UseSetting("Fonoteca:MusicBrainzContact", string.Empty);
            builder.UseSetting("Fonoteca:McpToken", token);
        });

    private async Task<McpClient> ConnectAsync()
    {
        var http = _factory!.CreateClient();

        var transport = new HttpClientTransport(
            new HttpClientTransportOptions
            {
                Endpoint = new Uri(http.BaseAddress!, "/mcp"),
                TransportMode = HttpTransportMode.StreamableHttp,
                AdditionalHeaders = new Dictionary<string, string>
                {
                    ["Authorization"] = $"Bearer {Secret}",
                },
            },
            http,
            loggerFactory: null,
            ownsHttpClient: true);

        return await McpClient.CreateAsync(transport, cancellationToken: Cancel);
    }

    private static StringContent Body() => new("{}", Encoding.UTF8, "application/json");

    private static string Text(CallToolResult result) =>
        string.Join('\n', result.Content.OfType<TextContentBlock>().Select(block => block.Text));
}
