using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Fonoteca.Domain.Abstractions;
using Fonoteca.Fixtures;
using Fonoteca.Providers.AppleMusic;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Polly;

namespace Fonoteca.Providers.Tests;

/// <summary>
/// Apple Music Classical's album and composer pages, read through the real DI
/// registration from a stub socket answering with recorded pages.
/// </summary>
/// <remarks>
/// The fixtures are as Apple served them on 2026-10-31, verbatim: Karajan's
/// Beethoven Fifth and Sixth (two pieces, an orchestra and its conductor),
/// Amy Dickson's <i>In Circles</i> (a recital whose page leaves several
/// tracks under no linked piece), each with its barcode lookup and its track
/// lookup, and Peter Sculthorpe's composer page. Previn's <i>Nutcracker</i>
/// was recorded the same way on 2026-11-03.
/// </remarks>
public sealed class AppleMusicClassicalTests : IDisposable
{
    private const string KarajanBarcode = "00028943900423";

    private const string InCirclesBarcode = "886447581360";

    private const string NutcrackerBarcode = "5059460454842";

    /// <summary>The first song each record's page lists, which its track lookup asks for first.</summary>
    private const string KarajanFirstSong = "1440744516";

    private const string InCirclesFirstSong = "1456009476";

    private const string NutcrackerFirstSong = "1850693207";

    private static readonly TimeSpan Gate = TimeSpan.FromMilliseconds(1);

    private readonly List<ServiceProvider> _providers = [];

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static AlbumToFind Album(string barcode, string title = "Beethoven: Symphony Nos. 5 & 6") =>
        new(title, null, [], null, barcode, []);

    /// <summary>
    /// Every track, numbered by the lookup, under the piece its section links,
    /// with its performers and the roles the album credits them with.
    /// </summary>
    [Fact]
    public async Task TheBarcodeFindsTheRecordAndEachTrackItsPieceAndItsPerformers()
    {
        var (classical, _) = Build(Recorded());

        var found = await classical.FindAsync(Album(KarajanBarcode), Token);

        Assert.NotNull(found);
        Assert.Equal("1440744257", found.AlbumId);
        Assert.Equal("barcode", found.MatchedBy);
        Assert.Equal(9, found.Tracks.Count);

        var first = found.Tracks[0];

        Assert.Equal((1, 1), (first.Disc, first.Number));
        Assert.Equal(TimeSpan.FromSeconds(442), first.Length);
        Assert.NotNull(first.Work);
        Assert.Equal("ludwig-van-beethoven-1770-pp27", first.Work.Id);
        Assert.Equal("Symphony No. 5 in C Minor, Op. 67 · “Fate Symphony”", first.Work.Title);
        Assert.Equal([new ClassicalArtist("1130028", "Ludwig van Beethoven")], first.Work.Composers);

        Assert.Equal(
            [
                ("Berlin Philharmonic Orchestra", "Orchestra, Symphony Orchestra"),
                ("Herbert von Karajan", "Conductor"),
            ],
            first.Performers.Select(performer => (performer.Name, string.Join(", ", performer.Roles))));

        var sixth = found.Tracks[4];

        Assert.Equal((1, 5), (sixth.Disc, sixth.Number));
        Assert.Equal("ludwig-van-beethoven-1770-pp28", sixth.Work?.Id);
    }

    /// <summary>
    /// A track whose section links no piece performs nothing, whatever the page
    /// prints as its work name.
    /// </summary>
    /// <remarks>
    /// The recital's first track is printed under "E. Pessard" and its sixth
    /// under "Sir J. MacMillan: Saxophone Concerto" — what the label typed, not
    /// a piece Apple knows. Its second is Sculthorpe's <i>Djilile</i>, linked.
    /// </remarks>
    [Fact]
    public async Task ATrackUnderNoLinkedPiecePerformsNothing()
    {
        var (classical, _) = Build(Recorded());

        var found = await classical.FindAsync(Album(InCirclesBarcode, "In Circles"), Token);

        Assert.NotNull(found);
        Assert.Equal(20, found.Tracks.Count);

        var byNumber = found.Tracks.ToDictionary(track => track.Number);

        Assert.Null(byNumber[1].Work);
        Assert.Null(byNumber[6].Work);
        Assert.Equal("peter-sculthorpe-1929-pp17", byNumber[2].Work?.Id);
        Assert.Equal([new ClassicalArtist("60621959", "Peter Sculthorpe")], byNumber[2].Work?.Composers);
    }

