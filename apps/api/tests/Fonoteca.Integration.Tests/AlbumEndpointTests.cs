using System.Globalization;
using System.Net.Http.Json;
using Fonoteca.Api.Endpoints;
using Fonoteca.Api.Library;
using Fonoteca.Domain.Abstractions;
using Fonoteca.Domain.Catalogue;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;

namespace Fonoteca.Integration.Tests;

/// <summary>
/// An album is a release group, whether or not the pressing its files are is known.
/// </summary>
/// <remarks>
/// The shapes an edition-keyed album list could not show: two rips of one album
/// filed under different pressings, or one of them under none, and an album whose
/// files are known to be it without any edition of it stored at all.
/// </remarks>
[Collection(nameof(PostgresCollection))]
public sealed class AlbumEndpointTests(PostgresFixture postgres) : IAsyncLifetime
{
    private string _connectionString = string.Empty;
    private string _root = string.Empty;
    private WebApplicationFactory<Program>? _factory;

    private Seeded _seed = new();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _connectionString = await postgres.CreateDatabaseAsync(Token);
        _root = Directory.CreateTempSubdirectory("fonoteca-albums-api-").FullName;

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:Fonoteca", _connectionString);
            builder.UseSetting("Fonoteca:LibraryPath", _root);
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
    public async Task TwoRipsOfOneAlbumAreOneAlbumWithTwoFolders()
    {
        using var client = _factory!.CreateClient();

        var list = await client.GetFromJsonAsync<AlbumListResponse>(
            new Uri("/api/catalogue/albums", UriKind.Relative), Token);

        var album = Assert.Single(list!.Items, item => item.Title == "Unforgettable");

        // One album, six files, and no pressing named: one rip is filed under the
        // standard edition and the other under none, so neither can speak for both.
        Assert.Equal(6, album.Files);
        Assert.Null(album.EditionId);
        Assert.Null(album.TrackCount);
        Assert.Null(album.Held);
        Assert.Equal(_seed.Standard, album.CoverReleaseId);
        Assert.Equal("Nat King Cole", album.Artist);

        var detail = await AlbumAsync(client, _seed.Unforgettable);

        Assert.Equal<(string, Guid?)>(
            [("Nat King Cole/Unforgettable", _seed.Standard), ("Nat King Cole/Unforgettable (Hi-Res)", null)],
            detail.Folders.Select(folder => (folder.Path, folder.EditionId)));

        // The standard edition's running order, then what only the deluxe prints.
        Assert.Equal(
            ["Mona Lisa", "Route 66", "Too Young", "Pretend", "Answer Me"],
            detail.Tracks.Select(track => track.Title));
        Assert.Equal([true, true, true, false, false], detail.Tracks.Select(track => track.Held));
        Assert.Equal([_seed.Standard, _seed.Deluxe], detail.Tracks[0].On);
        Assert.Equal([_seed.Deluxe], detail.Tracks[3].On);

        // Both rips of "Mona Lisa" sit under its one row.
        Assert.Equal(2, detail.Tracks[0].Files.Count);

        // And a file the hi-res folder holds that no edition prints is listed apart
        // rather than dropped.
        var stray = Assert.Single(detail.Unplaced);
        Assert.Equal("Orange Colored Sky", stray.Recording);

        // What each track is on is named against the editions listed, the
        // running order's edition first.
        Assert.Equal(["Unforgettable", "Unforgettable (Deluxe)"], detail.Editions.Select(edition => edition.Title));

        // Each folder in its own order: the pressing's where there is one, the
        // file names' numbers where there is not — and a position only against
        // a pressing.
        var standard = detail.Folders[0];
        Assert.Equal([1, 2, 3], standard.Files.Select(file => file.Position));
        var hiRes = detail.Folders[1];
        Assert.All(hiRes.Files, file => Assert.Null(file.Position));
        Assert.Equal(["Mona Lisa", "Orange Colored Sky", "Route 66"], hiRes.Files.Select(file => file.Recording));

        // No pressing is claimed, so nothing only a pressing has is shown — not
        // even a person's own correction to one, which is still a fact about a
        // pressing nobody chose.
        Assert.Null(detail.About.Label);
        Assert.Null(detail.About.Barcode);
        Assert.Null(detail.About.EditionMbid);
        Assert.Null(detail.Album.Country);

        // The deluxe's bonus tracks are listed, but nobody on them is credited to
        // an album that holds none of them.
        Assert.DoesNotContain(detail.Credits, credit => credit.Name == "A Guest");
    }

