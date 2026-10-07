using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Fonoteca.Domain.Abstractions;
using Fonoteca.Fixtures;
using Fonoteca.Providers.AppleMusic;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Polly;

namespace Fonoteca.Providers.Tests;

/// <summary>
/// Album motion artwork from Apple Music's public pages, built through its real
/// DI registration and pointed at a stub socket answering with recorded pages.
/// </summary>
/// <remarks>
/// The fixtures are Laufey's <i>Bewitched</i> as Apple served it on 2026-10-07:
/// the barcode lookup, the title search, the album page and both videos' HLS
/// playlists, verbatim. Only the video bytes are made up.
/// </remarks>
public sealed class AppleMusicMotionsTests : IDisposable
{
    private const string OwnBarcode = "197189040030";

    private static readonly TimeSpan Gate = TimeSpan.FromMilliseconds(1);

    /// <summary>An MP4's opening box, and a byte telling the two videos apart.</summary>
    private static readonly byte[] SquareBytes = [0, 0, 0, 0x18, (byte)'f', (byte)'t', (byte)'y', (byte)'p', 1];

    private static readonly byte[] TallBytes = [0, 0, 0, 0x18, (byte)'f', (byte)'t', (byte)'y', (byte)'p', 2];

    private readonly List<ServiceProvider> _providers = [];

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static AlbumToFind Bewitched(
        string? barcode = OwnBarcode,
        string[]? editions = null,
        string? artist = "Laufey",
        int? year = 2023,
        string title = "Bewitched") =>
        new(title, artist, artist is null ? [] : [artist], year, barcode, editions ?? []);

    /// <summary>
    /// The album's own barcode identifies the record, and both videos are kept.
    /// </summary>
    /// <remarks>
    /// The whole chain, on the fixtures: lookup, page, two ladders, two rungs,
    /// two files. Nothing searched, because a barcode is not a guess.
    /// </remarks>
    [Fact]
    public async Task TheBarcodeFindsTheRecordAndBothVideosAreKept()
    {
        var (motions, stub) = Build(Recorded());

        var found = await motions.FindAsync(Bewitched(), Token);

        Assert.NotNull(found);
        Assert.Equal("1690607869", found.AlbumId);
        Assert.Equal("us", found.Storefront);
        Assert.Equal("barcode", found.MatchedBy);
        Assert.Equal(SquareBytes, found.Square);
        Assert.Equal(TallBytes, found.Tall);

        Assert.DoesNotContain(stub.Requests, request => request.Uri.AbsolutePath == "/search");
    }

    /// <summary>
    /// The H.264 rung is the one kept, never the HEVC rungs above it.
    /// </summary>
    /// <remarks>
    /// The square ladder tops out at 2160 in HEVC, which some browsers and
    /// Android devices cannot decode; its best H.264 rung is 1080. The
    /// trick-play lines beside the rungs are keyframes, not the video.
    /// </remarks>
    [Fact]
    public async Task TheBestH264RungIsTheOneDownloaded()
    {
        var (motions, stub) = Build(Recorded());

        await motions.FindAsync(Bewitched(), Token);

        var files = stub.Requests
            .Where(request => request.Uri.AbsolutePath.EndsWith(".mp4", StringComparison.Ordinal))
            .Select(request => Path.GetFileName(request.Uri.AbsolutePath))
            .ToArray();

        Assert.Equal(
            ["P593753274_Anull_video_gr290_sdr_1080x1080-.mp4", "P593751764_Anull_video_gr290_sdr_1080x1440-.mp4"],
            files);
    }

    /// <summary>
    /// Another edition's barcode counts only under the same title.
    /// </summary>
    /// <remarks>
    /// The lookup answers a list of barcodes in no order and without saying
    /// which found which, and these three find <i>Bewitched</i> and
    /// <i>Bewitched: The Goddess Edition</i>. Only the first is this album's
    /// record; the second has a video of its own.
    /// </remarks>
    [Fact]
    public async Task AnotherEditionsBarcodeCountsOnlyUnderTheSameTitle()
    {
        var (motions, stub) = Build(Recorded(editions: true));

        var found = await motions.FindAsync(
            Bewitched(barcode: null, editions: ["5056167177784", "4547366681376", OwnBarcode]),
            Token);

        Assert.NotNull(found);
        Assert.Equal("1690607869", found.AlbumId);
        Assert.Equal("another edition's barcode", found.MatchedBy);
        Assert.DoesNotContain(stub.Requests, request => request.Uri.AbsolutePath.Contains("1727030380", StringComparison.Ordinal));

        var (other, _) = Build(Recorded(editions: true));

        Assert.Null(await other.FindAsync(
            Bewitched(barcode: null, editions: ["5056167177784"], artist: null, title: "Everything I Know About Love"),
            Token));
    }

