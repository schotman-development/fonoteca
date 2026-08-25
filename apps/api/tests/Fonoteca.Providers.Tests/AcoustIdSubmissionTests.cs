using System.Net;
using Fonoteca.Domain.Abstractions;
using Fonoteca.Domain.Catalogue;
using Fonoteca.Providers.AcoustId;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Polly;

namespace Fonoteca.Providers.Tests;

/// <summary>
/// The other direction: telling AcoustID what audio is, rather than asking.
/// </summary>
/// <remarks>
/// Built through the real registration like <see cref="AcoustIdClientTests"/>,
/// because the thing most worth pinning is the wire shape. A submission is the
/// only outbound claim this application makes and there is no way to withdraw
/// one, so a field in the wrong place is not a bug that shows up as a failure —
/// it shows up as a wrong assertion in somebody else's database.
/// </remarks>
public sealed class AcoustIdSubmissionTests : IDisposable
{
    private const string ApiKey = "test-key";
    private const string UserKey = "test-user-key";

    private const string OneAccepted =
        """{"status":"ok","submissions":[{"id":123456789,"status":"pending"}]}""";

    private static readonly TimeSpan Gate = TimeSpan.FromMilliseconds(10);

    /// <summary>
    /// A 641-second track. The fingerprint covers the first two minutes of it.
    /// </summary>
    private static readonly AudioFingerprint First =
        new("AQABz0qUkZK4oOfhL-CPc4e5C_wW2H2QH9uDL4cvoT8UNQ", TimeSpan.FromSeconds(641));

    private static readonly AudioFingerprint Second =
        new("AQADtEmi5FGiKMnxB92H_MeR_TjxHDmO_MFxHTmO_MFx3", TimeSpan.FromSeconds(212.4));

    private static readonly Mbid Recording =
        new(Guid.Parse("cd2e7c47-16f5-46c6-a37c-a1eb7bf599ff"));

    private static readonly Mbid Other =
        new(Guid.Parse("9ff43b6a-4f16-427c-93c2-92307ca505e0"));

    private readonly List<ServiceProvider> _providers = [];

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task TheBatchIsIndexedPerItemAndNamesBothKeys()
    {
        var (submission, stub) = Build(_ => Ok(OneAccepted));

        await submission.SubmitAsync(
            [
                new AcoustIdSubmissionItem(First, Recording),
                new AcoustIdSubmissionItem(Second, Other),
            ],
            Token);

        // One request, not two. Their API batches, and a thirteen-track album
        // that cost thirteen turns at the gate would take four seconds to send.
        var request = Assert.Single(stub.Requests);

        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.EndsWith("/v2/submit", request.Uri.AbsolutePath, StringComparison.Ordinal);
        Assert.True(request.IsGzipped, "a batch of fingerprints is the largest body this app sends");

        var form = request.Form();

        // Both keys, and they are not the same key. The application key says
        // which program is calling; the user key says whose claim this is, and
        // a submission without it is refused as anonymous.
        Assert.Equal(ApiKey, form["client"]);
        Assert.Equal(UserKey, form["user"]);

        // Zero-based, per item, three fields sharing one index.
        Assert.Equal(First.Value, form["fingerprint.0"]);
        Assert.Equal(Recording.Value.ToString("D"), form["mbid.0"]);
        Assert.Equal(Second.Value, form["fingerprint.1"]);
        Assert.Equal(Other.Value.ToString("D"), form["mbid.1"]);

        // The length of the *audio*, not of the fingerprint. The stored
        // fingerprints cover 120 seconds, and sending that would tell AcoustID
        // every track in the library is two minutes long — which is half of what
        // it uses to tell a track from its extended mix.
        Assert.Equal("641", form["duration.0"]);
        Assert.Equal("212", form["duration.1"]);
    }

    /// <summary>
    /// A second batch is numbered from zero again, or it carries nothing.
    /// </summary>
    /// <remarks>
    /// The one bug in this file that would have been invisible. AcoustID's
    /// parser walks <c>fingerprint.0</c>, <c>fingerprint.1</c> … and stops at
    /// the first index missing, so a second request whose fields start at
    /// <c>.50</c> is not numbered oddly — it is empty. It would be accepted,
    /// acknowledged, and drop every fingerprint past the fiftieth, on exactly
    /// the libraries big enough to have a box set in them.
    ///
    /// The request count is asserted rather than assumed, so raising the batch
    /// size fails this test instead of quietly turning it into a single-chunk
    /// one that proves nothing.
    /// </remarks>
    [Fact]
    public async Task EachBatchIsNumberedFromZero()
    {
        var (submission, stub) = Build(_ => Ok(OneAccepted));

        // One past the batch size, so the second request holds a single item.
        var items = Enumerable
            .Range(0, 51)
            .Select(_ => new AcoustIdSubmissionItem(First, Recording))
            .ToList();

        await submission.SubmitAsync(items, Token);

        Assert.Equal(2, stub.Requests.Count);

        var second = stub.Requests[1].Form();

        Assert.Equal(First.Value, second["fingerprint.0"]);
        Assert.False(second.ContainsKey("fingerprint.50"), "the index restarts per request");
        Assert.False(second.ContainsKey("fingerprint.1"), "the second batch holds one item");
    }