    /// <summary>
    /// An album with no pressing claimed is edited through its display edition,
    /// and the form's blanks for what only a pressing has delete nothing.
    /// </summary>
    /// <remarks>
    /// One rip of it is filed under that edition, so "any file under it" would
    /// have let the page's hidden, empty pressing fields through as edits.
    /// </remarks>
    [Fact]
    public async Task EditingAnAlbumWithNoPressingLeavesThePressingsFactsAlone()
    {
        using var client = _factory!.CreateClient();

        var form = new ReleaseEditRequest(
            Title: "Unforgettable (edited)",
            Credit: "Nat King Cole",
            Disambiguation: null,
            PrimaryType: "Album",
            SecondaryTypes: [],
            FirstReleaseYear: null,
            ReleasedYear: null,
            ReleasedMonth: null,
            ReleasedDay: null,
            Country: null,
            Status: null,
            Label: null,
            CatalogNumber: null,
            Barcode: null,
            Formats: null,
            Review: null);

        var saved = await client.PostAsJsonAsync(
            new Uri($"/api/catalogue/releases/{_seed.Standard}/edits", UriKind.Relative), form, Token);

        saved.EnsureSuccessStatusCode();

        await using var db = PostgresFixture.CreateContext(_connectionString);

        var standard = await db.Releases.SingleAsync(release => release.Id == new ReleaseId(_seed.Standard), Token);
        var edits = PersonEdits.Read(standard.EditsJson);

        // The album's title on the album, so it stays when the page shows another edition.
        var album = await db.ReleaseGroups.SingleAsync(group => group.Id == standard.ReleaseGroupId, Token);
        Assert.Equal("Unforgettable (edited)", PersonEdits.Read(album.EditsJson)["title"]);
        Assert.False(edits.ContainsKey("title"));
        Assert.Equal("Capitol (corrected)", edits["label"]);
        Assert.Equal("US", edits["country"]);
        Assert.Equal("0077778", PersonEdits.Apply(edits, "barcode", standard.Barcode));

        var detail = await AlbumAsync(client, _seed.Unforgettable);
        Assert.Equal("Unforgettable (edited)", detail.Album.Title);
    }

    /// <summary>
    /// On the artist page, an album with no pressing claimed shows its display
    /// edition's stored sleeve and billing line, as the album list does.
    /// </summary>
    [Fact]
    public async Task AnArtistsAlbumWithNoPressingIsDrawnFromItsDisplayEdition()
    {
        ArtistId cole;

        await using (var db = PostgresFixture.CreateContext(_connectionString))
        {
            cole = (await db.Artists.SingleAsync(artist => artist.Name == "Nat King Cole", Token)).Id;
            var song = await db.Recordings.SingleAsync(recording => recording.Title == "Orange Colored Sky", Token);

            db.ArtistCredits.Add(new ArtistCredit
            {
                Id = Guid.CreateVersion7(),
                ArtistId = cole,
                RecordingId = song.Id,
                Position = 0,
            });

            await db.SaveChangesAsync(Token);
        }

        using var client = _factory!.CreateClient();

        var page = await client.GetFromJsonAsync<ArtistDetailResponse>(
            new Uri($"/api/catalogue/artists/{cole.Value}", UriKind.Relative), Token);

        var sky = Assert.Single(page!.Tracks, track => track.Title == "Orange Colored Sky");

        Assert.NotNull(sky.Album);
        Assert.Null(sky.Album.EditionId);
        Assert.Equal(_seed.Standard, sky.Album.CoverReleaseId);
        Assert.Equal("Nat King Cole", sky.Album.Artist);
        Assert.True(sky.Album.Billed);
    }