    /// <summary>
    /// Each track is numbered by the lookup of its own id, so a record whose
    /// album lookup lists no songs still has every track placed.
    /// </summary>
    /// <remarks>
    /// Apple answered Previn's <i>Nutcracker</i> songs, asked by the album's
    /// id, with the album alone; asked by their own ids, with all 22, in one
    /// lookup.
    /// </remarks>
    [Fact]
    public async Task EachTrackIsNumberedByTheLookupOfItsOwnId()
    {
        var (classical, stub) = Build(Recorded());

        var found = await classical.FindAsync(Album(NutcrackerBarcode, "The Nutcracker (Tchaikovsky)"), Token);

        Assert.NotNull(found);
        Assert.Equal(Enumerable.Range(1, 22), found.Tracks.Select(track => track.Number));
        Assert.Equal(TimeSpan.FromMilliseconds(208200), found.Tracks[0].Length);
        Assert.Equal("pyotr-ilyich-tchaikovsky-1840-pp31", found.Tracks[0].Work?.Id);

        var asked = stub.Requests.Single(request => request.Uri.Query.StartsWith($"?id={NutcrackerFirstSong}", StringComparison.Ordinal)).Uri.Query;
        var ids = asked[4..asked.IndexOf('&', StringComparison.Ordinal)].Split(',');

        Assert.Equal(22, ids.Distinct().Count());
        Assert.All(ids, id => Assert.True(long.TryParse(id, CultureInfo.InvariantCulture, out _), id));
    }

    /// <summary>
    /// A record Apple Music Classical does not carry is nothing to say, and
    /// its tracks are never looked up.
    /// </summary>
    [Fact]
    public async Task ARecordWithNoClassicalPageIsNothing()
    {
        var (classical, stub) = Build(Recorded(page: _ => new HttpResponseMessage(HttpStatusCode.NotFound)));

        Assert.Null(await classical.FindAsync(Album(KarajanBarcode), Token));
        Assert.DoesNotContain(stub.Requests, request => request.Uri.Query.StartsWith($"?id={KarajanFirstSong}", StringComparison.Ordinal));
    }

    /// <summary>
    /// An outage is not an answer, and neither is a page that has changed shape.
    /// </summary>
    [Fact]
    public async Task AnOutageOrANewPageShapeIsNotAnAnswer()
    {
        var (down, _) = Build(Recorded(page: _ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)));
        await Assert.ThrowsAsync<ProviderUnavailableException>(() => down.FindAsync(Album(KarajanBarcode), Token));

        var (redesigned, _) = Build(Recorded(page: _ => Text("<html><body>A new player</body></html>")));
        await Assert.ThrowsAsync<ProviderRejectedException>(() => redesigned.FindAsync(Album(KarajanBarcode), Token));

