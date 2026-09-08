using System.Net;
using System.Text;
using Fonoteca.Domain.Abstractions;
using Fonoteca.Domain.Catalogue;
using Fonoteca.Fixtures;
using Fonoteca.Providers.Wikidata;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Polly;

namespace Fonoteca.Providers.Tests;

/// <summary>
/// The Wikidata adapter, built through its real DI registration and pointed at
/// a stub socket.
/// </summary>
/// <remarks>
/// The shape under test is a URL from a third party that ends up in an
/// <c>img src</c> on this application's origin. Everything here is about what
/// happens between "Wikidata said something" and "a browser is asked to fetch
/// it" — the host it may come from, the scheme it must be on, and which of
/// several answers is shown when there is more than one.
/// </remarks>
public sealed class WikidataPortraitsTests : IDisposable
{
    private static readonly Mbid Beethoven = new(new Guid("1f9df192-a621-4f54-8850-2c5373b7eac9"));
    private static readonly Mbid Gilmour = new(new Guid("1dce970e-34bc-48b2-ab51-48d87544a4c2"));

    /// <summary>Short enough to keep the suite quick, long enough to be measurable.</summary>
    private static readonly TimeSpan Gate = TimeSpan.FromMilliseconds(20);

    private readonly List<ServiceProvider> _providers = [];

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task TheIdsArePostedAsASparqlQuery()
    {
        var (portraits, stub) = Build(_ => Ok(Results()));

        await portraits.FindAsync([Named(Beethoven), Named(Gilmour)], Token);

        var request = Assert.Single(stub.Requests);

        // POST, not GET: three hundred ids is a 12 KB query string, and the
        // endpoint answers an over-long GET with an empty body that parses
        // cleanly as "nobody has a picture".
        Assert.Equal(HttpMethod.Post, request.Method);

        var query = Encoding.UTF8.GetString(request.Body);

        Assert.Contains("wdt:P434", query, StringComparison.Ordinal);
        Assert.Contains("wdt:P18", query, StringComparison.Ordinal);
        Assert.Contains(Beethoven.Value.ToString("D"), query, StringComparison.Ordinal);
        Assert.Contains(Gilmour.Value.ToString("D"), query, StringComparison.Ordinal);

        // Wikimedia's user-agent policy is MusicBrainz's, enforced the same way.
        Assert.Contains("fonoteca@example.test", request.UserAgent ?? "", StringComparison.Ordinal);
    }

    /// <summary>
    /// One artist, several pictures, and the same one every time.
    /// </summary>
    /// <remarks>
    /// Wikidata holds two P18 claims for Beethoven at equal rank and SPARQL
    /// result order is undefined, so the answer arrives in whatever order the
    /// server felt like. A tile that changed its face between two reads of the
    /// same page reads as a bug in the catalogue, so the pick is stated and
    /// stable rather than first-seen: the two orderings below must agree.
    /// </remarks>
    [Fact]
    public async Task AnArtistWithSeveralPicturesGetsTheSameOneEveryTime()
    {
        var (forwards, _) = Build(_ => Ok(Results(
            (Beethoven, Commons("Beethoven.jpg")),
            (Beethoven, Commons("Ernst%20Ludwig%20Gerber.jpg")))));

        var (backwards, _) = Build(_ => Ok(Results(
            (Beethoven, Commons("Ernst%20Ludwig%20Gerber.jpg")),
            (Beethoven, Commons("Beethoven.jpg")))));

        var first = await forwards.FindAsync([Named(Beethoven)], Token);
        var second = await backwards.FindAsync([Named(Beethoven)], Token);

        Assert.Equal(first[Beethoven], second[Beethoven]);
        Assert.EndsWith("Beethoven.jpg", first[Beethoven].AbsoluteUri, StringComparison.Ordinal);
    }