    /// <summary>
    /// A box carrying the album on vinyl and again on CD neither doubles the
    /// track list nor sets its order for a folder that is the download.
    /// </summary>
    /// <remarks>
    /// With no pressing claimed and no edition holding files, "longest first"
    /// led with the box, and the CD inside it listed every song a second time,
    /// with no file, as a track the library was missing.
    /// </remarks>
    [Fact]
    public async Task AVinylAndCdBoxListsEachSongOnceAndTheNearestEditionLeads()
    {
        Guid albumId;
        Guid download;

        await using (var db = PostgresFixture.CreateContext(_connectionString))
        {
            var album = new ReleaseGroup { Id = ReleaseGroupId.New(), Title = "One Deep River", Mbid = new Mbid(Guid.CreateVersion7()) };
            var box = Edition(album, "One Deep River (vinyl + CD)", 2024);
            var digital = Edition(album, "One Deep River", 2024);
            digital.MediumFormats = "Digital Media";

            db.ReleaseGroups.Add(album);
            db.Releases.AddRange(box, digital);

            var songs = new[] { "Two Pairs of Hands", "Ahead of the Game" }
                .Select(title => new Recording { Id = RecordingId.New(), Title = title, Mbid = new Mbid(Guid.CreateVersion7()) })
                .ToList();
            db.Recordings.AddRange(songs);

            Track(db, box, songs[0], 1);
            Track(db, box, songs[1], 2).DiscNumber = 2;
            Track(db, box, songs[0], 3).DiscNumber = 3;
            Track(db, box, songs[1], 4).DiscNumber = 3;
            Track(db, digital, songs[0], 1);
            Track(db, digital, songs[1], 2);

            foreach (var (song, n) in songs.Select((song, n) => (song, n)))
            {
                var file = File($"Mark Knopfler/One Deep River/0{n + 1}.m4a", song);
                file.ReleaseGroupId = album.Id;
                file.AttributionOutcome = ReleaseAttributionOutcome.GroupOnly;
                db.MediaFiles.Add(file);
            }

            await db.SaveChangesAsync(Token);

            albumId = album.Id.Value;
            download = digital.Id.Value;
        }

        using var client = _factory!.CreateClient();

        var detail = await AlbumAsync(client, albumId);

        Assert.Equal(["Two Pairs of Hands", "Ahead of the Game"], detail.Tracks.Select(track => track.Title));
        Assert.All(detail.Tracks, track => Assert.Single(track.Files));
        Assert.Equal(download, detail.Editions[0].Id);
    }

    /// <summary>
    /// A proven pressing that prints one recording twice keeps both places, each
    /// with the file filed on it.
    /// </summary>
    /// <remarks>
    /// "All Night Long" and its 12" version on one pressing are one recording in
    /// MusicBrainz. Collapsing them would read "2 of 3" on an album held whole.
    /// </remarks>
    [Fact]
    public async Task AClaimedPressingPrintingARecordingTwiceKeepsBothPlaces()
    {
        Guid albumId;

        await using (var db = PostgresFixture.CreateContext(_connectionString))
        {
            var album = new ReleaseGroup { Id = ReleaseGroupId.New(), Title = "Can't Slow Down", Mbid = new Mbid(Guid.CreateVersion7()) };
            var pressing = Edition(album, "Can't Slow Down", 1983);

            db.ReleaseGroups.Add(album);
            db.Releases.Add(pressing);

            var night = new Recording { Id = RecordingId.New(), Title = "All Night Long", Mbid = new Mbid(Guid.CreateVersion7()) };
            var hello = new Recording { Id = RecordingId.New(), Title = "Hello", Mbid = new Mbid(Guid.CreateVersion7()) };
            db.Recordings.AddRange(night, hello);

            var slots = new[] { Track(db, pressing, night, 1), Track(db, pressing, hello, 2), Track(db, pressing, night, 3) };

            foreach (var (slot, n) in slots.Select((slot, n) => (slot, n)))
            {
                var file = File($"Lionel Richie/Can't Slow Down/0{n + 1}.flac", n == 1 ? hello : night);
                file.ReleaseGroupId = album.Id;
                file.ReleaseId = pressing.Id;
                file.TrackId = slot.Id;
                file.AttributionOutcome = ReleaseAttributionOutcome.Attributed;
                db.MediaFiles.Add(file);
            }

            await db.SaveChangesAsync(Token);

            albumId = album.Id.Value;
        }

        using var client = _factory!.CreateClient();

        var detail = await AlbumAsync(client, albumId);

        Assert.Equal([1, 2, 3], detail.Tracks.Select(track => track.Position));
        Assert.Equal(
            ["Lionel Richie/Can't Slow Down/01.flac", "Lionel Richie/Can't Slow Down/02.flac", "Lionel Richie/Can't Slow Down/03.flac"],
            detail.Tracks.Select(track => Assert.Single(track.Files).Path));
    }

