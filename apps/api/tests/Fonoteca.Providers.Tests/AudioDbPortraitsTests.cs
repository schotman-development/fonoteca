using System.Net;
using System.Text;
using Fonoteca.Domain.Abstractions;
using Fonoteca.Domain.Catalogue;
using Fonoteca.Fixtures;
using Fonoteca.Providers.AudioDb;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Polly;

namespace Fonoteca.Providers.Tests;

/// <summary>
/// The TheAudioDB picture source, built through its real DI registration and
/// pointed at a stub socket.
/// </summary>
/// <remarks>
/// <b>Nothing here is about deciding which artist this is</b>, and that absence
/// is the source's whole character: it is looked up by MusicBrainz id, so the
/// class of failure the two name-searched sources manage does not arise. What
/// is left to get wrong is telling "they hold no picture" apart from "they did
/// not answer", which is what most of this file is.
/// </remarks>
public sealed class AudioDbPortraitsTests : IDisposable
{
    private static readonly Mbid Shostakovich =
        new(new Guid("2b2b1d9f-3a4e-4d5c-8e6f-1a2b3c4d5e6f"));

    /// <summary>Short enough to keep the suite quick, long enough to be measurable.</summary>
    private static readonly TimeSpan Gate = TimeSpan.FromMilliseconds(20);

    private readonly List<ServiceProvider> _providers = [];

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>
    /// The artist is asked about by MusicBrainz id, not by name.
    /// </summary>
    /// <remarks>
    /// The reason this source exists beside two that cover more: measured, it
    /// answers for 16 of the 40 album artists Qobuz cannot place where Deezer
    /// answers for 30 — but the ones only it reaches are the ones a name search
    /// structurally cannot. Шостакович, Стравинский and 内田光子 are indexed by
    /// both name-searched services under Latin spellings, and no folding table
    /// turns Cyrillic into "Shostakovich". Here the name is never sent.
    /// </remarks>
    [Fact]
    public async Task TheLookupIsByMusicBrainzIdAndTheNameIsNeverSent()
    {
        var (portraits, stub) = Build(_ => Ok(Thumb("shostakovich.jpg")));

        var found = await portraits.FindAsync(
            [new ArtistToPicture(Shostakovich, "Дмитрий Дмитриевич Шостакович")], Token);

        Assert.EndsWith("shostakovich.jpg", found[Shostakovich].AbsoluteUri, StringComparison.Ordinal);

        var request = Assert.Single(stub.Requests);
        Assert.Contains(Shostakovich.Value.ToString("D"), request.Uri.Query, StringComparison.Ordinal);
        Assert.DoesNotContain("Шостакович", request.Uri.ToString(), StringComparison.Ordinal);
    }

    /// <summary>
    /// An artist they hold nothing for is an answer; an empty body is not.
    /// </summary>
    /// <remarks>
    /// <b>The two are one character apart on the wire and opposite in
    /// meaning.</b> They say "we have no row for this id" with the literal
    /// <c>{"artists":null}</c>, which is a real answer and should stamp the
    /// artist as asked. They say nothing at all — an empty body with a 200 —
    /// when the shared test key is being throttled, which is not an answer and
    /// must mark the source down so the artists fall through to another one and
    /// are retried next run.
    ///
    /// Deserialising both yields a null <c>artists</c>, so without the explicit
    /// check a throttled run stamps the entire worklist "asked, no picture" and
    /// nothing revisits it.
    /// </remarks>
    [Fact]
    public async Task NoRowIsAnAnswerAndAnEmptyBodyIsAFailure()
    {
        var (held, _) = Build(_ => Ok("""{ "artists": null }"""));

        Assert.Empty(await held.FindAsync([new ArtistToPicture(Shostakovich, "Anyone")], Token));

        var (throttled, _) = Build(_ => Ok(string.Empty));

        await Assert.ThrowsAsync<ProviderUnavailableException>(
            () => throttled.FindAsync([new ArtistToPicture(Shostakovich, "Anyone")], Token));
    }