    /// <summary>
    /// A title search counts only where a billed name, the title and the year agree.
    /// </summary>
    /// <remarks>
    /// The search answers four records for "Laufey Bewitched", three of them
    /// singles and reworks under longer names. A different year refuses even
    /// the right title, and no credit line means no search at all.
    /// </remarks>
    [Fact]
    public async Task ATitleSearchNeedsTheArtistTheTitleAndTheYear()
    {
        var (motions, _) = Build(Recorded());

        var found = await motions.FindAsync(Bewitched(barcode: null), Token);

        Assert.NotNull(found);
        Assert.Equal("1690607869", found.AlbumId);
        Assert.Equal("title", found.MatchedBy);

        var (wrongYear, _) = Build(Recorded());
        Assert.Null(await wrongYear.FindAsync(Bewitched(barcode: null, year: 2010), Token));

        var (stranger, _) = Build(Recorded());
        Assert.Null(await stranger.FindAsync(Bewitched(barcode: null, artist: "Phoebe Bridgers"), Token));

        var (unbilled, stub) = Build(Recorded());
        Assert.Null(await unbilled.FindAsync(Bewitched(barcode: null, artist: null), Token));
        Assert.Empty(stub.Requests);
    }

    /// <summary>
    /// A record its barcode identified ends the asking, video or not.
    /// </summary>
    /// <remarks>
    /// A title search could only find the same record again, or a wrong one.
    /// </remarks>
    [Fact]
    public async Task ARecordTheBarcodeIdentifiedEndsTheAskingWithoutAVideo()
    {
        var (motions, stub) = Build(Recorded(pageHasVideo: false));

        Assert.Null(await motions.FindAsync(Bewitched(), Token));
        Assert.DoesNotContain(stub.Requests, request => request.Uri.AbsolutePath == "/search");
    }

    /// <summary>
    /// A record the US shop identified is not asked about in the second shop;
    /// an album it could not place is.
    /// </summary>
    [Fact]
    public async Task OnlyAnAlbumTheUsShopCannotPlaceIsAskedInTheSecondShop()
    {
        var (placed, placedStub) = Build(Recorded(pageHasVideo: false), options => options.FallbackStorefront = "nl");

        Assert.Null(await placed.FindAsync(Bewitched(), Token));
        Assert.DoesNotContain(placedStub.Requests, request => request.Uri.Query.Contains("country=nl", StringComparison.Ordinal));

        var (unplaced, unplacedStub) = Build(Recorded(), options => options.FallbackStorefront = "nl");

        Assert.Null(await unplaced.FindAsync(Bewitched(barcode: "724358188004", artist: null), Token));
        Assert.Contains(unplacedStub.Requests, request => request.Uri.Query.Contains("country=nl", StringComparison.Ordinal));
    }

