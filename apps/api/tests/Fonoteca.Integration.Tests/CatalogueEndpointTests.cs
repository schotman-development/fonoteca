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

        var body = await client.GetFromJsonAsync<ArtistListResponse>(
            new Uri("/api/catalogue/artists", UriKind.Relative), Token);

        Assert.NotNull(body);
        Assert.Equal(6, body.Total);

        // Sort name, not display name: "Karajan, Herbert von" belongs under K.
        Assert.Equal(
            [
                "Berliner Philharmoniker",
                "Bonamassa, Joe",
                "Hart, Beth",
                "Karajan, Herbert von",
                "Mozart, Wolfgang Amadeus",
                "Satie, Erik",
            ],
            body.Items.Select(a => a.SortName));

        var mozart = body.Items.Single(a => a.Id == _seed.Mozart);

        // Both movements, reached through the work rather than a credit line.
        Assert.Equal(2, mozart.TrackCount);
        Assert.Equal("Person", mozart.Type);
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

        var body = await client.GetFromJsonAsync<ArtistListResponse>(
            new Uri("/api/catalogue/artists", UriKind.Relative), Token);

        Assert.NotNull(body);
        Assert.DoesNotContain(body.Items, a => a.Id == _seed.Orphan);
    }

    /// <summary>
    /// An artist's picture is one of their albums, because there is no
    /// photograph of anybody to be had — MusicBrainz holds none and the Cover
    /// Art Archive is keyed on releases.
    /// </summary>
    /// <remarks>
    /// Which album is the whole question, and the greedy answer is wrong in the
    /// way this codebase keeps meeting: the release holding most of an artist's
    /// tracks is a compilation about as often as it is theirs. Measured against
    /// the real library, Joe Bonamassa's 454 tracks put a hundred-track
    /// anthology on top with 32 of them, ahead of every record with his name on
    /// the sleeve — so the seed here reproduces that shape at three against two.
    /// </remarks>
    [Fact]
    public async Task AnArtistsPictureIsAnAlbumTheyAreBilledOnRatherThanTheBiggerOneTheyGuestOn()
    {
        using var client = _factory!.CreateClient();

        var body = await client.GetFromJsonAsync<ArtistListResponse>(
            new Uri("/api/catalogue/artists", UriKind.Relative), Token);

        Assert.NotNull(body);

        var bonamassa = body.Items.Single(a => a.Id == _seed.Bonamassa);

        Assert.Equal(_seed.SeesawMbid, bonamassa.Cover);
        Assert.NotEqual(_seed.AnthologyMbid, bonamassa.Cover);

        // The same rule on the detail page, from the same input — a tile and the
        // page it opens must not show two different faces for one artist.
        var detail = await client.GetFromJsonAsync<ArtistDetailResponse>(
            new Uri($"/api/catalogue/artists/{_seed.Bonamassa}", UriKind.Relative), Token);

        Assert.NotNull(detail);
        Assert.Equal(_seed.SeesawMbid, detail.Artist.Cover);

        // Satie's one file was never attributed to a release, so there is
        // nothing to draw and the card falls back to its monogram.
        Assert.Null(body.Items.Single(a => a.Id == _seed.Satie).Cover);
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
            new Uri("/api/catalogue/artists?skip=1&take=2", UriKind.Relative), Token);

        Assert.NotNull(body);

        // "showing 2 of 6", answerable without a second request.
        Assert.Equal(6, body.Total);
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

        var list = await client.GetFromJsonAsync<ArtistListResponse>(
            new Uri("/api/catalogue/artists", UriKind.Relative), Token);

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
    /// The album list's three orders, each one a different answer.
    /// </summary>
    /// <remarks>
    /// Sorted in SQL because the endpoint pages in SQL, and every case here asks
    /// for <b>two of the three</b> deliberately. Asking for the whole list would
    /// pass identically against an implementation that sorted only the page it
    /// had already taken — which is the bug worth pinning, since a library of
    /// five hundred albums pages for real. The seed is chosen so all three
    /// orders name a different first two.
    /// </remarks>
    [Fact]
    public async Task AlbumsSortByTitleArtistOrYear()
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
        Assert.Equal(3, body.Total);

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
            new Uri("/api/catalogue/artists?sort=tracks", UriKind.Relative), Token);

        Assert.NotNull(body);

        Assert.Equal(
            [
                "Bonamassa, Joe",
                "Berliner Philharmoniker",
                "Karajan, Herbert von",
                "Mozart, Wolfgang Amadeus",
                "Hart, Beth",
                "Satie, Erik",
            ],
            body.Items.Select(a => a.SortName));

        Assert.Equal([4, 2, 2, 2, 1, 1], body.Items.Select(a => a.TrackCount));

        // An order nobody asked for is the default here too.
        var nonsense = await client.GetFromJsonAsync<ArtistListResponse>(
            new Uri("/api/catalogue/artists?sort=nonsense", UriKind.Relative), Token);

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

        // Four folders, each internally consistent, each naming one release.
        Assert.Equal(4, report.Folders);
        Assert.Equal(4, report.FoldersAgreeing);
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
        var bonamassa = Artist("Joe Bonamassa", "Bonamassa, Joe", "Person");
        var orphan = Artist("Nobody At All", "Nobody At All", "Group");

        db.Artists.AddRange(mozart, satie, karajan, berliner, hart, bonamassa, orphan);

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

    private static MediaFile File(string path, Recording recording) => new()
    {
        Id = MediaFileId.New(),
        Path = path,
        SizeBytes = 42_000_000,
        LastModifiedUtc = DateTimeOffset.Parse("2026-01-01T00:00:00Z", CultureInfo.InvariantCulture),
        RecordingId = recording.Id,
        RecordingLookupUtc = DateTimeOffset.UtcNow,
        EnrichmentOutcome = EnrichmentOutcome.Linked,
    };

    private sealed record Seeded
    {
        public Guid Mozart { get; init; }

        public Guid Satie { get; init; }

        public Guid Karajan { get; init; }

        public Guid Berliner { get; init; }

        public Guid Hart { get; init; }

        public Guid Bonamassa { get; init; }

        public Guid Orphan { get; init; }

        public Guid FirstMovement { get; init; }

        public Guid Duet { get; init; }

        public Guid Seesaw { get; init; }

        public Guid Symphony { get; init; }

        public Guid SeesawMbid { get; init; }

        public Guid AnthologyMbid { get; init; }
    }
}
