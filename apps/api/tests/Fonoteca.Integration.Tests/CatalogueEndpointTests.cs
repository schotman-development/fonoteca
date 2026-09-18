using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Fonoteca.Api.Endpoints;
using Fonoteca.Api.Library;
using Fonoteca.Data;
using Fonoteca.Domain.Abstractions;
using Fonoteca.Domain.Catalogue;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Fonoteca.Integration.Tests;

/// <summary>
/// The browse endpoints, through the real host against a seeded catalogue.
/// </summary>
/// <remarks>
/// The shape under test is the union that makes a classical track findable: the
/// same recording has to come back under the composer who is on its credit line,
/// under the conductor and orchestra who are linked to the recording, and under
/// the composer again through the work — three different joins answering one
/// question. A test that only seeded a credit line would pass against a query
/// that reads <c>ArtistCredits</c> alone, which is precisely the implementation
/// this exists to rule out.
/// </remarks>
[Collection(nameof(PostgresCollection))]
public sealed class CatalogueEndpointTests(PostgresFixture postgres) : IAsyncLifetime
{
    private string _connectionString = string.Empty;
    private string _root = string.Empty;
    private WebApplicationFactory<Program>? _factory;

    private Seeded _seed = new();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _connectionString = await postgres.CreateDatabaseAsync(Token);
        _root = Directory.CreateTempSubdirectory("fonoteca-catalogue-api-").FullName;

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:Fonoteca", _connectionString);
            builder.UseSetting("Fonoteca:LibraryPath", _root);