    /// <summary>
    /// "No such record" is an answer; an outage is not, and neither is a page
    /// that has changed shape.
    /// </summary>
    /// <remarks>
    /// The caller writes the first down and asks again in a week. Written down,
    /// either of the others would mark every album in a library as having no
    /// video because of one bad afternoon or one redesign — so both throw.
    /// </remarks>
    [Fact]
    public async Task NoRecordIsAnAnswerAndAnOutageOrANewPageShapeIsNot()
    {
        var (none, _) = Build(Recorded());
        Assert.Null(await none.FindAsync(Bewitched(barcode: "724358188004", artist: null), Token));

        var (down, _) = Build(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        await Assert.ThrowsAsync<ProviderUnavailableException>(() => down.FindAsync(Bewitched(), Token));

        // A throttled answer is a body with no result list, which is not "no such record".
        var (throttled, _) = Build(_ => Text("{}", "application/json"));
        await Assert.ThrowsAsync<ProviderUnavailableException>(() => throttled.FindAsync(Bewitched(), Token));

        var (redesigned, _) = Build(Recorded(page: "<html><body>A new player</body></html>"));
        await Assert.ThrowsAsync<ProviderRejectedException>(() => redesigned.FindAsync(Bewitched(), Token));
    }

    /// <summary>
    /// A video from anywhere but Apple's video host, or that is not an MP4, is not kept.
    /// </summary>
    [Fact]
    public async Task AVideoFromElsewhereOrNotAnMp4IsRefused()
    {
        var elsewhere = Fixture("apple-album-bewitched.html")
            .Replace("https://mvod.itunes.apple.com/", "https://example.test/", StringComparison.Ordinal);

        var (moved, movedStub) = Build(Recorded(page: elsewhere));

        Assert.Null(await moved.FindAsync(Bewitched(), Token));
        Assert.DoesNotContain(movedStub.Requests, request => request.Uri.Host == "example.test");

        var (html, _) = Build(Recorded(videoType: "text/html"));
        Assert.Null(await html.FindAsync(Bewitched(), Token));

        var (mislabelled, _) = Build(Recorded(video: () => Bytes([0, 0, 0, 0x18, (byte)'h', (byte)'t', (byte)'m', (byte)'l', 1], "video/mp4")));
        Assert.Null(await mislabelled.FindAsync(Bewitched(), Token));
    }

    /// <summary>
    /// A video over the cap is not kept, whether its size was declared or only
    /// found out by reading it.
    /// </summary>
    [Fact]
    public async Task AVideoOverTheCapIsRefused()
    {
        var (declared, _) = Build(Recorded(video: () =>
        {
            var response = Bytes(SquareBytes, "video/mp4");
            response.Content.Headers.ContentLength = AppleMusicMotions.MaxVideoBytes + 1;
            return response;
        }));

        Assert.Null(await declared.FindAsync(Bewitched(), Token));

        var (streamed, _) = Build(Recorded(video: () =>
        {
            var content = new StreamContent(new Zeros(AppleMusicMotions.MaxVideoBytes + 1));
            content.Headers.ContentType = new MediaTypeHeaderValue("video/mp4");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        }));

        Assert.Null(await streamed.FindAsync(Bewitched(), Token));
    }

    public void Dispose()
    {
        foreach (var provider in _providers) provider.Dispose();
    }

    /// <summary>Apple, as recorded: the lookup, the search, the page, the playlists, and two made-up files.</summary>
    private static Func<RecordedRequest, HttpResponseMessage> Recorded(
        bool editions = false,
        bool pageHasVideo = true,
        string? page = null,
        string videoType = "video/mp4",
        Func<HttpResponseMessage>? video = null) =>
        request =>
        {
            var uri = request.Uri;
            var path = uri.AbsolutePath;

            if (uri.Host == "itunes.apple.com" && path == "/lookup")
            {
                return Text(
                    editions ? Fixture("apple-lookup-bewitched-editions.json")
                    : uri.Query.Contains($"upc={OwnBarcode}&", StringComparison.Ordinal) ? Fixture("apple-lookup-bewitched.json")
                    : Fixture("apple-lookup-none.json"),
                    "application/json");
            }

            if (uri.Host == "itunes.apple.com" && path == "/search")
            {
                return Text(Fixture("apple-search-bewitched.json"), "application/json");
            }

            if (uri.Host == "music.apple.com" && path == "/us/album/1690607869")
            {
                var html = page ?? Fixture("apple-album-bewitched.html");

                return Text(
                    pageHasVideo ? html : html.Replace("\"videoArtwork\"", "\"x\"", StringComparison.Ordinal)
                        .Replace("\"tallVideoArtwork\"", "\"y\"", StringComparison.Ordinal),
                    "text/html");
            }

            if (uri.Host == "mvod.itunes.apple.com")
            {
                var file = Path.GetFileName(path);

                return file switch
                {
                    "P593753274_default.m3u8" => Text(Fixture("apple-motion-square.m3u8"), "application/vnd.apple.mpegurl"),
                    "P593751764_default.m3u8" => Text(Fixture("apple-motion-tall.m3u8"), "application/vnd.apple.mpegurl"),
                    "P593753274_Anull_video_gr290_sdr_1080x1080.m3u8" =>
                        Text(Fixture("apple-motion-square-1080x1080.m3u8"), "application/vnd.apple.mpegurl"),
                    "P593751764_Anull_video_gr290_sdr_1080x1440.m3u8" =>
                        Text(Fixture("apple-motion-tall-1080x1440.m3u8"), "application/vnd.apple.mpegurl"),
                    "P593753274_Anull_video_gr290_sdr_1080x1080-.mp4" => video?.Invoke() ?? Bytes(SquareBytes, videoType),
                    "P593751764_Anull_video_gr290_sdr_1080x1440-.mp4" => video?.Invoke() ?? Bytes(TallBytes, videoType),
                    _ => new HttpResponseMessage(HttpStatusCode.NotFound),
                };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        };

    private static HttpResponseMessage Text(string body, string mediaType) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, mediaType) };

    private static HttpResponseMessage Bytes(byte[] body, string mediaType)
    {
        var content = new ByteArrayContent(body);
        content.Headers.ContentType = new MediaTypeHeaderValue(mediaType);

        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }

    private static string Fixture(string name)
    {
        var assembly = typeof(AppleMusicMotionsTests).Assembly;
        var resource = $"{assembly.GetName().Name}.Responses.{name}";

        using var stream = assembly.GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException($"Missing embedded fixture '{resource}'.");

        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>A body of zeros of a given length, never all in memory, and of no declared length.</summary>
    private sealed class Zeros(long length) : Stream
    {
        private long _left = length;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = (int)Math.Min(count, _left);
            Array.Clear(buffer, offset, read);
            _left -= read;
            return read;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private (IAlbumMotions Motions, StubHttpHandler Stub) Build(
        Func<RecordedRequest, HttpResponseMessage> respond,
        Action<AppleMusicOptions>? configure = null)
    {
        var stub = new StubHttpHandler(respond);
        var services = new ServiceCollection();

        services.AddLogging();
        services.AddAppleMusic(options =>
        {
            options.MinimumRequestInterval = Gate;
            options.PageRequestInterval = Gate;
            configure?.Invoke(options);
        });

        foreach (var name in new[]
        {
            AppleMusicOptions.HttpClientName,
            AppleMusicOptions.PageHttpClientName,
            AppleMusicOptions.VideoHttpClientName,
        })
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

        return (provider.GetRequiredService<IAlbumMotions>(), stub);
    }
}
