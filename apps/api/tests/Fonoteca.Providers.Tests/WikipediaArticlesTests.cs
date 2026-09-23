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

public sealed class WikipediaArticlesTests : IDisposable
{
    private static readonly Mbid Tchaikovsky = new(new Guid("9ddd7abc-9e1b-471d-8031-583bc6bc8be9"));
    private static readonly Mbid Nobody = new(new Guid("00000000-0000-4000-8000-000000000001"));
    private static readonly Mbid Elsewhere = new(new Guid("00000000-0000-4000-8000-000000000002"));

    private readonly List<ServiceProvider> _providers = [];

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>
    /// Wikidata names the article, Wikipedia answers under its current title,
    /// and the text comes back keyed by the id with the sitelink to credit.
    /// </summary>
    [Fact]
    public async Task AnArticleIsFoundThroughItsSitelinkAndARedirect()
    {
        var (articles, stub) = Build(request => request.Uri.Host == "query.wikidata.org"
            ? Ok(Sitelinks(
                (Tchaikovsky, "https://en.wikipedia.org/wiki/Pyotr_Ilyich_Tchaikovsky"),
                (Elsewhere, "https://de.wikipedia.org/wiki/Anderswo")))
            : Ok("""
                {"batchcomplete":true,"query":{
                  "normalized":[{"fromencoded":false,"from":"Pyotr_Ilyich_Tchaikovsky","to":"Pyotr Ilyich Tchaikovsky"}],
                  "redirects":[{"from":"Pyotr Ilyich Tchaikovsky","to":"Tchaikovsky"}],
                  "pages":[{"pageid":1,"ns":0,"title":"Tchaikovsky",
                    "extract":"Pyotr Ilyich Tchaikovsky was a Russian composer.\nHe died in 1893."}]}}
                """));

        var found = await articles.ArtistsAsync([Tchaikovsky, Nobody, Elsewhere], Token);

        var article = Assert.Single(found);
        Assert.Equal(Tchaikovsky, article.Key);
        Assert.Equal(
            "Pyotr Ilyich Tchaikovsky was a Russian composer.\n\nHe died in 1893.",
            article.Value.Text);
        Assert.Equal(
            "https://en.wikipedia.org/wiki/Pyotr_Ilyich_Tchaikovsky",
            article.Value.Url.AbsoluteUri);

        var sparql = Encoding.UTF8.GetString(stub.Requests[0].Body);
        Assert.Contains("wdt:P434", sparql, StringComparison.Ordinal);

        // Only the English article was asked for; the German sitelink was dropped.
        var extracts = Assert.Single(stub.Requests, request => request.Uri.Host == "en.wikipedia.org");
        Assert.DoesNotContain("Anderswo", extracts.Uri.ToString(), StringComparison.Ordinal);

        // The whole article, not the lead. `exintro` would cost nothing to
        // leave in and would silently shorten every biography back to a
        // sentence, which is the bug this pins.
        Assert.DoesNotContain("exintro", extracts.Uri.Query, StringComparison.Ordinal);

        // `exlimit` above 1 makes Wikipedia answer about one page and drop the
        // rest *without failing*, so sending it is how articles go missing.
        Assert.DoesNotContain("exlimit", extracts.Uri.Query, StringComparison.Ordinal);
    }

    /// <summary>
    /// Wikipedia's appendices end the biography, and everything above them stays.
    /// </summary>
    /// <remarks>
    /// Left in, a biography ends "Discography References External links" — the
    /// headings survive <c>prop=extracts</c> even where their contents do not.
    /// </remarks>
    [Fact]
    public async Task TheBiographyStopsWhereTheListsBegin()
    {
        var (articles, _) = Build(request => request.Uri.Host == "query.wikidata.org"
            ? Ok(Sitelinks((Tchaikovsky, "https://en.wikipedia.org/wiki/Tchaikovsky")))
            : Ok(Extract(
                "Tchaikovsky was a Russian composer.",
                "== Early life ==",
                "He was born in 1840.",
                "=== Schooling ===",
                "He studied law.",
                "== Discography ==",
                "Symphony No. 1",
                "== References ==",
                "Brown, David.")));

        var article = Assert.Single(await articles.ArtistsAsync([Tchaikovsky], Token));

        Assert.Equal(
            "Tchaikovsky was a Russian composer.\n\nHe was born in 1840.\n\nHe studied law.",
            article.Value.Text);
    }