    /// <summary>
    /// A file filed under a claimed pressing that has lost its slot is still
    /// listed, under its recording, rather than dropped from the page.
    /// </summary>
    /// <remarks>
    /// Re-reading a track list MusicBrainz has since changed nulls the track on
    /// files filed there; the release and the recording stay.
    /// </remarks>
    [Fact]
    public async Task AFileThatLostItsSlotOnAClaimedPressingIsStillListed()
    {
        Guid albumId;

        await using (var db = PostgresFixture.CreateContext(_connectionString))
        {
            var album = new ReleaseGroup { Id = ReleaseGroupId.New(), Title = "Hello", Mbid = new Mbid(Guid.CreateVersion7()) };
            var pressing = Edition(album, "Hello", 1983);

            db.ReleaseGroups.Add(album);
            db.Releases.Add(pressing);

            var songs = new[] { "Hello", "Penny Lover" }
                .Select(title => new Recording { Id = RecordingId.New(), Title = title, Mbid = new Mbid(Guid.CreateVersion7()) })
                .ToList();
            db.Recordings.AddRange(songs);

            var slots = songs.Select((song, n) => Track(db, pressing, song, n + 1)).ToList();

            foreach (var (song, n) in songs.Select((song, n) => (song, n)))
            {
                var file = File($"Lionel Richie/Hello/0{n + 1}.flac", song);
                file.ReleaseGroupId = album.Id;
                file.ReleaseId = pressing.Id;
                file.TrackId = n == 0 ? slots[0].Id : null;
                file.AttributionOutcome = ReleaseAttributionOutcome.Attributed;
                db.MediaFiles.Add(file);
            }

            await db.SaveChangesAsync(Token);

            albumId = album.Id.Value;
        }

        using var client = _factory!.CreateClient();

        var detail = await AlbumAsync(client, albumId);

        Assert.Equal(
            ["Lionel Richie/Hello/01.flac", "Lionel Richie/Hello/02.flac"],
            detail.Tracks.Select(track => Assert.Single(track.Files).Path));
    }

    [Fact]
    public async Task AnAlbumKnownOnlyByItsGroupIsListedAndHasAPage()
    {
        using var client = _factory!.CreateClient();

        var list = await client.GetFromJsonAsync<AlbumListResponse>(
            new Uri("/api/catalogue/albums", UriKind.Relative), Token);

        var album = Assert.Single(list!.Items, item => item.Title == "Higher");

        Assert.Equal(_seed.HigherMbid, album.Mbid);
        Assert.Null(album.EditionId);
        Assert.Null(album.CoverReleaseId);
        Assert.Equal(2023, album.Year);
        Assert.Equal("GroupOnly", album.Certainty);

        // No edition stored, so the billing line is the album's own.
        Assert.Equal("Chris Stapleton", album.Artist);

        var detail = await AlbumAsync(client, _seed.Higher);

        Assert.Empty(detail.Tracks);
        Assert.Equal(2, detail.Unplaced.Count);
        Assert.Equal(["What Am I Gonna Do", "South Dakota"], detail.Unplaced.Select(file => file.Recording));
    }

