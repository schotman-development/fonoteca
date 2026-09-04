using Fonoteca.Fixtures;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Fonoteca.Domain.Abstractions;
using Fonoteca.Providers.Qobuz;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Polly;

namespace Fonoteca.Providers.Tests;

/// <summary>
/// The Qobuz adapter, built through its real DI registration and pointed at a
/// stub socket.
/// </summary>
/// <remarks>
/// The suite is weighted towards the request signature and the account headers
/// on purpose. Everything else here fails loudly — a bad URL is a 404, a changed
/// response shape is a null. Those two fail as <c>400 invalid request
/// signature</c>, which names none of the four things that could have caused it,
/// and which nobody sees until a download stops working.
/// </remarks>
public sealed class QobuzClientTests : IDisposable
{
    private const string AppId = "test-app-id";
    private const string AppSecret = "test-app-secret";
    private const string UserToken = "test-user-token";

    private static readonly TimeSpan Gate = TimeSpan.FromMilliseconds(20);

    private readonly List<ServiceProvider> _providers = [];

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public void TheSignatureIsTheEndpointThenSortedParametersThenTimestampThenSecret()
    {
        // Spelled out by hand rather than computed by the same code under test:
        // the whole value of this test is that it is a second opinion about the
        // recipe. Slashes out of the endpoint, parameters in ordinal name order
        // as name-then-value with no separators, then ts, then the secret.
        var expected = Md5("trackgetFileUrlformat_id27intentstreamtrack_id64868187" + "1700000000" + AppSecret);

        var actual = QobuzClient.Signature(
            "track/getFileUrl",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                // Deliberately not in sorted order here — sorting is the
                // client's job, and a dictionary's enumeration order is not it.
                ["track_id"] = "64868187",
                ["format_id"] = "27",
                ["intent"] = "stream",
            },
            "1700000000",
            AppSecret);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public async Task ATrackUrlRequestIsSignedAndCarriesTheAccountHeaders()
    {
        var (qobuz, stub) = Build(_ => Ok(
            """{"url":"https://cdn.qobuz.example/a.flac","format_id":27,"mime_type":"audio/flac"}"""));

        await qobuz.GetTrackFileUrlAsync(64868187, Token);

        var request = Assert.Single(stub.Requests);
        var query = Query(request.Uri);

        Assert.Equal("64868187", query["track_id"]);
        Assert.Equal("27", query["format_id"]);
        Assert.Equal("stream", query["intent"]);

        // request_ts is in the signature, so it has to be the one that was sent
        // — signing one timestamp and sending another is a 400 every time.
        var expected = QobuzClient.Signature(
            "track/getFileUrl",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["format_id"] = query["format_id"],
                ["intent"] = query["intent"],
                ["track_id"] = query["track_id"],
            },
            query["request_ts"],
            AppSecret);

        Assert.Equal(expected, query["request_sig"]);

