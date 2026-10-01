using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using Fonoteca.Api.Endpoints;
using Fonoteca.Data;
using Fonoteca.Domain.Abstractions;
using Fonoteca.Domain.Catalogue;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;

namespace Fonoteca.Integration.Tests;

/// <summary>
/// The upgrade list on the acquire screen, against a seeded catalogue.
/// </summary>
/// <remarks>
/// <see cref="Fonoteca.Domain.Acquisition.UpgradeScan"/> answers "is this file
/// lossy" and has its own unit tests. What is under test here is the half that
/// only exists in the endpoint: rolling files up onto the release Qobuz would
/// sell, which is where the three mistakes are — an album counted once per file,
/// an all-lossless album on the list, and a loose file with no release on it at
/// all.
///
/// No Qobuz, and that is the point: this endpoint reaches no provider, so it
/// needs no stub and works on an instance with nothing configured.
/// </remarks>
[Collection(nameof(PostgresCollection))]
public sealed class QobuzUpgradeTests(PostgresFixture postgres) : IAsyncLifetime
{
    private string _connectionString = string.Empty;
    private string _root = string.Empty;
    private WebApplicationFactory<Program>? _factory;

    /// <summary>The shorter edition of the album seeded with two, which a folder of it is measured against.</summary>
    private Guid _standard;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _connectionString = await postgres.CreateDatabaseAsync(Token);
        _root = Directory.CreateTempSubdirectory("fonoteca-qobuz-upgrades-").FullName;

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:Fonoteca", _connectionString);
            builder.UseSetting("Fonoteca:LibraryPath", _root);
            builder.UseSetting("Fonoteca:WarmCandidates", "false");
            builder.UseSetting("Fonoteca:MusicBrainzContact", string.Empty);
        });

        using var warm = _factory.CreateClient();

        await SeedAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (_factory is not null) await _factory.DisposeAsync();

        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task AnAlbumHeldEntirelyInFlacIsNotAnUpgrade()
    {
        var body = await ListAsync();

        // Two .flac files and nothing else — the plain case, judged by name
        // alone, with no measurement involved.
        Assert.DoesNotContain(body.Items, item => item.Title == "All Lossless");
    }

    [Fact]
    public async Task AnAlbumWithOneLossyTrackIsListedOnceWithBothCounts()
    {
        var body = await ListAsync();

        var mixed = Assert.Single(body.Items, item => item.Title == "Mostly Lossless");

        // One row for the album rather than one per file, and both numbers —
        // "1" alone reads as an album to re-buy where it is one track of four.
        Assert.Equal(4, mixed.Files);
        Assert.Equal(1, mixed.Upgradable);
        Assert.Equal(["MP3"], mixed.Formats);
        Assert.Equal("Lossy", mixed.Reason);
    }

    [Fact]
    public async Task TheSearchTermIsTheFirstBilledArtistAndTheTitleRatherThanTheBillingLine()
    {
        var body = await ListAsync();

        var lossy = Assert.Single(body.Items, item => item.Title == "All Lossy");

        // The printed line is what the row shows; the query is what finds it.
        Assert.Equal("Beth Hart & Joe Bonamassa", lossy.Artist);
        Assert.Equal("Beth Hart All Lossy", lossy.Query);
    }

    [Fact]
    public async Task TheWorstAlbumsComeFirstAndLooseFilesAreNotCounted()
    {
        var body = await ListAsync();

        // Lossy albums lead, most to replace first. "Loose" is the folder row —
        // an entirely-MP3 album no pass attributed, which had no release for a
        // row to be named after and used to be invisible here.
        Assert.Equal(
            ["Anthology Box", "All Lossy", "Half Filed", "Boxed Album", "Loose",
             "Twice Ripped", "Twice Ripped", "Mostly Lossless", "Below Hi-Res"],
            body.Items.Select(item => item.Title));

        // Every file in the library, attributed or not. The completeness
        // fixtures below are lossless and unprobed, so they add to this and to
        // nothing else on the upgrade list.
        // 38 since the group-linked FLAC joined the seed: lossless, loose and on
        // no list, but it is still a file in the library and this is the count
        // of every one of them. 44 since the two-edition album's folder and the
        // album ripped twice joined it, 54 since the four hidden-track albums did.
        Assert.Equal(54, body.Files);
    }

    [Fact]
    public async Task AProbedFileOutranksItsExtension()
    {
        var body = await ListAsync();

        // "Misnamed" holds one file, named .mp3 and probed as 24/96 FLAC, and it
        // is not on the list. A name-only rule puts it there as lossy.
        Assert.DoesNotContain(body.Items, item => item.Title == "Misnamed");
    }

    [Fact]
    public async Task AnUnattributedAlbumIsListedByItsFolder()
    {
        var body = await ListAsync();

        var loose = Assert.Single(body.Items, item => item.Title == "Loose");

        // No release, so the row is the folder — and both halves of it go into
        // the query, because neither is more trustworthy than the other.
        Assert.Null(loose.AlbumId);

        // And therefore no cover: the Cover Art Archive is keyed on the
        // MusicBrainz release, so the shelf draws a monogram. 75 of the 520
        // rows on the target library are in this position.
        Assert.Null(loose.Mbid);
        Assert.Equal("Nobody/Loose", loose.Folder);
        Assert.Equal("Nobody", loose.Artist);
        Assert.Equal("Nobody Loose", loose.Query);
        Assert.Equal(2, loose.Upgradable);
    }

    [Fact]
    public async Task ACdQualityRipIsAnUpgradeOnceSomethingHasMeasuredIt()
    {
        var body = await ListAsync();

        var cd = Assert.Single(body.Items, item => item.Title == "Below Hi-Res");

        Assert.Equal("BelowHiRes", cd.Reason);
        Assert.Equal(["FLAC 16/44.1"], cd.Formats);

        // And it sorts under every lossy album, however few files it has: the
        // two are not the same size of win, and 434 of these would otherwise
        // bury the 87 that matter more.
        Assert.Equal(body.Items.Count - 1, body.Items.ToList().FindIndex(item => item == cd));
    }

    [Fact]
    public async Task AnUnprobedFlacAlbumIsNotAQuestion()
    {
        var body = await ListAsync();

        // "All Lossless" is 16/44.1 for all anyone knows, but nothing has looked.
        // Listing it would make every FLAC in an unprobed library a maybe.
        Assert.DoesNotContain(body.Items, item => item.Title == "All Lossless");
    }

    [Fact]
    public async Task AnAlbumOnlyPartlyFiledIsOneRowThatCountsAllOfIt()
    {
        var body = await ListAsync();

        // The regression. Keying attributed files on their release and the rest
        // on their folder put this album on the list twice — "2 of 2" under the
        // release and "1 of 1" under the folder — and neither row said it holds
        // three files. 125 albums on the target library looked like this.
        var album = Assert.Single(body.Items, item => item.Title == "Half Filed");

        Assert.Equal(3, album.Files);
        Assert.Equal(3, album.Upgradable);
        Assert.Equal("Alan/Half Filed", album.Folder);

        // The identifier the shelf's cover comes from, carried through from the
        // release the folder is named after.
        Assert.NotNull(album.Mbid);

        // Named after the release the majority of it carries, not after the
        // folder — the folder is what is counted, the release is what it is
        // called.
        Assert.NotNull(album.AlbumId);
        Assert.Equal("Alan Half Filed", album.Query);

        Assert.DoesNotContain(body.Items, item => item.Title == "Half Filed (1991)");
    }

    [Fact]
    public async Task AFolderIsNotNamedAfterABoxSetItIsOnlyAFifthOf()
    {
        var body = await ListAsync();

        // The attribution pass's documented failure, seen from this screen: four
        // Brad Paisley folders are each filed under one 64-file reissue box, so
        // each would head a row named after a box set Qobuz does not sell. A
        // release that is mostly somewhere else is not what this folder is.
        var boxed = Assert.Single(body.Items, item => item.Folder == "Anth/Boxed Album");

        Assert.Null(boxed.AlbumId);
        Assert.Equal("Boxed Album", boxed.Title);
        Assert.Equal("Anth Boxed Album", boxed.Query);

        // The other half of the same box does name it, because that folder is
        // most of it.
        var majority = Assert.Single(body.Items, item => item.Folder == "Anth/The Box");
        Assert.NotNull(majority.AlbumId);
        Assert.Equal("Anthology Box", majority.Title);
    }

    [Fact]
    public async Task AnAlbumMissingOneTrackNamesTheTrackItIsMissing()
    {
        var body = await ListAsync();

        var album = Assert.Single(body.Incomplete, item => item.Title == "Nearly There");

        Assert.Equal(4, album.TrackCount);
        Assert.Equal(3, album.Held);
        Assert.Equal(0, album.Unmatched);
        Assert.NotNull(album.Mbid);

        // Named, not just counted. The whole track list is written down for
        // exactly this, and a person deciding whether to re-buy an album is
        // deciding about a song rather than about a number.
        var missing = Assert.Single(album.Missing);
        Assert.Equal(2, missing.Position);
        Assert.Equal("The Missing One", missing.Title);

        // The row is the folder a person is looking at, and the query is the
        // first billed artist and the title — the upgrade rows' rule, not a
        // second copy of it.
        Assert.Equal("Sas/Nearly There", album.Folder);
        Assert.Equal("Julian Sas Nearly There", album.Query);
    }

    [Fact]
    public async Task AGapTheFoldersOwnUnmatchedFilesCouldFillIsNotSomethingToBuy()
    {
        var body = await ListAsync();

        // Four printed tracks, two seated, two more files sitting right there
        // that no pass has matched. Measured on the target library this is 125
        // of 175 albums, so a list that did not subtract them would be four
        // fifths wrong in the direction that costs money. It is Identify's
        // question, and the count is what stops the short list reading as a
        // claim that the library is nearly whole.
        Assert.DoesNotContain(body.Incomplete, item => item.Title == "Waiting on Identify");
        Assert.Equal(2, body.UnmatchedAlbums);
    }

    [Fact]
    public async Task UnmatchedFilesCountFromEveryFolderTheAlbumSitsIn()
    {
        var body = await ListAsync();

        // A release across two folders: five tracks, three seated in the folder
        // that names it and two unmatched files in the other. Counting the loose
        // files from the naming folder alone subtracts nothing and reports two
        // tracks to buy that are already on disk one folder away — which
        // is Ray Charles' "The Birth of Soul" on the target library, 28/53 with
        // twenty-one to buy and twenty-four unmatched siblings.
        Assert.DoesNotContain(body.Incomplete, item => item.Title == "Spread About");
    }

    [Fact]
    public async Task AnAlbumWithEveryTrackHeldIsNotIncomplete()
    {
        var body = await ListAsync();

        Assert.DoesNotContain(body.Incomplete, item => item.Title == "All Present");

        // And the list is nearest-to-whole first: one missing track is an album
        // somebody can finish, nine is a decision about whether they want it.
        Assert.Equal(
            ["Hidden Track", "Nearly There", "Silence Unfiled", "Twice Ripped", "Unforgettable", "Half a Record"],
            body.Incomplete.Select(item => item.Title));
    }

    /// <summary>
    /// Silent placeholder tracks are not songs anybody is missing.
    /// </summary>
    /// <remarks>
    /// Marc Broussard's <i>Carencro</i> printed eleven <c>[silence]</c> tracks
    /// before its hidden track 23 and was listed as 12 of 23 with the silence as
    /// the missing tracks. Counted in SQL for the album page's pressing and in
    /// the edition rule here, so both halves are exercised.
    /// </remarks>
    [Fact]
    public async Task SilentPlaceholdersAreNeitherCountedNorMissing()
    {
        var body = await ListAsync();

        // Two of three songs, not two of five, and the one to buy is the song.
        var album = Assert.Single(body.Incomplete, item => item.Title == "Hidden Track");
        Assert.Equal(3, album.TrackCount);
        Assert.Equal(2, album.Held);
        Assert.Equal("Two", Assert.Single(album.Missing).Title);

        await using var db = PostgresFixture.CreateContext(_connectionString);
        var group = await db.ReleaseGroups.SingleAsync(row => row.Title == "Hidden Track", Token);

        using var client = _factory!.CreateClient();
        var page = await client.GetFromJsonAsync<JsonElement>(
            new Uri($"/api/catalogue/albums/{group.Id.Value}", UriKind.Relative), Token);

        // Two of three songs on the album page too, and no row for either silence.
        Assert.Equal(3, page.GetProperty("album").GetProperty("trackCount").GetInt32());
        Assert.Equal(2, page.GetProperty("album").GetProperty("held").GetInt32());
        Assert.DoesNotContain(
            page.GetProperty("tracks").EnumerateArray(),
            row => row.GetProperty("title").GetString() == "[silence]");

        // A placeholder a file sits on counts, on the page as in the rule.
        var keptGroup = await db.ReleaseGroups.SingleAsync(row => row.Title == "Silence Kept", Token);
        var keptPage = await client.GetFromJsonAsync<JsonElement>(
            new Uri($"/api/catalogue/albums/{keptGroup.Id.Value}", UriKind.Relative), Token);

        Assert.Equal(3, keptPage.GetProperty("album").GetProperty("trackCount").GetInt32());
        Assert.Equal(3, keptPage.GetProperty("album").GetProperty("held").GetInt32());
        Assert.DoesNotContain(body.Incomplete, item => item.Title == "Silence Kept");

        // And the attribution report's short pressings count songs too.
        var report = await client.GetFromJsonAsync<JsonElement>(
            new Uri("/api/catalogue/attribution", UriKind.Relative), Token);

        var pressing = Assert.Single(
            report.GetProperty("incomplete").EnumerateArray(),
            row => row.GetProperty("title").GetString() == "Hidden Track");
        Assert.Equal(3, pressing.GetProperty("trackCount").GetInt32());
        Assert.Equal(2, pressing.GetProperty("held").GetInt32());

        Assert.DoesNotContain(
            report.GetProperty("incomplete").EnumerateArray(),
            row => row.GetProperty("title").GetString() == "Silence Dropped");
        Assert.DoesNotContain(body.Incomplete, item => item.Title == "Silence Dropped");

        // Four tracks, the held silence among them, and the one to buy is a song.
        var unfiled = Assert.Single(body.Incomplete, item => item.Title == "Silence Unfiled");
        Assert.Equal(4, unfiled.TrackCount);
        Assert.Equal(3, unfiled.Held);
        Assert.Equal("Four", Assert.Single(unfiled.Missing).Title);
    }

    [Fact]
    public async Task TwoRipsOfOneAlbumAreBothNamedAfterIt()
    {
        var body = await ListAsync();

        var rips = body.Items.Where(item => item.Folder.StartsWith("Rips/", StringComparison.Ordinal)).ToList();

        Assert.Equal(2, rips.Count);
        Assert.All(rips, rip =>
        {
            Assert.NotNull(rip.AlbumId);
            Assert.Equal("Twice Ripped", rip.Title);
        });

        // Every count on the incomplete list is album-wide, so the album is one row.
        var shortOfOne = Assert.Single(body.Incomplete, item => item.Title == "Twice Ripped");
        Assert.Equal(["Third"], shortOfOne.Missing.Select(track => track.Title));
    }

    /// <summary>
    /// An album whose pressing is not known is measured against the edition its
    /// files are nearest to, never a longer one they were not.
    /// </summary>
    /// <remarks>
    /// The folder holds two of the standard edition's three songs and is filed to
    /// the album only. Measured against the deluxe it would be three tracks short
    /// and the bonus disc would read as something to buy.
    /// </remarks>
    [Fact]
    public async Task AnAlbumWithNoEditionIsMeasuredAgainstItsNearestEdition()
    {
        var body = await ListAsync();

        var album = Assert.Single(body.Incomplete, item => item.Title == "Unforgettable");

        Assert.Equal(_standard, album.EditionId);
        Assert.Equal(3, album.TrackCount);
        Assert.Equal(2, album.Held);
        Assert.Equal(["Too Young"], album.Missing.Select(track => track.Title));
    }

    [Fact]
    public async Task OnlyAFollowedArtistsRecordsTheLibraryHasNoneOfAreMissing()
    {
        var body = await ListAsync();

        var titles = body.Missing.Select(record => record.Title).ToList();

        Assert.Contains("Record Nobody Owns", titles);

        // A screen that offers to sell somebody a record they already own is
        // worse than no screen, and the link runs file → release → group.
        Assert.DoesNotContain("Record Already Held", titles);

        // The second arm of "held", which the attribution pass writes when it
        // knows the album but not the pressing: the file points straight at the
        // group, with no release in between. Untested until now.
        Assert.DoesNotContain("Record Held Only By A Group Link", titles);

        Assert.DoesNotContain("Greatest Hits", titles);
        Assert.DoesNotContain("Record By Somebody Unfollowed", titles);

        // Missing, and nobody said they wanted it. The shelf is the wanted list
        // rather than the discography — that is the whole reason the column
        // exists, since a few dozen followed artists otherwise make this shelf
        // longer than anybody reads.
        Assert.DoesNotContain("Record Nobody Marked", titles);
    }

    /// <summary>
    /// One record a shop sells as two products is one row, carrying the other.
    /// </summary>
    [Fact]
    public async Task OneRecordSoldAsTwoProductsIsOneRow()
    {
        var body = await ListAsync();

        var rows = body.Missing.Where(record => record.Title == "Live At The Blue Note").ToList();
        Assert.Equal(2, rows.Count);

        // The best quality is shown although the others were found first: the
        // deepest, then the fastest. The others ride along so that not wanting
        // the row stops wanting all three.
        var live = Assert.Single(rows, record => record.Year == 2026);
        Assert.Equal("hires", live.SourceId);

        await using var db = PostgresFixture.CreateContext(_connectionString);
        var others = await db.DiscoveredRecords
            .Where(record => record.SourceId == "cd" || record.SourceId == "hires-48")
            .Select(record => record.Id)
            .ToListAsync(Token);
        Assert.Equal(others.Select(id => id.Value).Order(), live.Also!.Order());

        // Another year is another record.
        Assert.Null(Assert.Single(rows, record => record.Year == 2019).Also);
    }

    [Fact]
    public async Task AGapNobodyMarkedIsCountedRatherThanShown()
    {
        var body = await ListAsync();

        // Two unmarked gaps in the seed: one catalogue record, and one record a
        // shop sells twice. Counted, because an empty shelf has two opposite
        // causes and only this one is something a person can act on: "nothing
        // marked" points at the artist page, "nothing missing" is good news, and
        // the length of `missing` alone cannot tell them apart. An unmarked twin
        // of a record already on the shelf is not a third.
        Assert.Equal(2, body.UnmonitoredGaps);
    }

    [Fact]
    public async Task AMissingRecordIsSearchedForByItsArtistAndTitle()
    {
        var body = await ListAsync();

        var record = Assert.Single(body.Missing, item => item.Title == "Record Nobody Owns");

        Assert.Equal("Followed Artist", record.Artist);
        Assert.Equal("Followed Artist Record Nobody Owns", record.Query);
        Assert.Equal("Album", record.PrimaryType);
    }

    [Fact]
    public async Task AnArtistNobodyHasBrowsedIsCountedRatherThanMistakenForHavingNoGaps()
    {
        var body = await ListAsync();

        // Three followed. Without the second number an empty shelf reads as
        // "nothing to buy" on a library where the pass has simply not run.
        Assert.Equal(3, body.FollowedArtists);

        // One, not two. "Pending Artist" has an MBID and is genuinely waiting
        // for the pass; "Artist With No Mbid" is followed and unbrowsed and the
        // pass's worklist excludes them, so counting them would leave the
        // "run the enrichment pass" line on the screen forever.
        Assert.Equal(1, body.UnbrowsedArtists);
    }

    private async Task<UpgradeListResponse> ListAsync()
    {
        using var client = _factory!.CreateClient();

        var body = await client.GetFromJsonAsync<UpgradeListResponse>(
            new Uri("/api/qobuz/upgrades", UriKind.Relative), Token);

        Assert.NotNull(body);
        return body;
    }

    private async Task SeedAsync()
    {
        await using var db = PostgresFixture.CreateContext(_connectionString);

        var hart = Artist("Beth Hart");
        var bonamassa = Artist("Joe Bonamassa");
        db.Artists.AddRange(hart, bonamassa);

        var lossy = Release(db, "All Lossy");
        db.ArtistCredits.Add(Credit(hart, lossy, 0, " & "));
        db.ArtistCredits.Add(Credit(bonamassa, lossy, 1, null));

        db.MediaFiles.AddRange(
            File("Hart/All Lossy/01.mp3", lossy),
            File("Hart/All Lossy/02.mp3", lossy),
            File("Hart/All Lossy/03.m4a", lossy));

        // One album ripped twice, into two folders, the second held to the album
        // alone. Each holds the same two of its three songs, so each is named
        // after it — measured in files the two would split it and neither would
        // be most of it — and the album is short one song, once.
        var twice = Release(db, "Twice Ripped", trackCount: 3);
        Tracks(db, twice, ["First", "Second", "Third"]);
        db.MediaFiles.AddRange(
            Seated(db, "Rips/Twice Ripped/01.mp3", twice, 1),
            Seated(db, "Rips/Twice Ripped/02.mp3", twice, 2));

        foreach (var copy in new[]
                 {
                     Seated(db, "Rips/Twice Ripped (Again)/01.mp3", twice, 1),
                     Seated(db, "Rips/Twice Ripped (Again)/02.mp3", twice, 2),
                 })
        {
            copy.ReleaseId = null;
            copy.TrackId = null;
            db.MediaFiles.Add(copy);
        }

        var mixed = Release(db, "Mostly Lossless");
        db.MediaFiles.AddRange(
            File("X/Mostly Lossless/01.flac", mixed),
            File("X/Mostly Lossless/02.flac", mixed),
            File("X/Mostly Lossless/03.flac", mixed),
            File("X/Mostly Lossless/04.mp3", mixed));

        var clean = Release(db, "All Lossless");
        db.MediaFiles.AddRange(
            File("X/All Lossless/01.flac", clean),
            File("X/All Lossless/02.flac", clean));

        var misnamed = Release(db, "Misnamed");

        // Named .mp3 and measured as FLAC — the forty ID3-prefixed files in the
        // target library, from the other direction. A decoder beats a filename.
        var probed = File("X/Misnamed/01.mp3", misnamed);
        probed.Quality = Measured(24, 96_000);

        db.MediaFiles.Add(probed);

        // Filed under nothing, which used to mean invisible. Two files so the
        // row's own count is not the same number as anything else's.
        db.MediaFiles.AddRange(
            File("Nobody/Loose/01.mp3", release: null),
            File("Nobody/Loose/02.mp3", release: null));

        // Two of three filed, one not — the shape that used to produce two rows.
        var alan = Artist("Alan");
        db.Artists.Add(alan);

        var half = Release(db, "Half Filed");
        db.ArtistCredits.Add(Credit(alan, half, 0, null));
        db.MediaFiles.AddRange(
            File("Alan/Half Filed/01.mp3", half),
            File("Alan/Half Filed/02.mp3", half),
            File("Alan/Half Filed/03.mp3", release: null));

        // One release across two folders, four files to one and two to the
        // other: the majority folder gets the release's name, the minority one
        // keeps its own.
        var box = Release(db, "Anthology Box");
        db.MediaFiles.AddRange(
            File("Anth/The Box/01.mp3", box),
            File("Anth/The Box/02.mp3", box),
            File("Anth/The Box/03.mp3", box),
            File("Anth/The Box/04.mp3", box),
            File("Anth/Boxed Album/01.mp3", box),
            File("Anth/Boxed Album/02.mp3", box));

        // Lossless, measured, and CD — the half of the list that only exists
        // once ProbeService has run.
        var cd = Release(db, "Below Hi-Res");
        var ripped = File("X/Below Hi-Res/01.flac", cd);
        ripped.Quality = Measured(16, 44_100);
        db.MediaFiles.Add(ripped);

        // Three albums with a track list, which is the only thing that can be
        // short of one. All lossless and unprobed, so none of them reaches the
        // upgrade list and the two questions stay separate.
        var sas = Artist("Julian Sas");
        db.Artists.Add(sas);

        var nearly = Release(db, "Nearly There", trackCount: 4);
        db.ArtistCredits.Add(Credit(sas, nearly, 0, null));
        Tracks(db, nearly, ["Opener", "The Missing One", "Third", "Closer"]);
        db.MediaFiles.AddRange(
            Seated(db, "Sas/Nearly There/01.flac", nearly, 1),
            Seated(db, "Sas/Nearly There/03.flac", nearly, 3),
            Seated(db, "Sas/Nearly There/04.flac", nearly, 4));

        // A hidden track behind silent placeholders, as on Marc Broussard's
        // Carencro: five positions printed, three songs, one of them not held.
        var padded = Release(db, "Hidden Track", trackCount: 5);
        Tracks(db, padded, ["One", "Two", "[silence]", "[silence]", "Hidden"]);
        db.MediaFiles.AddRange(
            Seated(db, "Broussard/Hidden Track/01.flac", padded, 1),
            Seated(db, "Broussard/Hidden Track/05.flac", padded, 5));

        // The same shape ripped with its silence kept: a file on every position,
        // the silent one included, which then counts.
        var silenceKept = Release(db, "Silence Kept", trackCount: 3);
        Tracks(db, silenceKept, ["One", "[silence]", "Hidden"]);
        db.MediaFiles.AddRange(
            Seated(db, "Broussard/Silence Kept/01.flac", silenceKept, 1),
            Seated(db, "Broussard/Silence Kept/02.flac", silenceKept, 2),
            Seated(db, "Broussard/Silence Kept/03.flac", silenceKept, 3));

        // And ripped whole without its silence: every song, nothing short.
        var silenceDropped = Release(db, "Silence Dropped", trackCount: 3);
        Tracks(db, silenceDropped, ["One", "[silence]", "Hidden"]);
        db.MediaFiles.AddRange(
            Seated(db, "Broussard/Silence Dropped/01.flac", silenceDropped, 1),
            Seated(db, "Broussard/Silence Dropped/03.flac", silenceDropped, 3));

        // Kept silence again, filed to the album alone: nothing is seated, so the
        // silent track counts because a file holds its recording, not a slot.
        var silenceUnfiled = Release(db, "Silence Unfiled", trackCount: 4);
        Tracks(db, silenceUnfiled, ["One", "[silence]", "Hidden", "Four"]);

        foreach (var position in new[] { 1, 2, 3 })
        {
            var file = Seated(db, $"Broussard/Silence Unfiled/{position:D2}.flac", silenceUnfiled, position);
            file.ReleaseId = null;
            file.TrackId = null;
            db.MediaFiles.Add(file);
        }

        var partial = Release(db, "Half a Record", trackCount: 4);
        Tracks(db, partial, ["One", "Two", "Three", "Four"]);
        db.MediaFiles.Add(Seated(db, "Y/Half a Record/01.flac", partial, 1));

        // One album, two editions, and a folder filed to the album alone.
        var cole = Group(db, "Unforgettable", "Album");
        var songs = new[] { "Mona Lisa", "Route 66", "Too Young", "Pretend", "Answer Me" }
            .Select(title => new Recording { Id = RecordingId.New(), Title = title })
            .ToList();
        db.Recordings.AddRange(songs);

        var standard = Release(db, "Unforgettable", album: cole);
        _standard = standard.Id.Value;

        foreach (var (edition, count) in new[] { (standard, 3), (Release(db, "Unforgettable (Deluxe)", album: cole), 5) })
        {

            for (var index = 0; index < count; index++)
            {
                db.Add(new Track
                {
                    Id = TrackId.New(),
                    ReleaseId = edition.Id,
                    RecordingId = songs[index].Id,
                    Position = index + 1,
                    DiscNumber = 1,
                    Title = songs[index].Title,
                });
            }
        }

        foreach (var song in songs.Take(2))
        {
            var file = File($"Cole/Unforgettable/{song.Title}.flac", null);
            file.RecordingId = song.Id;
            file.ReleaseGroupId = cole.Id;
            db.MediaFiles.Add(file);
        }

        var present = Release(db, "All Present", trackCount: 2);
        Tracks(db, present, ["One", "Two"]);
        db.MediaFiles.AddRange(
            Seated(db, "Y/All Present/01.flac", present, 1),
            Seated(db, "Y/All Present/02.flac", present, 2));

        // One folder names the release and the gap is in the other. Two files
        // to the naming folder so it clears the majority test both ways.
        var spread = Release(db, "Spread About", trackCount: 5);
        Tracks(db, spread, ["One", "Two", "Three", "Four", "Five"]);
        db.MediaFiles.AddRange(
            Seated(db, "Ray/Spread About/01.flac", spread, 1),
            Seated(db, "Ray/Spread About/02.flac", spread, 2),
            Seated(db, "Ray/Spread About/03.flac", spread, 3),
            File("Ray/Elsewhere/04.flac", spread),
            File("Ray/Elsewhere/05.flac", spread));

        // Two seated and two sitting beside them that nothing has matched: the
        // gap is already in the folder.
        var waiting = Release(db, "Waiting on Identify", trackCount: 4);
        Tracks(db, waiting, ["One", "Two", "Three", "Four"]);
        db.MediaFiles.AddRange(
            Seated(db, "Y/Waiting on Identify/01.flac", waiting, 1),
            Seated(db, "Y/Waiting on Identify/02.flac", waiting, 2),
            File("Y/Waiting on Identify/03.flac", waiting),
            File("Y/Waiting on Identify/04.flac", waiting));

        // The third list, which is about people rather than files. Two followed
        // artists and one nobody followed; one of the two has never been
        // browsed, so `UnbrowsedArtists` is exercised as well as the shelf.
        var kept = Artist("Followed Artist");
        kept.Followed = true;
        kept.DiscographyLookupUtc =
            DateTimeOffset.Parse("2026-02-01T00:00:00Z", CultureInfo.InvariantCulture);

        // Followed, never browsed, and reachable — the browse is keyed on an
        // MBID, so without one this artist would not be on the pass's worklist
        // and must not be counted as waiting for it.
        var pending = Artist("Pending Artist");
        pending.Followed = true;
        pending.Mbid = new Mbid(Guid.CreateVersion7());

        // Followed with no MBID at all, which is the ordinary shape here: every
        // artist in this catalogue is a byproduct of a credit line. The
        // enrichment pass can never reach them, so counting them as "not browsed
        // yet" tells somebody to run a pass that will change nothing, forever.
        var unreachable = Artist("Artist With No Mbid");
        unreachable.Followed = true;

        var ignored = Artist("Unfollowed Artist");

        db.Artists.AddRange(kept, pending, unreachable, ignored);

        // One live album a shop sells three times — two hi-res and CD quality,
        // three ids, the CD found first and the 24/48 before the 24/96 — and a
        // same-titled record from
        // another year, which is another record, wanted with an unmarked twin of
        // its own. Then a record sold twice that nobody marked: one more
        // unmarked gap, not two.
        foreach (var (sourceId, title, year, found, monitored, depth, rate) in new (string, string, int, string, bool, int?, double?)[]
                 {
                     ("cd", "Live At The Blue Note", 2026, "2026-09-01T00:00:00Z", true, 16, 44.1),
                     ("hires-48", "Live At The Blue Note", 2026, "2026-09-01T12:00:00Z", true, 24, 48),
                     ("hires", "Live At The Blue Note", 2026, "2026-09-02T00:00:00Z", true, 24, 96),
                     ("older", "Live At The Blue Note", 2019, "2026-09-03T00:00:00Z", true, null, null),
                     ("older-cd", "Live At The Blue Note", 2019, "2026-09-04T00:00:00Z", false, null, null),
                     ("unmarked-hires", "Unmarked Live Set", 2020, "2026-09-05T00:00:00Z", false, null, null),
                     ("unmarked-cd", "Unmarked Live Set", 2020, "2026-09-06T00:00:00Z", false, null, null),
                 })
        {
            db.DiscoveredRecords.Add(new DiscoveredRecord
            {
                Id = DiscoveredRecordId.New(),
                ArtistId = kept.Id,
                Source = "qobuz",
                SourceId = sourceId,
                Title = title,
                Year = year,
                Monitored = monitored,
                MaximumBitDepth = depth,
                MaximumSamplingRate = rate,
                FoundUtc = DateTimeOffset.Parse(found, CultureInfo.InvariantCulture),
                SeenUtc = DateTimeOffset.Parse(found, CultureInfo.InvariantCulture),
            });
        }

        // Marked as wanted, which is what puts it on the shelf. Everything a
        // first discography browse writes is unmonitored, so without this the
        // shelf is empty and the whole list is the count below.
        var wanted = Group(db, "Record Nobody Owns", "Album");
        wanted.Monitored = true;
        db.ArtistCredits.Add(GroupCredit(kept, wanted));

        // A gap nobody marked. Missing, admitted by `Discography.IsGap`, and
        // deliberately not on the shelf — it is counted instead, so the screen
        // can tell "nothing marked" from "nothing missing".
        db.ArtistCredits.Add(GroupCredit(kept, Group(db, "Record Nobody Marked", "Album")));

        // Held, and through the arm that is easy to miss: the file points at a
        // release, and the release at the group. "All Lossless" has two FLACs.
        var owned = Group(db, "Record Already Held", "Album");
        clean.ReleaseGroupId = owned.Id;
        db.ArtistCredits.Add(GroupCredit(kept, owned));

        // Held through the *other* arm, which is the one attribution writes when
        // it knows the album and not the pressing: the file points straight at
        // the release group, with no release in between. A rule reading only the
        // release arm offers to sell somebody a record already on their artist
        // page. Lossless and loose, so it adds nothing to either list above.
        var byGroup = Group(db, "Record Held Only By A Group Link", "Album");
        db.ArtistCredits.Add(GroupCredit(kept, byGroup));

        var grouped = File("Kept/Group Linked/01.flac", release: null);
        grouped.ReleaseGroupId = byGroup.Id;
        db.MediaFiles.Add(grouped);

        // Not held either, and still not a gap — `Discography.IsGap` hides it.
        db.ArtistCredits.Add(
            GroupCredit(kept, Group(db, "Greatest Hits", "Album", "Compilation")));

        // Nobody followed them, so their records are not a question.
        db.ArtistCredits.Add(
            GroupCredit(ignored, Group(db, "Record By Somebody Unfollowed", "Album")));

        await db.SaveChangesAsync(Token);
    }

    /// <summary>The release's printed track list, one recording each.</summary>
    private static void Tracks(FonotecaDbContext db, Release release, string[] titles)
    {
        for (var index = 0; index < titles.Length; index++)
        {
            var recording = new Recording { Id = RecordingId.New(), Title = titles[index] };
            db.Recordings.Add(recording);

            db.Add(new Track
            {
                Id = TrackId.New(),
                ReleaseId = release.Id,
                RecordingId = recording.Id,
                Position = index + 1,
                DiscNumber = 1,
                Title = titles[index],
            });
        }
    }

    /// <summary>A file filed on a position of a release.</summary>
    private static MediaFile Seated(
        FonotecaDbContext db,
        string path,
        Release release,
        int position)
    {
        var track = db.ChangeTracker.Entries<Track>()
            .Select(entry => entry.Entity)
            .Single(t => t.ReleaseId == release.Id && t.Position == position);

        var file = File(path, release);
        file.TrackId = track.Id;
        file.RecordingId = track.RecordingId;

        return file;
    }

    private static AudioQuality Measured(int depth, int rate) => new()
    {
        Codec = "flac",
        SampleRateHz = rate,
        Channels = 2,
        BitDepth = depth,
        BitrateBps = 900_000,
        IsLossless = true,
    };

    private static Artist Artist(string name) =>
        new() { Id = ArtistId.New(), Name = name, SortName = name };

    /// <summary>A release, and the album it is an edition of — every release the pass writes has one.</summary>
    private static Release Release(
        FonotecaDbContext db,
        string title,
        int? trackCount = null,
        ReleaseGroup? album = null)
    {
        if (album is null)
        {
            album = new ReleaseGroup
            {
                Id = ReleaseGroupId.New(),
                Title = title,
                Mbid = new Mbid(Guid.CreateVersion7()),
            };

            db.ReleaseGroups.Add(album);
        }

        var release = new Release
        {
            Id = ReleaseId.New(),
            Title = title,
            Mbid = new Mbid(Guid.CreateVersion7()),
            ReleaseGroupId = album.Id,
            Released = new ReleaseDate(2013, null, null),
            Status = "Official",
            TrackCount = trackCount,
        };

        db.Releases.Add(release);
        return release;
    }

    private static ArtistCredit Credit(Artist artist, Release release, int position, string? join) =>
        new()
        {
            Id = Guid.CreateVersion7(),
            ArtistId = artist.Id,
            ReleaseId = release.Id,
            Position = position,
            JoinPhrase = join,
            CreditedAs = artist.Name,
        };

    /// <summary>A release group, as the discography browse would have written it.</summary>
    private static ReleaseGroup Group(
        FonotecaDbContext db,
        string title,
        string? primaryType,
        string? secondaryTypes = null)
    {
        var group = new ReleaseGroup
        {
            Id = ReleaseGroupId.New(),
            Title = title,
            Mbid = new Mbid(Guid.CreateVersion7()),
            PrimaryType = primaryType,
            SecondaryTypes = secondaryTypes,
            FirstReleaseYear = 1999,
        };

        db.ReleaseGroups.Add(group);
        return group;
    }

    /// <summary>
    /// The credit the discography browse writes: an artist against a release
    /// group rather than a release, which is the only thing that fills
    /// <c>ArtistCredit.ReleaseGroupId</c>.
    /// </summary>
    private static ArtistCredit GroupCredit(Artist artist, ReleaseGroup group) =>
        new()
        {
            Id = Guid.CreateVersion7(),
            ArtistId = artist.Id,
            ReleaseGroupId = group.Id,
            Position = 0,
            CreditedAs = artist.Name,
        };

    private static MediaFile File(string path, Release? release) => new()
    {
        Id = MediaFileId.New(),
        Path = path,
        SizeBytes = 42_000_000,
        LastModifiedUtc = DateTimeOffset.Parse("2026-01-01T00:00:00Z", CultureInfo.InvariantCulture),
        ReleaseId = release?.Id,
        ReleaseGroupId = release?.ReleaseGroupId,
    };
}