    /// <summary>
    /// A discography sits mid-article, so the prose after it survives.
    /// </summary>
    /// <remarks>
    /// Measured against real articles: stopping at the first "Discography"
    /// cost Sol Gabetta her personal life and Alison Balsom her awards. The
    /// section's own contents must still go — they arrive as plain lines and
    /// would otherwise read as biography.
    /// </remarks>
    [Fact]
    public async Task ADiscographyIsSkippedRatherThanEndingTheBiography()
    {
        var (articles, _) = Build(request => request.Uri.Host == "query.wikidata.org"
            ? Ok(Sitelinks((Tchaikovsky, "https://en.wikipedia.org/wiki/Tchaikovsky")))
            : Ok(Extract(
                "A Russian composer.",
                "== Discography ==",
                "Some Album (1875)",
                "=== Studio albums ===",
                "Another Album (1880)",
                "== Personal life ==",
                "He kept diaries.",
                "== References ==",
                "Brown, David.")));

        var article = Assert.Single(await articles.ArtistsAsync([Tchaikovsky], Token));

        Assert.Equal("A Russian composer.\n\nHe kept diaries.", article.Value.Text);
    }

    /// <summary>
    /// An album's article drops an album's lists, which are not an artist's.
    /// </summary>
    /// <remarks>
    /// Measured on <i>Blue Train</i>: with only the artist words, the stored
    /// review ended on the personnel list read as prose.
    /// </remarks>
    [Fact]
    public async Task AnAlbumArticleDropsItsPersonnelAndTrackListing()
    {
        var (articles, _) = Build(request => request.Uri.Host == "query.wikidata.org"
            ? Ok(Sitelinks((Tchaikovsky, "https://en.wikipedia.org/wiki/Blue_Train")))
            : Ok(Extract(
                "Blue Train is a 1958 album.",
                "== Track listing ==",
                "All compositions by John Coltrane.",
                "== Personnel ==",
                "Rudy Van Gelder – recording engineer",
                "== Reception ==",
                "It was well received.")));

        var article = Assert.Single(await articles.ReleaseGroupsAsync([Tchaikovsky], Token));

        Assert.Equal("Blue Train is a 1958 album.\n\nIt was well received.", article.Value.Text);
    }

    /// <summary>
    /// A very long article is cut at a paragraph, and the first is always kept.
    /// </summary>
    /// <remarks>
    /// David Bowie's article is 78,000 characters, which is not a biography any
    /// more. The cut lands on a paragraph boundary so what is shown is prose
    /// rather than a sentence stopping mid-word.
    /// </remarks>
    [Fact]
    public async Task AVeryLongArticleIsCutAtAParagraph()
    {
        var paragraph = new string('a', 3_000);

        var (articles, _) = Build(request => request.Uri.Host == "query.wikidata.org"
            ? Ok(Sitelinks((Tchaikovsky, "https://en.wikipedia.org/wiki/Tchaikovsky")))
            : Ok(Extract(paragraph, paragraph, paragraph, paragraph)));

        var article = Assert.Single(await articles.ArtistsAsync([Tchaikovsky], Token));

        // Two whole paragraphs fit under 8,000; the third would overrun it.
        Assert.Equal(string.Join("\n\n", paragraph, paragraph), article.Value.Text);
    }

    /// <summary>One paragraph longer than the clamp is kept whole, not dropped.</summary>
    [Fact]
    public async Task TheFirstParagraphSurvivesWhateverItsLength()
    {
        var paragraph = new string('a', 20_000);

        var (articles, _) = Build(request => request.Uri.Host == "query.wikidata.org"
            ? Ok(Sitelinks((Tchaikovsky, "https://en.wikipedia.org/wiki/Tchaikovsky")))
            : Ok(Extract(paragraph)));

        var article = Assert.Single(await articles.ArtistsAsync([Tchaikovsky], Token));

        Assert.Equal(paragraph, article.Value.Text);
    }