    [Fact]
    public async Task WithoutAUserKeyNothingIsSent()
    {
        var (submission, stub) = Build(_ => Ok(OneAccepted), options => options.UserKey = "");

        var failure = await Assert.ThrowsAsync<ProviderRejectedException>(
            async () => await submission.SubmitAsync(
                [new AcoustIdSubmissionItem(First, Recording)], Token));

        // Refused here, so the message names the setting. Sent, it would come
        // back "invalid user API key", which reads as a wrong key rather than an
        // absent one — and an application key alone is a request AcoustID cannot
        // attribute to anybody.
        Assert.Empty(stub.Requests);
        Assert.Contains("Fonoteca:AcoustIdUserKey", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NothingToSubmitIsNotARequest()
    {
        var (submission, stub) = Build(_ => Ok(OneAccepted));

        Assert.Empty(await submission.SubmitAsync([], Token));
        Assert.Empty(stub.Requests);
    }

    [Fact]
    public async Task TheReceiptsCarryTheirIdsAndStatus()
    {
        var (submission, _) = Build(_ => Ok(
            """
            {
              "status": "ok",
              "submissions": [
                { "id": 123456789, "status": "pending", "index": 0 },
                { "id": 123456790, "status": "pending", "index": 1 }
              ]
            }
            """));

        var receipts = await submission.SubmitAsync(
            [
                new AcoustIdSubmissionItem(First, Recording),
                new AcoustIdSubmissionItem(Second, Other),
            ],
            Token);

        Assert.Equal([123456789L, 123456790L], receipts.Select(receipt => receipt.Id));

        // "pending" is the answer, always: their import happens out of band and
        // nothing here waits for it. A test asserting "imported" would be
        // asserting a fiction.
        Assert.All(receipts, receipt => Assert.Equal("pending", receipt.Status));
    }

    [Fact]
    public async Task ARefusalIsRaisedRatherThanReportedAsNoReceipts()
    {
        var (submission, _) = Build(_ => StubHttpHandler.Json(
            HttpStatusCode.BadRequest,
            """{"status":"error","error":{"code":6,"message":"invalid user API key"}}"""));

        var failure = await Assert.ThrowsAsync<ProviderRejectedException>(
            async () => await submission.SubmitAsync(
                [new AcoustIdSubmissionItem(First, Recording)], Token));

        // An empty receipt list and a rejected batch must not look alike: the
        // caller stamps every file it sent, and a silent failure would record a
        // contribution that never happened and never offer it again.
        Assert.Contains("invalid user API key", failure.Message, StringComparison.Ordinal);
    }

    public void Dispose()
    {
        foreach (var provider in _providers) provider.Dispose();
    }

    private static HttpResponseMessage Ok(string body) =>
        StubHttpHandler.Json(HttpStatusCode.OK, body);

    private (IAcoustIdSubmission Submission, StubHttpHandler Stub) Build(
        Func<RecordedRequest, HttpResponseMessage> respond,
        Action<AcoustIdOptions>? configure = null)
    {
        var stub = new StubHttpHandler(respond);
        var services = new ServiceCollection();

        services.AddAcoustId(options =>
        {
            options.ApiKey = ApiKey;
            options.UserKey = UserKey;
            options.MinimumRequestInterval = Gate;
            configure?.Invoke(options);
        });

        services.AddHttpClient(AcoustIdOptions.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(() => stub);

        services.ConfigureAll<HttpStandardResilienceOptions>(options =>
        {
            options.Retry.MaxRetryAttempts = 1;
            options.Retry.Delay = TimeSpan.FromMilliseconds(1);
            options.Retry.BackoffType = DelayBackoffType.Constant;
            options.Retry.UseJitter = false;
        });

        var provider = services.BuildServiceProvider();
        _providers.Add(provider);

        return (provider.GetRequiredService<IAcoustIdSubmission>(), stub);
    }
}