    /// <summary>
    /// http is corrected to https, or every picture silently fails to load.
    /// </summary>
    /// <remarks>
    /// Wikidata returns these as <c>http://commons.wikimedia.org/…</c>. Served
    /// into a page delivered over https, every one is blocked as mixed content —
    /// which looks exactly like an artist having no photograph, on every card at
    /// once, with nothing in the console anybody is reading.
    /// </remarks>
    [Fact]
    public async Task AnHttpUrlIsCorrectedToHttps()
    {
        var (portraits, _) = Build(_ => Ok(Results(
            (Gilmour, "http://commons.wikimedia.org/wiki/Special:FilePath/DGilmour.jpg"))));

        var found = await portraits.FindAsync([Named(Gilmour)], Token);

        Assert.Equal(Uri.UriSchemeHttps, found[Gilmour].Scheme);
        Assert.Equal(
            "https://commons.wikimedia.org/wiki/Special:FilePath/DGilmour.jpg",
            found[Gilmour].AbsoluteUri);
    }

    /// <summary>
    /// A picture from anywhere but Wikimedia is not a picture.
    /// </summary>
    /// <remarks>
    /// P18 is documented as a Commons file, so this should never happen — which
    /// is the same thing that was true of <c>image/svg+xml</c> in an embedded
    /// cover right up until it turned out to be a live XSS. The value is a
    /// third party's, it goes straight into an <c>img src</c> on this
    /// application's origin, and an allowlist costs nothing.
    /// </remarks>
    [Theory]
    [InlineData("https://example.test/tracker.png")]
    [InlineData("javascript:alert(1)")]
    [InlineData("data:image/svg+xml;base64,PHN2Zz48L3N2Zz4=")]
    [InlineData("ftp://commons.wikimedia.org/wiki/Special:FilePath/Nope.jpg")]
    // The right host serving the wrong thing: Commons is a wiki as well as a
    // file store, and an ordinary page in an img is a broken tile.
    [InlineData("https://commons.wikimedia.org/wiki/Main_Page")]
    [InlineData("https://commons.wikimedia.org/w/index.php?title=Special:FilePath/X.jpg")]
    public async Task APictureFromAnywhereElseIsIgnored(string url)
    {
        var (portraits, _) = Build(_ => Ok(Results((Gilmour, url))));

        Assert.Empty(await portraits.FindAsync([Named(Gilmour)], Token));
    }

    /// <summary>
    /// A URL too long for the column is dropped rather than stored.
    /// </summary>
    /// <remarks>
    /// <c>Artists.PortraitUrl</c> is <c>varchar(1000)</c>. Written through, an
    /// overrun throws out of <c>SaveChangesAsync</c>, takes the "we asked" stamp
    /// with it, and puts the artist back on a worklist it will fail on
    /// identically forever — which is exactly what <c>Artists.Genres</c> paid
    /// for. Losing one picture is the cheap half of that trade.
    /// </remarks>
    [Fact]
    public async Task AUrlTooLongForTheColumnIsNotReturned()
    {
        var enormous = Commons(new string('a', 1_100) + ".jpg");

        var (portraits, _) = Build(_ => Ok(Results((Gilmour, enormous))));

        Assert.Empty(await portraits.FindAsync([Named(Gilmour)], Token));
    }

    /// <summary>
    /// An answer about somebody who was not asked about is dropped.
    /// </summary>
    /// <remarks>
    /// The caller stamps every artist it sent as asked, so a row naming an
    /// artist that was not in the batch would put a picture on a row nothing had
    /// looked up — and the honest reading of the result is "these are answers to
    /// the question I asked".
    /// </remarks>
    [Fact]
    public async Task AnAnswerAboutAnArtistNobodyAskedAboutIsDropped()
    {
        var (portraits, _) = Build(_ => Ok(Results(
            (Beethoven, Commons("Beethoven.jpg")),
            (Gilmour, Commons("DGilmour.jpg")))));

        var found = await portraits.FindAsync([Named(Beethoven)], Token);

        Assert.Equal([Beethoven], found.Keys);
    }

