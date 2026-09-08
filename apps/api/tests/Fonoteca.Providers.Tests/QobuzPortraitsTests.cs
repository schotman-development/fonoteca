using System.Net;
using System.Text;
using Fonoteca.Domain.Abstractions;
using Fonoteca.Domain.Catalogue;
using Fonoteca.Fixtures;
using Fonoteca.Providers.Qobuz;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Polly;

namespace Fonoteca.Providers.Tests;

/// <summary>
/// The Qobuz picture source, built through its real DI registration and pointed
/// at a stub socket.
/// </summary>
/// <remarks>
/// <b>This is the only picture source in the application that has to decide
/// rather than look up.</b> Wikidata is asked with a MusicBrainz id and answers
/// about that artist or about nobody; Qobuz has never heard of MusicBrainz, so
/// it is asked with a name and answers with whatever its search ranked highest.
/// Everything here is about the gap between those two things — because the
/// failure it prevents is a stranger's face on an artist page, which is worse
/// than the mediocre photograph this source exists to replace and, unlike the
/// mediocre one, invisible.
/// </remarks>
public sealed class QobuzPortraitsTests : IDisposable
{
    private static readonly Mbid Petty = new(new Guid("1a3f5a1e-9e79-4b0f-9b2e-6b9e8c3f7a11"));

    /// <summary>Short enough to keep the suite quick, long enough to be measurable.</summary>
    private static readonly TimeSpan Gate = TimeSpan.FromMilliseconds(20);

    private readonly List<ServiceProvider> _providers = [];

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>
    /// The exact match is taken, and it is not the first result.
    /// </summary>
    /// <remarks>
    /// Measured against the real service: searching "Tom Petty" returns
    /// <i>Tom Petty &amp; The Heartbreakers</i> first. Taking <c>items[0]</c> —
    /// which is what the album search's caller does, because there the ranking
    /// is the answer — puts the band's photograph on the man's page, and there
    /// is nothing on the screen to say it is wrong.
    /// </remarks>
    [Fact]
    public async Task TheExactNameIsTakenEvenWhenItIsNotRankedFirst()
    {
        var (portraits, _) = Build(_ => Ok(Artists(
            ("Tom Petty & The Heartbreakers", Image("band.jpg")),
            ("Tom Petty", Image("petty.jpg")))));

        var found = await portraits.FindAsync([new ArtistToPicture(Petty, "Tom Petty")], Token);

        Assert.EndsWith("petty.jpg", found[Petty].AbsoluteUri, StringComparison.Ordinal);
    }

    /// <summary>
    /// A name that is merely similar is refused, and the artist keeps no picture.
    /// </summary>
    /// <remarks>
    /// Both of these are real answers from the live service. "Daniel de Borah"
    /// returns a Barenboim compilation credited to six people; "The Sy Oliver
    /// Choir" returns a Louis Armstrong record that mentions them. Neither is
    /// the artist, both are the top hit, and refusing is what sends them to the
    /// fallback source instead of to a stranger.
    /// </remarks>
    [Theory]
    [InlineData(
        "Daniel de Borah",
        "Deborah Polaski, Alessandra Marc, Daniel Barenboim & Staatskapelle Berlin")]
    [InlineData("The Sy Oliver Choir", "Louis Armstrong and The All Stars with The Sy Oliver Choir")]
    [InlineData("Miles Davis Sextet", "Bob Dorough with Miles Davis Sextet")]
    public async Task AnArtistWhoIsOnlyNearlyTheSameIsRefused(string wanted, string returned)
    {
        var (portraits, _) = Build(_ => Ok(Artists((returned, Image("stranger.jpg")))));

        Assert.Empty(await portraits.FindAsync([new ArtistToPicture(Petty, wanted)], Token));
    }