    /// <summary>
    /// Each article is one request, because Wikipedia will not answer about two.
    /// </summary>
    [Fact]
    public async Task EachArticleIsAskedForOnItsOwn()
    {
        var (articles, stub) = Build(request => request.Uri.Host == "query.wikidata.org"
            ? Ok(Sitelinks(
                (Tchaikovsky, "https://en.wikipedia.org/wiki/Tchaikovsky"),
                (Nobody, "https://en.wikipedia.org/wiki/Borodin")))
            : Ok(Extract("A Russian composer.")));

        var found = await articles.ArtistsAsync([Tchaikovsky, Nobody], Token);

        Assert.Equal(2, found.Count);

        var asked = stub.Requests
            .Where(request => request.Uri.Host == "en.wikipedia.org")
            .ToList();

        Assert.Equal(2, asked.Count);
        Assert.All(asked, request =>
            Assert.DoesNotContain("%7C", request.Uri.Query, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>An album is asked for by its release group id, under P436.</summary>
    [Fact]
    public async Task AnAlbumIsAskedForByItsReleaseGroup()
    {
        var (articles, stub) = Build(_ => Ok(Sitelinks()));

        var found = await articles.ReleaseGroupsAsync([Tchaikovsky], Token);

        Assert.Empty(found);
        Assert.Contains("wdt:P436", Encoding.UTF8.GetString(stub.Requests[0].Body), StringComparison.Ordinal);

        // Nothing to look up on Wikipedia, so nothing was sent there.
        Assert.Single(stub.Requests);
    }

    /// <summary>An outage is not "no article": the caller must not stamp the batch.</summary>
    [Fact]
    public async Task AFailedWikipediaRequestIsAnOutage()
    {
        var (articles, _) = Build(request => request.Uri.Host == "query.wikidata.org"
            ? Ok(Sitelinks((Tchaikovsky, "https://en.wikipedia.org/wiki/Tchaikovsky")))
            : new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));

        await Assert.ThrowsAsync<ProviderUnavailableException>(
            () => articles.ArtistsAsync([Tchaikovsky], Token));
    }

    public void Dispose()
    {
        foreach (var provider in _providers) provider.Dispose();
    }

    /// <summary>A <c>prop=extracts</c> body whose article is these lines.</summary>
    private static string Extract(params string[] lines) =>
        "{\"batchcomplete\":true,\"query\":{\"pages\":[{\"pageid\":1,\"ns\":0,"
        + "\"title\":\"Tchaikovsky\",\"extract\":"
        + System.Text.Json.JsonSerializer.Serialize(string.Join('\n', lines))
        + "}]}}";

    private static string Sitelinks(params (Mbid Id, string Article)[] rows) =>
        "{\"head\":{\"vars\":[\"mbid\",\"article\"]},\"results\":{\"bindings\":["
        + string.Join(',', rows.Select(row =>
            $"{{\"mbid\":{{\"type\":\"literal\",\"value\":\"{row.Id.Value:D}\"}},"
            + $"\"article\":{{\"type\":\"uri\",\"value\":\"{row.Article}\"}}}}"))
        + "]}}";

    private static HttpResponseMessage Ok(string body) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };

    private (IEncyclopedia Articles, StubHttpHandler Stub) Build(
        Func<RecordedRequest, HttpResponseMessage> respond)
    {
        var stub = new StubHttpHandler(respond);
        var services = new ServiceCollection();

        services.AddWikidata(options =>
        {
            options.Contact = "fonoteca@example.test";
            options.MinimumRequestInterval = TimeSpan.FromMilliseconds(1);
        });

        services.AddHttpClient(WikidataOptions.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(() => stub);
        services.AddHttpClient(WikipediaArticles.HttpClientName)
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

        return (provider.GetRequiredService<IEncyclopedia>(), stub);
    }
}