    /// <summary>
    /// Nothing found is an answer; nothing asked is a failure.
    /// </summary>
    /// <remarks>
    /// The two have to be distinguishable, because the caller does opposite
    /// things with them: an empty result is stamped so those artists are never
    /// asked about again, and a throw leaves the whole batch on the worklist for
    /// the next run. Answering an outage with "no pictures exist" would write
    /// that outage permanently into the catalogue.
    /// </remarks>
    [Fact]
    public async Task NoPicturesIsAnEmptyResultAndAnOutageThrows()
    {
        var (found, _) = Build(_ => Ok(Results()));
        Assert.Empty(await found.FindAsync([Named(Beethoven)], Token));

        var (down, _) = Build(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
        {
            Content = new StringContent("upstream is down"),
        });

        await Assert.ThrowsAsync<ProviderUnavailableException>(
            () => down.FindAsync([Named(Beethoven)], Token));
    }

    /// <summary>
    /// A query this build gets wrong is not worth retrying, ever.
    /// </summary>
    /// <remarks>
    /// <see cref="ProviderRejectedException"/> is what stops the pass rather
    /// than the batch — every remaining batch would fail identically, and a
    /// dozen more requests to be told the same thing is the traffic pattern that
    /// gets an address blocked. The missing contact below is the same class of
    /// problem and is caught without a request at all.
    /// </remarks>
    [Fact]
    public async Task AMalformedQueryIsRejectedAndAMissingContactIsNotEvenSent()
    {
        var (bad, _) = Build(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("MalformedQueryException"),
        });

        await Assert.ThrowsAsync<ProviderRejectedException>(() => bad.FindAsync([Named(Beethoven)], Token));

        var (anonymous, stub) = Build(_ => Ok(Results()), options => options.Contact = string.Empty);

        var refused = await Assert.ThrowsAsync<ProviderRejectedException>(
            () => anonymous.FindAsync([Named(Beethoven)], Token));

        Assert.Empty(stub.Requests);
        Assert.Contains("contact", refused.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>An empty batch asks nothing rather than asking about nobody.</summary>
    [Fact]
    public async Task AnEmptyBatchMakesNoRequest()
    {
        var (portraits, stub) = Build(_ => Ok(Results()));

        Assert.Empty(await portraits.FindAsync([], Token));
        Assert.Empty(stub.Requests);
    }

    public void Dispose()
    {
        foreach (var provider in _providers) provider.Dispose();
    }

    /// <summary>The name is irrelevant to this source, which is worth saying once.</summary>
    private static ArtistToPicture Named(Mbid artist) => new(artist, "ignored by this source");

    private static string Commons(string file) =>
        $"https://commons.wikimedia.org/wiki/Special:FilePath/{file}";

    /// <summary>A SPARQL results document, in the shape the endpoint returns one.</summary>
    private static string Results(params (Mbid Artist, string Image)[] rows)
    {
        var bindings = string.Join(
            ",",
            rows.Select(row =>
                $$"""
                  {
                    "mbid": { "type": "literal", "value": "{{row.Artist.Value:D}}" },
                    "image": { "type": "uri", "value": "{{row.Image}}" }
                  }
                  """));

        return $$"""
                 { "head": { "vars": [ "mbid", "image" ] },
                   "results": { "bindings": [ {{bindings}} ] } }
                 """;
    }

    private static HttpResponseMessage Ok(string body) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/sparql-results+json"),
        };

    private (IArtistPortraits Portraits, StubHttpHandler Stub) Build(
        Func<RecordedRequest, HttpResponseMessage> respond,
        Action<WikidataOptions>? configure = null)
    {
        var stub = new StubHttpHandler(respond);
        var services = new ServiceCollection();

        services.AddWikidata(options =>
        {
            options.Contact = "fonoteca@example.test";
            options.MinimumRequestInterval = Gate;
            configure?.Invoke(options);
        });

        // Re-opening the same named client appends configuration and the last
        // primary handler wins, so the gate and the resilience pipeline
        // AddWikidata put above it are still in place.
        services.AddHttpClient(WikidataOptions.HttpClientName)
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

        // Keyed, exactly as the application resolves it — there are two sources
        // now and the key is how the pass says which it wants first.
        return (
            provider.GetRequiredKeyedService<IArtistPortraits>(ArtistPortraitSources.Wikidata),
            stub);
    }
}
