using System.Net;
using System.Text;
using Fonoteca.Fixtures;
using Fonoteca.Providers.Qobuz;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Polly;

namespace Fonoteca.Providers.Tests;

/// <summary>
/// The shop as a second source of album covers, built through its real DI
/// registration and pointed at a stub socket.
/// </summary>
/// <remarks>
/// <b>Everything here is about not putting the wrong sleeve on a record.</b>
/// The Cover Art Archive is keyed on the release's own MusicBrainz id and can
/// only be wrong about the pressing; this is reached by a search, so the
/// failure it has to be held away from is somebody else's album drawn under
/// your title. Measured on the live library, the shelf this fills is 105 albums
/// of 612 — enough that a rule which matched loosely would put a visible error
/// on a sixth of the screen.
/// </remarks>
public sealed class QobuzCoversTests : IDisposable
{
    /// <summary>Short enough to keep the suite quick, long enough to be measurable.</summary>
    private static readonly TimeSpan Gate = TimeSpan.FromMilliseconds(20);

    private readonly List<ServiceProvider> _providers = [];

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>
    /// A barcode that agrees is the answer, wherever it sits in the results.
    /// </summary>
    /// <remarks>
    /// The first row here matches on title and artist and is a different
    /// pressing — which is the ordinary shape of a shop's search results, since
    /// a remaster and the original are two rows carrying one name. Taking the
    /// first title match would draw the remaster's sleeve on the original, and
    /// nothing on the page would say so.
    /// </remarks>
    [Fact]
    public async Task ABarcodeIsPreferredToATitleThatAlsoMatches()
    {
        var (covers, _) = Build(Albums(
            ("Peach", "Larkin Poe", "2017-06-09", "0700261464312", "remaster.jpg"),
            ("Peach", "Larkin Poe", "2017-06-09", "0888295563550", "the-pressing.jpg")));

        var found = await covers.FindAsync("Peach", "Larkin Poe", 2017, "888295563550", Token);

        Assert.Equal("the-pressing.jpg", Assert.IsType<QobuzCover>(found).AlbumId);
    }

    /// <summary>
    /// Two catalogues writing one barcode differently still agree.
    /// </summary>
    /// <remarks>
    /// <b>The same digits behind a leading zero are the same record</b>, and the
    /// two catalogues genuinely disagree about it: MusicBrainz holds plenty of
    /// 12-digit UPCs for records the shop sells under the 13-digit EAN of the
    /// same number. Compared literally, the key that makes this source safe
    /// fails on exactly the releases it was reached for — and the fallback
    /// would then answer from a title, which is the weaker claim this test
    /// exists to keep it off.
    /// </remarks>
    [Theory]
    [InlineData("0888295563550", "888295563550")]
    [InlineData("888295563550", "0888295563550")]
    [InlineData("8 88295 56355 0", "888295563550")]
    public async Task ABarcodeMatchesAcrossTheWayTheTwoCataloguesWriteIt(string shop, string held)
    {
        var (covers, _) = Build(Albums(
            ("Something Else Entirely", "Another Band", "1999-01-01", shop, "same-record.jpg")));

        var found = await covers.FindAsync("Peach", "Larkin Poe", 2017, held, Token);

        Assert.Equal("same-record.jpg", Assert.IsType<QobuzCover>(found).AlbumId);
    }

    /// <summary>
    /// Without a barcode, a title and an artist must both fold onto ours.
    /// </summary>
    /// <remarks>
    /// The spellings here are the ones two catalogues legitimately differ over —
    /// a full stop, an apostrophe, a capital in the middle of a word — and
    /// refusing them would cost a cover for nothing. This is
    /// <c>ReleaseTitleMatch</c>'s own table, reached through this caller so that
    /// a source which stopped using it could not pass.
    /// </remarks>
    [Theory]
    [InlineData("Sgt. Pepper's Lonely Hearts Club Band", "Sgt Peppers Lonely Hearts Club Band")]
    [InlineData("Blues DeLuxe", "Blues Deluxe")]
    [InlineData("Venom & Faith", "Venom  &  Faith")]
    public async Task ATitleSpelledDifferentlyIsStillTheSameRecord(string shop, string held)
    {
        var (covers, _) = Build(Albums(("The Band", shop, null, "sleeve.jpg")));

        Assert.NotNull(await covers.FindAsync(held, "The Band", null, null, Token));
    }