    /// <summary>
    /// Two catalogues spelling one artist differently still agree.
    /// </summary>
    /// <remarks>
    /// Every one of these is a real disagreement between MusicBrainz and Qobuz
    /// over the same artist, and none of them is either party being wrong: a
    /// capital letter in "of", an accent, a dropped full stop. A comparison
    /// strict enough to reject them would cost an orchestra its photograph over
    /// the letter O.
    ///
    /// <b>The Liège row is the one that earned this test.</b> It failed on the
    /// first run, and what it caught was not a typo: the folding was written as
    /// <c>Normalize(FormKD)</c> plus dropping combining marks, which is the
    /// documented way to do this and <i>does nothing at all</i> under the
    /// <c>InvariantGlobalization</c> this project sets. No exception, no
    /// warning — the accented name simply stopped matching, and every other row
    /// here passed. Two of the four are ASCII-only and would have gone on
    /// passing forever.
    /// </remarks>
    [Theory]
    [InlineData("BBC National Orchestra of Wales", "BBC National Orchestra Of Wales")]
    [InlineData("Orchestre Philharmonique Royal de Liège", "Orchestre Philharmonique Royal de Liege")]
    [InlineData("AC/DC", "AC-DC")]
    [InlineData("Academy of St Martin in the Fields", "Academy of St. Martin in the Fields")]
    public async Task PunctuationCaseAndAccentsDoNotSeparateOneArtistFromThemselves(
        string wanted,
        string returned)
    {
        var (portraits, _) = Build(_ => Ok(Artists((returned, Image("them.jpg")))));

        var found = await portraits.FindAsync([new ArtistToPicture(Petty, wanted)], Token);

        Assert.EndsWith("them.jpg", found[Petty].AbsoluteUri, StringComparison.Ordinal);
    }

    /// <summary>
    /// Two artists of one name and comparable catalogues is a refusal, not a
    /// coin flip.
    /// </summary>
    /// <remarks>
    /// Taking the first would be picking by Qobuz's relevance ranking, which
    /// ranks search results and says nothing about which of two people this is.
    /// The fallback source loses nothing by being asked; an artist page showing a
    /// stranger cannot be spotted by the person looking at it.
    ///
    /// Note what this does <b>not</b> fix, and cannot: where Qobuz holds only
    /// <i>one</i> artist of the name and it is the wrong one, both strings still
    /// match. Measured live, that is exactly the shape of the catalogue's Jan
    /// Jansen.
    /// </remarks>
    [Fact]
    public async Task SeveralArtistsOfTheSameNameAreRefusedRatherThanGuessedBetween()
    {
        var (portraits, _) = Build(_ => Ok(Rivals(
            ("Jan Jansen", Image("one.jpg"), 3),
            ("Jan Jansen", Image("another.jpg"), 2))));

        Assert.Empty(
            await portraits.FindAsync([new ArtistToPicture(Petty, "Jan Jansen")], Token));
    }

    /// <summary>
    /// A namesake with a fraction of the catalogue does not make a household
    /// name ambiguous.
    /// </summary>
    /// <remarks>
    /// The numbers are the live answer for "AC/DC": the band, with 500 albums,
    /// beside a second row of the same name with 4, both pictured. Refusing that
    /// left the single best-known artist in a 222-artist library on Wikidata's
    /// P18 — a wide shot of the Olympic Stadium — while every neighbour had a
    /// face, which is how the bug was reported.
    ///
    /// <c>albums_count</c> is not relevance: it is the one field in a search
    /// result that describes the artist rather than the search. The margin is
    /// what keeps this from becoming the coin flip the test above refuses, and
    /// the order of the results is deliberately the wrong way round here, so a
    /// rule that took the first would fail.
    /// </remarks>
    [Fact]
    public async Task ANamesakeWithAFractionOfTheCatalogueIsNotACompetingAnswer()
    {
        var (portraits, _) = Build(_ => Ok(Rivals(
            ("AC/DC", Image("impostor.jpg"), 4),
            ("AC/DC", Image("the-band.jpg"), 500))));

        var found = await portraits.FindAsync([new ArtistToPicture(Petty, "AC/DC")], Token);

        Assert.EndsWith("the-band.jpg", found[Petty].AbsoluteUri, StringComparison.Ordinal);
    }