            // The background warmer would put its own questions to the providers,
            // out of a thread nothing here waits for.
            builder.UseSetting("Fonoteca:WarmCandidates", "false");
            builder.UseSetting("Fonoteca:MusicBrainzContact", string.Empty);
        });

        // Forces the host to start, which migrates.
        using var warm = _factory.CreateClient();

        _seed = await SeedAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (_factory is not null) await _factory.DisposeAsync();

        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task ArtistsAreListedBySortNameWithTheirTrackCounts()
    {
        using var client = _factory!.CreateClient();

        // `scope=all`, because the union is what this asserts: the default list
        // is the sleeve, and Mozart is not on one.
        var body = await client.GetFromJsonAsync<ArtistListResponse>(
            new Uri("/api/catalogue/artists?scope=all", UriKind.Relative), Token);

        Assert.NotNull(body);
        Assert.Equal(7, body.Total);

        // Sort name, not display name: "Karajan, Herbert von" belongs under K.
        Assert.Equal(
            [
                "Berliner Philharmoniker",
                "Bonamassa, Joe",
                "Hart, Beth",
                "Karajan, Herbert von",
                "Mozart, Wolfgang Amadeus",
                "Satie, Erik",
                "Solti, Georg",
            ],
            body.Items.Select(a => a.SortName));

        var mozart = body.Items.Single(a => a.Id == _seed.Mozart);

        // Both movements, reached through the work rather than a credit line.
        Assert.Equal(2, mozart.TrackCount);
        Assert.Equal("Person", mozart.Type);
    }

    /// <summary>
    /// The default list is a shelf of records, not everyone on them.
    /// </summary>
    /// <remarks>
    /// Measured against the real library, the union reaches 2,860 artists and
    /// 2,157 of them arrive as writers of a work — every songwriter of every pop
    /// song, most with one track. A front page of that is unusable, so the
    /// default is who the albums are by.
    ///
    /// Four artists, and each is here for one of the rule's branches:
    ///
    /// <list type="bullet">
    /// <item><b>Hart and Bonamassa</b> are the release's own credit line, and
    /// between them are what "collaborations" means — one release, two names,
    /// and the release credit is the only source carrying both.</item>
    /// <item><b>Satie</b> would otherwise be lost silently. His file was never
    /// attributed, so there is no release credit to find him by, and on the real
    /// library 987 of 8,411 files are in that position — without the fallback a
    /// refused album takes its artist off this page with nothing to say
    /// why.</item>
    /// <item><b>Solti</b> is the classical case, and the one this endpoint gets
    /// wrong if it reads release credits alone. The sleeve of his Ring names
    /// Wagner; Solti is on the credit line of every recording on it. He is here
    /// on that strength — three of three — and the guest spots on the anthology
    /// are what shows the rule is not merely "credited on something".</item>
    /// <item><b>Karajan</b> is billed on a release the library holds.</item>
    /// </list>
    ///
    /// Mozart and the Berliner Philharmoniker are the exclusions, and both are
    /// the point: a composer reached through the work hop and an orchestra
    /// linked to a recording it is not billed on are exactly the names that turn
    /// a shelf into a phone book. Both still have a page, and both are one
    /// <c>scope=all</c> away.
    /// </remarks>
    [Fact]
    public async Task TheDefaultListIsAlbumArtistsRatherThanEveryoneCredited()
    {
        using var client = _factory!.CreateClient();

        var shelf = await client.GetFromJsonAsync<ArtistListResponse>(
            new Uri("/api/catalogue/artists", UriKind.Relative), Token);

        Assert.NotNull(shelf);

        Assert.Equal(
            [
                "Bonamassa, Joe",
                "Hart, Beth",
                "Karajan, Herbert von",
                "Satie, Erik",
                "Solti, Georg",
            ],
            shelf.Items.Select(a => a.SortName));

        Assert.Equal(5, shelf.Total);

        // The track counts are the artist's whole holdings either way: this
        // narrows who is listed, never what a listed artist has.
        var everyone = await client.GetFromJsonAsync<ArtistListResponse>(
            new Uri("/api/catalogue/artists?scope=all", UriKind.Relative), Token);

        Assert.NotNull(everyone);

        Assert.Equal(
            everyone.Items.Single(a => a.Id == _seed.Karajan).TrackCount,
            shelf.Items.Single(a => a.Id == _seed.Karajan).TrackCount);
    }

    /// <summary>
    /// Following an artist the library holds nothing by has to show them.
    /// </summary>
    /// <remarks>
    /// <b>Three separate cuts in <c>GetArtists</c> would each, on its own, hide
    /// the artist somebody has just followed</b> — and every one of them is
    /// right about the case it was written for. The zero-recording drop exists
    /// because an artist with no tracks is a credit row nothing browses; the
    /// album shelf exists because the default list is the sleeve; and neither
    /// has any way to know this row is there on purpose. Following is a person's
    /// explicit act and outranks both.
    ///
    /// "Nobody At All" is seeded with no recordings at all, which is why the
    /// test above reports seven artists from the nine this seed adds. That makes
    /// it the exact fixture: if following stops beating any one of the three,
    /// the artist vanishes and the button appears to do nothing at all — no
    /// error, no empty state, just a list that did not change.
    ///
    /// The default shelf is asserted and not only <c>scope=following</c>,
    /// because the default shelf is what somebody is looking at when they press
    /// it.
    /// </remarks>
    [Fact]
    public async Task AFollowedArtistIsListedEvenWithNothingOfTheirsInTheLibrary()
    {
        using var client = _factory!.CreateClient();

        var before = await client.GetFromJsonAsync<ArtistListResponse>(
            new Uri("/api/catalogue/artists?scope=all", UriKind.Relative), Token);

        Assert.NotNull(before);
        Assert.DoesNotContain(before.Items, a => a.Id == _seed.Orphan);

        var followed = await client.PostAsJsonAsync(
            new Uri($"/api/catalogue/artists/{_seed.Orphan}/follow", UriKind.Relative),
            new ArtistFollowRequest(true),
            Token);

        Assert.Equal(HttpStatusCode.OK, followed.StatusCode);

        var shelf = await client.GetFromJsonAsync<ArtistListResponse>(
            new Uri("/api/catalogue/artists", UriKind.Relative), Token);

        Assert.NotNull(shelf);

        var orphan = Assert.Single(shelf.Items, a => a.Id == _seed.Orphan);

        Assert.True(orphan.Following);

        // Nothing of theirs is held, and the count says so rather than being
        // suppressed: the row is honest about being empty.
        Assert.Equal(0, orphan.TrackCount);

        var following = await client.GetFromJsonAsync<ArtistListResponse>(
            new Uri("/api/catalogue/artists?scope=following", UriKind.Relative), Token);

        Assert.NotNull(following);
        Assert.Equal([_seed.Orphan], following.Items.Select(a => a.Id));

        // Unfollowing puts them back out of reach, or the flag is write-once and
        // the catalogue grows artists nobody can remove.
        var unfollowed = await client.PostAsJsonAsync(
            new Uri($"/api/catalogue/artists/{_seed.Orphan}/follow", UriKind.Relative),
            new ArtistFollowRequest(false),
            Token);

        Assert.Equal(HttpStatusCode.OK, unfollowed.StatusCode);

        var after = await client.GetFromJsonAsync<ArtistListResponse>(
            new Uri("/api/catalogue/artists", UriKind.Relative), Token);

        Assert.NotNull(after);
        Assert.DoesNotContain(after.Items, a => a.Id == _seed.Orphan);
    }

    /// <summary>
    /// Being on every track of an album is what makes somebody its artist; being
    /// on one of them is a guest.
    /// </summary>
    /// <remarks>
    /// The rule that recovers Solti has to not recover the anthology's guests,
    /// and the seed is built so that a rule reading "credited on a track of a
    /// release" cannot tell them apart: both are recording credits on a release
    /// billed to somebody else. Three of three against one of three is the only
    /// thing separating them.
    ///
    /// The count is the tracks the library <i>holds</i>, not the ones the
    /// release prints — a box set held one disc of is still an album to whoever
    /// is browsing it.
    /// </remarks>
    [Fact]
    public async Task AnArtistOnEveryTrackOfAnAlbumIsItsArtistAndAGuestIsNot()
    {
        using var client = _factory!.CreateClient();

        var shelf = await client.GetFromJsonAsync<ArtistListResponse>(
            new Uri("/api/catalogue/artists", UriKind.Relative), Token);

        Assert.NotNull(shelf);

        // Three recordings of the Ring, all three credited to him, and no
        // release credit anywhere in the catalogue.
        var solti = shelf.Items.Single(a => a.Id == _seed.Solti);
        Assert.Equal(3, solti.TrackCount);

        // Wagner is on the sleeve and on no recording, so he is on the shelf's
        // set and off the page — the same rule that has always excluded an
        // artist with nothing to show.
        Assert.DoesNotContain(shelf.Items, a => a.Name == "Richard Wagner");

        // And the composer who is only ever a composer stays out.
        Assert.DoesNotContain(shelf.Items, a => a.Id == _seed.Mozart);
    }

    /// <summary>
    /// An orchestra with no file left in the library is not an artist you can
    /// browse to — the page would be empty and the row is noise in a list of
    /// hundreds.
    /// </summary>
    [Fact]
    public async Task AnArtistWithNoTracksInTheLibraryIsNotListed()
    {
        using var client = _factory!.CreateClient();

        // `scope=all`, or the assertion passes on the strength of the shelf
        // filter and would go on passing if the rule it names broke.
        var body = await client.GetFromJsonAsync<ArtistListResponse>(
            new Uri("/api/catalogue/artists?scope=all", UriKind.Relative), Token);

        Assert.NotNull(body);
        Assert.DoesNotContain(body.Items, a => a.Id == _seed.Orphan);
    }

    /// <summary>
    /// An artist with no photograph sends no picture, rather than an album.
    /// </summary>
    /// <remarks>
    /// <b>An album sleeve used to travel here as the fallback, and it was
    /// removed because of how it read on the page rather than because the
    /// derivation was wrong.</b> The artists reaching a fallback are by
    /// definition the ones no picture source has heard of, which is very nearly
    /// the same set as the artists who are not on the front of their own
    /// sleeves — so in practice the tile with a face was a household name and
    /// the tile with a cover was a conductor, a session player or a guest
    /// wearing somebody else's record. A monogram says "no picture"; a sleeve
    /// says "this is them", and is wrong.
    ///
    /// Asserted on the list and the detail page together for the reason their
    /// track counts are: a tile and the page it opens disagreeing about one
    /// artist is the failure somebody notices immediately, having just clicked
    /// the first.
    ///
    /// The seed still holds the shape the old rule was measured against —
    /// Bonamassa billed on <c>Seesaw</c> and guesting on a bigger anthology — so
    /// a reinstated fallback would have something to be caught picking.
    /// </remarks>
    [Fact]
    public async Task AnArtistWithNoPhotographSendsNoPictureRatherThanAnAlbum()
    {
        using var client = _factory!.CreateClient();

        var body = await client.GetFromJsonAsync<ArtistListResponse>(
            new Uri("/api/catalogue/artists", UriKind.Relative), Token);

        Assert.NotNull(body);

        // Billed on one album, guesting on a bigger one, and photographed by
        // nobody: the row carries no picture of any kind.
        var bonamassa = body.Items.Single(a => a.Id == _seed.Bonamassa);
        Assert.Null(bonamassa.Portrait);

        var detail = await client.GetFromJsonAsync<ArtistDetailResponse>(
            new Uri($"/api/catalogue/artists/{_seed.Bonamassa}", UriKind.Relative), Token);

        Assert.NotNull(detail);
        Assert.Null(detail.Artist.Portrait);

        // And the wire carries no album for an artist at all — the check that
        // fails if the field comes back, rather than merely if a client stops
        // reading it.
        Assert.DoesNotContain(
            "\"cover\"",
            await client.GetStringAsync(
                new Uri("/api/catalogue/artists", UriKind.Relative), Token),
            StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The photograph is the artist's picture, and it is the only one.
    /// </summary>
    /// <remarks>
    /// Four sources stand behind it — two searched by name and two looked up by
    /// MusicBrainz id — which is what made dropping the album fallback
    /// affordable rather than merely correct.
    ///
    /// Asserted on the list and the detail page together, for the reason above.
    /// </remarks>
    [Fact]
    public async Task AnArtistsPhotographIsTheSameOnTheListAndTheDetailPage()
    {
        using var client = _factory!.CreateClient();

        var list = await client.GetFromJsonAsync<ArtistListResponse>(
            new Uri("/api/catalogue/artists", UriKind.Relative), Token);

        Assert.NotNull(list);

        var hart = list.Items.Single(a => a.Id == _seed.Hart);

        Assert.Equal(
            "https://commons.wikimedia.org/wiki/Special:FilePath/Beth%20Hart.jpg",
            hart.Portrait);

        var detail = await client.GetFromJsonAsync<ArtistDetailResponse>(
            new Uri($"/api/catalogue/artists/{_seed.Hart}", UriKind.Relative), Token);

        Assert.NotNull(detail);
        Assert.Equal(hart.Portrait, detail.Artist.Portrait);

        // Nobody has photographed Joe Bonamassa as far as this catalogue knows,
        // which is null rather than an empty string — the difference between
        // "no picture" and "a picture at no address".
        Assert.Null(list.Items.Single(a => a.Id == _seed.Bonamassa).Portrait);
    }

    [Fact]
    public async Task TheFilterMatchesAnywhereInTheNameAndIgnoresCase()
    {
        using var client = _factory!.CreateClient();

        var body = await client.GetFromJsonAsync<ArtistListResponse>(
            new Uri("/api/catalogue/artists?query=BONAM", UriKind.Relative), Token);

        Assert.NotNull(body);
        Assert.Equal("Joe Bonamassa", Assert.Single(body.Items).Name);
    }

    /// <summary>
    /// Without escaping, a search for "%" matches everything — not a security
    /// hole, since the value is still parameterised, but a search box that lies.
    /// </summary>
    [Fact]
    public async Task WildcardsInTheFilterAreLiteral()
    {
        using var client = _factory!.CreateClient();

        var body = await client.GetFromJsonAsync<ArtistListResponse>(
            new Uri("/api/catalogue/artists?query=%25", UriKind.Relative), Token);

        Assert.NotNull(body);
        Assert.Empty(body.Items);
        Assert.Equal(0, body.Total);
    }

    [Fact]
    public async Task PagingReportsTheTotalBeforeItPages()
    {
        using var client = _factory!.CreateClient();

        var body = await client.GetFromJsonAsync<ArtistListResponse>(
            new Uri("/api/catalogue/artists?scope=all&skip=1&take=2", UriKind.Relative), Token);

        Assert.NotNull(body);

        // "showing 2 of 7", answerable without a second request.
        Assert.Equal(7, body.Total);
        Assert.Equal(["Bonamassa, Joe", "Hart, Beth"], body.Items.Select(a => a.SortName));
    }

    /// <summary>
    /// The case the whole feature exists for. One recording, three artists, and
    /// only one of them anywhere near the credit line.
    /// </summary>
    [Fact]
    public async Task AClassicalTrackIsReachableUnderComposerConductorAndOrchestra()
    {
        using var client = _factory!.CreateClient();

        var composer = await ArtistAsync(client, _seed.Mozart);
        var conductor = await ArtistAsync(client, _seed.Karajan);
        var orchestra = await ArtistAsync(client, _seed.Berliner);

        Assert.Contains(composer.Tracks, t => t.RecordingId == _seed.FirstMovement);
        Assert.Contains(conductor.Tracks, t => t.RecordingId == _seed.FirstMovement);
        Assert.Contains(orchestra.Tracks, t => t.RecordingId == _seed.FirstMovement);

        // And each says why it is there, so the page is legible rather than
        // mysterious.
        Assert.Equal(
            ["composer"],
            composer.Tracks.Single(t => t.RecordingId == _seed.FirstMovement).Roles);

        Assert.Equal(
            ["conductor"],
            conductor.Tracks.Single(t => t.RecordingId == _seed.FirstMovement).Roles);

        Assert.Equal(
            ["ensemble"],
            orchestra.Tracks.Single(t => t.RecordingId == _seed.FirstMovement).Roles);
    }

    [Fact]
    public async Task ACollaborationAppearsUnderBothArtists()
    {
        using var client = _factory!.CreateClient();

        var hart = await ArtistAsync(client, _seed.Hart);
        var bonamassa = await ArtistAsync(client, _seed.Bonamassa);

        var forHart = Assert.Single(hart.Tracks);

        // Named rather than singled: he is also on the anthology, which is the
        // shape the artist picture rule exists to see past.
        var forBonamassa = bonamassa.Tracks.Single(t => t.RecordingId == _seed.Duet);

        Assert.Equal(_seed.Duet, forHart.RecordingId);
        Assert.Equal(["billed"], forHart.Roles);
        Assert.Equal(["billed"], forBonamassa.Roles);
    }

    [Fact]
    public async Task ATrackCarriesEveryFileHoldingIt()
    {
        using var client = _factory!.CreateClient();

        var hart = await ArtistAsync(client, _seed.Hart);
        var track = Assert.Single(hart.Tracks);

        // The same recording in two encodings. Not a duplicate — the split that
        // makes that expressible is the whole reason Recording and MediaFile are
        // different tables.
        Assert.Equal(
            ["Hart & Bonamassa/Seesaw/01 - Nutbush.flac", "Hart & Bonamassa/Seesaw/01 - Nutbush.mp3"],
            track.Files.Select(f => f.Path));

        // The folder is still returned, but it is no longer the answer: the
        // track now names the release attribution decided on.
        Assert.Equal("Hart & Bonamassa/Seesaw", track.Folder);
        Assert.Equal("Seesaw", track.Album?.Title);
        Assert.Equal(2013, track.Album?.Year);

        Assert.Equal("4:12", track.Duration);
        Assert.Null(track.WorkTitle);
    }

    [Fact]
    public async Task AWorksTitleTravelsWithItsTracks()
    {
        using var client = _factory!.CreateClient();

        var karajan = await ArtistAsync(client, _seed.Karajan);

        Assert.All(
            karajan.Tracks,
            track => Assert.Equal("Symphony no. 40 in G minor, K. 550", track.WorkTitle));
    }

    [Fact]
    public async Task AnUnknownArtistIsAProblemDocumentRatherThanAnEmptyPage()
    {
        using var client = _factory!.CreateClient();

        using var response = await client.GetAsync(
            new Uri($"/api/catalogue/artists/{Guid.CreateVersion7()}", UriKind.Relative), Token);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Token));

        Assert.Contains(
            "no artist",
            document.RootElement.GetProperty("detail").GetString() ?? "",
            StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Counts as JSON numbers, not strings. A regression here retypes every
    /// count in the generated client as <c>string | number</c> and arithmetic on
    /// one stops compiling — see the NumberHandling note in Program.cs.
    /// </summary>
    [Fact]
    public async Task CountsAndSizesSerialiseAsNumbers()
    {
        using var client = _factory!.CreateClient();

        using var response = await client.GetAsync(
            new Uri("/api/catalogue/artists", UriKind.Relative), Token);

        using var list = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Token));

        Assert.Equal(JsonValueKind.Number, list.RootElement.GetProperty("total").ValueKind);
        Assert.Equal(
            JsonValueKind.Number,
            list.RootElement.GetProperty("items")[0].GetProperty("trackCount").ValueKind);

        using var detail = await client.GetAsync(
            new Uri($"/api/catalogue/artists/{_seed.Hart}", UriKind.Relative), Token);

        using var artist = JsonDocument.Parse(await detail.Content.ReadAsStringAsync(Token));

        Assert.Equal(
            JsonValueKind.Number,
            artist.RootElement.GetProperty("tracks")[0].GetProperty("files")[0]
                .GetProperty("sizeBytes").ValueKind);
    }

    /// <summary>
    /// The list's count and the detail page's tracks are two copies of one rule,
    /// because EF cannot express it once — see the note on
    /// <c>CatalogueEndpoints.TracksOf</c>. This is what stops them drifting, and
    /// the way they would drift is an artist whose row says twelve and whose page
    /// shows nine.
    /// </summary>
    [Fact]
    public async Task TheListAndTheDetailPageAgree()
    {
        using var client = _factory!.CreateClient();

        // `scope=all`: the two artists this could drift on — Mozart through the
        // work hop and the Berliner through a recording relationship — are
        // exactly the two the shelf leaves out, so the default list would
        // exercise only the credit-line branch of the rule under test.
        var list = await client.GetFromJsonAsync<ArtistListResponse>(
            new Uri("/api/catalogue/artists?scope=all", UriKind.Relative), Token);

        Assert.NotNull(list);
        Assert.NotEmpty(list.Items);

        foreach (var summary in list.Items)
        {
            var detail = await ArtistAsync(client, summary.Id);

            Assert.Equal(summary.TrackCount, detail.Tracks.Count);
            Assert.Equal(summary.TrackCount, detail.Artist.TrackCount);
        }
    }

    private static async Task<ArtistDetailResponse> ArtistAsync(HttpClient client, Guid id)
    {
        var body = await client.GetFromJsonAsync<ArtistDetailResponse>(
            new Uri($"/api/catalogue/artists/{id}", UriKind.Relative), Token);

        Assert.NotNull(body);
        return body;
    }

    /// <summary>
    /// A catalogue in the shape enrichment leaves behind, written directly.
    /// </summary>
    /// <remarks>
    /// Not by running the enrichment pass: that would make a failure in either
    /// piece fail both suites, and what these tests are about is the query, not
    /// how the rows got there.
    /// </remarks>

    [Fact]
    public async Task AReleaseListsWhatTheLibraryHoldsOfIt()
    {
        using var client = _factory!.CreateClient();

        var list = await client.GetFromJsonAsync<ReleaseListResponse>(
            new Uri("/api/catalogue/releases", UriKind.Relative), Token);

        Assert.NotNull(list);

        var release = list.Items.Single(item => item.Title == "Seesaw");

        // The Cover Art Archive is keyed on this, and it is the only way the
        // browser can ask for a sleeve.
        Assert.Equal(_seed.SeesawMbid, release.Mbid);

        // The release's own billing line, rebuilt with its join phrase intact.
        Assert.Equal("Beth Hart & Joe Bonamassa", release.Artist);

        Assert.Equal(2013, release.Year);
        Assert.Equal("Digital Media", release.Formats);

        // Two of three tracks held, across three files: one track is held in two
        // encodings. Held counts tracks, so an album ripped twice at two thirds
        // of its length does not read as complete.
        Assert.Equal(3, release.TrackCount);
        Assert.Equal(2, release.Held);
        Assert.Equal(3, release.Files);
        Assert.Equal("Attributed", release.Certainty);
    }

    /// <summary>
    /// A track the library does not hold is a gap on the page, not an absence
    /// from it — which is the whole reason the full track list is persisted.
    /// </summary>
    [Fact]
    public async Task AReleasePageShowsTheTracksTheLibraryIsMissing()
    {
        using var client = _factory!.CreateClient();

        var release = await client.GetFromJsonAsync<ReleaseDetailResponse>(
            new Uri($"/api/catalogue/releases/{_seed.Seesaw}", UriKind.Relative), Token);

        Assert.NotNull(release);
        Assert.Equal(3, release.Tracks.Count);

        var held = release.Tracks[0];
        Assert.True(held.Held);
        Assert.Equal("Nutbush City Limits", held.Title);
        Assert.Equal("4:12", held.Duration);
        Assert.Equal(2, held.Files.Count);

        var missing = release.Tracks[2];
        Assert.False(missing.Held);
        Assert.Equal("I'd Rather Go Blind", missing.Title);
        Assert.Empty(missing.Files);
    }

    /// <summary>
    /// The heading four movements sit under, carried on every track row.
    /// </summary>
    /// <remarks>
    /// Read off the recording's work, one hop from the track — which is a join
    /// EF has to translate rather than a column, so a silent null here would
    /// look on screen exactly like a pop album and nothing would notice.
    /// </remarks>
    [Fact]
    public async Task ATrackListCarriesTheWorkItsTracksPerform()
    {
        using var client = _factory!.CreateClient();

        var symphony = await client.GetFromJsonAsync<ReleaseDetailResponse>(
            new Uri($"/api/catalogue/releases/{_seed.Symphony}", UriKind.Relative), Token);

        Assert.NotNull(symphony);

        Assert.All(
            symphony.Tracks,
            track => Assert.Equal("Symphony no. 40 in G minor, K. 550", track.WorkTitle));

        // And an album that performs no work says so, rather than repeating the
        // track title back as one.
        var seesaw = await client.GetFromJsonAsync<ReleaseDetailResponse>(
            new Uri($"/api/catalogue/releases/{_seed.Seesaw}", UriKind.Relative), Token);

        Assert.NotNull(seesaw);
        Assert.All(seesaw.Tracks, track => Assert.Null(track.WorkTitle));
    }

    /// <summary>
    /// The album list's four orders, each one a different answer.
    /// </summary>
    /// <remarks>
    /// Sorted in SQL because the endpoint pages in SQL, and every case here asks
    /// for <b>two of the four</b> deliberately. Asking for the whole list would
    /// pass identically against an implementation that sorted only the page it
    /// had already taken — which is the bug worth pinning, since a library of
    /// five hundred albums pages for real. The seed is chosen so all four
    /// orders name a different first two.
    /// </remarks>
    [Fact]
    public async Task AlbumsSortByTitleArtistYearOrArrival()
    {
        using var client = _factory!.CreateClient();

        Assert.Equal(
            ["Blues Summit 100", "Mozart: Symphony no. 40"],
            await TitlesAsync(client, sort: null));

        // The first billed name, which is the one the assembled credit line
        // starts with. The anthology is billed to nobody and sorts last, so a
        // page taken by title and then reordered would not produce this.
        Assert.Equal(
            ["Seesaw", "Mozart: Symphony no. 40"],
            await TitlesAsync(client, sort: "artist"));

        Assert.Equal(
            ["Blues Summit 100", "Seesaw"],
            await TitlesAsync(client, sort: "year"));

        // The seed adds its files in release order, so the last two seeded lead
        // — which is a different pair again, and in particular not the pair the
        // year gives: the Ring is the oldest record here and the newest arrival.
        Assert.Equal(
            ["Wagner: Der Ring des Nibelungen", "Blues Summit 100"],
            await TitlesAsync(client, sort: "added"));

        // An order nobody asked for is the default, not an error page.
        Assert.Equal(
            ["Blues Summit 100", "Mozart: Symphony no. 40"],
            await TitlesAsync(client, sort: "nonsense"));
    }

    private static async Task<IReadOnlyList<string>> TitlesAsync(HttpClient client, string? sort)
    {
        var body = await client.GetFromJsonAsync<ReleaseListResponse>(
            new Uri(
                sort is null
                    ? "/api/catalogue/releases?take=2"
                    : $"/api/catalogue/releases?take=2&sort={sort}",
                UriKind.Relative),
            Token);

        Assert.NotNull(body);

        // The page is cut, the total is not — so a sort cannot quietly shrink
        // the list it is ordering.
        Assert.Equal(4, body.Total);

        return [.. body.Items.Select(item => item.Title)];
    }

    /// <summary>
    /// Who this library is actually about, which is rarely who you would guess.
    /// </summary>
    /// <remarks>
    /// The count is not a column — it is the size of the recording set the
    /// endpoint assembles — so this sort happens in memory, and the tie between
    /// three artists holding two tracks each is what proves it stayed stable and
    /// kept PostgreSQL's collation order underneath.
    /// </remarks>
    [Fact]
    public async Task ArtistsSortByHoldingsWithAlphabeticalTies()
    {
        using var client = _factory!.CreateClient();

        var body = await client.GetFromJsonAsync<ArtistListResponse>(
            new Uri("/api/catalogue/artists?scope=all&sort=tracks", UriKind.Relative), Token);

        Assert.NotNull(body);

        Assert.Equal(
            [
                "Bonamassa, Joe",
                "Solti, Georg",
                "Berliner Philharmoniker",
                "Karajan, Herbert von",
                "Mozart, Wolfgang Amadeus",
                "Hart, Beth",
                "Satie, Erik",
            ],
            body.Items.Select(a => a.SortName));

        Assert.Equal([4, 3, 2, 2, 2, 1, 1], body.Items.Select(a => a.TrackCount));

        // An order nobody asked for is the default here too.
        var nonsense = await client.GetFromJsonAsync<ArtistListResponse>(
            new Uri("/api/catalogue/artists?scope=all&sort=nonsense", UriKind.Relative), Token);

        Assert.NotNull(nonsense);
        Assert.Equal("Berliner Philharmoniker", nonsense.Items[0].SortName);
    }

    [Fact]
    public async Task AnUnknownReleaseIsAProblemDocument()
    {
        using var client = _factory!.CreateClient();

        using var response = await client.GetAsync(
            new Uri($"/api/catalogue/releases/{Guid.CreateVersion7()}", UriKind.Relative), Token);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <summary>
    /// The folders are read exactly here and nowhere else, and they disagree.
    /// </summary>
    /// <remarks>
    /// The seed puts two encodings of one track in two directories, which is a
    /// release spanning folders — the shape a report reading only the folders
    /// could never notice, because each folder on its own looks consistent.
    /// </remarks>
    [Fact]
    public async Task TheReportNamesWhereTheFoldersAndTheCatalogueDisagree()
    {
        using var client = _factory!.CreateClient();

        var report = await client.GetFromJsonAsync<AttributionReportResponse>(
            new Uri("/api/catalogue/attribution", UriKind.Relative), Token);

        Assert.NotNull(report);

        // Five folders, each internally consistent, each naming one release.
        Assert.Equal(5, report.Folders);
        Assert.Equal(5, report.FoldersAgreeing);
        Assert.Empty(report.FoldersSplit);

        var spanning = Assert.Single(report.ReleasesSpanningFolders);
        Assert.Equal("Seesaw", spanning.Title);
        Assert.Equal(2, spanning.Folders.Count);

        // And the release is short a track, with the format beside it so the gap
        // can be judged rather than merely counted.
        var incomplete = Assert.Single(report.Incomplete);
        Assert.Equal(2, incomplete.Held);
        Assert.Equal(3, incomplete.TrackCount);
    }

    private async Task<Seeded> SeedAsync()
    {
        await using var db = PostgresFixture.CreateContext(_connectionString);

        var mozart = Artist("Wolfgang Amadeus Mozart", "Mozart, Wolfgang Amadeus", "Person");
        var satie = Artist("Erik Satie", "Satie, Erik", "Person");
        var karajan = Artist("Herbert von Karajan", "Karajan, Herbert von", "Person");
        var berliner = Artist("Berliner Philharmoniker", "Berliner Philharmoniker", "Orchestra");
        var hart = Artist("Beth Hart", "Hart, Beth", "Person");
        hart.PortraitUrl = "https://commons.wikimedia.org/wiki/Special:FilePath/Beth%20Hart.jpg";
        var bonamassa = Artist("Joe Bonamassa", "Bonamassa, Joe", "Person");
        var solti = Artist("Sir Georg Solti", "Solti, Georg", "Person");
        var wagner = Artist("Richard Wagner", "Wagner, Richard", "Person");
        var orphan = Artist("Nobody At All", "Nobody At All", "Group");

        db.Artists.AddRange(mozart, satie, karajan, berliner, hart, bonamassa, solti, wagner, orphan);

        var work = new Work
        {
            Id = WorkId.New(),
            Title = "Symphony no. 40 in G minor, K. 550",
            Type = "Symphony",
        };

        db.Works.Add(work);

        // The composer, through the work — which is where MusicBrainz puts them
        // and the join a credit-line-only query cannot make.
        db.Relationships.Add(Link(mozart, "composer", work: work));

        var first = Movement(db, work, "I. Molto allegro", karajan, berliner);
        var second = Movement(db, work, "II. Andante", karajan, berliner);

        // A release whose every track performs one work, which is what the track
        // list groups on. Nothing else in this seed has a work *and* a release,
        // and the two together are the whole classical case.
        var symphonyGroup = new ReleaseGroup
        {
            Id = ReleaseGroupId.New(),
            Title = "Mozart: Symphony no. 40",
            Mbid = new Mbid(Guid.CreateVersion7()),
            PrimaryType = "Album",
        };

        var symphony = new Release
        {
            Id = ReleaseId.New(),
            Title = "Mozart: Symphony no. 40",
            Mbid = new Mbid(Guid.CreateVersion7()),
            ReleaseGroupId = symphonyGroup.Id,
            Released = new ReleaseDate(1985, null, null),
            Status = "Official",
            MediumFormats = "CD",
            TrackCount = 2,
            DiscCount = 1,
        };

        db.ReleaseGroups.Add(symphonyGroup);
        db.Releases.Add(symphony);
        db.ArtistCredits.Add(ReleaseCredit(karajan, symphony, 0, null));

        var movementOne = TrackOn(db, symphony, first, 1, "I. Molto allegro", 437);
        var movementTwo = TrackOn(db, symphony, second, 2, "II. Andante", 437);

        db.MediaFiles.Add(Attributed(
            File("Karajan/Mozart 40/01 - Molto allegro.flac", first),
            symphony,
            symphonyGroup,
            movementOne));

        db.MediaFiles.Add(Attributed(
            File("Karajan/Mozart 40/02 - Andante.flac", second),
            symphony,
            symphonyGroup,
            movementTwo));

        var duet = new Recording
        {
            Id = RecordingId.New(),
            Title = "Nutbush City Limits",
            Mbid = new Mbid(Guid.CreateVersion7()),
            Duration = TimeSpan.FromSeconds(252),
        };

        db.Recordings.Add(duet);
        db.ArtistCredits.Add(Credit(hart, duet, 0, " & "));
        db.ArtistCredits.Add(Credit(bonamassa, duet, 1, null));

        var elsewhere = new Recording
        {
            Id = RecordingId.New(),
            Title = "Close to My Fire",
            Mbid = new Mbid(Guid.CreateVersion7()),
            Duration = TimeSpan.FromSeconds(280),
        };

        var missing = new Recording
        {
            Id = RecordingId.New(),
            Title = "I'd Rather Go Blind",
            Mbid = new Mbid(Guid.CreateVersion7()),
            Duration = TimeSpan.FromSeconds(300),
        };

        db.Recordings.AddRange(elsewhere, missing);

        var group = new ReleaseGroup
        {
            Id = ReleaseGroupId.New(),
            Title = "Seesaw",
            Mbid = new Mbid(Guid.CreateVersion7()),
            PrimaryType = "Album",
        };

        var seesaw = new Release
        {
            Id = ReleaseId.New(),
            Title = "Seesaw",
            Mbid = new Mbid(Guid.CreateVersion7()),
            ReleaseGroupId = group.Id,
            Released = new ReleaseDate(2013, null, null),
            Country = "XW",
            Status = "Official",
            MediumFormats = "Digital Media",
            TrackCount = 3,
            DiscCount = 1,
        };

        db.ReleaseGroups.Add(group);
        db.Releases.Add(seesaw);
        db.ArtistCredits.Add(ReleaseCredit(hart, seesaw, 0, " & "));
        db.ArtistCredits.Add(ReleaseCredit(bonamassa, seesaw, 1, null));

        var held = TrackOn(db, seesaw, duet, 1, "Nutbush City Limits", 252);
        var strayTrack = TrackOn(db, seesaw, elsewhere, 2, "Close to My Fire", 280);

        // A track of the release the library does not hold, so "what am I
        // missing" has something to answer with.
        TrackOn(db, seesaw, missing, 3, "I'd Rather Go Blind", 300);

        // The same recording in two encodings, in one folder.
        db.MediaFiles.Add(Attributed(File("Hart & Bonamassa/Seesaw/01 - Nutbush.flac", duet), seesaw, group, held));
        db.MediaFiles.Add(Attributed(File("Hart & Bonamassa/Seesaw/01 - Nutbush.mp3", duet), seesaw, group, held));

        // And one track of the same release filed somewhere else entirely, which
        // is the disagreement the report exists to surface — a shape no report
        // reading only folders could notice, since each folder looks consistent.
        db.MediaFiles.Add(Attributed(
            File("Singles/Close to My Fire.flac", elsewhere), seesaw, group, strayTrack));

        // An anthology holding more of Bonamassa's recordings than his own album
        // does, and billed to nobody. It is what a picture for an artist has to
        // see past: the greedy answer here is somebody else's compilation.
        var anthologyGroup = new ReleaseGroup
        {
            Id = ReleaseGroupId.New(),
            Title = "Blues Summit 100",
            Mbid = new Mbid(Guid.CreateVersion7()),
            PrimaryType = "Album",
        };

        var anthology = new Release
        {
            Id = ReleaseId.New(),
            Title = "Blues Summit 100",
            Mbid = new Mbid(Guid.CreateVersion7()),
            ReleaseGroupId = anthologyGroup.Id,
            Released = new ReleaseDate(2019, null, null),
            Status = "Official",
            MediumFormats = "CD",
            TrackCount = 3,
            DiscCount = 1,
        };

        db.ReleaseGroups.Add(anthologyGroup);
        db.Releases.Add(anthology);

        for (var n = 1; n <= 3; n++)
        {
            var guest = new Recording
            {
                Id = RecordingId.New(),
                Title = $"Guest spot {n}",
                Mbid = new Mbid(Guid.CreateVersion7()),
                Duration = TimeSpan.FromSeconds(200),
            };

            db.Recordings.Add(guest);
            db.ArtistCredits.Add(Credit(bonamassa, guest, 0, null));

            var slot = TrackOn(db, anthology, guest, n, $"Guest spot {n}", 200);

            db.MediaFiles.Add(Attributed(
                File($"Various/Blues Summit 100/0{n} - Guest spot {n}.flac", guest),
                anthology,
                anthologyGroup,
                slot));
        }

        // Held, but on nothing the attribution pass has placed. There is no
        // release to draw, so the card falls back to a monogram — and sorted
        // last, so the paging above still pages over the same names.
        var gymnopedie = new Recording
        {
            Id = RecordingId.New(),
            Title = "Gymnopédie no. 1",
            Mbid = new Mbid(Guid.CreateVersion7()),
            Duration = TimeSpan.FromSeconds(212),
        };

        db.Recordings.Add(gymnopedie);
        db.ArtistCredits.Add(Credit(satie, gymnopedie, 0, null));
        db.MediaFiles.Add(File("Satie/Gymnopedies/01 - Gymnopedie no. 1.flac", gymnopedie));

        // The shape the release credit line alone gets wrong, measured on the real
        // library and reproduced here at three tracks instead of 178: the sleeve
        // names Wagner and nobody else, while the conductor is on the credit line
        // of every recording on it. Read from the release, the largest work in
        // the library browses under a man who died in 1883 and under nobody who
        // played it.
        //
        // Wagner has no track of his own here — no work relation, no recording
        // credit — so he is on the shelf's list and filtered off the page by the
        // same rule that has always excluded an artist with nothing to show.
        var ringGroup = new ReleaseGroup
        {
            Id = ReleaseGroupId.New(),
            Title = "Wagner: Der Ring des Nibelungen",
            Mbid = new Mbid(Guid.CreateVersion7()),
            PrimaryType = "Album",
        };

        var ring = new Release
        {
            Id = ReleaseId.New(),
            Title = "Wagner: Der Ring des Nibelungen",
            Mbid = new Mbid(Guid.CreateVersion7()),
            ReleaseGroupId = ringGroup.Id,
            Released = new ReleaseDate(1997, null, null),
            Status = "Official",
            MediumFormats = "CD",
            TrackCount = 3,
            DiscCount = 1,
        };

        db.ReleaseGroups.Add(ringGroup);
        db.Releases.Add(ring);
        db.ArtistCredits.Add(ReleaseCredit(wagner, ring, 0, null));

        for (var n = 1; n <= 3; n++)
        {
            var scene = new Recording
            {
                Id = RecordingId.New(),
                Title = $"Das Rheingold: Scene {n}",
                Mbid = new Mbid(Guid.CreateVersion7()),
                Duration = TimeSpan.FromSeconds(600),
            };

            db.Recordings.Add(scene);
            db.ArtistCredits.Add(Credit(solti, scene, 0, null));

            var slot = TrackOn(db, ring, scene, n, $"Das Rheingold: Scene {n}", 600);

            db.MediaFiles.Add(Attributed(
                File($"Wagner/Der Ring des Nibelungen/0{n} - Das Rheingold Scene {n}.flac", scene),
                ring,
                ringGroup,
                slot));
        }

        // Credited on a recording the library does not hold. Enrichment cannot
        // produce this, but a rescan that unlinks every file of a recording can.
        var absent = new Recording { Id = RecordingId.New(), Title = "Never Ripped" };
        db.Recordings.Add(absent);
        db.ArtistCredits.Add(Credit(orphan, absent, 0, null));

        await db.SaveChangesAsync(Token);

        return new Seeded
        {
            Mozart = mozart.Id.Value,
            Satie = satie.Id.Value,
            Karajan = karajan.Id.Value,
            Berliner = berliner.Id.Value,
            Hart = hart.Id.Value,
            Bonamassa = bonamassa.Id.Value,
            Solti = solti.Id.Value,
            Wagner = wagner.Id.Value,
            Orphan = orphan.Id.Value,
            FirstMovement = first.Id.Value,
            Duet = duet.Id.Value,
            Seesaw = seesaw.Id.Value,
            Symphony = symphony.Id.Value,
            SeesawMbid = seesaw.Mbid!.Value.Value,
            AnthologyMbid = anthology.Mbid!.Value.Value,
        };
    }

    private static Track TrackOn(
        FonotecaDbContext db,
        Release release,
        Recording recording,
        int position,
        string title,
        int seconds)
    {
        var track = new Track
        {
            Id = TrackId.New(),
            ReleaseId = release.Id,
            RecordingId = recording.Id,
            Position = position,
            DiscNumber = 1,
            Number = position.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Title = title,
            Length = TimeSpan.FromSeconds(seconds),
        };

        db.Tracks.Add(track);
        return track;
    }

    private static MediaFile Attributed(MediaFile file, Release release, ReleaseGroup group, Track track)
    {
        file.ReleaseId = release.Id;
        file.ReleaseGroupId = group.Id;
        file.TrackId = track.Id;
        file.AttributionOutcome = ReleaseAttributionOutcome.Attributed;
        return file;
    }

    private static ArtistCredit ReleaseCredit(Artist artist, Release release, int position, string? join) =>
        new()
        {
            Id = Guid.CreateVersion7(),
            ArtistId = artist.Id,
            ReleaseId = release.Id,
            Position = position,
            JoinPhrase = join,
            CreditedAs = artist.Name,
        };

    private static Recording Movement(
        FonotecaDbContext db,
        Work work,
        string title,
        Artist conductor,
        Artist orchestra)
    {
        var recording = new Recording
        {
            Id = RecordingId.New(),
            Title = $"Symphony No. 40 in G minor, K. 550: {title}",
            Mbid = new Mbid(Guid.CreateVersion7()),
            Duration = TimeSpan.FromSeconds(437),
            WorkId = work.Id,
        };

        db.Recordings.Add(recording);
        db.Relationships.Add(Link(conductor, "conductor", recording: recording));
        db.Relationships.Add(Link(orchestra, "ensemble", recording: recording));

        return recording;
    }

    private static Artist Artist(string name, string sortName, string type) => new()
    {
        Id = ArtistId.New(),
        Name = name,
        SortName = sortName,
        Type = type,
        Mbid = new Mbid(Guid.CreateVersion7()),
    };

    private static Relationship Link(
        Artist artist,
        string type,
        Recording? recording = null,
        Work? work = null) => new()
        {
            Id = Guid.CreateVersion7(),
            SourceType = RelationshipTargets.Artist,
            SourceId = artist.Id.Value,
            TargetType = recording is null ? RelationshipTargets.Work : RelationshipTargets.Recording,
            TargetId = recording?.Id.Value ?? work!.Id.Value,
            Type = type,
            ArtistId = artist.Id,
            RecordingId = recording?.Id,
            WorkId = work?.Id,
        };

    private static ArtistCredit Credit(
        Artist artist,
        Recording recording,
        int position,
        string? joinPhrase) => new()
        {
            Id = Guid.CreateVersion7(),
            ArtistId = artist.Id,
            RecordingId = recording.Id,
            Position = position,
            JoinPhrase = joinPhrase,
        };

    /// <summary>
    /// Minted from an increasing instant rather than from the clock, because the
    /// order these files were <i>added</i> in is the order of these calls.
    /// </summary>
    /// <remarks>
    /// A UUIDv7 orders to the millisecond and the rest of it is random, so a seed
    /// that writes every file inside one millisecond — which is every seed —
    /// leaves <c>sort=added</c> to shuffle. One minute apart is not a claim about
    /// anything; it is far enough apart to be an order.
    /// </remarks>
    private static int _added;

    private static MediaFile File(string path, Recording recording) => new()
    {
        Id = new MediaFileId(Guid.CreateVersion7(
            DateTimeOffset.Parse("2026-01-01T00:00:00Z", CultureInfo.InvariantCulture)
                .AddMinutes(Interlocked.Increment(ref _added)))),
        Path = path,
        SizeBytes = 42_000_000,
        LastModifiedUtc = DateTimeOffset.Parse("2026-01-01T00:00:00Z", CultureInfo.InvariantCulture),
        RecordingId = recording.Id,
        RecordingLookupUtc = DateTimeOffset.UtcNow,
        EnrichmentOutcome = EnrichmentOutcome.Linked,
    };

    /// <summary>
    /// Whose album it is, as three separate facts, and none of them is the role.
    /// </summary>
    /// <remarks>
    /// <b>The shelf an album lands on is decided from these three fields, and
    /// every one of them was a bug before it existed.</b> A track's "billed"
    /// role is a claim about a <i>recording</i>; whose record it is, is a claim
    /// about the <i>release</i>. Reading the first as the second put a B.B. King
    /// tribute album into Marc Broussard's discography — he sings one song on
    /// it, and the record is Joe Bonamassa's.
    ///
    /// The seed already carries all three shapes, which is why this test seeds
    /// almost nothing:
    ///
    /// <list type="bullet">
    /// <item><b>Seesaw</b> prints both names, so Bonamassa is on the release's
    /// own line: <c>true</c>.</item>
    /// <item><b>The Ring</b> is billed to Wagner with Solti on every recording:
    /// <c>false</c>, which is the demotion.</item>
    /// <item><b>Blues Summit 100</b> is billed to nobody at all, and that is
    /// <c>null</c> rather than <c>false</c>. The distinction is load-bearing: a
    /// release the catalogue holds no credit for is the catalogue not knowing,
    /// and demoting on it would move an artist's own record off their
    /// discography on the strength of a row nobody wrote.</item>
    /// </list>
    ///
    /// <c>Band</c> is the fourth fact and the one no credit can carry.
    /// The membership seeded below is <i>synthetic</i> — Solti was not a member
    /// of Wagner, and no rule here cares — because what is under test is the
    /// join: the release's credited artist id, matched against the set of bands
    /// the page's artist belongs to. Nothing else in the application can tell
    /// Mark Knopfler's Dire Straits albums from somebody covering him.
    /// </remarks>
    [Fact]
    public async Task AnAlbumCarriesItsOwnBillingLineAndWhetherItIsTheirBand()
    {
        using var client = _factory!.CreateClient();

        var bonamassa = await ArtistAsync(client, _seed.Bonamassa);

        var seesaw = bonamassa.Tracks
            .Select(t => t.Album)
            .First(a => a is not null && a.Title == "Seesaw")!;

        Assert.Equal("Beth Hart & Joe Bonamassa", seesaw.Artist);
        Assert.True(seesaw.Billed);
        Assert.Null(seesaw.Band);

        // Billed to nobody: null, and not false.
        var anthology = bonamassa.Tracks
            .Select(t => t.Album)
            .First(a => a is not null && a.Title == "Blues Summit 100")!;

        Assert.Null(anthology.Artist);
        Assert.Null(anthology.Billed);
        Assert.Null(anthology.Band);

        // Somebody else's sleeve, with him on every recording of it.
        var solti = await ArtistAsync(client, _seed.Solti);
        var ring = solti.Tracks.Select(t => t.Album).First(a => a is not null)!;

        Assert.Equal("Richard Wagner", ring.Artist);
        Assert.False(ring.Billed);
        Assert.Null(ring.Band);

        await using (var db = PostgresFixture.CreateContext(_connectionString))
        {
            db.Relationships.Add(new Relationship
            {
                Id = Guid.CreateVersion7(),
                SourceType = RelationshipTargets.Artist,
                SourceId = _seed.Solti,
                TargetType = RelationshipTargets.Artist,
                TargetId = _seed.Wagner,
                Type = RelationshipTargets.Member,
                ArtistId = new ArtistId(_seed.Solti),
            });

            await db.SaveChangesAsync(Token);
        }

        var afterwards = await ArtistAsync(client, _seed.Solti);
        var sameRing = afterwards.Tracks.Select(t => t.Album).First(a => a is not null)!;

        // The same release, the same credit line, the same "not on it" — and now
        // a group he belongs to, which is the only thing that changed.
        Assert.Equal(ring.ReleaseId, sameRing.ReleaseId);
        Assert.False(sameRing.Billed);
        Assert.NotNull(sameRing.Band);

        // The group's own name, not the release's printed credit — which is what
        // keeps one band from becoming two shelves when two sleeves spell it
        // differently.
        Assert.Equal(_seed.Wagner, sameRing.Band.Id);
        Assert.Equal("Richard Wagner", sameRing.Band.Name);
    }

    private sealed record Seeded
    {
        public Guid Mozart { get; init; }

        public Guid Satie { get; init; }

        public Guid Karajan { get; init; }

        public Guid Berliner { get; init; }

        public Guid Hart { get; init; }

        public Guid Bonamassa { get; init; }

        public Guid Solti { get; init; }

        public Guid Wagner { get; init; }

        public Guid Orphan { get; init; }

        public Guid FirstMovement { get; init; }

        public Guid Duet { get; init; }

        public Guid Seesaw { get; init; }

        public Guid Symphony { get; init; }

        public Guid SeesawMbid { get; init; }

        public Guid AnthologyMbid { get; init; }
    }
}