        var (sectionless, _) = Build(Recorded(page: _ => Text(
            """<script type="application/json" id="serialized-server-data">{"data":[{"data":{}}]}</script>""")));
        await Assert.ThrowsAsync<ProviderRejectedException>(() => sectionless.FindAsync(Album(KarajanBarcode), Token));
    }

    /// <summary>A page Apple sent to its ordinary shop is not a classical page, and its tracks are never looked up.</summary>
    [Fact]
    public async Task APageThatLandsOffTheClassicalHostIsNothing()
    {
        var (classical, stub) = Build(Recorded(page: _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(Fixture("apple-classical-album-karajan.html"), Encoding.UTF8, "text/html"),
            RequestMessage = new HttpRequestMessage(HttpMethod.Get, "https://music.apple.com/us/album/1440744257"),
        }));

        Assert.Null(await classical.FindAsync(Album(KarajanBarcode), Token));
        Assert.DoesNotContain(stub.Requests, request => request.Uri.Query.StartsWith($"?id={KarajanFirstSong}", StringComparison.Ordinal));
    }

    /// <summary>A track the lookup does not number is nobody's; the rest keep their places.</summary>
    [Fact]
    public async Task ATrackTheLookupDoesNotNumberIsLeftOut()
    {
        var songs = JsonNode.Parse(Fixture("apple-lookup-karajan-songs.json"))!;
        var results = songs["results"]!.AsArray();
        results.Remove(results.First(row => (int?)row!["trackNumber"] == 1));

        var (classical, _) = Build(Recorded(songs: songs.ToJsonString()));

        var found = await classical.FindAsync(Album(KarajanBarcode), Token);

        Assert.NotNull(found);
        Assert.Equal([2, 3, 4, 5, 6, 7, 8, 9], found.Tracks.Select(track => track.Number));
    }

    /// <summary>A section headed by anything but a piece performs nothing, whatever it links.</summary>
    [Fact]
    public async Task ASectionHeadedByNoPiecePerformsNothing()
    {
        var page = Fixture("apple-classical-album-karajan.html")
            .Replace("\"header\":{\"kind\":\"work\"", "\"header\":{\"kind\":\"other\"", StringComparison.Ordinal);

        var (classical, _) = Build(Recorded(page: _ => Text(page)));

        var found = await classical.FindAsync(Album(KarajanBarcode), Token);

        Assert.NotNull(found);
        Assert.Equal(9, found.Tracks.Count);
        Assert.All(found.Tracks, track => Assert.Null(track.Work));
    }

    /// <summary>A composer's biography is the page's, as plain text, credited to the page.</summary>
    [Fact]
    public async Task AComposersBiographyIsReadAsPlainTextAndCreditedToItsPage()
    {
        var (classical, _) = Build(Recorded());

        var biography = await classical.BiographyAsync("60621959", Token);

        Assert.NotNull(biography);
        Assert.StartsWith("Peter Sculthorpe (born 1929, Launceston, Tasmania)", biography.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("<", biography.Text, StringComparison.Ordinal);
        Assert.Equal(new Uri("https://classical.music.apple.com/us/artist/60621959"), biography.Url);
    }

    /// <summary>A composer with no page has no biography, and an id that is not Apple's is never sent.</summary>
    [Fact]
    public async Task NoPageOrAForeignIdIsNoBiography()
    {
        var (classical, stub) = Build(Recorded());

        Assert.Null(await classical.BiographyAsync("1130028", Token));
        Assert.Null(await classical.BiographyAsync("../album/1440744257", Token));
        Assert.DoesNotContain(stub.Requests, request => request.Uri.AbsolutePath.Contains("album", StringComparison.Ordinal));
    }

    public void Dispose()
    {
        foreach (var provider in _providers) provider.Dispose();
    }

    /// <summary>Apple, as recorded: the three records' lookups and pages, and one composer's page.</summary>
    private static Func<RecordedRequest, HttpResponseMessage> Recorded(
        Func<RecordedRequest, HttpResponseMessage>? page = null,
        string? songs = null) =>
        request =>
        {
            var uri = request.Uri;

            if (uri.Host == "itunes.apple.com")
            {
                var query = uri.Query;

                return query switch
                {
                    _ when query.Contains($"upc={KarajanBarcode}", StringComparison.Ordinal) => Json(Fixture("apple-lookup-karajan.json")),
                    _ when query.Contains($"upc={InCirclesBarcode}", StringComparison.Ordinal) => Json(Fixture("apple-lookup-in-circles.json")),
                    _ when query.Contains($"upc={NutcrackerBarcode}", StringComparison.Ordinal) => Json(Fixture("apple-lookup-nutcracker.json")),
                    _ when query.StartsWith($"?id={KarajanFirstSong}", StringComparison.Ordinal) => Json(songs ?? Fixture("apple-lookup-karajan-songs.json")),
                    _ when query.StartsWith($"?id={InCirclesFirstSong}", StringComparison.Ordinal) => Json(Fixture("apple-lookup-in-circles-songs.json")),
                    _ when query.StartsWith($"?id={NutcrackerFirstSong}", StringComparison.Ordinal) => Json(Fixture("apple-lookup-nutcracker-songs.json")),
                    _ => Json(Fixture("apple-lookup-none.json")),
                };
            }

            if (uri.Host == "classical.music.apple.com")
            {
                return uri.AbsolutePath switch
                {
                    "/us/album/1440744257" => page?.Invoke(request) ?? Text(Fixture("apple-classical-album-karajan.html")),
                    "/us/album/1456009475" => page?.Invoke(request) ?? Text(Fixture("apple-classical-album-in-circles.html")),
                    "/us/album/1850693206" => page?.Invoke(request) ?? Text(Fixture("apple-classical-album-nutcracker.html")),
                    "/us/artist/60621959" => Text(Fixture("apple-classical-composer-sculthorpe.html")),
                    _ => new HttpResponseMessage(HttpStatusCode.NotFound),
                };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        };

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Text(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "text/html") };

    private static string Fixture(string name)
    {
        var assembly = typeof(AppleMusicClassicalTests).Assembly;
        var resource = $"{assembly.GetName().Name}.Responses.{name}";

        using var stream = assembly.GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException($"Missing embedded fixture '{resource}'.");

        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private (IClassicalAlbums Classical, StubHttpHandler Stub) Build(Func<RecordedRequest, HttpResponseMessage> respond)
    {
        var stub = new StubHttpHandler(respond);
        var services = new ServiceCollection();

        services.AddLogging();
        services.AddAppleMusic(options =>
        {
            options.MinimumRequestInterval = Gate;
            options.PageRequestInterval = Gate;
        });

        foreach (var name in new[] { AppleMusicOptions.HttpClientName, AppleMusicOptions.PageHttpClientName })
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

        return (provider.GetRequiredService<IClassicalAlbums>(), stub);
    }
}