    /// <summary>
    /// An ampersand written out as a word is not folded, and the cover is lost.
    /// </summary>
    /// <remarks>
    /// <b>A stated limitation, pinned so it is a decision rather than a
    /// surprise.</b> The normaliser drops punctuation rather than translating
    /// it, so <i>Venom &amp; Faith</i> folds to <c>venomfaith</c> and
    /// <i>Venom and Faith</i> to <c>venomandfaith</c>. Widening it is not this
    /// source's call to make: the same table decides which artist gets which
    /// photograph, where a loosened match is the invisible error
    /// <c>ArtistNameMatch</c> is written to refuse. The cost here is an album
    /// keeping its monogram, and the cover dialog takes an upload.
    /// </remarks>
    [Fact]
    public async Task AnAmpersandSpelledOutIsNotMatched()
    {
        var (covers, _) = Build(Albums(("Larkin Poe", "Venom and Faith", null, "sleeve.jpg")));

        Assert.Null(await covers.FindAsync("Venom & Faith", "Larkin Poe", null, null, Token));
    }

    /// <summary>
    /// A title that is merely similar is refused, and the album keeps no cover.
    /// </summary>
    /// <remarks>
    /// Containment is the tempting rule and it is what would fold
    /// <i>Greatest Hits</i> into <i>Greatest Hits, Volume 2</i>. A year that
    /// contradicts is the other refusal: one title, two records, and the live
    /// one is not the studio one.
    /// </remarks>
    [Theory]
    [InlineData("Greatest Hits, Volume 2", "Greatest Hits", 1990, 1990)]
    [InlineData("Live", "Live at Leeds", 1970, 1970)]
    [InlineData("Unplugged", "Unplugged", 1992, 2013)]
    public async Task ARecordThatIsOnlyNearlyTheSameIsRefused(
        string shop,
        string held,
        int shopYear,
        int heldYear)
    {
        var (covers, _) = Build(Albums(
            ("The Band", shop, $"{shopYear}-05-01", "stranger.jpg")));

        Assert.Null(await covers.FindAsync(held, "The Band", heldYear, null, Token));
    }

    /// <summary>
    /// The same title by somebody else is the commonest wrong answer there is.
    /// </summary>
    /// <remarks>
    /// Album titles collide across artists constantly, and a search for one
    /// returns the others. The artist is therefore checked before the title and
    /// not as a tie-break: without it, every <i>Greatest Hits</i> in the library
    /// would wear the sleeve of whichever one the shop ranked first.
    /// </remarks>
    [Fact]
    public async Task TheSameTitleByADifferentArtistIsRefused()
    {
        var (covers, _) = Build(Albums(("Some Other Band", "Peach", "2017-01-01", "theirs.jpg")));

        Assert.Null(await covers.FindAsync("Peach", "Larkin Poe", 2017, null, Token));
    }

    /// <summary>
    /// A release nothing is credited on cannot be matched by title alone.
    /// </summary>
    /// <remarks>
    /// The one shape where the safe direction costs a cover somebody might have
    /// wanted, and it is still the right way round: a bare title search matches
    /// a compilation carrying the song as readily as the album, and there is
    /// nothing here able to tell them apart. A barcode still works, which is
    /// what the second case says.
    /// </remarks>
    [Fact]
    public async Task AnUncreditedReleaseIsMatchedByBarcodeOrNotAtAll()
    {
        var (covers, _) = Build(Albums(
            ("Peach", "Larkin Poe", "2017-06-09", "0888295563550", "found.jpg")));

        Assert.Null(await covers.FindAsync("Peach", artist: null, 2017, barcode: null, Token));

        var (again, _) = Build(Albums(
            ("Peach", "Larkin Poe", "2017-06-09", "0888295563550", "found.jpg")));

        Assert.NotNull(await again.FindAsync("Peach", artist: null, 2017, "888295563550", Token));
    }

    /// <summary>
    /// A picture from anywhere but their CDN is not fetched at all.
    /// </summary>
    /// <remarks>
    /// The portrait source's allowlist, and it matters more here: a portrait URL
    /// is handed to a browser, while these bytes are downloaded by this process
    /// and stored in the catalogue under this application's own origin. Asserted
    /// on the requests rather than on the answer, because "refused" has to mean
    /// the socket was never opened.
    /// </remarks>
    [Theory]
    [InlineData("https://example.test/tracker.png")]
    [InlineData("http://static.qobuz.com/covers/cleartext.jpg")]
    [InlineData("javascript:alert(1)")]
    public async Task APictureFromAnywhereElseIsNeverFetched(string url)
    {
        var (covers, stub) = Build(Albums(("The Band", "An Album", null, url)));

        Assert.Null(await covers.FindAsync("An Album", "The Band", null, null, Token));
        Assert.Single(stub.Requests);
    }

    /// <summary>
    /// Bytes that are not a picture are refused after they arrive.
    /// </summary>
    /// <remarks>
    /// The host allowlist cannot see this: a redirect inside their own CDN, or a
    /// wrong path answering with an error page, is a document served from the
    /// right address. <c>FilePreview</c>'s raster allowlist is the same one an
    /// upload and the archive's own download are held to, and the reason is the
    /// same — this is served back to a browser.
    /// </remarks>
    [Fact]
    public async Task BytesThatAreNotAPictureAreRefused()
    {
        var (covers, _) = Build(
            Albums(("The Band", "An Album", null, "sleeve.jpg")),
            image: new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("<html>not here</html>", Encoding.UTF8, "text/html"),
            });