    /// <summary>
    /// The Files page counts a file held to an album as filed, whether or not
    /// its pressing is known — "0 filed" would send a person to a screen with
    /// nothing on it to answer.
    /// </summary>
    [Fact]
    public async Task AFolderFiledOnlyToItsAlbumCountsAsFiled()
    {
        var folder = Path.Combine(_root, "Chris Stapleton", "Higher (2023)");
        Directory.CreateDirectory(folder);
        await System.IO.File.WriteAllTextAsync(Path.Combine(folder, "01 - What Am I Gonna Do.flac"), "", Token);
        await System.IO.File.WriteAllTextAsync(Path.Combine(folder, "02 - South Dakota.flac"), "", Token);

        using var client = _factory!.CreateClient();

        var listing = await client.GetFromJsonAsync<FolderListing>(
            new Uri("/api/files?path=Chris%20Stapleton", UriKind.Relative), Token);

        var album = Assert.Single(listing!.Entries, entry => entry.Name == "Higher (2023)");
        Assert.Equal(2, album.CataloguedFiles);
        Assert.Equal(2, album.Attributed);
    }

    [Fact]
    public async Task AFolderAnsweredAsNoReleaseIsAnAlbumUnlessSomethingElseClaimsIt()
    {
        await using (var db = PostgresFixture.CreateContext(_connectionString))
        {
            MediaFile Loose(string path) => new()
            {
                Id = MediaFileId.New(),
                Path = path,
                SizeBytes = 30_000_000,
                LastModifiedUtc = DateTimeOffset.Parse("2026-01-01T00:00:00Z", CultureInfo.InvariantCulture),
                AcoustIdOutcome = AcoustIdOutcome.Unreleased,
                IdentityDecidedUtc = DateTimeOffset.UtcNow,
            };

            var open = Loose("Pepita Salim/Island Sun/02 - Island Sun (Live).flac");
            open.AcoustIdOutcome = AcoustIdOutcome.Unknown;
            open.IdentityDecidedUtc = null;

            // By attribution alone, a disc down: still the one album folder.
            var disc = Loose("Emma Kok/Disney in Concert/CD 1/01 - Show Yourself.flac");
            disc.AcoustIdOutcome = AcoustIdOutcome.Identified;
            disc.AttributionOutcome = ReleaseAttributionOutcome.UnreleasedByAgent;

            var unlinked = Loose("Emma Kok/5 Mei Concert/01 - Als Wij Niks Doen.flac");
            unlinked.AcoustIdOutcome = AcoustIdOutcome.Identified;
            unlinked.EnrichmentOutcome = EnrichmentOutcome.UnreleasedByAgent;

            var unreadable = Loose("Pepita Salim/Demos/01 - Demo.flac");
            unreadable.AcoustIdOutcome = AcoustIdOutcome.Unfingerprintable;

            db.MediaFiles.AddRange(
                Loose("Pepita Salim/Covers/01 - Gravity.flac"),
                Loose("Pepita Salim/Covers/02 - True Colors.flac"),
                Loose("Pepita Salim/Island Sun/01 - Island Sun.flac"),
                open,
                disc,
                unlinked,
                unreadable,

                // Beside files already filed under an album: that album, not a second.
                Loose("Nat King Cole/Unforgettable/09 - Home Recording.flac"),

                // Loose under the artist: the artist's folder is its album, and the
                // filed albums below it are not.
                Loose("Nat King Cole/Home Recording.flac"));

            await db.SaveChangesAsync(Token);
        }

        using var client = _factory!.CreateClient();

        var list = await client.GetFromJsonAsync<AlbumListResponse>(
            new Uri("/api/catalogue/albums", UriKind.Relative), Token);

        Assert.Equal(2, list!.Total);
        Assert.Equal(
            [
                new NoReleaseAlbum("Emma Kok/5 Mei Concert", "5 Mei Concert", "Emma Kok", 1),
                new NoReleaseAlbum("Pepita Salim/Covers", "Covers", "Pepita Salim", 2),
                new NoReleaseAlbum("Emma Kok/Disney in Concert", "Disney in Concert", "Emma Kok", 1),
                new NoReleaseAlbum("Nat King Cole", "Nat King Cole", null, 1),
            ],
            list.NoRelease);

        var filtered = await client.GetFromJsonAsync<AlbumListResponse>(
            new Uri("/api/catalogue/albums?query=disney", UriKind.Relative), Token);

        Assert.Equal(["Emma Kok/Disney in Concert"], filtered!.NoRelease.Select(album => album.Folder));

        var info = await client.GetFromJsonAsync<SystemInfoResponse>(
            new Uri("/api/system/info", UriKind.Relative), Token);

        Assert.Equal(6, info!.Counts.Albums);
    }