        // The two headers Qobuz answers 400 without. On the client rather than
        // per request, so no call path can forget them.
        Assert.Equal(AppId, Assert.Single(request.Headers("X-App-Id")));
        Assert.Equal(UserToken, Assert.Single(request.Headers("X-User-Auth-Token")));
    }

    [Fact]
    public async Task AResponseWithNoUrlComesBackAsARefusalRatherThanAUrl()
    {
        // The trap: Qobuz answer a restricted request with HTTP 200 and a body
        // carrying no url. Trusting the status code downloads nothing and
        // reports success.
        var (qobuz, _) = Build(_ => Ok("""{"restrictions":[{"code":"TrackRestrictedByRights"}]}"""));

        var audio = await qobuz.GetTrackFileUrlAsync(1, Token);

        // Returned, not thrown. "The subscription does not cover this track" is
        // the ordinary outcome for a compilation — the same category as a
        // fingerprint AcoustID has never heard, which ProviderException's own
        // remarks refuse to make an exception. Thrown, it is indistinguishable
        // at the catch from a wrong app secret.
        Assert.Null(audio.Url);
        Assert.Contains("TrackRestrictedByRights", audio.Refusal, StringComparison.Ordinal);
    }

    [Fact]
    public async Task APreviewComesBackAsARefusalRatherThanAsTheTrack()
    {
        var (qobuz, _) = Build(_ => Ok(
            """{"url":"https://cdn.qobuz.example/sample.mp3","format_id":5,"sample":true}"""));

        var audio = await qobuz.GetTrackFileUrlAsync(1, Token);

        Assert.Null(audio.Url);
        Assert.Contains("preview", audio.Refusal, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ABadSignatureStillThrows()
    {
        // The other side of the split, and the one the Ring cycle paid for: a
        // refusal that is NOT about the track must stay an exception, so a
        // caller can abandon an album instead of asking 177 more times.
        var (qobuz, _) = Build(_ => StubHttpHandler.Json(
            HttpStatusCode.BadRequest,
            """{"status":"error","message":"Invalid Request Signature parameter"}"""));

        var refused = await Assert.ThrowsAsync<ProviderRejectedException>(
            () => qobuz.GetTrackFileUrlAsync(1, Token));

        // It must name the secret. The message used to list only an expired
        // token and a rotated app id — neither of which was the cause.
        Assert.Contains("app SECRET", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NoAppSecretRefusesLocallyAndNamesTheSetting()
    {
        var (qobuz, stub) = Build(_ => Ok("{}"), options => options.AppSecret = string.Empty);

        var refused = await Assert.ThrowsAsync<ProviderRejectedException>(
            () => qobuz.GetTrackFileUrlAsync(1, Token));

        Assert.Contains("AppSecret", refused.Message, StringComparison.Ordinal);

        // Refused here, not there: their answer names neither setting, and the
        // request would spend a turn at the gate to say so.
        Assert.Empty(stub.Requests);
    }

    [Fact]
    public async Task SearchAndAlbumLookupsStillWorkWithNoAppSecret()
    {
        // The secret signs one call. Losing it must not take the whole provider
        // down, or a person cannot see what they are unable to download.
        var (qobuz, _) = Build(
            _ => Ok("""{"albums":{"items":[{"id":"0060254795287","title":"Rumours"}]}}"""),
            options => options.AppSecret = string.Empty);

        var albums = await qobuz.SearchAlbumsAsync("rumours", cancellationToken: Token);

        Assert.Equal("0060254795287", Assert.Single(albums).Id);
    }

    [Fact]
    public async Task AnEditionSuffixIsFoldedIntoTheTitle()
    {
        // Qobuz keep "Remastered" in a separate `version` field; dropping it
        // makes four editions of one album four identical rows on a chooser.
        var (qobuz, _) = Build(_ => Ok(
            """
            {"id":"1","title":"Off the Wall","version":"Remastered 2015",
             "artist":{"name":"Michael Jackson"},"tracks_count":10,"media_count":1,
             "tracks":{"items":[{"id":5,"title":"Rock with You","track_number":2,"media_number":1,
                                 "duration":220,"streamable":true}]}}
            """));

        var album = await qobuz.GetAlbumAsync("1", Token);

        Assert.NotNull(album);
        Assert.Equal("Off the Wall (Remastered 2015)", album.Title);
        Assert.Equal("Michael Jackson", album.Artist);
        Assert.Equal("Rock with You", Assert.Single(album.Tracks).Title);
    }

    [Fact]
    public async Task ATruncatedTrackListIsVisibleRatherThanSilent()
    {
        // Their track list paginates. A box set that came back short must say
        // so, or it reads as a complete album that happens to be small.
        var (qobuz, _) = Build(_ => Ok(
            """
            {"id":"1","title":"Box","tracks_count":112,
             "tracks":{"items":[{"id":5,"title":"One","track_number":1}]}}
            """));

        var album = await qobuz.GetAlbumAsync("1", Token);

        Assert.NotNull(album);
        Assert.Equal(112, album.TrackCount);
        Assert.Single(album.Tracks);
    }

    [Fact]
    public async Task AnExpiredTokenIsRejectedRatherThanRetriedForever()
    {
        var (qobuz, _) = Build(_ => StubHttpHandler.Json(
            HttpStatusCode.Unauthorized, """{"status":"error","message":"Invalid user auth token"}"""));

        var refused = await Assert.ThrowsAsync<ProviderRejectedException>(
            () => qobuz.SearchAlbumsAsync("anything", cancellationToken: Token));

        Assert.Contains("Invalid user auth token", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AServerFaultIsUnavailableSoTheCallerMayComeBack()
    {
        var (qobuz, _) = Build(_ => StubHttpHandler.Json(
            HttpStatusCode.ServiceUnavailable, "{}"));

        await Assert.ThrowsAsync<ProviderUnavailableException>(
            () => qobuz.SearchAlbumsAsync("anything", cancellationToken: Token));
    }

    [Fact]
    public async Task AudioIsFetchedWithoutTheAccountHeaders()
    {
        // The URL is already signed and time-limited. Sending a subscription
        // token to a CDN host Qobuz picked at request time is not a default.
        var (qobuz, stub) = Build(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent([1, 2, 3]),
        });

        using var response = await qobuz.OpenAudioAsync(
            new Uri("https://cdn.qobuz.example/a.flac"), Token);

        var request = Assert.Single(stub.Requests);

        Assert.Empty(request.Headers("X-User-Auth-Token"));
        Assert.Empty(request.Headers("X-App-Id"));
    }

    public void Dispose()
    {
        foreach (var provider in _providers) provider.Dispose();
    }

    private static HttpResponseMessage Ok(string body) =>
        StubHttpHandler.Json(HttpStatusCode.OK, body);

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Security",
        "CA5351:Do not use broken cryptographic algorithms",
        Justification = "Reproducing Qobuz's signature scheme, not protecting anything.")]
    private static string Md5(string material) =>
        Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes(material)));

    private static Dictionary<string, string> Query(Uri uri)
    {
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var split = pair.IndexOf('=', StringComparison.Ordinal);
            if (split < 0) continue;

            fields[Uri.UnescapeDataString(pair[..split])] =
                Uri.UnescapeDataString(pair[(split + 1)..]);
        }

        return fields;
    }

    /// <summary>The real registration, with both sockets replaced and the backoff collapsed.</summary>
    private (QobuzClient Client, StubHttpHandler Stub) Build(
        Func<RecordedRequest, HttpResponseMessage> respond,
        Action<QobuzOptions>? configure = null)
    {
        var stub = new StubHttpHandler(respond);
        var services = new ServiceCollection();

        services.AddLogging();

        services.AddQobuz(options =>
        {
            options.AppId = AppId;
            options.AppSecret = AppSecret;
            options.UserAuthToken = UserToken;
            options.MinimumRequestInterval = Gate;
            configure?.Invoke(options);
        });

        // Both named clients, because the point of two of them is that they are
        // configured differently — and a test that only replaced one would pass
        // while the other went to the real internet.
        services.AddHttpClient(QobuzOptions.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(() => stub);
        services.AddHttpClient(QobuzClient.ContentHttpClientName)
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

        return (provider.GetRequiredService<QobuzClient>(), stub);
    }
}
