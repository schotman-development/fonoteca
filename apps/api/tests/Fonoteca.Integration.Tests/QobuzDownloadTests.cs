using Fonoteca.Fixtures;
using Fonoteca.Api.Acquisition;
using Fonoteca.Api.Configuration;
using Fonoteca.Domain.Abstractions;
using Fonoteca.Providers;
using Fonoteca.Providers.Qobuz;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Polly;

namespace Fonoteca.Integration.Tests;

/// <summary>
/// The download path against a stub socket and a real temporary directory.
/// </summary>
/// <remarks>
/// Real files, because what is under test is the promise the final filename
/// makes — that it means "complete". No database and no container: nothing here
/// touches the catalogue, which is itself one of the things asserted.
/// </remarks>
public sealed class QobuzDownloadTests : IDisposable
{
    private const string Album = """
        {"id":"1","title":"Rumours","artist":{"name":"Fleetwood Mac"},
         "tracks_count":1,"media_count":1,
         "tracks":{"items":[{"id":55,"title":"The Chain","track_number":8,
                             "media_number":1,"streamable":true}]}}
        """;

    private const string FileUrl =
        """{"url":"https://cdn.qobuz.example/a.flac","format_id":27,"mime_type":"audio/flac"}""";

    private readonly List<ServiceProvider> _providers = [];
    private readonly string _library =
        Directory.CreateTempSubdirectory("fonoteca-library-").FullName;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AnAlbumLandsUnderArtistThenAlbumThenNumberedTitle()
    {
        var (downloads, _) = Build(Answer([1, 2, 3, 4]));

        var result = await downloads.DownloadAlbumAsync("1", Token);

        Assert.Equal(1, result.Downloaded);
        Assert.Equal(
            "Fleetwood Mac/Rumours/08 The Chain.flac",
            Assert.Single(result.Tracks).Path);

        // The two-deep shape the matching screen cuts album folders at.
        Assert.True(File.Exists(Path.Combine(
            _library, "Fleetwood Mac", "Rumours", "08 The Chain.flac")));

        // Nothing may be left half-written under a name that means complete.
        Assert.Empty(Directory.GetFiles(_library, "*.part", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task ABodyShorterThanTheServerPromisedIsRefusedAndNothingIsKept()
    {
        // The failure this library has already paid for once: a FLAC truncated
        // to a fraction of its bytes reads back at its original duration and
        // 3 kbps, typed lossless. A short file under the final name is worse
        // than no file, because nothing downstream can tell.
        var (downloads, _) = Build(request => request.Uri.Host.StartsWith("cdn", StringComparison.Ordinal)
            ? Truncated([1, 2, 3, 4], claimed: 9_000_000)
            : StubHttpHandler.Json(System.Net.HttpStatusCode.OK, Json(request)));

        var result = await downloads.DownloadAlbumAsync("1", Token);

        Assert.Equal(0, result.Downloaded);
        Assert.Equal(TrackOutcome.Failed, Assert.Single(result.Tracks).Outcome);

        Assert.Empty(Directory.GetFiles(_library, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task ATrackAlreadyInStagingCostsNoRequestAtAll()
    {
        // The resume story, and the assertion is the request COUNT. Checking
        // after asking for the signed URL would still skip the file — and would
        // still spend a turn at the rate limit per already-downloaded track,
        // which on a re-run of a large album is the whole cost.
        var (downloads, stub) = Build(Answer([1, 2, 3, 4]));

        await downloads.DownloadAlbumAsync("1", Token);
        var afterFirst = stub.Requests.Count;

        await downloads.DownloadAlbumAsync("1", Token);

        // One album lookup for the second run, and nothing else: no signed URL
        // request, no CDN fetch.
        Assert.Equal(afterFirst + 1, stub.Requests.Count);
    }

    [Fact]
    public async Task ASecondDownloadIsRefusedRatherThanRacingTheFirst()
    {
        var gate = new TaskCompletionSource();

        var (downloads, _) = Build(async (request, _) =>
        {
            if (request.Uri.Host.StartsWith("cdn", StringComparison.Ordinal))
            {
                await gate.Task;
                return Answer([1, 2, 3, 4])(request);
            }

            return StubHttpHandler.Json(System.Net.HttpStatusCode.OK, Json(request));
        });

        var first = downloads.DownloadAlbumAsync("1", Token);

        while (!downloads.IsBusy) await Task.Yield();

        // Two runs write the same paths; the loser would corrupt the winner's
        // files rather than failing.
        await Assert.ThrowsAsync<DownloadInProgressException>(
            () => downloads.DownloadAlbumAsync("1", Token));

        gate.SetResult();
        await first;
    }

    [Fact]
    public async Task AnAlbumFolderHoldingADifferentEditionIsRefusedRatherThanMerged()
    {
        // The one thing the removed staging-then-import step was genuinely
        // buying. Artist/Album is not unique across editions, so downloading
        // over an album already held would otherwise produce one directory
        // containing two rips — which every later pass reads as a single very
        // strange album, with nothing able to tell them apart afterwards.
        var folder = Path.Combine(_library, "Fleetwood Mac", "Rumours");
        Directory.CreateDirectory(folder);
        await File.WriteAllTextAsync(
            Path.Combine(folder, "04 Songbird.flac"), "other edition", Token);

        var (downloads, stub) = Build(Answer([1, 2, 3, 4]));

        var refused = await Assert.ThrowsAsync<ProviderRejectedException>(
            () => downloads.DownloadAlbumAsync("1", Token));

        Assert.Contains("Songbird", refused.Message, StringComparison.Ordinal);

        // Refused before a single track was asked for: one album lookup, no more.
        Assert.Single(stub.Requests);
    }

    [Fact]
    public async Task ReDownloadingTheSameAlbumIsNotACollision()
    {
        // The collision check must not break resume. A folder holding only
        // files this album would produce is an interrupted download, not a
        // second edition, and every one of them is skipped for a stat.
        var (downloads, _) = Build(Answer([1, 2, 3, 4]));
        await downloads.DownloadAlbumAsync("1", Token);

        var again = await downloads.DownloadAlbumAsync("1", Token);

        Assert.Equal(TrackOutcome.Skipped, Assert.Single(again.Tracks).Outcome);
    }

    [Fact]
    public async Task ARefusalThatIsNotAboutOneTrackAbandonsTheAlbumOnTheFirstOne()
    {
        // Measured on a real 178-track box set: a wrong app secret produced 178
        // identical "Invalid Request Signature" failures, one per second, over
        // three minutes — then HTTP 200 and a page reporting 178 individually
        // unavailable tracks. The cause was one setting, invisible in the noise.
        //
        // Retrying a ProviderRejectedException is documented as pure waste. The
        // assertion is the request COUNT: it must give up on the first.
        var (downloads, stub) = Build(request =>
            request.Uri.AbsolutePath.Contains("getFileUrl", StringComparison.Ordinal)
                ? StubHttpHandler.Json(
                    System.Net.HttpStatusCode.BadRequest,
                    """{"status":"error","message":"Invalid Request Signature parameter"}""")
                : StubHttpHandler.Json(System.Net.HttpStatusCode.OK, Album));

        var refused = await Assert.ThrowsAsync<ProviderRejectedException>(
            () => downloads.DownloadAlbumAsync("1", Token));

        // The message has to send somebody to the setting, not to the track.
        Assert.Contains("Providers:Qobuz", refused.Message, StringComparison.Ordinal);

        // One album lookup and exactly one signed-URL attempt. Not one per track.
        Assert.Equal(2, stub.Requests.Count);
        Assert.Empty(Directory.GetFiles(_library, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task ATrackTheSubscriptionDoesNotCoverIsSkippedAndTheAlbumCarriesOn()
    {
        // The other half of the same split, and the reason it cannot be an
        // exception: this is the ordinary outcome for a compilation, exactly as
        // "AcoustID has never heard this" is ordinary for a bootleg.
        var (downloads, _) = Build(request =>
            request.Uri.AbsolutePath.Contains("getFileUrl", StringComparison.Ordinal)
                ? StubHttpHandler.Json(
                    System.Net.HttpStatusCode.OK,
                    """{"restrictions":[{"code":"TrackRestrictedByRights"}]}""")
                : StubHttpHandler.Json(System.Net.HttpStatusCode.OK, Album));

        var result = await downloads.DownloadAlbumAsync("1", Token);

        var track = Assert.Single(result.Tracks);
        Assert.Equal(TrackOutcome.Skipped, track.Outcome);
        Assert.Contains("TrackRestrictedByRights", track.Detail, StringComparison.Ordinal);
        Assert.Equal(0, result.Downloaded);
    }

    public void Dispose()
    {
        foreach (var provider in _providers) provider.Dispose();

        if (Directory.Exists(_library)) Directory.Delete(_library, recursive: true);
    }

    private static string Json(RecordedRequest request) =>
        request.Uri.AbsolutePath.Contains("getFileUrl", StringComparison.Ordinal) ? FileUrl : Album;

    private static Func<RecordedRequest, HttpResponseMessage> Answer(byte[] audio) =>
        request => request.Uri.Host.StartsWith("cdn", StringComparison.Ordinal)
            ? new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(audio),
            }
            : StubHttpHandler.Json(System.Net.HttpStatusCode.OK, Json(request));

    /// <summary>A response that states a length and then sends less than it.</summary>
    private static HttpResponseMessage Truncated(byte[] audio, long claimed)
    {
        var content = new ByteArrayContent(audio);
        content.Headers.ContentLength = claimed;

        return new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = content };
    }

    private (QobuzDownloadService Downloads, StubHttpHandler Stub) Build(
        Func<RecordedRequest, HttpResponseMessage> respond) =>
        Build((request, _) => Task.FromResult(respond(request)));

    private (QobuzDownloadService Downloads, StubHttpHandler Stub) Build(
        Func<RecordedRequest, CancellationToken, Task<HttpResponseMessage>> respond)
    {
        var stub = new StubHttpHandler(respond);
        var services = new ServiceCollection();

        services.AddLogging();

        services.AddQobuz(options =>
        {
            options.AppId = "app";
            options.AppSecret = "secret";
            options.UserAuthToken = "token";
            options.MinimumRequestInterval = TimeSpan.Zero;
        });

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

        var options = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Fonoteca:LibraryPath"] = _library,
                ["Fonoteca:Download:TrackDelayMs"] = "0",
            })
            .Build()
            .GetSection(FonotecaOptions.SectionName)
            .Get<FonotecaOptions>()!;

        var downloads = new QobuzDownloadService(
            provider.GetRequiredService<QobuzClient>(),
            Options.Create(options),
            new SystemClock(),
            NullLogger<QobuzDownloadService>.Instance);

        return (downloads, stub);
    }
}