    /// <summary>
    /// A row with no thumbnail is an answer, not a picture.
    /// </summary>
    /// <remarks>
    /// They hold rows for far more artists than they hold pictures of. Returning
    /// nothing is what lets the pass fall through to Wikidata rather than
    /// storing a blank.
    /// </remarks>
    [Fact]
    public async Task ARowWithNoThumbnailAnswersNothing()
    {
        var (portraits, _) = Build(_ => Ok("""{ "artists": [ { "strArtistThumb": null } ] }"""));

        Assert.Empty(await portraits.FindAsync([new ArtistToPicture(Shostakovich, "Anyone")], Token));
    }

    /// <summary>
    /// Their http URLs are served as https, or every tile silently loses its
    /// picture.
    /// </summary>
    /// <remarks>
    /// <c>WikidataPortraits.Image</c>'s lesson, and the same one: served into a
    /// page delivered over https, a plain-http image is blocked as mixed content
    /// and nothing on the screen says why. The same file is at both addresses,
    /// so correcting the scheme is not rewriting the provider's answer.
    /// </remarks>
    [Fact]
    public async Task AnHttpPictureIsServedAsHttps()
    {
        var (portraits, _) = Build(_ => Ok(
            """{ "artists": [ { "strArtistThumb": "http://r2.theaudiodb.com/images/media/artist/thumb/x.jpg" } ] }"""));

        var found = await portraits.FindAsync(
            [new ArtistToPicture(Shostakovich, "Anyone")], Token);

        Assert.StartsWith("https://", found[Shostakovich].AbsoluteUri, StringComparison.Ordinal);
    }

    /// <summary>A picture from anywhere but their hosts is not a picture.</summary>
    [Theory]
    [InlineData("https://example.test/tracker.png")]
    [InlineData("javascript:alert(1)")]
    [InlineData("not a url at all")]
    public async Task APictureFromSomewhereElseIsRefused(string url)
    {
        var (portraits, _) = Build(_ => Ok(
            $$"""{ "artists": [ { "strArtistThumb": {{System.Text.Json.JsonSerializer.Serialize(url)}} } ] }"""));

        Assert.Empty(await portraits.FindAsync([new ArtistToPicture(Shostakovich, "Anyone")], Token));
    }

    /// <summary>
    /// The key travels in the path, and the shipped default is their test key.
    /// </summary>
    /// <remarks>
    /// It is a key rather than a credential — it identifies the caller and buys
    /// nothing that costs money — so unlike the AcoustID one a missing setting
    /// is not worth refusing the lookup over. There is a working default and
    /// there is no way to be anonymous here, which is what lets this source work
    /// on a fresh install.
    /// </remarks>
    [Fact]
    public async Task TheDefaultKeyIsTheirPublishedTestKey()
    {
        var (portraits, stub) = Build(_ => Ok("""{ "artists": null }"""));

        await portraits.FindAsync([new ArtistToPicture(Shostakovich, "Anyone")], Token);

        Assert.Contains("/api/v1/json/2/", Assert.Single(stub.Requests).Uri.AbsolutePath, StringComparison.Ordinal);
    }

    public void Dispose()
    {
        foreach (var provider in _providers) provider.Dispose();
    }

    private static string Thumb(string file) =>
        $$"""
          { "artists": [ { "strArtistThumb":
            "https://r2.theaudiodb.com/images/media/artist/thumb/{{file}}" } ] }
          """;

    private static HttpResponseMessage Ok(string body) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };

    private (IArtistPortraits Portraits, StubHttpHandler Stub) Build(
        Func<RecordedRequest, HttpResponseMessage> respond,
        Action<AudioDbOptions>? configure = null)
    {
        var stub = new StubHttpHandler(respond);
        var services = new ServiceCollection();

        services.AddAudioDbPortraits(options =>
        {
            options.MinimumRequestInterval = Gate;
            configure?.Invoke(options);
        });

        services.AddHttpClient(AudioDbOptions.HttpClientName)
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

        return (
            provider.GetRequiredKeyedService<IArtistPortraits>(ArtistPortraitSources.AudioDb),
            stub);
    }
}