    private static async Task<AlbumDetailResponse> AlbumAsync(HttpClient client, Guid id)
    {
        var body = await client.GetFromJsonAsync<AlbumDetailResponse>(
            new Uri($"/api/catalogue/albums/{id}", UriKind.Relative), Token);

        Assert.NotNull(body);
        return body;
    }

    private async Task<Seeded> SeedAsync()
    {
        await using var db = PostgresFixture.CreateContext(_connectionString);

        var cole = new Artist
        {
            Id = ArtistId.New(),
            Name = "Nat King Cole",
            SortName = "Cole, Nat King",
            Type = "Person",
            Mbid = new Mbid(Guid.CreateVersion7()),
        };

        var stapleton = new Artist
        {
            Id = ArtistId.New(),
            Name = "Chris Stapleton",
            SortName = "Stapleton, Chris",
            Type = "Person",
            Mbid = new Mbid(Guid.CreateVersion7()),
        };

        db.Artists.AddRange(cole, stapleton);

        var songs = new[] { "Mona Lisa", "Route 66", "Too Young", "Pretend", "Answer Me", "Orange Colored Sky" }
            .Select(title => new Recording
            {
                Id = RecordingId.New(),
                Title = title,
                Mbid = new Mbid(Guid.CreateVersion7()),
            })
            .ToList();

        db.Recordings.AddRange(songs);

        var unforgettable = new ReleaseGroup
        {
            Id = ReleaseGroupId.New(),
            Title = "Unforgettable",
            Mbid = new Mbid(Guid.CreateVersion7()),
            PrimaryType = "Album",
        };

        var standard = Edition(unforgettable, "Unforgettable", 1954);
        standard.Label = "Capitol";
        standard.Barcode = "0077778";
        standard.EditsJson = PersonEdits.Write(new Dictionary<string, string?> { ["label"] = "Capitol (corrected)", ["country"] = "US" });
        var deluxe = Edition(unforgettable, "Unforgettable (Deluxe)", 2004);

        db.ReleaseGroups.Add(unforgettable);
        db.Releases.AddRange(standard, deluxe);
        db.ArtistCredits.Add(new ArtistCredit
        {
            Id = Guid.CreateVersion7(),
            ArtistId = cole.Id,
            ReleaseId = standard.Id,
            Position = 0,
        });

        var standardTracks = songs.Take(3)
            .Select((song, n) => Track(db, standard, song, n + 1))
            .ToList();

        foreach (var (song, n) in songs.Take(5).Select((song, n) => (song, n))) Track(db, deluxe, song, n + 1);

        // A guest billed on a bonus track only the deluxe prints.
        var guest = new Artist
        {
            Id = ArtistId.New(),
            Name = "A Guest",
            SortName = "Guest, A",
            Type = "Person",
            Mbid = new Mbid(Guid.CreateVersion7()),
        };
        db.Artists.Add(guest);
        db.ArtistCredits.Add(new ArtistCredit
        {
            Id = Guid.CreateVersion7(),
            ArtistId = guest.Id,
            RecordingId = songs[4].Id,
            Position = 0,
        });

        // One rip filed under the standard edition...
        for (var n = 0; n < 3; n++)
        {
            var file = File($"Nat King Cole/Unforgettable/0{n + 1} - {songs[n].Title}.flac", songs[n]);
            file.ReleaseGroupId = unforgettable.Id;
            file.ReleaseId = standard.Id;
            file.TrackId = standardTracks[n].Id;
            file.AttributionOutcome = ReleaseAttributionOutcome.Attributed;
            db.MediaFiles.Add(file);
        }

        // ...and one held only to the album: two of its songs, and one no edition
        // prints.
        foreach (var song in new[] { songs[0], songs[1], songs[5] })
        {
            var file = File($"Nat King Cole/Unforgettable (Hi-Res)/{song.Title}.flac", song);
            file.ReleaseGroupId = unforgettable.Id;
            file.AttributionOutcome = ReleaseAttributionOutcome.GroupOnly;
            db.MediaFiles.Add(file);
        }

        // An album with no edition stored, credited to its artist by the
        // discography browse.
        var higher = new ReleaseGroup
        {
            Id = ReleaseGroupId.New(),
            Title = "Higher",
            Mbid = new Mbid(Guid.CreateVersion7()),
            PrimaryType = "Album",
            FirstReleaseYear = 2023,
        };

        db.ReleaseGroups.Add(higher);
        db.ArtistCredits.Add(new ArtistCredit
        {
            Id = Guid.CreateVersion7(),
            ArtistId = stapleton.Id,
            ReleaseGroupId = higher.Id,
            Position = 0,
        });

        foreach (var (title, n) in new[] { "What Am I Gonna Do", "South Dakota" }.Select((title, n) => (title, n)))
        {
            var song = new Recording { Id = RecordingId.New(), Title = title, Mbid = new Mbid(Guid.CreateVersion7()) };
            db.Recordings.Add(song);

            var file = File($"Chris Stapleton/Higher (2023)/0{n + 1} - {title}.flac", song);
            file.ReleaseGroupId = higher.Id;
            file.AttributionOutcome = ReleaseAttributionOutcome.GroupOnly;
            db.MediaFiles.Add(file);
        }

        await db.SaveChangesAsync(Token);

        return new Seeded
        {
            Unforgettable = unforgettable.Id.Value,
            Standard = standard.Id.Value,
            Deluxe = deluxe.Id.Value,
            Higher = higher.Id.Value,
            HigherMbid = higher.Mbid!.Value.Value,
        };
    }