        Assert.Null(await covers.FindAsync("An Album", "The Band", null, null, Token));
    }

    /// <summary>
    /// An installation with no Qobuz credentials asks nothing.
    /// </summary>
    /// <remarks>
    /// This source is reached by a page drawing a tile rather than by a person
    /// choosing an album, so an unconfigured installation would otherwise send a
    /// request per coverless album on every browse — and be refused each time.
    /// </remarks>
    [Fact]
    public async Task AnUnconfiguredInstallationAsksNothing()
    {
        var (covers, stub) = Build(
            Albums(("The Band", "An Album", null, "sleeve.jpg")),
            configure: options => options.UserAuthToken = string.Empty);

        Assert.Null(await covers.FindAsync("An Album", "The Band", null, null, Token));
        Assert.Empty(stub.Requests);
    }

    /// <summary>One album is one search, which is what this source is rationed by.</summary>
    [Fact]
    public async Task OneAlbumCostsOneSearch()
    {
        var (covers, stub) = Build(Albums(("The Band", "An Album", null, "sleeve.jpg")));

        await covers.FindAsync("An Album", "The Band", null, null, Token);

        Assert.Equal(2, stub.Requests.Count);
        Assert.Contains("album/search", stub.Requests[0].Uri.ToString(), StringComparison.Ordinal);
        Assert.Contains("static.qobuz.com", stub.Requests[1].Uri.Host, StringComparison.Ordinal);
    }

    public void Dispose()
    {
        foreach (var provider in _providers) provider.Dispose();
    }

    /// <summary>An <c>album/search</c> answer with no barcodes, in the service's shape.</summary>
    /// <remarks>
    /// The id is the image file name, so an assertion on which album was chosen
    /// reads as the picture it supplied — these tests are all about which of
    /// several near-identical rows won.
    /// </remarks>
    private static string Albums(params (string Artist, string Title, string? Date, string Image)[] items) =>
        Albums(items
            .Select(item => (item.Title, item.Artist, item.Date, (string?)null, item.Image))
            .ToArray());

    /// <summary>The same answer, with the barcodes that decide it.</summary>
    private static string Albums(
        params (string Title, string Artist, string? Date, string? Upc, string Image)[] items)
    {
        var rows = string.Join(
            ",",
            items.Select(item =>
                $$"""
                  { "id": {{Json(item.Image)}}, "title": {{Json(item.Title)}},
                    "artist": { "name": {{Json(item.Artist)}} },
                    "release_date_original": {{Json(item.Date)}},
                    "upc": {{Json(item.Upc)}},
                    "image": { "large": {{Json(Picture(item.Image))}} } }
                  """));

        return $$"""{ "albums": { "items": [ {{rows}} ] } }""";
    }

    /// <summary>A URL on their CDN, unless the test already supplied a whole one.</summary>
    private static string Picture(string image) =>
        image.Contains("://", StringComparison.Ordinal) || image.StartsWith("javascript:", StringComparison.Ordinal)
            ? image
            : $"https://static.qobuz.com/images/covers/{image}";

    private static string Json(string? value) =>
        value is null ? "null" : System.Text.Json.JsonSerializer.Serialize(value);

    /// <summary>
    /// The source over a stub socket answering both of its clients.
    /// </summary>
    /// <remarks>
    /// Two named clients are stubbed, not one: the search goes through the gated,
    /// authenticated API client and the picture through the unauthenticated
    /// content client, and a test that stubbed only the first would reach the
    /// real CDN.
    /// </remarks>
    private (QobuzCovers Covers, StubHttpHandler Stub) Build(
        string search,
        HttpResponseMessage? image = null,
        Action<QobuzOptions>? configure = null)
    {
        var stub = new StubHttpHandler(request =>
            request.Uri.Host.Contains("qobuz.com", StringComparison.Ordinal)
            && !request.Uri.AbsolutePath.Contains("/api.json/", StringComparison.Ordinal)
                ? image ?? Jpeg()
                : Ok(search));

        var services = new ServiceCollection();

        services.AddLogging();
        services.AddQobuz(options =>
        {
            options.AppId = "test-app";
            options.UserAuthToken = "test-token";
            options.MinimumRequestInterval = Gate;
            configure?.Invoke(options);
        });

        foreach (var client in new[] { QobuzOptions.HttpClientName, QobuzClient.ContentHttpClientName })
        {
            services.AddHttpClient(client).ConfigurePrimaryHttpMessageHandler(() => stub);
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

        return (provider.GetRequiredService<QobuzCovers>(), stub);
    }

    private static HttpResponseMessage Jpeg() =>
        new(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent([0xFF, 0xD8, 0xFF, 0xE0])
            {
                Headers = { ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/jpeg") },
            },
        };

    private static HttpResponseMessage Ok(string body) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
}