    /// <summary>
    /// Two namesakes Qobuz sent no album count for are still a coin flip, and
    /// the result does not depend on which one they listed first.
    /// </summary>
    /// <remarks>
    /// <c>albums_count</c> deserialises to <c>0</c> when the field is absent, so
    /// the arithmetic the margin is written in reads two unknowns as "the leader
    /// is not less than nothing" and takes the first row — deciding by Qobuz's
    /// relevance ranking, which is precisely what
    /// <see cref="SeveralArtistsOfTheSameNameAreRefusedRatherThanGuessedBetween"/>
    /// exists to refuse. Every other test here hands the stub a count, so the
    /// suite could not see it.
    ///
    /// Asserted in both input orders, because the symptom is not "the wrong
    /// artist" but "a different artist depending on the order a search came back
    /// in" — and no response order is anything Qobuz promise.
    ///
    /// The third row is the reason the guard is not simply "refuse when the
    /// runner-up is zero": one album beating an unmeasured row is not a
    /// catalogue dwarfing another, it is a comparison against nothing.
    /// </remarks>
    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 0)]
    public async Task NamesakesWithNoAlbumCountAreRefusedInEitherOrder(int first, int second)
    {
        var (forwards, _) = Build(_ => Ok(Rivals(
            ("Jan Jansen", Image("one.jpg"), first),
            ("Jan Jansen", Image("another.jpg"), second))));

        Assert.Empty(await forwards.FindAsync([new ArtistToPicture(Petty, "Jan Jansen")], Token));

        var (backwards, _) = Build(_ => Ok(Rivals(
            ("Jan Jansen", Image("another.jpg"), second),
            ("Jan Jansen", Image("one.jpg"), first))));

        Assert.Empty(await backwards.FindAsync([new ArtistToPicture(Petty, "Jan Jansen")], Token));
    }

    /// <summary>
    /// A namesake with no picture does not make the one with a picture ambiguous.
    /// </summary>
    /// <remarks>
    /// Counting every exact match rather than every usable one would refuse the
    /// commonest shape there is — a well-known artist beside a homonym Qobuz
    /// carries a row for and no photograph of — and would cost most of the
    /// feature to prevent a choice that was never available.
    /// </remarks>
    [Fact]
    public async Task ANamesakeWithNoPictureIsNotACompetingAnswer()
    {
        var (portraits, _) = Build(_ => Ok(Artists(
            ("Jan Jansen", null),
            ("Jan Jansen", Image("the-only-one.jpg")))));

        var found = await portraits.FindAsync([new ArtistToPicture(Petty, "Jan Jansen")], Token);

        Assert.EndsWith("the-only-one.jpg", found[Petty].AbsoluteUri, StringComparison.Ordinal);
    }

    /// <summary>
    /// The right artist with no picture is not a picture.
    /// </summary>
    /// <remarks>
    /// Measured, three names in forty match exactly and carry no image — Qobuz
    /// hold photographs for the artists they sell records by, not for every
    /// session player. Returning nothing is what lets the caller fall through to
    /// the other source rather than storing a blank.
    /// </remarks>
    [Fact]
    public async Task AnExactMatchWithNoPictureAnswersNothing()
    {
        var (portraits, _) = Build(_ => Ok(Artists(("Tom Petty", null))));

        Assert.Empty(await portraits.FindAsync([new ArtistToPicture(Petty, "Tom Petty")], Token));
    }

    /// <summary>
    /// A picture from anywhere but their CDN is not a picture.
    /// </summary>
    /// <remarks>
    /// The Wikidata source's allowlist, against a URL of the same kind and for
    /// the same reason: it goes straight into an <c>img src</c> on this
    /// application's origin.
    /// </remarks>
    [Theory]
    [InlineData("https://example.test/tracker.png")]
    [InlineData("javascript:alert(1)")]
    [InlineData("http://static.qobuz.com/images/artists/covers/large/x.jpg")]
    public async Task APictureFromAnywhereElseIsIgnored(string url)
    {
        var (portraits, _) = Build(_ => Ok(Artists(("Tom Petty", url))));

        Assert.Empty(await portraits.FindAsync([new ArtistToPicture(Petty, "Tom Petty")], Token));
    }

    /// <summary>
    /// An unconfigured instance asks nothing rather than failing.
    /// </summary>
    /// <remarks>
    /// This source is the preferred one, not the required one. An installation
    /// with no Qobuz subscription must get Wikidata's pictures and a pass that
    /// finishes — not an exception per batch, and not a worklist that never
    /// empties.
    /// </remarks>
    [Fact]
    public async Task WithoutCredentialsItAsksNothingAndAnswersNothing()
    {
        var (portraits, stub) = Build(
            _ => Ok(Artists(("Tom Petty", Image("petty.jpg")))),
            options => options.UserAuthToken = string.Empty);

        Assert.Empty(await portraits.FindAsync([new ArtistToPicture(Petty, "Tom Petty")], Token));
        Assert.Empty(stub.Requests);
    }

    /// <summary>
    /// The two halves of the accent table line up.
    /// </summary>
    /// <remarks>
    /// They are read by index, so a table one character out of step would fold
    /// every accent onto its neighbour's letter — "Dvořák" to "dvorak" is right,
    /// "dvorbk" is not — and would still match enough plain names to look like
    /// it worked. Asserted through the public behaviour rather than by reaching
    /// at the constants: every letter in the table has to survive a round trip
    /// as itself.
    /// </remarks>
    [Theory]
    [InlineData("Antonín Dvořák", "Antonin Dvorak")]
    [InlineData("María Dueñas", "Maria Duenas")]
    [InlineData("Sarah Àlainn", "Sarah Alainn")]
    [InlineData("Thorbjørn Risager", "Thorbjorn Risager")]
    [InlineData("Sinfóníuhljómsveit Íslands", "Sinfoniuhljomsveit Islands")]
    public async Task TheAccentTableFoldsEachLetterOntoItsOwnPlainForm(
        string wanted,
        string returned)
    {
        var (portraits, _) = Build(_ => Ok(Artists((returned, Image("them.jpg")))));

        var found = await portraits.FindAsync([new ArtistToPicture(Petty, wanted)], Token);

        Assert.True(found.ContainsKey(Petty), $"{wanted} did not fold onto {returned}");
    }

    /// <summary>One request per artist, and that is the cost this source is rationed for.</summary>
    [Fact]
    public async Task EveryArtistCostsARequest()
    {
        var (portraits, stub) = Build(_ => Ok(Artists()));

        await portraits.FindAsync(
            [
                new ArtistToPicture(Petty, "Tom Petty"),
                new ArtistToPicture(new Mbid(Guid.CreateVersion7()), "Beth Hart"),
            ],
            Token);

        Assert.Equal(2, stub.Requests.Count);
        Assert.All(
            stub.Requests,
            request => Assert.Contains("artist/search", request.Uri.ToString(), StringComparison.Ordinal));
    }

    public void Dispose()
    {
        foreach (var provider in _providers) provider.Dispose();
    }

    private static string Image(string file) =>
        $"https://static.qobuz.com/images/artists/covers/large/{file}";

    /// <summary>An <c>artist/search</c> answer, in the shape the service returns one.</summary>
    /// <remarks>
    /// One album each, which is the shape every test that is not about the
    /// tie-break wants: equal catalogues are exactly what makes two namesakes a
    /// coin flip. <see cref="Rivals"/> is the one that varies them.
    /// </remarks>
    private static string Artists(params (string Name, string? Image)[] items) =>
        Rivals(items.Select(item => (item.Name, item.Image, 1)).ToArray());

    /// <summary>The same answer, with the album counts that break a tie.</summary>
    private static string Rivals(params (string Name, string? Image, int Albums)[] items)
    {
        var rows = string.Join(
            ",",
            items.Select(item =>
                $$"""
                  { "name": {{Json(item.Name)}}, "id": 1, "albums_count": {{item.Albums}},
                    "image": { "large": {{Json(item.Image)}} } }
                  """));

        return $$"""{ "artists": { "items": [ {{rows}} ] } }""";
    }

    private static string Json(string? value) =>
        value is null ? "null" : System.Text.Json.JsonSerializer.Serialize(value);

    private static HttpResponseMessage Ok(string body) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };

    private (IArtistPortraits Portraits, StubHttpHandler Stub) Build(
        Func<RecordedRequest, HttpResponseMessage> respond,
        Action<QobuzOptions>? configure = null)
    {
        var stub = new StubHttpHandler(respond);
        var services = new ServiceCollection();

        services.AddQobuz(options =>
        {
            options.AppId = "test-app";
            options.UserAuthToken = "test-token";
            options.MinimumRequestInterval = Gate;
            configure?.Invoke(options);
        });

        services.AddHttpClient(QobuzOptions.HttpClientName)
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
            provider.GetRequiredKeyedService<IArtistPortraits>(ArtistPortraitSources.Qobuz),
            stub);
    }
}