    private static Release Edition(ReleaseGroup group, string title, int year) => new()
    {
        Id = ReleaseId.New(),
        Title = title,
        Mbid = new Mbid(Guid.CreateVersion7()),
        ReleaseGroupId = group.Id,
        Released = new ReleaseDate(year, null, null),
        Status = "Official",
        MediumFormats = "CD",
        DiscCount = 1,
    };

    private static Track Track(Fonoteca.Data.FonotecaDbContext db, Release release, Recording song, int position)
    {
        var track = new Track
        {
            Id = TrackId.New(),
            ReleaseId = release.Id,
            RecordingId = song.Id,
            Position = position,
            DiscNumber = 1,
            Number = position.ToString(CultureInfo.InvariantCulture),
            Title = song.Title,
            Length = TimeSpan.FromSeconds(180),
        };

        db.Tracks.Add(track);
        return track;
    }

    private static MediaFile File(string path, Recording recording) => new()
    {
        Id = MediaFileId.New(),
        Path = path,
        SizeBytes = 30_000_000,
        LastModifiedUtc = DateTimeOffset.Parse("2026-01-01T00:00:00Z", CultureInfo.InvariantCulture),
        RecordingId = recording.Id,
        RecordingLookupUtc = DateTimeOffset.UtcNow,
        EnrichmentOutcome = EnrichmentOutcome.Linked,
    };

    private sealed record Seeded
    {
        public Guid Unforgettable { get; init; }

        public Guid Standard { get; init; }

        public Guid Deluxe { get; init; }

        public Guid Higher { get; init; }

        public Guid HigherMbid { get; init; }
    }
}
