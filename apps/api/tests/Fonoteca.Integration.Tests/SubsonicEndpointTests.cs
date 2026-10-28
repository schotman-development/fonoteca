using System.Buffers.Text;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Fonoteca.Data;
using Fonoteca.Domain.Abstractions;
using Fonoteca.Domain.Catalogue;
using Fonoteca.Fixtures;
using Fonoteca.Providers.Wikidata;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Fonoteca.Integration.Tests;

/// <summary>
/// The OpenSubsonic surface: what somebody else's music client gets.
/// </summary>
/// <remarks>
/// Five things, and each of them is a way the whole surface stops working
/// rather than a detail of one endpoint: the door, the two wire formats, the
/// bytes, the half of the library the catalogue has not described yet, and the
/// generated contract this must stay out of.
/// </remarks>
[Collection(nameof(PostgresCollection))]
public sealed class SubsonicEndpointTests(PostgresFixture postgres) : IAsyncLifetime
{
    private const string Password = "a-test-passphrase";

    private string _connectionString = string.Empty;
    private string _root = string.Empty;
    private WebApplicationFactory<Program>? _factory;
    private MediaFileId _filed;
    private MediaFileId _scanned;
    private Guid _account;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _connectionString = await postgres.CreateDatabaseAsync(Token);
        _root = Directory.CreateTempSubdirectory("fonoteca-subsonic-").FullName;

        WriteFile("Bill Evans/Waltz for Debby/01 My Foolish Heart.flac", new string('x', 4096));
        WriteFile("Somebody/A Bootleg/01 Untitled.flac", new string('y', 512));
        WriteFile("Somebody/Scanned Not Filed/01 Nameless.flac", new string('z', 256));

        _factory = Factory();

        using (var warm = _factory.CreateClient())
        {
            using var boot = await warm.GetAsync(new Uri("/health", UriKind.Relative), Token);
        }

        var (signedIn, account) = await _factory.SetUpAsync("me", Password, Token);
        signedIn.Dispose();
        _account = account;

        await using var db = PostgresFixture.CreateContext(_connectionString);

        var artist = new Artist { Id = ArtistId.New(), Name = "Bill Evans" };

        var group = new ReleaseGroup { Id = ReleaseGroupId.New(), Title = "Waltz for Debby" };

        var release = new Release
        {
            Id = ReleaseId.New(),
            Title = "Waltz for Debby",
            ReleaseGroupId = group.Id,
            Released = new ReleaseDate(1962, null, null),
            TrackCount = 1,
            DiscCount = 1,
        };

        var recording = new Recording { Id = RecordingId.New(), Title = "My Foolish Heart" };

        var track = new Track
        {
            Id = TrackId.New(),
            ReleaseId = release.Id,
            RecordingId = recording.Id,
            Position = 1,
            DiscNumber = 1,
            Title = "My Foolish Heart",
        };

        // Filed: identified, credited and attributed, which is what the album
        // half of the protocol answers from.
        var filed = new MediaFile
        {
            Id = MediaFileId.New(),
            Path = "Bill Evans/Waltz for Debby/01 My Foolish Heart.flac",
            SizeBytes = 4096,
            LastModifiedUtc = DateTimeOffset.Parse("2026-01-01T00:00:00Z", CultureInfo.InvariantCulture),
            RecordingId = recording.Id,
            ReleaseId = release.Id,
            ReleaseGroupId = group.Id,
            TrackId = track.Id,
            Quality = new AudioQuality
            {
                Codec = "flac",
                SampleRateHz = 44_100,
                Channels = 2,
                BitDepth = 16,
                BitrateBps = 900_000,
                IsLossless = true,
                Duration = TimeSpan.FromSeconds(292),
            },
        };

        // Scanned and probed, and nothing more: no recording, no release, no
        // track. The state most of a real library is in for most of its life.
        var scanned = new MediaFile
        {
            Id = MediaFileId.New(),
            Path = "Somebody/Scanned Not Filed/01 Nameless.flac",
            SizeBytes = 256,
            LastModifiedUtc = DateTimeOffset.Parse("2026-01-02T00:00:00Z", CultureInfo.InvariantCulture),
            Quality = new AudioQuality
            {
                Codec = "flac",
                SampleRateHz = 44_100,
                Channels = 2,
                BitDepth = 16,
                BitrateBps = 700_000,
                IsLossless = true,
                Duration = TimeSpan.FromSeconds(61),
            },
        };

        _filed = filed.Id;
        _scanned = scanned.Id;

        db.Artists.Add(artist);
        db.ReleaseGroups.Add(group);
        db.Releases.Add(release);
        db.Recordings.Add(recording);
        db.Tracks.Add(track);
        db.MediaFiles.Add(filed);
        db.MediaFiles.Add(scanned);

        db.ArtistCredits.Add(new ArtistCredit
        {
            Id = Guid.CreateVersion7(),
            ArtistId = artist.Id,
            ReleaseId = release.Id,
            Position = 0,
        });

        await db.SaveChangesAsync(Token);
    }

    public async ValueTask DisposeAsync()
    {
        if (_factory is not null)
        {
            await _factory.DisposeAsync();
        }

        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    /// <summary>
    /// Before the first account is made nobody signs in, with any password.
    /// </summary>
    [Fact]
    public async Task BeforeAnyAccountNobodySignsIn()
    {
        await using var fresh = Factory(await postgres.CreateDatabaseAsync(Token));
        using var client = fresh.CreateClient();

        using var response = await client.GetAsync(
            new Uri("/rest/ping.view?u=me&p=anything&v=1.16.1&c=test&f=json", UriKind.Relative),
            Token);

        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Token)).RootElement
            .GetProperty("subsonic-response");

        Assert.Equal(40, body.GetProperty("error").GetProperty("code").GetInt32());
    }

    /// <summary>
    /// All three ways a client may send the password are accepted.
    /// </summary>
    /// <remarks>
    /// Clients differ in which they send and none of them negotiates: DSub sends
    /// the salted token, older ones send <c>enc:</c> hex, and several send the
    /// password itself. Supporting one is supporting a third of the ecosystem.
    /// </remarks>
    [Fact]
    public async Task AllThreeWaysOfSendingThePasswordAreAccepted()
    {
        using var client = _factory!.CreateClient();

        const string salt = "abc123";

        var token = Convert.ToHexStringLower(
            MD5.HashData(Encoding.UTF8.GetBytes(Password + salt)));

        var hex = Convert.ToHexStringLower(Encoding.UTF8.GetBytes(Password));

        foreach (var credentials in new[]
        {
            $"p={Password}",
            $"p=enc:{hex}",
            $"t={token}&s={salt}",
        })
        {
            using var response = await client.GetAsync(
                new Uri($"/rest/ping.view?u=me&{credentials}&v=1.16.1&c=test&f=json", UriKind.Relative),
                Token);

            var json = JsonDocument
                .Parse(await response.Content.ReadAsStringAsync(Token))
                .RootElement
                .GetProperty("subsonic-response");

            Assert.Equal("ok", json.GetProperty("status").GetString());
        }
    }

    /// <summary>
    /// A client's page on any origin may call /rest, preflight and all, and
    /// nothing else opens to it.
    /// </summary>
    [Fact]
    public async Task AClientOnAnyOriginIsLetInToRestAndNowhereElse()
    {
        using var client = _factory!.CreateClient();

        using var ping = new HttpRequestMessage(
            HttpMethod.Get,
            new Uri($"/rest/ping?u=me&p={Password}&v=1.16.1&c=test&f=json", UriKind.Relative));
        ping.Headers.Add("Origin", "http://example.org");

        using var pinged = await client.SendAsync(ping, Token);
        Assert.Equal("*", Assert.Single(pinged.Headers.GetValues("Access-Control-Allow-Origin")));
        Assert.False(pinged.Headers.Contains("Access-Control-Allow-Credentials"));

        using var preflight = new HttpRequestMessage(HttpMethod.Options, new Uri("/rest/getOpenSubsonicExtensions", UriKind.Relative));
        preflight.Headers.Add("Origin", "http://example.org");
        preflight.Headers.Add("Access-Control-Request-Method", "GET");
        preflight.Headers.Add("Access-Control-Request-Headers", "content-type");

        using var preflighted = await client.SendAsync(preflight, Token);
        Assert.True(preflighted.IsSuccessStatusCode, $"{preflighted.StatusCode}");
        Assert.Equal("*", Assert.Single(preflighted.Headers.GetValues("Access-Control-Allow-Origin")));

        using var api = new HttpRequestMessage(HttpMethod.Get, new Uri("/api/library/scan", UriKind.Relative));
        api.Headers.Add("Origin", "http://example.org");

        using var refused = await client.SendAsync(api, Token);
        Assert.False(refused.Headers.Contains("Access-Control-Allow-Origin"));
    }

    /// <summary>
    /// A wrong password is a 200 carrying error 40, not a 401.
    /// </summary>
    /// <remarks>
    /// The protocol's own shape, and it is not cosmetic: a client that receives
    /// an HTTP error reports the server as unreachable, which sends somebody to
    /// look at their network instead of at their password.
    /// </remarks>
    [Fact]
    public async Task AWrongPasswordIsAnErrorInsideAnOrdinaryResponse()
    {
        using var client = _factory!.CreateClient();

        using var response = await client.GetAsync(
            new Uri("/rest/ping.view?u=me&p=not-it&v=1.16.1&c=test&f=json", UriKind.Relative),
            Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Token)).RootElement
            .GetProperty("subsonic-response");

        Assert.Equal("failed", body.GetProperty("status").GetString());
        Assert.Equal(40, body.GetProperty("error").GetProperty("code").GetInt32());
    }

    /// <summary>
    /// XML is what a client that does not ask gets, and both formats say the
    /// same thing.
    /// </summary>
    /// <remarks>
    /// <c>f=xml</c> is the protocol's default rather than a legacy option, so a
    /// server that only speaks JSON is broken for every client that omits the
    /// parameter. Asserting the album appears in both is what stops one of the
    /// two writers drifting.
    /// </remarks>
    [Fact]
    public async Task TheDefaultFormatIsXmlAndBothFormatsCarryTheSameAlbum()
    {
        using var client = _factory!.CreateClient();

        using var response = await client.GetAsync(
            new Uri($"/rest/getAlbumList2.view?{Credentials}&type=alphabeticalByName", UriKind.Relative),
            Token);

        Assert.Equal("application/xml", response.Content.Headers.ContentType?.MediaType);

        var xml = XDocument.Parse(await response.Content.ReadAsStringAsync(Token));
        XNamespace ns = "http://subsonic.org/restapi";

        Assert.Equal("ok", xml.Root?.Attribute("status")?.Value);

        var album = xml.Descendants(ns + "album").Single();

        Assert.Equal("Waltz for Debby", album.Attribute("name")?.Value);
        Assert.Equal("Bill Evans", album.Attribute("artist")?.Value);
        Assert.Equal("1962", album.Attribute("year")?.Value);

        var json = await JsonAsync(client, "/rest/getAlbumList2.view?type=alphabeticalByName");

        var same = json.GetProperty("subsonic-response")
            .GetProperty("albumList2")
            .GetProperty("album")
            .EnumerateArray()
            .Single();

        Assert.Equal("Waltz for Debby", same.GetProperty("name").GetString());
        Assert.Equal("Bill Evans", same.GetProperty("artist").GetString());
        Assert.Equal(1962, same.GetProperty("year").GetInt32());
        Assert.Equal(292, same.GetProperty("duration").GetInt32());

        // Added is when the library gained the file, the time in its UUIDv7
        // id, not the file's own change time.
        var added = DateTimeOffset.FromUnixTimeMilliseconds(
            Convert.ToInt64(_filed.Value.ToString("N")[..12], 16));
        var created = DateTimeOffset.Parse(same.GetProperty("created").GetString()!, CultureInfo.InvariantCulture);
        Assert.InRange(created, added.AddSeconds(-1), added.AddSeconds(1));
    }

    /// <summary>
    /// A song carries what the probe pass measured, and streams with ranges.
    /// </summary>
    /// <remarks>
    /// Seeking is the whole reason ranges matter to a music client, and the
    /// duration is what a progress bar is drawn from — which is why an unprobed
    /// library is a usable one only in the sense that the audio comes out.
    /// The bytes come from <c>FileEndpoints.GetContent</c>, so this also pins
    /// that <c>/rest</c> never grew its own copy of the file-serving path.
    /// </remarks>
    [Fact]
    public async Task AFiledSongCarriesItsMeasurementsAndStreamsARange()
    {
        using var client = _factory!.CreateClient();

        var album = await AlbumIdAsync();

        var song = (await JsonAsync(client, $"/rest/getAlbum.view?id={album}"))
            .GetProperty("subsonic-response")
            .GetProperty("album")
            .GetProperty("song")
            .EnumerateArray()
            .Single();

        Assert.Equal($"tr-{_filed.Value:N}", song.GetProperty("id").GetString());
        Assert.Equal("My Foolish Heart", song.GetProperty("title").GetString());
        Assert.Equal(292, song.GetProperty("duration").GetInt32());
        Assert.Equal(900, song.GetProperty("bitRate").GetInt32());
        Assert.Equal(44_100, song.GetProperty("samplingRate").GetInt32());
        Assert.Equal("audio/flac", song.GetProperty("contentType").GetString());

        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            new Uri($"/rest/stream.view?{Credentials}&id=tr-{_filed.Value:N}", UriKind.Relative));

        request.Headers.Range = new(1024, 2047);

        using var stream = await client.SendAsync(request, Token);

        Assert.Equal(HttpStatusCode.PartialContent, stream.StatusCode);
        Assert.Equal("audio/flac", stream.Content.Headers.ContentType?.MediaType);
        Assert.Equal(1024, stream.Content.Headers.ContentRange?.From);
        Assert.Equal(4096, stream.Content.Headers.ContentRange?.Length);
    }

    /// <summary>
    /// A file no pass has filed is reachable, by folder, and plays.
    /// </summary>
    /// <remarks>
    /// This is the reason both browsing modes are implemented rather than one.
    /// The album half of the protocol cannot describe this file — no artist, no
    /// album, no track — and the alternative to the folder half would have been
    /// inventing all three, which is a fourth matching pass whose worklist is
    /// exactly the files the other three refused.
    ///
    /// The file has no catalogue row at all here, so its id is its path: that is
    /// what makes it streamable before anything has scanned it.
    /// </remarks>
    [Fact]
    public async Task AFileNoPassHasFiledIsStillReachableByFolder()
    {
        using var client = _factory!.CreateClient();

        var folder = FolderId("Somebody/A Bootleg");

        var directory = (await JsonAsync(client, $"/rest/getMusicDirectory.view?id={folder}"))
            .GetProperty("subsonic-response")
            .GetProperty("directory");

        Assert.Equal("A Bootleg", directory.GetProperty("name").GetString());

        var child = directory.GetProperty("child").EnumerateArray().Single();

        Assert.Equal("01 Untitled", child.GetProperty("title").GetString());
        Assert.False(child.GetProperty("isDir").GetBoolean());

        // Nothing has decoded it, so nothing claims a duration for it.
        Assert.False(child.TryGetProperty("duration", out _));

        using var stream = await client.GetAsync(
            new Uri(
                $"/rest/stream.view?{Credentials}&id={child.GetProperty("id").GetString()}",
                UriKind.Relative),
            Token);

        Assert.Equal(HttpStatusCode.OK, stream.StatusCode);
        Assert.Equal(512, stream.Content.Headers.ContentLength);
    }

    /// <summary>
    /// A file with a row but no album is a song with no album, not an error.
    /// </summary>
    /// <remarks>
    /// The state most of a library is in for most of its life: scanned, probed,
    /// and not yet filed under anything. Every navigation off the media file is
    /// null here, so this is also what pins the projection — a query written to
    /// assume a release joins to one would fall over on the majority of a real
    /// catalogue rather than on an edge case.
    ///
    /// It keeps its catalogue id, because it has one. What it has no claim to is
    /// an album or an artist, and it makes neither.
    /// </remarks>
    [Fact]
    public async Task AScannedFileWithNoAlbumIsStillASongWithEverythingMeasured()
    {
        using var client = _factory!.CreateClient();

        var directory = (await JsonAsync(
                client,
                $"/rest/getMusicDirectory.view?id={FolderId("Somebody/Scanned Not Filed")}"))
            .GetProperty("subsonic-response")
            .GetProperty("directory");

        var child = directory.GetProperty("child").EnumerateArray().Single();

        Assert.Equal($"tr-{_scanned.Value:N}", child.GetProperty("id").GetString());
        Assert.Equal("01 Nameless", child.GetProperty("title").GetString());
        Assert.Equal(61, child.GetProperty("duration").GetInt32());
        Assert.False(child.TryGetProperty("album", out _));
        Assert.False(child.TryGetProperty("artist", out _));

        // And it does not turn up on the album shelf, which describes releases.
        var albums = (await JsonAsync(client, "/rest/getAlbumList2.view?size=500"))
            .GetProperty("subsonic-response")
            .GetProperty("albumList2")
            .GetProperty("album");

        Assert.Equal(1, albums.GetArrayLength());
    }

    /// <summary>
    /// An album whose pressing is not known is still an album a client can open
    /// and play, in the order its file names give.
    /// </summary>
    /// <remarks>
    /// Removes what it adds, because the class shares one database and the test
    /// above counts the album shelf.
    /// </remarks>
    [Fact]
    public async Task AnAlbumWithNoPressingIsStillAnAlbumWithItsSongsInOrder()
    {
        var album = new ReleaseGroup { Id = ReleaseGroupId.New(), Title = "Higher" };
        var songs = new[] { "10 The Bottom.flac", "2 South Dakota.flac" }
            .Select(name => (Name: name, Recording: new Recording { Id = RecordingId.New(), Title = name[(name.IndexOf(' ', StringComparison.Ordinal) + 1)..^5] }))
            .ToList();
        var files = songs
            .Select(song => new MediaFile
            {
                Id = MediaFileId.New(),
                Path = $"Chris Stapleton/Higher/{song.Name}",
                SizeBytes = 1024,
                LastModifiedUtc = DateTimeOffset.Parse("2026-01-03T00:00:00Z", CultureInfo.InvariantCulture),
                RecordingId = song.Recording.Id,
                ReleaseGroupId = album.Id,
                AttributionOutcome = ReleaseAttributionOutcome.GroupOnly,
            })
            .ToList();

        await using (var db = PostgresFixture.CreateContext(_connectionString))
        {
            db.ReleaseGroups.Add(album);
            db.Recordings.AddRange(songs.Select(song => song.Recording));
            db.MediaFiles.AddRange(files);
            await db.SaveChangesAsync(Token);
        }

        try
        {
            using var client = _factory!.CreateClient();

            var body = (await JsonAsync(client, $"/rest/getAlbum.view?id=al-{album.Id.Value:N}"))
                .GetProperty("subsonic-response")
                .GetProperty("album");

            Assert.Equal("Higher", body.GetProperty("name").GetString());

            // 2 before 10: the numbers in the names, not their characters.
            Assert.Equal(
                ["South Dakota", "The Bottom"],
                body.GetProperty("song").EnumerateArray().Select(song => song.GetProperty("title").GetString()));
        }
        finally
        {
            await using var db = PostgresFixture.CreateContext(_connectionString);
            db.MediaFiles.RemoveRange(await db.MediaFiles.Where(file => file.ReleaseGroupId == album.Id).ToListAsync(Token));
            db.ReleaseGroups.Remove(await db.ReleaseGroups.SingleAsync(row => row.Id == album.Id, Token));
            var recordings = songs.Select(song => song.Recording.Id).ToList();
            db.Recordings.RemoveRange(await db.Recordings.Where(row => recordings.Contains(row.Id)).ToListAsync(Token));
            await db.SaveChangesAsync(Token);
        }
    }

    /// <summary>
    /// An album lasts what its songs add up to, each song's length taken the
    /// way the song states it.
    /// </summary>
    /// <remarks>
    /// One file per source: the probe over the pressing's track length, the
    /// track length without a probe, the recording's length without either,
    /// and a file nothing measured, which adds nothing. Removes what it adds,
    /// because the class shares one database and a test above counts the
    /// album shelf.
    /// </remarks>
    [Fact]
    public async Task AnAlbumLastsWhatItsSongsAddUpTo()
    {
        var album = new ReleaseGroup { Id = ReleaseGroupId.New(), Title = "Lengths" };
        var pressing = new Release { Id = ReleaseId.New(), Title = "Lengths", ReleaseGroupId = album.Id, TrackCount = 2, DiscCount = 1 };
        var recordings = Enumerable.Range(1, 4)
            .Select(n => new Recording { Id = RecordingId.New(), Title = $"Song {n}" })
            .ToList();
        recordings[2].Duration = TimeSpan.FromSeconds(30.6);
        var tracks = new[]
        {
            new Track { Id = TrackId.New(), ReleaseId = pressing.Id, RecordingId = recordings[0].Id, Position = 1, DiscNumber = 1, Title = "Song 1", Length = TimeSpan.FromSeconds(999) },
            new Track { Id = TrackId.New(), ReleaseId = pressing.Id, RecordingId = recordings[1].Id, Position = 2, DiscNumber = 1, Title = "Song 2", Length = TimeSpan.FromSeconds(50.4) },
        };
        var files = recordings
            .Select((recording, i) => new MediaFile
            {
                Id = MediaFileId.New(),
                Path = $"Lengths/0{i + 1} Song.flac",
                SizeBytes = 1024,
                LastModifiedUtc = DateTimeOffset.Parse("2026-01-03T00:00:00Z", CultureInfo.InvariantCulture),
                RecordingId = recording.Id,
                ReleaseId = pressing.Id,
                ReleaseGroupId = album.Id,
                TrackId = i < tracks.Length ? tracks[i].Id : null,
                Quality = i == 0
                    ? new AudioQuality { Codec = "flac", SampleRateHz = 44_100, Channels = 2, BitDepth = 16, BitrateBps = 900_000, IsLossless = true, Duration = TimeSpan.FromSeconds(100.4) }
                    : null,
            })
            .ToList();

        await using (var db = PostgresFixture.CreateContext(_connectionString))
        {
            db.ReleaseGroups.Add(album);
            db.Releases.Add(pressing);
            db.Recordings.AddRange(recordings);
            db.Tracks.AddRange(tracks);
            db.MediaFiles.AddRange(files);
            await db.SaveChangesAsync(Token);
        }

        try
        {
            using var client = _factory!.CreateClient();
            var id = $"al-{album.Id.Value:N}";

            var body = (await JsonAsync(client, $"/rest/getAlbum.view?id={id}"))
                .GetProperty("subsonic-response")
                .GetProperty("album");

            Assert.Equal(
                [null, 31, 50, 100],
                body.GetProperty("song")
                    .EnumerateArray()
                    .Select(song => song.TryGetProperty("duration", out var seconds) ? seconds.GetInt32() : (int?)null)
                    .Order());
            Assert.Equal(181, body.GetProperty("duration").GetInt32());

            var listed = (await JsonAsync(client, "/rest/getAlbumList2.view?size=500"))
                .GetProperty("subsonic-response")
                .GetProperty("albumList2")
                .GetProperty("album")
                .EnumerateArray()
                .Single(row => row.GetProperty("id").GetString() == id);

            Assert.Equal(181, listed.GetProperty("duration").GetInt32());
        }
        finally
        {
            await using var db = PostgresFixture.CreateContext(_connectionString);
            db.MediaFiles.RemoveRange(await db.MediaFiles.Where(file => file.ReleaseGroupId == album.Id).ToListAsync(Token));
            db.Tracks.RemoveRange(await db.Tracks.Where(row => row.ReleaseId == pressing.Id).ToListAsync(Token));
            db.Releases.Remove(await db.Releases.SingleAsync(row => row.Id == pressing.Id, Token));
            db.ReleaseGroups.Remove(await db.ReleaseGroups.SingleAsync(row => row.Id == album.Id, Token));
            var ids = recordings.Select(recording => recording.Id).ToList();
            db.Recordings.RemoveRange(await db.Recordings.Where(row => ids.Contains(row.Id)).ToListAsync(Token));
            await db.SaveChangesAsync(Token);
        }
    }

    /// <summary>
    /// A classical song names its piece and its movement, a whole piece names
    /// only itself, and a song names neither; each says who composed it.
    /// </summary>
    [Fact]
    public async Task AClassicalSongNamesItsPieceAndMovement()
    {
        var album = new ReleaseGroup { Id = ReleaseGroupId.New(), Title = "Beethoven", Genres = "classical" };
        var pressing = new Release { Id = ReleaseId.New(), Title = "Beethoven", ReleaseGroupId = album.Id, TrackCount = 3, DiscCount = 1 };
        var beethoven = new Artist
        {
            Id = ArtistId.New(),
            Name = "Ludwig van Beethoven",
            Mbid = new Mbid(Guid.CreateVersion7()),
        };
        var symphony = new Work
        {
            Id = WorkId.New(),
            Title = "Symphony No. 5 in C minor, Op. 67",
            Mbid = new Mbid(Guid.CreateVersion7()),
            Type = "Symphony",
            PartCount = 4,
        };
        var movement = new Work
        {
            Id = WorkId.New(),
            Title = "Symphony No. 5 in C minor, Op. 67: I. Allegro con brio",
            ParentId = symphony.Id,
            Position = 1,
        };
        var overture = new Work { Id = WorkId.New(), Title = "Egmont, Op. 84: Overture", Mbid = new Mbid(Guid.CreateVersion7()) };
        var song = new Work { Id = WorkId.New(), Title = "Adelaide", Type = "Song" };
        var works = new[] { movement, overture, song };
        var recordings = works
            .Select(work => new Recording { Id = RecordingId.New(), Title = work.Title, WorkId = work.Id })
            .ToList();
        var files = recordings
            .Select((recording, i) => new MediaFile
            {
                Id = MediaFileId.New(),
                Path = $"Beethoven/0{i + 1} Track.flac",
                SizeBytes = 1024,
                LastModifiedUtc = DateTimeOffset.Parse("2026-01-03T00:00:00Z", CultureInfo.InvariantCulture),
                RecordingId = recording.Id,
                ReleaseId = pressing.Id,
                ReleaseGroupId = album.Id,
            })
            .ToList();

        Contribution Composer(WorkId work, Mbid? artist, string name) => new()
        {
            Id = Guid.CreateVersion7(),
            WorkId = work,
            Type = "composer",
            ArtistMbid = artist,
            Name = name,
            Position = 0,
        };

        await using (var db = PostgresFixture.CreateContext(_connectionString))
        {
            db.ReleaseGroups.Add(album);
            db.Releases.Add(pressing);
            db.Artists.Add(beethoven);
            db.Works.AddRange(symphony, movement, overture, song);
            db.Recordings.AddRange(recordings);
            db.MediaFiles.AddRange(files);

            // The movement's own writer, printed under another name than the
            // catalogue's; the overture's, with no artist row at all.
            db.Contributions.AddRange(
                Composer(movement.Id, beethoven.Mbid, "Beethoven"),
                Composer(overture.Id, null, "L. van Beethoven"));
            await db.SaveChangesAsync(Token);
        }

        try
        {
            using var client = _factory!.CreateClient();

            var songs = (await JsonAsync(client, $"/rest/getAlbum.view?id=al-{album.Id.Value:N}"))
                .GetProperty("subsonic-response")
                .GetProperty("album")
                .GetProperty("song")
                .EnumerateArray()
                .ToDictionary(row => row.GetProperty("title").GetString()!);

            var first = songs[movement.Title];
            var piece = Assert.Single(first.GetProperty("works").EnumerateArray());
            Assert.Equal(symphony.Title, piece.GetProperty("name").GetString());
            Assert.Equal(symphony.Mbid!.Value.Value.ToString(), piece.GetProperty("musicBrainzId").GetString());
            var part = Assert.Single(first.GetProperty("movements").EnumerateArray());
            Assert.Equal("I. Allegro con brio", part.GetProperty("name").GetString());
            Assert.Equal(1, part.GetProperty("number").GetInt32());
            Assert.Equal(4, part.GetProperty("count").GetInt32());
            Assert.Equal("Ludwig van Beethoven", first.GetProperty("displayComposer").GetString());

            var whole = songs[overture.Title];
            Assert.Equal(overture.Title, Assert.Single(whole.GetProperty("works").EnumerateArray()).GetProperty("name").GetString());
            Assert.Empty(whole.GetProperty("movements").EnumerateArray());
            Assert.Equal("L. van Beethoven", whole.GetProperty("displayComposer").GetString());

            var lied = songs[song.Title];
            Assert.Empty(lied.GetProperty("works").EnumerateArray());
            Assert.Empty(lied.GetProperty("movements").EnumerateArray());
            Assert.False(lied.TryGetProperty("displayComposer", out _));
        }
        finally
        {
            await using var db = PostgresFixture.CreateContext(_connectionString);
            var workIds = new[] { symphony.Id, movement.Id, overture.Id, song.Id };
            db.Contributions.RemoveRange(await db.Contributions.Where(row => row.WorkId != null && workIds.Contains(row.WorkId.Value)).ToListAsync(Token));
            db.MediaFiles.RemoveRange(await db.MediaFiles.Where(file => file.ReleaseGroupId == album.Id).ToListAsync(Token));
            var ids = recordings.Select(recording => recording.Id).ToList();
            db.Recordings.RemoveRange(await db.Recordings.Where(row => ids.Contains(row.Id)).ToListAsync(Token));
            await db.SaveChangesAsync(Token);
            db.Works.RemoveRange(await db.Works.Where(row => workIds.Contains(row.Id)).OrderBy(row => row.ParentId == null).ToListAsync(Token));
            db.Artists.Remove(await db.Artists.SingleAsync(row => row.Id == beethoven.Id, Token));
            db.Releases.Remove(await db.Releases.SingleAsync(row => row.Id == pressing.Id, Token));
            db.ReleaseGroups.Remove(await db.ReleaseGroups.SingleAsync(row => row.Id == album.Id, Token));
            await db.SaveChangesAsync(Token);
        }
    }

    /// <summary>
    /// A movement on an album nobody voted on is classical by its tracks, and a
    /// film cue there is not; an opera of the Ring is the piece rather than the
    /// Ring, and an untyped ballet whose numbers are not all typed stays whole;
    /// a Russian title is named in English; and the piece is taken off a
    /// movement's name however the movement spells it.
    /// </summary>
    [Fact]
    public async Task ASongNamesThePieceAListenerWould()
    {
        var album = new ReleaseGroup { Id = ReleaseGroupId.New(), Title = "Wagner and Tchaikovsky" };
        var pressing = new Release { Id = ReleaseId.New(), Title = album.Title, ReleaseGroupId = album.Id, TrackCount = 3, DiscCount = 1 };
        var ring = new Work
        {
            Id = WorkId.New(),
            Title = "Der Ring des Nibelungen, WWV 86",
            Mbid = new Mbid(Guid.CreateVersion7()),
            PartCount = 4,
            PartsTyped = true,
        };
        var rheingold = new Work
        {
            Id = WorkId.New(),
            Title = "Das Rheingold, WWV 86A",
            Mbid = new Mbid(Guid.CreateVersion7()),
            Type = "Opera",
            ParentId = ring.Id,
            Position = 1,
            PartCount = 4,

            // A piece whose every part is typed is still the piece.
            PartsTyped = true,
        };
        var scene = new Work { Id = WorkId.New(), Title = "Das Rheingold: Scene I", ParentId = rheingold.Id, Position = 1 };
        var nutcracker = new Work
        {
            Id = WorkId.New(),
            Title = "Щелкунчик, op. 71",
            LatinTitle = "The Nutcracker, op. 71",
            Mbid = new Mbid(Guid.CreateVersion7()),
            PartCount = 3,
        };
        var overture = new Work
        {
            Id = WorkId.New(),
            Title = "Щелкунчик, op. 71: Увертюра",
            LatinTitle = "The Nutcracker, op. 71: Overture",
            Type = "Overture",
            ParentId = nutcracker.Id,
            Position = 1,
        };

        // A film cue under its score, neither typed.
        var score = new Work { Id = WorkId.New(), Title = "Gladiator", Mbid = new Mbid(Guid.CreateVersion7()) };
        var cue = new Work { Id = WorkId.New(), Title = "Elysium", ParentId = score.Id };

        // Only the overture is voted classical, and the album not at all.
        var recordings = new[] { (scene, (string?)null), (overture, "classical"), (cue, (string?)null) }
            .Select(pair => new Recording { Id = RecordingId.New(), Title = pair.Item1.Title, WorkId = pair.Item1.Id, Genres = pair.Item2 })
            .ToList();
        var files = recordings
            .Select((recording, i) => new MediaFile
            {
                Id = MediaFileId.New(),
                Path = $"Wagner/0{i + 1} Track.flac",
                SizeBytes = 1024,
                LastModifiedUtc = DateTimeOffset.Parse("2026-01-03T00:00:00Z", CultureInfo.InvariantCulture),
                RecordingId = recording.Id,
                ReleaseId = pressing.Id,
                ReleaseGroupId = album.Id,
            })
            .ToList();
        var workIds = new[] { ring.Id, rheingold.Id, scene.Id, nutcracker.Id, overture.Id, score.Id, cue.Id };

        await using (var db = PostgresFixture.CreateContext(_connectionString))
        {
            db.ReleaseGroups.Add(album);
            db.Releases.Add(pressing);
            db.Works.AddRange(ring, rheingold, scene, nutcracker, overture, score, cue);
            db.Recordings.AddRange(recordings);
            db.MediaFiles.AddRange(files);
            await db.SaveChangesAsync(Token);
        }

        try
        {
            using var client = _factory!.CreateClient();

            var songs = (await JsonAsync(client, $"/rest/getAlbum.view?id=al-{album.Id.Value:N}"))
                .GetProperty("subsonic-response")
                .GetProperty("album")
                .GetProperty("song")
                .EnumerateArray()
                .ToDictionary(row => row.GetProperty("title").GetString()!);

            static (string?, string?, int?, int?) Named(JsonElement song)
            {
                var part = Assert.Single(song.GetProperty("movements").EnumerateArray());
                return (
                    Assert.Single(song.GetProperty("works").EnumerateArray()).GetProperty("name").GetString(),
                    part.GetProperty("name").GetString(),
                    part.TryGetProperty("number", out var number) ? number.GetInt32() : null,
                    part.TryGetProperty("count", out var count) ? count.GetInt32() : null);
            }

            Assert.Equal((rheingold.Title, "Scene I", 1, 4), Named(songs[scene.Title]));
            Assert.Equal(("The Nutcracker, op. 71", "Overture", 1, 3), Named(songs[overture.Title]));
            Assert.Empty(songs[cue.Title].GetProperty("works").EnumerateArray());

            var listed = (await JsonAsync(client, "/rest/getCompositions.view"))
                .GetProperty("subsonic-response")
                .GetProperty("compositions")
                .GetProperty("composition")
                .EnumerateArray()
                .Select(row => (row.GetProperty("id").GetString(), row.GetProperty("name").GetString()))
                .ToList();

            Assert.Contains(($"wk-{rheingold.Id.Value:N}", rheingold.Title), listed);
            Assert.Contains(($"wk-{nutcracker.Id.Value:N}", "The Nutcracker, op. 71"), listed);
            Assert.DoesNotContain(listed, row => row.Item1 == $"wk-{ring.Id.Value:N}");

            // The opera is a composition with a parent on its row, and takes a heart.
            Assert.Equal("ok", (await JsonAsync(client, $"/rest/star.view?id=wk-{rheingold.Id.Value:N}")).GetProperty("subsonic-response").GetProperty("status").GetString());
            Assert.Equal(70, (await JsonAsync(client, $"/rest/star.view?id=wk-{ring.Id.Value:N}")).GetProperty("subsonic-response").GetProperty("error").GetProperty("code").GetInt32());
        }
        finally
        {
            await using var db = PostgresFixture.CreateContext(_connectionString);
            db.Loves.RemoveRange(await db.Loves.IgnoreQueryFilters().Where(love => love.WorkId != null && workIds.Contains(love.WorkId.Value)).ToListAsync(Token));
            db.MediaFiles.RemoveRange(await db.MediaFiles.Where(file => file.ReleaseGroupId == album.Id).ToListAsync(Token));
            var ids = recordings.Select(recording => recording.Id).ToList();
            db.Recordings.RemoveRange(await db.Recordings.Where(row => ids.Contains(row.Id)).ToListAsync(Token));
            await db.SaveChangesAsync(Token);
            db.Works.RemoveRange(await db.Works.Where(row => workIds.Contains(row.Id)).ToListAsync(Token));
            db.Releases.Remove(await db.Releases.SingleAsync(row => row.Id == pressing.Id, Token));
            db.ReleaseGroups.Remove(await db.ReleaseGroups.SingleAsync(row => row.Id == album.Id, Token));
            await db.SaveChangesAsync(Token);
        }
    }

    /// <summary>
    /// Whoever composed a held classical piece is listed with how many they
    /// wrote; the pieces are listed by name, all or one composer's, and one
    /// opens with its movements in order and every song of it on every album.
    /// A song, a movement, nobody and a composer with no row are none of these.
    /// Each carries its form, key, period, movements, longest performance,
    /// lyricists and languages; a film score's suite stays with its form.
    /// </summary>
    [Fact]
    public async Task TheComposersAndTheirCompositionsAreListedAndOneOpensWithItsSongs()
    {
        var studio = new ReleaseGroup { Id = ReleaseGroupId.New(), Title = "Beethoven & Weber", Genres = "classical" };
        var live = new ReleaseGroup { Id = ReleaseGroupId.New(), Title = "Beethoven Live", Genres = "classical" };
        var beethoven = new Artist
        {
            Id = ArtistId.New(),
            Name = "Ludwig van Beethoven",
            SortName = "Beethoven, Ludwig van",
            Mbid = new Mbid(Guid.CreateVersion7()),
            PortraitUrl = "https://commons.wikimedia.org/wiki/Special:FilePath/Beethoven.jpg",
            BeganYear = 1770,
        };
        var brahms = new Artist { Id = ArtistId.New(), Name = "Johannes Brahms", SortName = "Brahms, Johannes", Mbid = new Mbid(Guid.CreateVersion7()), BeganYear = 1833 };
        var weber = new Artist { Id = ArtistId.New(), Name = "Carl Maria von Weber", SortName = "Weber, Carl Maria von", Mbid = new Mbid(Guid.CreateVersion7()), BeganYear = 1786 };
        var goethe = new Artist { Id = ArtistId.New(), Name = "Johann Wolfgang von Goethe", Mbid = new Mbid(Guid.CreateVersion7()) };
        var traditional = new Artist { Id = ArtistId.New(), Name = "[traditional]", Mbid = new Mbid(Guid.Parse("9be7f096-97ec-4615-8957-8d40b5dcbc41")) };
        var symphony = new Work
        {
            Id = WorkId.New(),
            Title = "Symphony No. 5 in C minor, Op. 67",
            Mbid = new Mbid(Guid.CreateVersion7()),
            Type = "Symphony",
            Key = "C minor",
            PartCount = 4,
            ComposedBegin = "1804",
            ComposedEnd = "1808",
        };
        var second = new Work { Id = WorkId.New(), Title = "Symphony No. 5 in C minor, Op. 67: II. Andante con moto", ParentId = symphony.Id, Position = 2 };
        var first = new Work { Id = WorkId.New(), Title = "Symphony No. 5 in C minor, Op. 67: I. Allegro con brio", ParentId = symphony.Id, Position = 1 };
        var freischutz = new Work { Id = WorkId.New(), Title = "Der Freischütz, J. 277: Overture", Type = "Overture" };
        var festival = new Work { Id = WorkId.New(), Title = "Academic Festival Overture, Op. 80" };

        // Ordered by place, which here is not the titles' order.
        var egmont = new Work { Id = WorkId.New(), Title = "Egmont, Op. 84", Languages = "zxx" };
        var lied = new Work { Id = WorkId.New(), Title = "Egmont, Op. 84: Nr. 1 Lied: Die Trommel gerühret", ParentId = egmont.Id, Position = 2, Languages = "deu" };
        var ouverture = new Work { Id = WorkId.New(), Title = "Egmont, Op. 84: Ouvertüre", ParentId = egmont.Id, Position = 1, Languages = "zxx, deu" };
        var adelaide = new Work { Id = WorkId.New(), Title = "Adelaide", Type = "Song" };

        // Typed on the whole, recorded by its movement: kept, as its form.
        var suite = new Work { Id = WorkId.New(), Title = "The Empire Strikes Back: Suite", Type = "Soundtrack", ComposedBegin = "1980" };
        var throne = new Work { Id = WorkId.New(), Title = "The Empire Strikes Back: Suite: The Imperial March", ParentId = suite.Id, Position = 1 };
        var played = new (Work Work, ReleaseGroup Album)[]
        {
            (second, studio), (first, studio), (first, live), (freischutz, studio), (lied, studio), (ouverture, studio), (festival, live), (adelaide, studio), (throne, studio),
        };

        // The studio's two movements together outlast the live one, and one
        // of its files was measured longer than its recording says.
        var lengths = new Dictionary<WorkId, TimeSpan> { [second.Id] = TimeSpan.FromSeconds(600), [first.Id] = TimeSpan.FromSeconds(420) };
        var recordings = played.Select(row => new Recording { Id = RecordingId.New(), Title = row.Work.Title, WorkId = row.Work.Id, Duration = lengths.TryGetValue(row.Work.Id, out var length) ? length : null }).ToList();
        var files = played
            .Select((row, i) => new MediaFile
            {
                Id = MediaFileId.New(),
                Path = $"{row.Album.Title}/0{i + 1} Track.flac",
                SizeBytes = 1024,
                LastModifiedUtc = DateTimeOffset.Parse("2026-01-03T00:00:00Z", CultureInfo.InvariantCulture),
                RecordingId = recordings[i].Id,
                ReleaseGroupId = row.Album.Id,
                Quality = row.Album == studio && row.Work == second
                    ? new AudioQuality { Codec = "flac", SampleRateHz = 44_100, Channels = 2, BitDepth = 16, BitrateBps = 900_000, IsLossless = true, Duration = TimeSpan.FromSeconds(800) }
                    : null,
            })
            .ToList();

        Contribution Composer(WorkId work, Mbid artist, string name, string type = "composer") => new()
        {
            Id = Guid.CreateVersion7(),
            WorkId = work,
            Type = type,
            ArtistMbid = artist,
            Name = name,
            Position = 0,
        };

        await using (var db = PostgresFixture.CreateContext(_connectionString))
        {
            db.ReleaseGroups.AddRange(studio, live);
            db.Artists.AddRange(beethoven, brahms, weber, traditional, goethe);
            db.Works.AddRange(symphony, second, first, freischutz, festival, egmont, lied, ouverture, adelaide, suite, throne);
            db.Recordings.AddRange(recordings);
            db.MediaFiles.AddRange(files);

            // The symphony's composer is on its movements, Egmont's on the
            // piece; the overture's has no artist row; Adelaide is a song.
            db.Contributions.AddRange(
                Composer(first.Id, beethoven.Mbid!.Value, "Beethoven"),
                Composer(second.Id, beethoven.Mbid!.Value, "Beethoven"),
                Composer(freischutz.Id, weber.Mbid!.Value, "Weber"),
                Composer(freischutz.Id, traditional.Mbid!.Value, "[traditional]"),
                Composer(festival.Id, brahms.Mbid!.Value, "Brahms"),
                Composer(egmont.Id, beethoven.Mbid!.Value, "Beethoven"),
                Composer(ouverture.Id, new Mbid(Guid.CreateVersion7()), "L. van Beethoven"),
                Composer(adelaide.Id, beethoven.Mbid!.Value, "Beethoven"),

                // Goethe twice, once on the piece; a writer with no row; nobody.
                Composer(lied.Id, goethe.Mbid!.Value, "Goethe", "lyricist"),
                Composer(egmont.Id, goethe.Mbid!.Value, "Goethe", "librettist"),
                Composer(ouverture.Id, new Mbid(Guid.CreateVersion7()), "Friedrich Mosengeil", "librettist"),
                Composer(lied.Id, new Mbid(Guid.CreateVersion7()), "Friedrich Schiller", "lyricist"),
                Composer(lied.Id, traditional.Mbid!.Value, "[traditional]", "lyricist"));
            await db.SaveChangesAsync(Token);
        }

        using var client = _factory!.CreateClient();

        var composers = (await JsonAsync(client, "/rest/getComposers.view"))
            .GetProperty("subsonic-response")
            .GetProperty("composers")
            .GetProperty("index")
            .EnumerateArray()
            .Select(index => (
                index.GetProperty("name").GetString(),
                index.GetProperty("composer").EnumerateArray().Select(composer => (
                    composer.GetProperty("id").GetString(),
                    composer.GetProperty("name").GetString(),
                    composer.GetProperty("sortName").GetString(),
                    composer.GetProperty("musicBrainzId").GetString(),
                    composer.TryGetProperty("coverArt", out var cover) ? cover.GetString() : null,
                    composer.GetProperty("compositionCount").GetInt32()))))
            .SelectMany(index => index.Item2.Select(composer => (index.Item1, composer)))
            .ToList();

        var b = $"ar-{beethoven.Id.Value:N}";
        var w = $"ar-{weber.Id.Value:N}";
        Assert.Equal(
            [
                ("B", (b, "Ludwig van Beethoven", "Beethoven, Ludwig van", beethoven.Mbid!.Value.Value.ToString(), b, 2)),
                ("B", ($"ar-{brahms.Id.Value:N}", "Johannes Brahms", "Brahms, Johannes", brahms.Mbid!.Value.Value.ToString(), (string?)null, 1)),
                ("W", (w, "Carl Maria von Weber", "Weber, Carl Maria von", weber.Mbid!.Value.Value.ToString(), (string?)null, 1)),
            ],
            composers);

        async Task<List<JsonElement>> CompositionsAsync(string query) =>
            [.. (await JsonAsync(client, $"/rest/getCompositions.view{query}"))
                .GetProperty("subsonic-response")
                .GetProperty("compositions")
                .GetProperty("composition")
                .EnumerateArray()];

        static List<(string?, string?)> Writers(JsonElement piece) =>
            [.. piece.GetProperty("composers").EnumerateArray().Select(composer => (composer.GetProperty("id").GetString(), composer.GetProperty("name").GetString()))];

        static string? Fact(JsonElement piece, string name) =>
            piece.TryGetProperty(name, out var fact) ? fact.ValueKind == JsonValueKind.Number ? fact.GetInt32().ToString(CultureInfo.InvariantCulture) : fact.GetString() : null;

        var all = await CompositionsAsync("");
        Assert.Equal([festival.Title, freischutz.Title, egmont.Title, symphony.Title, suite.Title], all.Select(piece => piece.GetProperty("name").GetString()));
        Assert.Equal(
            [
                (null, null, "Romantic", null, null),
                ("Overture", null, "Classical", null, null),
                (null, null, "Classical", "2", null),
                ("Symphony", "C minor", "Classical", "4", "1220"),
                ("Soundtrack", null, "Contemporary", "1", null),
            ],
            all.Select(piece => (Fact(piece, "form"), Fact(piece, "key"), Fact(piece, "period"), Fact(piece, "movementCount"), Fact(piece, "duration"))));
        Assert.Equal(
            [("", "Friedrich Mosengeil"), ("", "Friedrich Schiller"), ($"ar-{goethe.Id.Value:N}", "Johann Wolfgang von Goethe")],
            all[2].GetProperty("lyricists").EnumerateArray().Select(writer => (writer.GetProperty("id").GetString(), writer.GetProperty("name").GetString())).Order());
        Assert.Equal(["deu"], all[2].GetProperty("languages").EnumerateArray().Select(code => code.GetString()));
        Assert.Empty(all[3].GetProperty("lyricists").EnumerateArray());
        Assert.False(all[3].TryGetProperty("languages", out _));
        Assert.Equal(
            suite.Title,
            Assert.Single(Assert.Single(await CompositionsAsync($"?id=wk-{suite.Id.Value:N}")).GetProperty("song").EnumerateArray())
                .GetProperty("works").EnumerateArray().Single().GetProperty("name").GetString());
        Assert.Equal([(w, "Carl Maria von Weber")], Writers(all[1]));
        Assert.Equal([(b, "Ludwig van Beethoven")], Writers(all[2]));
        Assert.False(all[2].TryGetProperty("composed", out _));
        Assert.Equal(1, all[2].GetProperty("albumCount").GetInt32());

        var piece = all[3];
        Assert.Equal($"wk-{symphony.Id.Value:N}", piece.GetProperty("id").GetString());
        Assert.Equal(symphony.Mbid!.Value.Value.ToString(), piece.GetProperty("musicBrainzId").GetString());
        Assert.Equal([(b, "Ludwig van Beethoven")], Writers(piece));
        Assert.Equal("""{"year":1804}""", piece.GetProperty("composed").GetProperty("from").GetRawText());
        Assert.Equal("""{"year":1808}""", piece.GetProperty("composed").GetProperty("to").GetRawText());
        Assert.Equal(2, piece.GetProperty("albumCount").GetInt32());
        Assert.Equal($"al-{studio.Id.Value:N}", piece.GetProperty("coverArt").GetString());
        Assert.False(piece.TryGetProperty("song", out _));

        Assert.Equal([egmont.Title, symphony.Title], (await CompositionsAsync($"?composerId={b}")).Select(row => row.GetProperty("name").GetString()));
        Assert.Equal(
            [("Ouvertüre", 1), ("Nr. 1 Lied: Die Trommel gerühret", 2)],
            Assert.Single(await CompositionsAsync($"?id=wk-{egmont.Id.Value:N}")).GetProperty("movement").EnumerateArray()
                .Select(part => (part.GetProperty("name").GetString(), part.GetProperty("number").GetInt32())));

        var opened = Assert.Single(await CompositionsAsync($"?id=wk-{symphony.Id.Value:N}"));
        Assert.Equal(
            [("I. Allegro con brio", 1), ("II. Andante con moto", 2)],
            opened.GetProperty("movement").EnumerateArray().Select(part => (part.GetProperty("name").GetString(), part.GetProperty("number").GetInt32())));
        var songs = opened.GetProperty("song").EnumerateArray().ToList();
        Assert.Equal(
            files.Take(3).Select(file => $"tr-{file.Id.Value:N}").Order(),
            songs.Select(song => song.GetProperty("id").GetString()).Order());
        Assert.Contains(songs, song => song.GetProperty("albumId").GetString() == $"al-{live.Id.Value:N}");
        Assert.All(songs, song => Assert.Equal(symphony.Title, Assert.Single(song.GetProperty("works").EnumerateArray()).GetProperty("name").GetString()));

        foreach (var wrong in new[] { $"composerId=ar-{traditional.Id.Value:N}", $"composerId=ar-{Guid.CreateVersion7():N}", $"id=wk-{first.Id.Value:N}", $"id=wk-{adelaide.Id.Value:N}", "id=al-1" })
        {
            var refused = (await JsonAsync(client, $"/rest/getCompositions.view?{wrong}")).GetProperty("subsonic-response");
            Assert.Equal("failed", refused.GetProperty("status").GetString());
            Assert.Equal(70, refused.GetProperty("error").GetProperty("code").GetInt32());
        }

        // A heart on a whole piece shows in both modes; a movement is no composition.
        Assert.Equal(70, (await JsonAsync(client, $"/rest/star.view?id=wk-{first.Id.Value:N}")).GetProperty("subsonic-response").GetProperty("error").GetProperty("code").GetInt32());
        Assert.Equal("ok", (await JsonAsync(client, $"/rest/star.view?id=wk-{symphony.Id.Value:N}")).GetProperty("subsonic-response").GetProperty("status").GetString());
        Assert.Equal([symphony.Title], (await CompositionsAsync("")).Where(row => row.TryGetProperty("starred", out _)).Select(row => row.GetProperty("name").GetString()));
        Assert.True(Assert.Single(await CompositionsAsync($"?id=wk-{symphony.Id.Value:N}")).TryGetProperty("starred", out _));
        Assert.Equal("ok", (await JsonAsync(client, $"/rest/unstar.view?id=wk-{symphony.Id.Value:N}")).GetProperty("subsonic-response").GetProperty("status").GetString());
        Assert.DoesNotContain(await CompositionsAsync(""), row => row.TryGetProperty("starred", out _));

        // A composer no album is billed to shows a heart where composers are listed, and among the loved.
        Assert.Equal("ok", (await JsonAsync(client, $"/rest/star.view?artistId={w}")).GetProperty("subsonic-response").GetProperty("status").GetString());
        Assert.Equal(
            [w],
            (await JsonAsync(client, "/rest/getComposers.view")).GetProperty("subsonic-response").GetProperty("composers").GetProperty("index").EnumerateArray()
                .SelectMany(index => index.GetProperty("composer").EnumerateArray())
                .Where(composer => composer.TryGetProperty("starred", out _))
                .Select(composer => composer.GetProperty("id").GetString()));
        var lovedComposer = Assert.Single((await JsonAsync(client, "/rest/getStarred2.view")).GetProperty("subsonic-response").GetProperty("starred2").GetProperty("artist").EnumerateArray());
        Assert.Equal((w, 0), (lovedComposer.GetProperty("id").GetString(), lovedComposer.GetProperty("albumCount").GetInt32()));
        Assert.Equal("ok", (await JsonAsync(client, $"/rest/unstar.view?artistId={w}")).GetProperty("subsonic-response").GetProperty("status").GetString());

        using var response = await client.GetAsync(
            new Uri($"/rest/getCompositions.view?{Credentials}&id=wk-{symphony.Id.Value:N}", UriKind.Relative),
            Token);

        var xml = XDocument.Parse(await response.Content.ReadAsStringAsync(Token));
        XNamespace ns = "http://subsonic.org/restapi";
        var element = Assert.Single(xml.Descendants(ns + "composition"));
        Assert.Equal(b, element.Element(ns + "composers")?.Attribute("id")?.Value);
        Assert.Equal(("Symphony", "C minor", "1220"), (element.Attribute("form")?.Value, element.Attribute("key")?.Value, element.Attribute("duration")?.Value));
        Assert.Equal("1804", element.Element(ns + "composed")?.Element(ns + "from")?.Attribute("year")?.Value);
        Assert.Equal(["1", "2"], element.Elements(ns + "movement").Select(part => part.Attribute("number")?.Value));
        Assert.Equal(3, element.Elements(ns + "song").Count());
    }

    /// <summary>
    /// A song names who played on it and wrote it, each once, under the tag
    /// write's roles; when and where it was played; and when its piece was
    /// written — in both formats, and a song MusicBrainz says none of this
    /// about carries an empty list and nothing else. Its artists are whom its
    /// artist line bills, each once, under the ids the credits carry.
    /// </summary>
    [Fact]
    public async Task ASongCarriesItsPlayersAndWhenAndWhereItWasPlayed()
    {
        var album = new ReleaseGroup { Id = ReleaseGroupId.New(), Title = "The Sleeping Beauty", Genres = "classical" };
        var previn = new Artist { Id = ArtistId.New(), Name = "André Previn", Mbid = new Mbid(Guid.CreateVersion7()) };
        var haendel = new Artist { Id = ArtistId.New(), Name = "Ида Гендель", LatinName = "Ida Haendel" };
        var lso = new Artist { Id = ArtistId.New(), Name = "London Symphony Orchestra" };
        var orchestra = new Mbid(Guid.CreateVersion7());
        var tchaikovsky = new Mbid(Guid.CreateVersion7());
        var ballet = new Work { Id = WorkId.New(), Title = "The Sleeping Beauty, op. 66", Mbid = new Mbid(Guid.CreateVersion7()), Type = "Ballet", PartCount = 3, ComposedBegin = "1888", ComposedEnd = "1889" };
        var scene = new Work { Id = WorkId.New(), Title = "The Sleeping Beauty, op. 66: Acte II", ParentId = ballet.Id, Position = 2 };
        var played = new Recording { Id = RecordingId.New(), Title = "Act Two: Scene", WorkId = scene.Id };
        var bare = new Recording { Id = RecordingId.New(), Title = "Untold" };
        var pop = new Recording { Id = RecordingId.New(), Title = "Sultans of Swing" };
        var guitarist = new Mbid(Guid.CreateVersion7());
        var traditional = new Mbid(Guid.Parse("9be7f096-97ec-4615-8957-8d40b5dcbc41"));
        var files = new[] { played, bare, pop }
            .Select((recording, i) => new MediaFile
            {
                Id = MediaFileId.New(),
                Path = $"Previn/Sleeping Beauty/0{i + 1} Track.flac",
                SizeBytes = 1024,
                LastModifiedUtc = DateTimeOffset.Parse("2026-01-03T00:00:00Z", CultureInfo.InvariantCulture),
                RecordingId = recording.Id,
                ReleaseGroupId = album.Id,
            })
            .ToList();

        var position = 0;

        Contribution Link(RecordingId? recording, WorkId? work, string type, Mbid? artist, string name, string? begin = null, string? end = null, string? attributes = null) => new()
        {
            Id = Guid.CreateVersion7(),
            RecordingId = recording,
            WorkId = work,
            Type = type,
            Attributes = attributes,
            ArtistMbid = artist,
            Name = name,
            Position = position++,
            Begin = begin,
            End = end,
        };

        ArtistCredit Credit(Artist artist, RecordingId? recording, ReleaseGroupId? album, string? creditedAs, string? join) => new()
        {
            Id = Guid.CreateVersion7(),
            ArtistId = artist.Id,
            RecordingId = recording,
            ReleaseGroupId = album,
            Position = position++,
            CreditedAs = creditedAs,
            JoinPhrase = join,
        };

        RecordingSession Session(string type, string name, string? begin, string? end) => new()
        {
            Id = Guid.CreateVersion7(),
            RecordingId = played.Id,
            Type = type,
            Name = name,
            Begin = begin,
            End = end,
            Position = position++,
        };

        await using (var db = PostgresFixture.CreateContext(_connectionString))
        {
            db.ReleaseGroups.Add(album);
            db.Artists.AddRange(previn, haendel, lso);
            db.Works.AddRange(ballet, scene);
            db.Recordings.AddRange(played, bare, pop);
            db.MediaFiles.AddRange(files);

            // Two sessions, so two conductor links to one man; a producer
            // nobody dated; the years it was written on the whole ballet.
            db.Contributions.AddRange(
                Link(played.Id, null, "conductor", previn.Mbid, "Andre Previn", "1974-04-08", "1974-04-09"),
                Link(played.Id, null, "conductor", previn.Mbid, "Andre Previn", "1974-06-29", "1974-06-29"),
                Link(played.Id, null, "performing orchestra", orchestra, "London Symphony Orchestra", "1974-04-08", "1974-06-29"),
                Link(played.Id, null, "producer", null, "Christopher Bishop"),
                Link(pop.Id, null, "instrument", guitarist, "Mark Knopfler", "1977-07", "1977-07", "guitar"),
                Link(pop.Id, null, "writer", traditional, "[traditional]"),
                Link(null, scene.Id, "composer", tchaikovsky, "Пётр Ильич Чайковский"));
            db.RecordingSessions.AddRange(
                Session("recorded at", "Abbey Road Studios: Studio 1", "1974-04-08", "1974-04-09"),
                Session("recorded at", "Abbey Road Studios: Studio 1", "1974-06-29", "1974-06-29"),
                Session("recorded in", "London", null, null));

            // The recording bills a soloist as the sleeve spells her and the
            // conductor twice, as MusicBrainz writes "Marvin Gaye = Marvin
            // Gaye"; the album bills the conductor alone.
            db.ArtistCredits.AddRange(
                Credit(haendel, played.Id, null, "Ida Händel", ", "),
                Credit(lso, played.Id, null, null, ", "),
                Credit(previn, played.Id, null, "Previn", " = "),
                Credit(previn, played.Id, null, "Previn", null),
                Credit(previn, null, album.Id, null, null));
            await db.SaveChangesAsync(Token);
        }

        try
        {
            using var client = _factory!.CreateClient();

            var songs = (await JsonAsync(client, $"/rest/getAlbum.view?id=al-{album.Id.Value:N}"))
                .GetProperty("subsonic-response")
                .GetProperty("album")
                .GetProperty("song")
                .EnumerateArray()
                .ToDictionary(row => row.GetProperty("title").GetString()!);

            var song = songs[played.Title];

            Assert.Equal(
                [
                    ("conductor", null, $"ar-{previn.Id.Value:N}", "André Previn", previn.Mbid!.Value.Value.ToString()),
                    ("performer", "orchestra", "", "London Symphony Orchestra", orchestra.Value.ToString()),
                    ("producer", null, "", "Christopher Bishop", null),
                    ("composer", null, "", "Пётр Ильич Чайковский", tchaikovsky.Value.ToString()),
                ],
                song.GetProperty("contributors").EnumerateArray().Select(credit =>
                {
                    var artist = credit.GetProperty("artist");

                    return (
                        credit.GetProperty("role").GetString(),
                        credit.TryGetProperty("subRole", out var part) ? part.GetString() : null,
                        artist.GetProperty("id").GetString(),
                        artist.GetProperty("name").GetString(),
                        artist.TryGetProperty("musicBrainzId", out var mbid) ? mbid.GetString() : null);
                }));

            List<(string?, string?)> Artists(JsonElement row) =>
                [.. row.GetProperty("artists").EnumerateArray().Select(artist => (artist.GetProperty("id").GetString(), artist.GetProperty("name").GetString()))];

            Assert.Equal("Ida Händel, London Symphony Orchestra, Previn = Previn", song.GetProperty("artist").GetString());
            Assert.Equal(
                [($"ar-{haendel.Id.Value:N}", "Ida Haendel"), ($"ar-{lso.Id.Value:N}", "London Symphony Orchestra"), ($"ar-{previn.Id.Value:N}", "André Previn")],
                Artists(song));
            Assert.Equal([($"ar-{previn.Id.Value:N}", "André Previn")], Artists(songs[bare.Title]));
            Assert.All(song.GetProperty("artists").EnumerateArray(), artist => Assert.Equal(2, artist.EnumerateObject().Count()));

            var recorded = song.GetProperty("recorded");
            Assert.Equal("""{"year":1974,"month":4,"day":8}""", recorded.GetProperty("from").GetRawText());
            Assert.Equal("""{"year":1974,"month":6,"day":29}""", recorded.GetProperty("to").GetRawText());
            Assert.Equal(["Abbey Road Studios: Studio 1"], recorded.GetProperty("places").EnumerateArray().Select(place => place.GetString()));

            var composed = Assert.Single(song.GetProperty("works").EnumerateArray()).GetProperty("composed");
            Assert.Equal("""{"year":1888}""", composed.GetProperty("from").GetRawText());
            Assert.Equal("""{"year":1889}""", composed.GetProperty("to").GetRawText());

            var silent = songs[bare.Title];
            Assert.Empty(silent.GetProperty("contributors").EnumerateArray());
            Assert.False(silent.TryGetProperty("recorded", out _));

            // A song of no work: its recording's own links alone, and nobody
            // [traditional] names stands as a writer.
            var single = songs[pop.Title];
            var player = Assert.Single(single.GetProperty("contributors").EnumerateArray());
            Assert.Equal(("performer", "guitar"), (player.GetProperty("role").GetString(), player.GetProperty("subRole").GetString()));
            Assert.Equal(("", "Mark Knopfler"), (player.GetProperty("artist").GetProperty("id").GetString(), player.GetProperty("artist").GetProperty("name").GetString()));
            Assert.Equal("""{"year":1977,"month":7}""", single.GetProperty("recorded").GetProperty("from").GetRawText());
            Assert.Empty(single.GetProperty("works").EnumerateArray());

            using var response = await client.GetAsync(
                new Uri($"/rest/getAlbum.view?{Credentials}&id=al-{album.Id.Value:N}", UriKind.Relative),
                Token);

            var xml = XDocument.Parse(await response.Content.ReadAsStringAsync(Token));
            XNamespace ns = "http://subsonic.org/restapi";
            var element = xml.Descendants(ns + "song").Single(row => row.Attribute("title")?.Value == played.Title);

            Assert.Equal(
                ["conductor", "performer", "producer", "composer"],
                element.Elements(ns + "contributors").Select(credit => credit.Attribute("role")?.Value));
            Assert.Equal("", element.Elements(ns + "contributors").ElementAt(1).Element(ns + "artist")?.Attribute("id")?.Value);
            Assert.Equal("1974", element.Element(ns + "recorded")?.Element(ns + "from")?.Attribute("year")?.Value);
            Assert.Equal(["Abbey Road Studios: Studio 1"], element.Element(ns + "recorded")!.Elements(ns + "places").Select(place => place.Value));
            Assert.Equal("1889", element.Element(ns + "works")?.Element(ns + "composed")?.Element(ns + "to")?.Attribute("year")?.Value);
            Assert.Equal(
                [$"ar-{haendel.Id.Value:N}", $"ar-{lso.Id.Value:N}", $"ar-{previn.Id.Value:N}"],
                element.Elements(ns + "artists").Select(artist => artist.Attribute("id")?.Value));

            var extensions = (await JsonAsync(client, "/rest/getOpenSubsonicExtensions.view"))
                .GetProperty("subsonic-response")
                .GetProperty("openSubsonicExtensions")
                .EnumerateArray()
                .Select(extension => extension.GetProperty("name").GetString());
            Assert.Contains("fonotecaPerformance", extensions);
        }
        finally
        {
            await using var db = PostgresFixture.CreateContext(_connectionString);
            var artistIds = new[] { previn.Id, haendel.Id, lso.Id };
            db.ArtistCredits.RemoveRange(await db.ArtistCredits.Where(row => artistIds.Contains(row.ArtistId)).ToListAsync(Token));
            var workIds = new[] { ballet.Id, scene.Id };
            db.Contributions.RemoveRange(await db.Contributions.Where(row => row.WorkId != null && workIds.Contains(row.WorkId.Value)).ToListAsync(Token));
            db.MediaFiles.RemoveRange(await db.MediaFiles.Where(file => file.ReleaseGroupId == album.Id).ToListAsync(Token));
            var ids = new[] { played.Id, bare.Id, pop.Id };
            db.Recordings.RemoveRange(await db.Recordings.Where(row => ids.Contains(row.Id)).ToListAsync(Token));
            await db.SaveChangesAsync(Token);
            db.Works.RemoveRange(await db.Works.Where(row => workIds.Contains(row.Id)).OrderBy(row => row.ParentId == null).ToListAsync(Token));
            db.Artists.RemoveRange(await db.Artists.Where(row => artistIds.Contains(row.Id)).ToListAsync(Token));
            db.ReleaseGroups.Remove(await db.ReleaseGroups.SingleAsync(row => row.Id == album.Id, Token));
            await db.SaveChangesAsync(Token);
        }
    }

    /// <summary>
    /// Every method this server claims answers, and nothing 500s.
    /// </summary>
    /// <remarks>
    /// Thin on purpose and wide on purpose. Most of these have no assertion
    /// worth making beyond "it answered", but the projections behind them are
    /// where an EF query that cannot be translated hides — and an untranslatable
    /// query compiles, ships, and throws the first time a client opens that
    /// screen. Running each one against a real database is what catches it.
    /// </remarks>
    [Theory]
    [InlineData("ping")]
    [InlineData("getLicense")]
    [InlineData("getOpenSubsonicExtensions")]
    [InlineData("getMusicFolders")]
    [InlineData("getIndexes")]
    [InlineData("getArtists")]
    [InlineData("getUser")]
    [InlineData("getPlaylists")]
    [InlineData("scrobble&id=1")]
    [InlineData("getScanStatus")]
    [InlineData("search3&query=evans")]
    [InlineData("search3")]
    [InlineData("getAlbumList2&type=newest")]
    [InlineData("getAlbumList2&type=random")]
    [InlineData("getAlbumList2&type=alphabeticalByArtist")]
    [InlineData("getAlbumList2&type=byYear&fromYear=2000&toYear=1900")]
    [InlineData("getAlbumList2&type=frequent")]
    [InlineData("getLyrics&artist=Nobody&title=Nothing")]
    [InlineData("getLyrics")]
    [InlineData("getComposers")]
    [InlineData("getCompositions")]
    [InlineData("getStarred2")]
    [InlineData("getAlbumList2&type=starred")]
    [InlineData("getTopSongs&artist=Nobody")]
    [InlineData("getTags")]
    [InlineData("getPlaylistFolders")]
    public async Task EveryMethodAnswers(string method)
    {
        using var client = _factory!.CreateClient();

        var route = method.Split('&', 2);

        var json = await JsonAsync(
            client,
            route.Length == 1 ? $"/rest/{route[0]}.view" : $"/rest/{route[0]}.view?{route[1]}");

        Assert.Equal("ok", json.GetProperty("subsonic-response").GetProperty("status").GetString());
    }

    /// <summary>
    /// The words, timed line by line by song id and plain by title; a song
    /// with none, or a file that cannot be read, answers an empty list.
    /// </summary>
    [Fact]
    public async Task TheWordsAreServedTimedBySongAndPlainByTitle()
    {
        await using (var db = PostgresFixture.CreateContext(_connectionString))
        {
            db.RecordingLyrics.Add(new RecordingLyrics
            {
                RecordingId = (await db.MediaFiles.SingleAsync(file => file.Id == _filed, Token)).RecordingId!.Value,
                Synced = "[offset:-100]\n[00:00.78] My foolish heart\n[00:12.80] How can I",
                Plain = "My foolish heart\nHow can I",
                SavedUtc = DateTimeOffset.UtcNow,
            });

            await db.SaveChangesAsync(Token);
        }

        using var client = _factory!.CreateClient();

        var lyrics = (await JsonAsync(client, $"/rest/getLyricsBySongId.view?id=tr-{_filed.Value:N}"))
            .GetProperty("subsonic-response")
            .GetProperty("lyricsList")
            .GetProperty("structuredLyrics")
            .EnumerateArray()
            .Single();

        Assert.True(lyrics.GetProperty("synced").GetBoolean());
        Assert.Equal(-100, lyrics.GetProperty("offset").GetInt32());
        Assert.Equal("My Foolish Heart", lyrics.GetProperty("displayTitle").GetString());
        Assert.Equal(
            [(780, "My foolish heart"), (12800, "How can I")],
            lyrics.GetProperty("line").EnumerateArray().Select(line => (line.GetProperty("start").GetInt32(), line.GetProperty("value").GetString())));

        var plain = (await JsonAsync(client, "/rest/getLyrics.view?artist=evans&title=my%20foolish%20heart"))
            .GetProperty("subsonic-response")
            .GetProperty("lyrics");

        Assert.Equal("My foolish heart\nHow can I", plain.GetProperty("value").GetString());
        Assert.Equal("My Foolish Heart", plain.GetProperty("title").GetString());

        var none = (await JsonAsync(client, $"/rest/getLyricsBySongId.view?id=tr-{_scanned.Value:N}"))
            .GetProperty("subsonic-response")
            .GetProperty("lyricsList")
            .GetProperty("structuredLyrics");

        Assert.Equal(0, none.GetArrayLength());

        var extensions = (await JsonAsync(client, "/rest/getOpenSubsonicExtensions.view"))
            .GetProperty("subsonic-response")
            .GetProperty("openSubsonicExtensions")
            .EnumerateArray()
            .Select(extension => extension.GetProperty("name").GetString());

        Assert.Equal(["songLyrics", "formPost", "apiKeyAuthentication", "fonotecaAlbumMotion", "fonotecaPerformance", "fonotecaCompositions", "playlistFolders", "fonotecaTags", "fonotecaGenres"], extensions);
    }

    /// <summary>
    /// The album and artist endpoints answer for the seeded release.
    /// </summary>
    /// <remarks>
    /// Separate from the table above because both need an id that only exists
    /// once the fixture has been written, and because these two carry the
    /// heaviest projections on the surface.
    /// </remarks>
    [Fact]
    public async Task TheAlbumAndItsArtistAnswerForARealId()
    {
        using var client = _factory!.CreateClient();

        var album = (await JsonAsync(client, $"/rest/getAlbum.view?id={await AlbumIdAsync()}"))
            .GetProperty("subsonic-response")
            .GetProperty("album");

        var artistId = album.GetProperty("artistId").GetString();

        var artist = (await JsonAsync(client, $"/rest/getArtist.view?id={artistId}"))
            .GetProperty("subsonic-response")
            .GetProperty("artist");

        Assert.Equal("Bill Evans", artist.GetProperty("name").GetString());
        Assert.Equal(1, artist.GetProperty("albumCount").GetInt32());
        Assert.Equal("Waltz for Debby", artist.GetProperty("album").EnumerateArray().Single()
            .GetProperty("name").GetString());

        var song = (await JsonAsync(
                client,
                $"/rest/getSong.view?id=tr-{_filed.Value:N}"))
            .GetProperty("subsonic-response")
            .GetProperty("song");

        Assert.Equal("My Foolish Heart", song.GetProperty("title").GetString());
    }

    /// <summary>
    /// An artist with a provider's picture carries a cover art id, and
    /// getCoverArt serves the picture itself, fetched once and scaled per size,
    /// so a client never contacts the CDN; an artist with none carries no id.
    /// </summary>
    [Fact]
    public async Task AnArtistsPictureComesFromFonotecaFetchedOnceAndSized()
    {
        const string Url = "https://commons.wikimedia.org/wiki/Special:FilePath/Bill_Evans.jpg";

        var made = Path.Combine(_root, "portrait-800x1200.jpg");
        Tool("ffmpeg", "-v", "error", "-y", "-f", "lavfi", "-i", "color=c=green:s=800x1200", "-frames:v", "1", made);
        var original = await File.ReadAllBytesAsync(made, Token);
        File.Delete(made);

        var cdn = new StubHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(original) { Headers = { ContentType = new("image/jpeg") } },
        });

        await using var factory = _factory!.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.AddHttpClient(WikidataOptions.PicturesClientName).ConfigurePrimaryHttpMessageHandler(() => cdn)));

        var scaler = factory.Services.GetRequiredService<Fonoteca.Ingest.FfmpegCoverScaler>();
        var asked = PortraitRendition.Sized(Url, 1280);
        var kept = Path.Combine(
            Path.GetTempPath(),
            "fonoteca-portraits",
            Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(asked))) + ".jpg");

        using var client = factory.CreateClient();

        try
        {
            var bare = await BillEvansAsync(client);
            Assert.False(bare.TryGetProperty("coverArt", out _));

            await using (var db = PostgresFixture.CreateContext(_connectionString))
            {
                var evans = await db.Artists.SingleAsync(row => row.Name == "Bill Evans", Token);
                evans.PortraitUrl = Url;
                await db.SaveChangesAsync(Token);
            }

            var pictured = await BillEvansAsync(client);
            var id = pictured.GetProperty("id").GetString()!;
            Assert.Equal(id, pictured.GetProperty("coverArt").GetString());

            var scaled = await PictureAsync(client, id, 300);
            Assert.Equal(scaled, await PictureAsync(client, id, 300));
            Assert.Equal(original, await PictureAsync(client, id, null));

            // One fetch for all three, at the shelf's width, and kept.
            Assert.Equal(asked, Assert.Single(cdn.Requests).Uri.AbsoluteUri);
            Assert.Equal(original, await File.ReadAllBytesAsync(kept, Token));

            var file = Path.Combine(_root, "portrait-scaled.jpg");
            await File.WriteAllBytesAsync(file, scaled, Token);
            Assert.Equal(
                "200,300",
                Tool("ffprobe", "-v", "error", "-select_streams", "v:0", "-show_entries", "stream=width,height", "-of", "csv=p=0", file).Trim());
            File.Delete(file);
        }
        finally
        {
            if (File.Exists(kept)) File.Delete(kept);

            foreach (var kind in new[] { ".jpg", ".as-is" })
            {
                File.Delete(Path.Combine(scaler.Directory, Fonoteca.Ingest.FfmpegCoverScaler.KeyOf(original, 300) + kind));
            }

            await using var db = PostgresFixture.CreateContext(_connectionString);
            (await db.Artists.SingleAsync(row => row.Name == "Bill Evans", Token)).PortraitUrl = null;
            await db.SaveChangesAsync(Token);
        }
    }

    /// <summary>
    /// An upload stands for the artist with no fetch; a person's removal of the
    /// provider's picture leaves no id; a fetch that fails is a problem
    /// document and keeps nothing.
    /// </summary>
    [Fact]
    public async Task AnArtistsUploadNeedsNoFetchAndAFailedFetchKeepsNothing()
    {
        const string Url = "https://commons.wikimedia.org/wiki/Special:FilePath/Bill_Evans_failing.jpg";

        var cdn = new StubHttpHandler(_ => throw new HttpRequestException("the CDN is down"));

        await using var factory = _factory!.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.AddHttpClient(WikidataOptions.PicturesClientName).ConfigurePrimaryHttpMessageHandler(() => cdn)));

        var kept = Path.Combine(
            Path.GetTempPath(),
            "fonoteca-portraits",
            Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(PortraitRendition.Sized(Url, 1280)))) + ".jpg");

        using var client = factory.CreateClient();
        ArtistId evans;

        await using (var db = PostgresFixture.CreateContext(_connectionString))
        {
            var row = await db.Artists.SingleAsync(artist => artist.Name == "Bill Evans", Token);
            evans = row.Id;
            db.ArtistImages.Add(new ArtistImage
            {
                ArtistId = evans,
                Kind = ArtistImageKind.Portrait.Name,
                Bytes = [0xFF, 0xD8, 0xFF, 0xD9],
                MediaType = "image/jpeg",
                SavedUtc = DateTimeOffset.Parse("2026-01-05T00:00:00Z", CultureInfo.InvariantCulture),
            });
            await db.SaveChangesAsync(Token);
        }

        try
        {
            var uploaded = await BillEvansAsync(client);
            var id = uploaded.GetProperty("coverArt").GetString()!;
            var served = await PictureAsync(client, id, null);
            Assert.Equal<byte>([0xFF, 0xD8, 0xFF, 0xD9], served);
            Assert.Empty(cdn.Requests);

            await using (var db = PostgresFixture.CreateContext(_connectionString))
            {
                db.ArtistImages.RemoveRange(await db.ArtistImages.Where(image => image.ArtistId == evans).ToListAsync(Token));
                var row = await db.Artists.SingleAsync(artist => artist.Id == evans, Token);
                row.PortraitUrl = Url;
                row.EditsJson = PersonEdits.Write(new Dictionary<string, string?> { [ArtistImageKind.Portrait.Name] = string.Empty });
                await db.SaveChangesAsync(Token);
            }

            Assert.False((await BillEvansAsync(client)).TryGetProperty("coverArt", out _));

            await using (var db = PostgresFixture.CreateContext(_connectionString))
            {
                (await db.Artists.SingleAsync(artist => artist.Id == evans, Token)).EditsJson = null;
                await db.SaveChangesAsync(Token);
            }

            Assert.Equal(id, (await BillEvansAsync(client)).GetProperty("coverArt").GetString());

            using var failed = await client.GetAsync(
                new Uri($"/rest/getCoverArt.view?{Credentials}&id={id}&size=300", UriKind.Relative),
                Token);

            Assert.Equal(HttpStatusCode.BadGateway, failed.StatusCode);
            Assert.Equal("application/problem+json", failed.Content.Headers.ContentType?.MediaType);
            Assert.Single(cdn.Requests);
            Assert.False(File.Exists(kept));
        }
        finally
        {
            await using var db = PostgresFixture.CreateContext(_connectionString);
            db.ArtistImages.RemoveRange(await db.ArtistImages.Where(image => image.ArtistId == evans).ToListAsync(Token));
            var row = await db.Artists.SingleAsync(artist => artist.Id == evans, Token);
            row.PortraitUrl = null;
            row.EditsJson = null;
            await db.SaveChangesAsync(Token);
        }
    }

    /// <summary>
    /// A form body is read as the query (<c>formPost</c>): the password, the
    /// format and the arguments in it, or split between it and the URL with
    /// the body's winning a key both name, a wrong password still refused, a
    /// method this server lacks still in the envelope, a body past the form
    /// limits answered in it, and a browser elsewhere let in to send it.
    /// </summary>
    [Fact]
    public async Task AFormBodyIsReadAsTheQuery()
    {
        using var client = _factory!.CreateClient();

        async Task<JsonElement> PostAsync(string route, params (string Key, string Value)[] fields)
        {
            using var content = new FormUrlEncodedContent(fields.Select(field => KeyValuePair.Create(field.Key, field.Value)));
            using var response = await client.PostAsync(new Uri(route, UriKind.Relative), content, Token);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);

            return JsonDocument.Parse(await response.Content.ReadAsStringAsync(Token)).RootElement.GetProperty("subsonic-response").Clone();
        }

        (string, string)[] login = [("u", "me"), ("p", Password), ("v", "1.16.1"), ("c", "test"), ("f", "json")];
        var song = $"tr-{_filed.Value:N}";

        try
        {
            Assert.Equal("ok", (await PostAsync("/rest/ping.view", login)).GetProperty("status").GetString());
            Assert.Equal("ok", (await PostAsync("/rest/star", [.. login, ("id", song)])).GetProperty("status").GetString());
            Assert.NotNull((await JsonAsync(client, $"/rest/getSong.view?id={song}")).GetProperty("subsonic-response").GetProperty("song").GetProperty("starred").GetString());

            // Split between the URL and the body, which wins a key both name, in any case.
            Assert.Equal("ok", (await PostAsync($"/rest/unstar.view?u=me&p=not-it&v=1.16.1&c=test&f=xml&id={song}", ("P", Password), ("f", "json"))).GetProperty("status").GetString());
            Assert.False((await JsonAsync(client, $"/rest/getSong.view?id={song}")).GetProperty("subsonic-response").GetProperty("song").TryGetProperty("starred", out _));

            var wrong = await PostAsync("/rest/star", ("u", "me"), ("p", "not-it"), ("v", "1.16.1"), ("c", "test"), ("f", "json"), ("id", song));
            Assert.Equal(40, wrong.GetProperty("error").GetProperty("code").GetInt32());

            var missing = await PostAsync("/rest/getPodcasts", login);
            Assert.Equal(0, missing.GetProperty("error").GetProperty("code").GetInt32());

            // Past the form limits, f=json is in the body that went unread.
            using var flood = new FormUrlEncodedContent([.. login.Select(field => KeyValuePair.Create(field.Item1, field.Item2)), .. Enumerable.Repeat(KeyValuePair.Create("id", song), 1100)]);
            using var flooded = await client.PostAsync(new Uri("/rest/star", UriKind.Relative), flood, Token);
            Assert.Equal(HttpStatusCode.OK, flooded.StatusCode);
            Assert.Contains("code=\"0\"", await flooded.Content.ReadAsStringAsync(Token), StringComparison.Ordinal);

            using var preflight = new HttpRequestMessage(HttpMethod.Options, new Uri("/rest/star", UriKind.Relative));
            preflight.Headers.Add("Origin", "http://example.org");
            preflight.Headers.Add("Access-Control-Request-Method", "POST");
            preflight.Headers.Add("Access-Control-Request-Headers", "content-type");

            using var preflighted = await client.SendAsync(preflight, Token);
            Assert.True(preflighted.IsSuccessStatusCode, $"{preflighted.StatusCode}");
            Assert.Equal("*", Assert.Single(preflighted.Headers.GetValues("Access-Control-Allow-Origin")));

            // Any other body is not read: no password is found in it.
            using var multipart = new MultipartFormDataContent();
            foreach (var (key, value) in login)
            {
                multipart.Add(new StringContent(value), key);
            }

            using var refused = await client.PostAsync(new Uri("/rest/ping.view", UriKind.Relative), multipart, Token);
            Assert.Contains("code=\"40\"", await refused.Content.ReadAsStringAsync(Token), StringComparison.Ordinal);

            using var misnamed = new StringContent($"u=me&p={Password}&v=1.16.1&c=test&f=json");
            misnamed.Headers.Remove("Content-Type");
            misnamed.Headers.TryAddWithoutValidation("Content-Type", "application/x-www-form-urlencodedx");

            using var unread = await client.PostAsync(new Uri("/rest/ping.view", UriKind.Relative), misnamed, Token);
            Assert.Equal(HttpStatusCode.OK, unread.StatusCode);
            Assert.Contains("code=\"40\"", await unread.Content.ReadAsStringAsync(Token), StringComparison.Ordinal);
        }
        finally
        {
            await using var db = PostgresFixture.CreateContext(_connectionString);
            db.Loves.RemoveRange(await db.Loves.IgnoreQueryFilters().ToListAsync(Token));
            await db.SaveChangesAsync(Token);
        }
    }

    /// <summary>
    /// A heart on a song is on every copy of it, and an album, an artist and a
    /// whole piece take one too; a request naming anything that is none of
    /// those changes nothing, and unstarring takes each off again.
    /// </summary>
    /// <remarks>
    /// A best-of's lossy copy of the filed song carries the heart; the scanned
    /// file, with no recording yet, is loved as itself and keeps it once it
    /// gains one. Removes what it adds, because the class shares one database.
    /// </remarks>
    [Fact]
    public async Task ALoveIsOnEveryCopyOfASongAndComesOffAgain()
    {
        var copy = MediaFileId.New();
        var later = new Recording { Id = RecordingId.New(), Title = "Nameless" };
        var piece = new Work { Id = WorkId.New(), Title = "Peace Piece" };
        var sunday = new ReleaseGroup { Id = ReleaseGroupId.New(), Title = "Sunday at the Village Vanguard" };
        var unheld = new ReleaseGroup { Id = ReleaseGroupId.New(), Title = "Explorations" };
        var gloria = new MediaFile
        {
            Id = MediaFileId.New(),
            Path = "Bill Evans/Sunday at the Village Vanguard/01 Gloria's Step.flac",
            SizeBytes = 1024,
            LastModifiedUtc = DateTimeOffset.Parse("2026-01-05T00:00:00Z", CultureInfo.InvariantCulture),
            ReleaseGroupId = sunday.Id,
        };
        var part = new Work { Id = WorkId.New(), Title = "Peace Piece: I.", ParentId = piece.Id, Position = 1 };
        ArtistId artist;
        ReleaseGroupId album;

        await using (var seed = PostgresFixture.CreateContext(_connectionString))
        {
            var filed = await seed.MediaFiles.SingleAsync(file => file.Id == _filed, Token);
            artist = (await seed.Artists.SingleAsync(row => row.Name == "Bill Evans", Token)).Id;
            album = filed.ReleaseGroupId!.Value;

            seed.MediaFiles.Add(new MediaFile
            {
                Id = copy,
                Path = "Bill Evans/The Best Of/03 My Foolish Heart.mp3",
                SizeBytes = 2048,
                LastModifiedUtc = DateTimeOffset.Parse("2026-01-04T00:00:00Z", CultureInfo.InvariantCulture),
                RecordingId = filed.RecordingId,
                Quality = new AudioQuality { Codec = "mp3", SampleRateHz = 44_100, Channels = 2, BitrateBps = 320_000, IsLossless = false, Duration = TimeSpan.FromSeconds(292) },
            });
            seed.Recordings.Add(later);
            seed.Works.AddRange(piece, part);
            seed.ReleaseGroups.AddRange(sunday, unheld);
            seed.MediaFiles.Add(gloria);
            await seed.SaveChangesAsync(Token);
        }

        using var client = _factory!.CreateClient();

        async Task<JsonElement> Answer(string route) => (await JsonAsync(client, route)).GetProperty("subsonic-response");

        async Task<string?> Starred(string route, string name) =>
            (await Answer(route)).GetProperty(name).TryGetProperty("starred", out var when) ? when.GetString() : null;

        async Task<List<string?>> Listed(string name)
        {
            var starred = (await Answer("/rest/getStarred2.view")).GetProperty("starred2");
            return starred.TryGetProperty(name, out var rows) ? [.. rows.EnumerateArray().Select(row => row.GetProperty("id").GetString()).Order()] : [];
        }

        var a = $"al-{album.Value:N}";
        var song = $"/rest/getSong.view?id=tr-{_filed.Value:N}";
        var scanned = $"/rest/getSong.view?id=tr-{_scanned.Value:N}";

        try
        {
            // All or nothing: the movement is no composition, so the album is not loved either.
            Assert.Equal(70, (await Answer($"/rest/star.view?albumId={a}&id=wk-{part.Id.Value:N}")).GetProperty("error").GetProperty("code").GetInt32());
            Assert.Equal(70, (await Answer($"/rest/star.view?id=tr-{Guid.CreateVersion7():N}")).GetProperty("error").GetProperty("code").GetInt32());
            Assert.Equal(70, (await Answer($"/rest/star.view?artistId=ar-{Guid.CreateVersion7():N}")).GetProperty("error").GetProperty("code").GetInt32());
            Assert.Equal(70, (await Answer($"/rest/star.view?albumId=al-{Guid.CreateVersion7():N}")).GetProperty("error").GetProperty("code").GetInt32());
            Assert.Equal(70, (await Answer($"/rest/star.view?albumId=al-{unheld.Id.Value:N}")).GetProperty("error").GetProperty("code").GetInt32());
            Assert.Equal(10, (await Answer("/rest/star.view")).GetProperty("error").GetProperty("code").GetInt32());
            Assert.Null(await Starred($"/rest/getAlbum.view?id={a}", "album"));

            // A double tap: the same heart at once, every request answered, one heart kept.
            foreach (var verb in new[] { "star", "unstar" })
            {
                Assert.All(
                    await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Answer($"/rest/{verb}.view?id=tr-{_filed.Value:N}&albumId={a}"))),
                    tap => Assert.Equal("ok", tap.GetProperty("status").GetString()));
            }

            Assert.Null(await Starred(song, "song"));

            // Two copies of one song in one request are one heart.
            Assert.Equal("ok", (await Answer($"/rest/star.view?id=tr-{_filed.Value:N}&id=tr-{copy.Value:N}")).GetProperty("status").GetString());
            Assert.Equal("ok", (await Answer($"/rest/unstar.view?id=tr-{_filed.Value:N}&id=tr-{copy.Value:N}")).GetProperty("status").GetString());
            Assert.Null(await Starred(song, "song"));

            Assert.Equal(
                "ok",
                (await Answer($"/rest/star.view?id=%20tr-{copy.Value:N}%20&id=tr-{_scanned.Value:N}&albumId={a}&artistId=ar-{artist.Value:N}&id=wk-{piece.Id.Value:N}")).GetProperty("status").GetString());

            // The best-of's copy was starred; the album's copy shows it too.
            var first = await Starred(song, "song");
            Assert.NotNull(first);
            Assert.NotNull(await Starred($"/rest/getSong.view?id=tr-{copy.Value:N}", "song"));
            Assert.NotNull(await Starred(scanned, "song"));
            Assert.NotNull(await Starred($"/rest/getAlbum.view?id={a}", "album"));
            Assert.NotNull((await Answer($"/rest/getAlbum.view?id={a}")).GetProperty("album").GetProperty("song")[0].GetProperty("starred").GetString());
            Assert.NotNull(await Starred($"/rest/getArtist.view?id=ar-{artist.Value:N}", "artist"));

            // Each song once, as the copy a playlist plays: the FLAC, not the MP3.
            Assert.Equal([$"ar-{artist.Value:N}"], await Listed("artist"));
            Assert.Equal([a], await Listed("album"));
            Assert.Equal(new[] { $"tr-{_filed.Value:N}", $"tr-{_scanned.Value:N}" }.Order(), await Listed("song"));

            // The MP3 loved as itself before it was identified is still that one song.
            await using (var db = PostgresFixture.CreateContext(_connectionString))
            {
                db.Loves.Add(new Love { Id = Guid.CreateVersion7(), MediaFileId = copy, AccountId = _account, LovedUtc = DateTimeOffset.UtcNow });
                await db.SaveChangesAsync(Token);
                Assert.Equal(new[] { $"tr-{_filed.Value:N}", $"tr-{_scanned.Value:N}" }.Order(), await Listed("song"));
                db.Loves.Remove(await db.Loves.IgnoreQueryFilters().SingleAsync(love => love.MediaFileId == copy, Token));
                await db.SaveChangesAsync(Token);
            }

            Assert.Equal(
                [a],
                (await Answer("/rest/getAlbumList2.view?type=starred")).GetProperty("albumList2").GetProperty("album").EnumerateArray().Select(row => row.GetProperty("id").GetString()));

            // Newest love first.
            var b = $"al-{sunday.Id.Value:N}";
            Assert.Equal("ok", (await Answer($"/rest/star.view?albumId={b}")).GetProperty("status").GetString());
            Assert.Equal(
                [b, a],
                (await Answer("/rest/getAlbumList2.view?type=starred")).GetProperty("albumList2").GetProperty("album").EnumerateArray().Select(row => row.GetProperty("id").GetString()));
            Assert.Equal(
                [b, a],
                (await Answer("/rest/getStarred2.view")).GetProperty("starred2").GetProperty("album").EnumerateArray().Select(row => row.GetProperty("id").GetString()));
            Assert.Equal("ok", (await Answer($"/rest/unstar.view?albumId={b}")).GetProperty("status").GetString());

            // Starring again keeps the first time.
            Assert.Equal("ok", (await Answer($"/rest/star.view?id=tr-{_filed.Value:N}")).GetProperty("status").GetString());
            Assert.Equal(first, await Starred(song, "song"));

            // The scanned file gains a recording and keeps its heart; unstarring it takes it off.
            await using (var db = PostgresFixture.CreateContext(_connectionString))
            {
                (await db.MediaFiles.SingleAsync(file => file.Id == _scanned, Token)).RecordingId = later.Id;
                await db.SaveChangesAsync(Token);
            }

            Assert.NotNull(await Starred(scanned, "song"));
            Assert.Equal("ok", (await Answer($"/rest/unstar.view?id=tr-{_scanned.Value:N}")).GetProperty("status").GetString());
            Assert.Null(await Starred(scanned, "song"));

            Assert.Equal(
                "ok",
                (await Answer($"/rest/unstar.view?id=tr-{_filed.Value:N}&albumId={a}&artistId=ar-{artist.Value:N}&id=wk-{piece.Id.Value:N}")).GetProperty("status").GetString());
            Assert.Null(await Starred($"/rest/getSong.view?id=tr-{copy.Value:N}", "song"));
            Assert.Null(await Starred($"/rest/getAlbum.view?id={a}", "album"));
            Assert.False((await Answer("/rest/getAlbumList2.view?type=starred")).GetProperty("albumList2").TryGetProperty("album", out var none) && none.GetArrayLength() > 0);
            Assert.Empty(await Listed("artist"));
            Assert.Empty(await Listed("song"));
            Assert.Equal("ok", (await Answer($"/rest/unstar.view?albumId={a}")).GetProperty("status").GetString());

            // A copy loved as itself before it was identified is the song's heart:
            // every copy shows it, starring again keeps its time, and unstarring
            // any copy takes it off.
            await using (var db = PostgresFixture.CreateContext(_connectionString))
            {
                db.Loves.Add(new Love { Id = Guid.CreateVersion7(), MediaFileId = copy, AccountId = _account, LovedUtc = DateTimeOffset.Parse("2026-02-01T00:00:00Z", CultureInfo.InvariantCulture) });
                await db.SaveChangesAsync(Token);
            }

            var early = await Starred(song, "song");
            Assert.NotNull(early);
            Assert.Equal([$"tr-{_filed.Value:N}"], await Listed("song"));
            Assert.Equal("ok", (await Answer($"/rest/star.view?id=tr-{_filed.Value:N}")).GetProperty("status").GetString());
            Assert.Equal(early, await Starred(song, "song"));
            Assert.Equal("ok", (await Answer($"/rest/unstar.view?id=tr-{_filed.Value:N}")).GetProperty("status").GetString());
            Assert.Null(await Starred($"/rest/getSong.view?id=tr-{copy.Value:N}", "song"));

            await using var check = PostgresFixture.CreateContext(_connectionString);
            Assert.False(await check.Loves.IgnoreQueryFilters().AnyAsync(Token));
        }
        finally
        {
            await using var db = PostgresFixture.CreateContext(_connectionString);
            db.Loves.RemoveRange(await db.Loves.IgnoreQueryFilters().ToListAsync(Token));
            (await db.MediaFiles.SingleAsync(file => file.Id == _scanned, Token)).RecordingId = null;
            db.MediaFiles.Remove(await db.MediaFiles.SingleAsync(file => file.Id == copy, Token));
            db.MediaFiles.Remove(await db.MediaFiles.SingleAsync(file => file.Id == gloria.Id, Token));
            await db.SaveChangesAsync(Token);
            db.ReleaseGroups.RemoveRange(await db.ReleaseGroups.Where(row => row.Id == sunday.Id || row.Id == unheld.Id).ToListAsync(Token));
            db.Recordings.Remove(await db.Recordings.SingleAsync(row => row.Id == later.Id, Token));
            db.Works.RemoveRange(await db.Works.Where(row => row.Id == part.Id || row.Id == piece.Id).ToListAsync(Token));
            await db.SaveChangesAsync(Token);
        }
    }

    /// <summary>
    /// A listener's own tags: nested at any depth, on a song's recording so
    /// every copy shows them, counted with the tags below, and nobody else's
    /// to see or change.
    /// </summary>
    [Fact]
    public async Task ATagIsOnEveryCopyOfASongAndOnlyItsAccountSeesIt()
    {
        var copy = MediaFileId.New();
        var later = new Recording { Id = RecordingId.New(), Title = "Nameless" };

        await using (var seed = PostgresFixture.CreateContext(_connectionString))
        {
            var filed = await seed.MediaFiles.SingleAsync(file => file.Id == _filed, Token);

            seed.MediaFiles.Add(new MediaFile
            {
                Id = copy,
                Path = "Bill Evans/The Best Of/03 My Foolish Heart.mp3",
                SizeBytes = 2048,
                LastModifiedUtc = DateTimeOffset.Parse("2026-01-04T00:00:00Z", CultureInfo.InvariantCulture),
                RecordingId = filed.RecordingId,
                Quality = new AudioQuality { Codec = "mp3", SampleRateHz = 44_100, Channels = 2, BitrateBps = 320_000, IsLossless = false, Duration = TimeSpan.FromSeconds(292) },
            });
            seed.Recordings.Add(later);
            await seed.SaveChangesAsync(Token);
        }

        using var client = _factory!.CreateClient();

        async Task<JsonElement> Answer(string route, string user = "me")
        {
            var separator = route.Contains('?', StringComparison.Ordinal) ? "&" : "?";
            using var response = await client.GetAsync(
                new Uri($"/rest/{route}{separator}u={user}&p={Password}&v=1.16.1&c=test&f=json", UriKind.Relative),
                Token);

            return JsonDocument.Parse(await response.Content.ReadAsStringAsync(Token)).RootElement.GetProperty("subsonic-response").Clone();
        }

        static int Code(JsonElement answer) => answer.GetProperty("error").GetProperty("code").GetInt32();
        static string Status(JsonElement answer) => answer.GetProperty("status").GetString()!;

        async Task<string> MakeAsync(string query) =>
            (await Answer($"createTag.view?{query}")).GetProperty("tag").GetProperty("id").GetString()!;

        async Task<Dictionary<string, int>> CountsAsync(string user = "me") =>
            (await Answer("getTags.view", user)).GetProperty("tags") is var tags && tags.TryGetProperty("tag", out var rows)
                ? rows.EnumerateArray().ToDictionary(tag => tag.GetProperty("name").GetString()!, tag => tag.GetProperty("songCount").GetInt32())
                : [];

        async Task<List<string>> TagIdsAsync(MediaFileId song, string user = "me") =>
            (await Answer($"getSong.view?id=tr-{song.Value:N}", user)).GetProperty("song").TryGetProperty("tagIds", out var ids)
                ? [.. ids.EnumerateArray().Select(id => id.GetString()!).Order()]
                : [];

        async Task<List<string>> SongsAsync(string tag) =>
            (await Answer($"getTag.view?id={tag}")).GetProperty("tag") is var listed && listed.TryGetProperty("song", out var songs)
                ? [.. songs.EnumerateArray().Select(song => song.GetProperty("id").GetString()!).Order()]
                : [];

        var jazz = await MakeAsync("name=Jazz");
        var piano = await MakeAsync($"name=Piano&parentId={jazz}");
        var ballads = await MakeAsync("name=Ballads");
        var cool = await MakeAsync($"name=Cool&parentId={jazz}");

        var made = (await Answer($"createTag.view?name=Trio&parentId={piano}")).GetProperty("tag");
        Assert.Equal(piano, made.GetProperty("parentId").GetString());
        Assert.Equal(0, made.GetProperty("songCount").GetInt32());
        Assert.False((await Answer("createTag.view?name=Solo")).GetProperty("tag").TryGetProperty("parentId", out _));

        Assert.Equal(10, Code(await Answer("createTag.view?name=%20")));
        Assert.Equal(70, Code(await Answer($"createTag.view?name=x&parentId=tg-{Guid.CreateVersion7():N}")));
        Assert.Equal(0, Code(await Answer("createTag.view?name=jAZZ")));
        Assert.Equal(0, Code(await Answer($"createTag.view?name={new string('x', 201)}")));

        // A name is unique among its siblings only.
        var elsewhere = await MakeAsync($"name=cool&parentId={ballads}");
        Assert.Equal(0, Code(await Answer($"updateTag.view?tagId={elsewhere}&parentId={jazz}")));
        Assert.Equal("ok", Status(await Answer($"deleteTag.view?tagId={elsewhere}")));

        // The MP3 tagged: its recording is, so the album's FLAC shows it too.
        Assert.Equal("ok", Status(await Answer($"updateTag.view?tagId={piano}&songIdToAdd=tr-{copy.Value:N}")));
        Assert.Equal([piano], await TagIdsAsync(_filed));
        Assert.Equal([piano], await TagIdsAsync(copy));
        Assert.Equal("ok", Status(await Answer($"updateTag.view?tagId={jazz}&songIdToAdd=tr-{_scanned.Value:N}")));
        Assert.Equal([jazz], await TagIdsAsync(_scanned));

        // A song once under every tag above it, as the copy a playlist plays.
        Assert.Equal(2, (await CountsAsync())["Jazz"]);
        Assert.Equal(1, (await CountsAsync())["Piano"]);
        Assert.Equal(0, (await CountsAsync())["Cool"]);
        Assert.Equal(new[] { $"tr-{_filed.Value:N}", $"tr-{_scanned.Value:N}" }.Order(), await SongsAsync(jazz));
        Assert.Equal([$"tr-{_filed.Value:N}"], await SongsAsync(piano));
        Assert.Equal(1, (await Answer($"getTag.view?id={piano}")).GetProperty("tag").GetProperty("songCount").GetInt32());

        // Nothing is half done, and nothing twice.
        Assert.Equal(70, Code(await Answer($"updateTag.view?tagId={ballads}&songIdToAdd=tr-{_filed.Value:N}&songIdToAdd=tr-{Guid.CreateVersion7():N}")));
        Assert.Equal(70, Code(await Answer($"updateTag.view?tagId={ballads}&name=Slow&songIdToAdd={FolderId("Somebody/A Bootleg/01 Untitled.flac")}")));
        Assert.Equal(0, (await CountsAsync())["Ballads"]);
        Assert.Equal(10, Code(await Answer($"updateTag.view?tagId={ballads}")));
        Assert.Equal(10, Code(await Answer("updateTag.view?name=x")));
        Assert.Equal(70, Code(await Answer($"updateTag.view?tagId=tg-{Guid.CreateVersion7():N}&name=x")));
        // Connections opened first, or one tap runs ahead while the rest open theirs and never meets them.
        await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => Answer("getTags.view")));
        Assert.All(
            await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => Answer($"updateTag.view?tagId={ballads}&songIdToAdd=tr-{_filed.Value:N}&songIdToAdd=tr-{copy.Value:N}"))),
            tap => Assert.Equal("ok", Status(tap)));
        Assert.Equal(1, (await CountsAsync())["Ballads"]);

        // Taken off and put on in one request, a song keeps the tag.
        Assert.Equal("ok", Status(await Answer($"updateTag.view?tagId={ballads}&songIdToRemove=tr-{_filed.Value:N}&songIdToAdd=tr-{copy.Value:N}")));
        Assert.Equal(1, (await CountsAsync())["Ballads"]);
        Assert.Equal("ok", Status(await Answer($"updateTag.view?tagId={ballads}&songIdToRemove=tr-{copy.Value:N}")));
        Assert.Equal("ok", Status(await Answer($"updateTag.view?tagId={ballads}&songIdToRemove=tr-{copy.Value:N}")));
        Assert.Equal([piano], await TagIdsAsync(_filed));

        // A tag moves under another, never under itself or below it; an empty parent is the root.
        Assert.Equal(0, Code(await Answer($"updateTag.view?tagId={jazz}&parentId={piano}")));
        Assert.Equal(0, Code(await Answer($"updateTag.view?tagId={jazz}&parentId={jazz}")));
        Assert.Equal(70, Code(await Answer($"updateTag.view?tagId={jazz}&parentId=tg-{Guid.CreateVersion7():N}")));
        // As a form, too: an empty value is still sent, and a repeated one every time.
        using (var form = new FormUrlEncodedContent([
            new("u", "me"), new("p", Password), new("v", "1.16.1"), new("c", "test"), new("f", "json"),
            new("tagId", piano), new("parentId", string.Empty), new("songIdToAdd", $"tr-{_filed.Value:N}"), new("songIdToAdd", $"tr-{_scanned.Value:N}")]))
        using (var posted = await client.PostAsync(new Uri("/rest/updateTag.view", UriKind.Relative), form, Token))
        {
            Assert.Equal("ok", Status(JsonDocument.Parse(await posted.Content.ReadAsStringAsync(Token)).RootElement.GetProperty("subsonic-response")));
        }

        Assert.Equal(new[] { piano, jazz }.Order(), await TagIdsAsync(_scanned));
        Assert.Equal("ok", Status(await Answer($"updateTag.view?tagId={piano}&songIdToRemove=tr-{_scanned.Value:N}")));
        Assert.Equal(0, Code(await Answer($"updateTag.view?tagId={piano}&name=ballads")));
        Assert.Equal(0, Code(await Answer($"updateTag.view?tagId={ballads}&parentId={jazz}&name=cool")));
        Assert.Equal("ok", Status(await Answer($"updateTag.view?tagId={piano}&name=keys")));
        Assert.Equal("ok", Status(await Answer($"updateTag.view?tagId={piano}&name=Keys")));
        Assert.Equal(1, (await CountsAsync())["Jazz"]);
        Assert.Equal(1, (await CountsAsync())["Keys"]);

        // Taking a tag off a song takes it off only that tag, and off a copy tagged as itself.
        await using (var db = PostgresFixture.CreateContext(_connectionString))
        {
            db.SongTags.Add(new SongTag { Id = Guid.CreateVersion7(), TagId = SubsonicTag(ballads), MediaFileId = copy });
            await db.SaveChangesAsync(Token);
        }

        Assert.Equal(new[] { piano, ballads }.Order(), await TagIdsAsync(_filed));
        Assert.Equal(1, (await CountsAsync())["Ballads"]);
        Assert.Equal("ok", Status(await Answer($"updateTag.view?tagId={ballads}&songIdToRemove=tr-{_filed.Value:N}")));
        Assert.Equal([piano], await TagIdsAsync(copy));

        // A recording no file holds is no song yet.
        await using (var db = PostgresFixture.CreateContext(_connectionString))
        {
            db.SongTags.Add(new SongTag { Id = Guid.CreateVersion7(), TagId = SubsonicTag(cool), RecordingId = later.Id });
            await db.SaveChangesAsync(Token);
        }

        Assert.Equal(0, (await CountsAsync())["Cool"]);

        // The scanned file gains that recording and keeps its own tag.
        await using (var db = PostgresFixture.CreateContext(_connectionString))
        {
            (await db.MediaFiles.SingleAsync(file => file.Id == _scanned, Token)).RecordingId = later.Id;
            await db.SaveChangesAsync(Token);
        }

        Assert.Equal(1, (await CountsAsync())["Cool"]);
        Assert.Equal(1, (await CountsAsync())["Jazz"]);
        Assert.Equal(new[] { jazz, cool }.Order(), await TagIdsAsync(_scanned));
        Assert.Equal([$"tr-{_scanned.Value:N}"], await SongsAsync(jazz));

        // Somebody else sees none of it and can change none of it.
        Assert.Equal("ok", Status(await Answer("createUser.view?username=you&password=a-test-passphrase&email=you@example.org")));
        Assert.Empty(await CountsAsync("you"));
        Assert.Empty(await TagIdsAsync(_filed, "you"));
        Assert.Equal(70, Code(await Answer($"getTag.view?id={jazz}", "you")));
        Assert.Equal(70, Code(await Answer($"updateTag.view?tagId={jazz}&name=Mine", "you")));
        Assert.Equal(70, Code(await Answer($"createTag.view?name=Mine&parentId={jazz}", "you")));
        Assert.Equal(70, Code(await Answer($"deleteTag.view?tagId={jazz}", "you")));
        Assert.Equal("ok", Status(await Answer("createTag.view?name=Jazz", "you")));

        // A tag deleted takes those below it; the songs stay.
        Assert.Equal("ok", Status(await Answer($"deleteTag.view?tagId={jazz}")));
        Assert.Equal(new[] { "Ballads", "Keys", "Solo", "Trio" }.Order(), (await CountsAsync()).Keys.Order());
        Assert.Equal(70, Code(await Answer($"getTag.view?id={cool}")));
        Assert.Empty(await TagIdsAsync(_scanned));
        Assert.Equal(10, Code(await Answer("deleteTag.view")));
        Assert.Equal(new[] { "Jazz" }, (await CountsAsync("you")).Keys);

        Assert.Contains(
            (await Answer("getOpenSubsonicExtensions.view")).GetProperty("openSubsonicExtensions").EnumerateArray().Select(extension => extension.GetProperty("name").GetString()),
            name => name == "fonotecaTags");

        // The XML carries a song's tags as repeated elements.
        using var xml = await client.GetAsync(new Uri($"/rest/getSong.view?id=tr-{copy.Value:N}&{Credentials}", UriKind.Relative), Token);
        var song = XDocument.Parse(await xml.Content.ReadAsStringAsync(Token)).Descendants().Single(element => element.Name.LocalName == "song");
        Assert.Equal([piano], song.Elements().Where(element => element.Name.LocalName == "tagIds").Select(element => element.Value));

        static Guid SubsonicTag(string id) => Guid.ParseExact(id["tg-".Length..], "N");
    }

    private async Task<JsonElement> BillEvansAsync(HttpClient client) =>
        (await JsonAsync(client, "/rest/getArtists.view"))
            .GetProperty("subsonic-response")
            .GetProperty("artists")
            .GetProperty("index")
            .EnumerateArray()
            .SelectMany(index => index.GetProperty("artist").EnumerateArray())
            .Single(artist => artist.GetProperty("name").GetString() == "Bill Evans");

    private static async Task<byte[]> PictureAsync(HttpClient client, string id, int? size)
    {
        var sized = size is { } pixels ? $"&size={pixels}" : string.Empty;

        using var response = await client.GetAsync(
            new Uri($"/rest/getCoverArt.view?{Credentials}&id={id}{sized}", UriKind.Relative),
            Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.StartsWith("image/", response.Content.Headers.ContentType?.MediaType, StringComparison.Ordinal);

        return await response.Content.ReadAsByteArrayAsync(Token);
    }

    /// <summary>
    /// A method this server does not have is an answer, not an HTTP failure.
    /// </summary>
    /// <remarks>
    /// Several clients read a 404 as "this server is broken" and stop there,
    /// which turns one missing feature into an unusable server. Error 0 naming
    /// the method is a thing a person can act on.
    /// </remarks>
    [Fact]
    public async Task AMethodThisServerDoesNotHaveStillAnswersInTheEnvelope()
    {
        using var client = _factory!.CreateClient();

        var body = (await JsonAsync(client, "/rest/getPodcasts.view"))
            .GetProperty("subsonic-response");

        Assert.Equal("failed", body.GetProperty("status").GetString());
        Assert.Equal(0, body.GetProperty("error").GetProperty("code").GetInt32());
        Assert.Contains(
            "getPodcasts",
            body.GetProperty("error").GetProperty("message").GetString(),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// An artist's biography and an album's notes are their Wikipedia leads,
    /// as HTML with a link to the article, in both formats; an album answers
    /// for one of its songs too, and without an article there is nothing.
    /// </summary>
    [Fact]
    public async Task AnArtistsBiographyAndAnAlbumsNotesComeWithTheirCredit()
    {
        using var client = _factory!.CreateClient();

        var album = await AlbumIdAsync();
        var artistId = (await JsonAsync(client, $"/rest/getAlbum.view?id={album}"))
            .GetProperty("subsonic-response").GetProperty("album").GetProperty("artistId").GetString();

        var bare = (await JsonAsync(client, $"/rest/getArtistInfo2.view?id={artistId}"))
            .GetProperty("subsonic-response");

        Assert.Equal("ok", bare.GetProperty("status").GetString());
        Assert.False(bare.GetProperty("artistInfo2").TryGetProperty("biography", out _));

        await using (var db = PostgresFixture.CreateContext(_connectionString))
        {
            await db.Artists.ExecuteUpdateAsync(
                update => update
                    .SetProperty(artist => artist.BiographyText, "Pianist <b>and</b> composer.\nBorn 1929.")
                    .SetProperty(artist => artist.BiographyUrl, "https://en.wikipedia.org/wiki/Bill_Evans"),
                Token);

            await db.ReleaseGroups.ExecuteUpdateAsync(
                update => update
                    .SetProperty(group => group.ReviewText, "A live album.")
                    .SetProperty(group => group.ReviewUrl, "https://en.wikipedia.org/wiki/Waltz_for_Debby"),
                Token);
        }

        var biography = (await JsonAsync(client, $"/rest/getArtistInfo2.view?id={artistId}"))
            .GetProperty("subsonic-response").GetProperty("artistInfo2").GetProperty("biography").GetString();

        Assert.Equal(
            "Pianist &lt;b&gt;and&lt;/b&gt; composer.<br/>Born 1929.<br/><br/>"
            + "<a href=\"https://en.wikipedia.org/wiki/Bill_Evans\">Read more on Wikipedia</a>",
            biography);

        using var response = await client.GetAsync(
            new Uri($"/rest/getArtistInfo2.view?id={artistId}&{Credentials}", UriKind.Relative),
            Token);
        var xml = XDocument.Parse(await response.Content.ReadAsStringAsync(Token));
        XNamespace ns = "http://subsonic.org/restapi";

        Assert.Equal(biography, xml.Descendants(ns + "biography").Single().Value);

        foreach (var id in new[] { album, $"tr-{_filed.Value:N}" })
        {
            var notes = (await JsonAsync(client, $"/rest/getAlbumInfo2.view?id={id}"))
                .GetProperty("subsonic-response").GetProperty("albumInfo").GetProperty("notes").GetString();

            Assert.StartsWith("A live album.<br/><br/><a href=", notes, StringComparison.Ordinal);
        }

        foreach (var method in new[] { "getArtistInfo2", "getAlbumInfo2" })
        {
            var missing = (await JsonAsync(client, $"/rest/{method}.view?id=ar-{Guid.CreateVersion7():N}"))
                .GetProperty("subsonic-response");

            Assert.Equal(70, missing.GetProperty("error").GetProperty("code").GetInt32());
        }
    }

    /// <summary>
    /// In a folder listing, a song's parent is the folder it was listed in.
    /// </summary>
    /// <remarks>
    /// <c>parent</c> is how a client walks back up, and the two halves of this
    /// surface answer it differently on purpose: browsing by album, up is the
    /// album; browsing by folder, up is the directory. A catalogued file is the
    /// one row that appears in both, so it is the one that can get this wrong —
    /// and did, by reporting its album inside a directory listing that does not
    /// contain it.
    /// </remarks>
    [Fact]
    public async Task InAFolderListingASongsParentIsTheFolder()
    {
        using var client = _factory!.CreateClient();

        var folder = FolderId("Bill Evans/Waltz for Debby");

        var child = (await JsonAsync(client, $"/rest/getMusicDirectory.view?id={folder}"))
            .GetProperty("subsonic-response")
            .GetProperty("directory")
            .GetProperty("child")
            .EnumerateArray()
            .Single();

        Assert.Equal($"tr-{_filed.Value:N}", child.GetProperty("id").GetString());
        Assert.Equal(folder, child.GetProperty("parent").GetString());

        // Reached the other way, the same file's parent is the album.
        var song = (await JsonAsync(client, $"/rest/getAlbum.view?id={await AlbumIdAsync()}"))
            .GetProperty("subsonic-response")
            .GetProperty("album")
            .GetProperty("song")
            .EnumerateArray()
            .Single();

        Assert.Equal(await AlbumIdAsync(), song.GetProperty("parent").GetString());
    }

    /// <summary>
    /// A JSONP callback is an identifier or it is not honoured.
    /// </summary>
    /// <remarks>
    /// What comes back is a script on this application's own origin. The
    /// protocol only ever needs a function name there; anything else is somebody
    /// writing the body of the response, and the answer is to stop being JSONP
    /// rather than to escape it.
    /// </remarks>
    [Fact]
    public async Task AJsonpCallbackThatIsNotAnIdentifierIsNotEchoed()
    {
        using var client = _factory!.CreateClient();

        using var honoured = await client.GetAsync(
            new Uri($"/rest/ping.view?{Credentials}&f=jsonp&callback=cb", UriKind.Relative),
            Token);

        Assert.Equal("text/javascript", honoured.Content.Headers.ContentType?.MediaType);
        Assert.StartsWith("cb(", await honoured.Content.ReadAsStringAsync(Token), StringComparison.Ordinal);

        using var refused = await client.GetAsync(
            new Uri(
                $"/rest/ping.view?{Credentials}&f=jsonp&callback=alert(1);//",
                UriKind.Relative),
            Token);

        var body = await refused.Content.ReadAsStringAsync(Token);

        Assert.Equal("application/json", refused.Content.Headers.ContentType?.MediaType);
        Assert.DoesNotContain("alert", body, StringComparison.Ordinal);
        Assert.StartsWith("{", body, StringComparison.Ordinal);
    }

    /// <summary>
    /// Nothing under <c>/rest</c> reaches the generated contract.
    /// </summary>
    /// <remarks>
    /// OpenSubsonic is somebody else's contract. Described, it would land in
    /// <c>openapi.json</c>, regenerate <c>packages/api-client</c> and fail
    /// <c>gen:api:check</c> on a change nothing in the workspace calls — so the
    /// exclusion is load-bearing for the build rather than for the protocol.
    /// </remarks>
    [Fact]
    public void NothingUnderRestReachesTheGeneratedContract()
    {
        var endpoints = _factory!.Services
            .GetRequiredService<EndpointDataSource>()
            .Endpoints
            .OfType<RouteEndpoint>()
            .Where(endpoint => endpoint.RoutePattern.RawText?.StartsWith("/rest", StringComparison.Ordinal) == true)
            .ToList();

        Assert.NotEmpty(endpoints);

        Assert.All(
            endpoints,
            endpoint => Assert.Contains(
                endpoint.Metadata,
                item => item is IExcludeFromDescriptionMetadata { ExcludeFromDescription: true }));
    }

    /// <summary>
    /// A song carries its album's genres, and a client can browse by them —
    /// in both formats, since a genre's name is element text in XML.
    /// </summary>
    [Fact]
    public async Task GenresAreOnEverySongAndBrowsable()
    {
        await using (var db = PostgresFixture.CreateContext(_connectionString))
        {
            await db.ReleaseGroups.ExecuteUpdateAsync(
                update => update.SetProperty(group => group.Genres, "jazz, cool jazz"),
                Token);

            await db.Recordings.ExecuteUpdateAsync(
                update => update.SetProperty(recording => recording.Genres, "pop"),
                Token);

            // A second album, under another genre, with two songs: what the
            // filters have to leave out, and a song count that differs from
            // the album count.
            var rock = new ReleaseGroup { Id = ReleaseGroupId.New(), Title = "Loud", Genres = "rock, blues rock" };
            db.ReleaseGroups.Add(rock);

            foreach (var n in new[] { 1, 2 })
            {
                db.MediaFiles.Add(new MediaFile
                {
                    Id = MediaFileId.New(),
                    Path = $"Somebody/Loud/0{n} Song.flac",
                    SizeBytes = 100,
                    LastModifiedUtc = DateTimeOffset.Parse("2026-01-03T00:00:00Z", CultureInfo.InvariantCulture),
                    ReleaseGroupId = rock.Id,
                });
            }

            await db.SaveChangesAsync(Token);
        }

        using var client = _factory!.CreateClient();

        var song = (await JsonAsync(client, $"/rest/getSong.view?id=tr-{_filed.Value:N}"))
            .GetProperty("subsonic-response")
            .GetProperty("song");

        Assert.Equal("Jazz", song.GetProperty("genre").GetString());
        Assert.Equal(
            ["Jazz", "Cool Jazz"],
            song.GetProperty("genres").EnumerateArray().Select(genre => genre.GetProperty("name").GetString()));

        var genres = (await JsonAsync(client, "/rest/getGenres.view"))
            .GetProperty("subsonic-response")
            .GetProperty("genres")
            .GetProperty("genre")
            .EnumerateArray()
            .ToList();

        var cool = genres.Single(genre => genre.GetProperty("value").GetString() == "Cool Jazz");
        Assert.Equal(1, cool.GetProperty("songCount").GetInt32());
        Assert.Equal(1, cool.GetProperty("albumCount").GetInt32());

        var loud = genres.Single(genre => genre.GetProperty("value").GetString() == "Rock");
        Assert.Equal(2, loud.GetProperty("songCount").GetInt32());
        Assert.Equal(1, loud.GetProperty("albumCount").GetInt32());

        // A subgenre names its top, a fusion both, and a top none.
        string?[] Parents(string name) =>
            genres.Single(genre => genre.GetProperty("value").GetString() == name).TryGetProperty("parents", out var parents)
                ? [.. parents.EnumerateArray().Select(parent => parent.GetString())]
                : [];

        Assert.Equal(["Jazz"], Parents("Cool Jazz"));
        Assert.Equal(["Blues", "Rock"], Parents("Blues Rock"));
        Assert.Empty(Parents("Rock"));
        Assert.Empty(Parents("Blues"));

        // The album's genres stand for the file: its recording's "pop" is not one.
        Assert.DoesNotContain(genres, genre => genre.GetProperty("value").GetString() == "Pop");

        var byGenre = (await JsonAsync(client, "/rest/getAlbumList2.view?type=byGenre&genre=Cool%20Jazz"))
            .GetProperty("subsonic-response")
            .GetProperty("albumList2")
            .GetProperty("album");

        var listed = Assert.Single(byGenre.EnumerateArray());
        Assert.Equal("Waltz for Debby", listed.GetProperty("name").GetString());
        Assert.Equal(
            ["Jazz", "Cool Jazz"],
            listed.GetProperty("genres").EnumerateArray().Select(genre => genre.GetProperty("name").GetString()));

        var songs = (await JsonAsync(client, "/rest/getSongsByGenre.view?genre=jazz"))
            .GetProperty("subsonic-response")
            .GetProperty("songsByGenre")
            .GetProperty("song");

        Assert.Single(songs.EnumerateArray());

        // "cool" is inside "cool jazz" as text and is no genre of anything.
        Assert.Empty(SongsByGenre(await JsonAsync(client, "/rest/getSongsByGenre.view?genre=cool")));
        Assert.Single(SongsByGenre(await JsonAsync(client, "/rest/getSongsByGenre.view?genre=rock&offset=1")));

        using var response = await client.GetAsync(
            new Uri($"/rest/getGenres.view?{Credentials}", UriKind.Relative),
            Token);

        var xml = XDocument.Parse(await response.Content.ReadAsStringAsync(Token));
        XNamespace ns = "http://subsonic.org/restapi";

        Assert.Contains(xml.Descendants(ns + "genre"), genre => genre.Value == "Cool Jazz");
        Assert.Equal(
            "Blues, Rock",
            xml.Descendants(ns + "genre").Single(genre => genre.Value == "Blues Rock").Attribute("parents")?.Value);
        Assert.Null(xml.Descendants(ns + "genre").Single(genre => genre.Value == "Rock").Attribute("parents"));
    }

    /// <summary>
    /// A song carries its ReplayGain — the album values over its album folder —
    /// and the bit depth its audio really uses.
    /// </summary>
    [Fact]
    public async Task ASongCarriesItsReplayGainAndItsRealBitDepth()
    {
        await using (var db = PostgresFixture.CreateContext(_connectionString))
        {
            var filed = await db.MediaFiles.SingleAsync(f => f.Id == _filed, Token);
            filed.Quality = filed.Quality! with { BitDepth = 24 };
            filed.Measurement = new AudioMeasurement
            {
                IntegratedLufs = -12,
                SamplePeakDbfs = -1,
                DurationSeconds = 292,
                EffectiveBitDepth = 16,
            };

            await db.SaveChangesAsync(Token);
        }

        using var client = _factory!.CreateClient();

        var song = (await JsonAsync(client, $"/rest/getSong.view?id=tr-{_filed.Value:N}"))
            .GetProperty("subsonic-response")
            .GetProperty("song");

        Assert.Equal(16, song.GetProperty("bitDepth").GetInt32());

        var gain = song.GetProperty("replayGain");
        Assert.Equal(-6, gain.GetProperty("trackGain").GetDouble());
        Assert.Equal(-6, gain.GetProperty("albumGain").GetDouble());
        Assert.Equal(0.891251, gain.GetProperty("trackPeak").GetDouble());
    }

    private static List<JsonElement> SongsByGenre(JsonElement json) =>
        [.. json.GetProperty("subsonic-response").GetProperty("songsByGenre").GetProperty("song").EnumerateArray()];

    private const string Credentials = "u=me&p=" + Password + "&v=1.16.1&c=test";

    /// <summary>The album's Subsonic id, which is its release group's.</summary>
    private async Task<string> AlbumIdAsync()
    {
        await using var db = PostgresFixture.CreateContext(_connectionString);

        var album = db.ReleaseGroups.Single();

        return $"al-{album.Id.Value:N}";
    }

    private static string FolderId(string path) =>
        "fo-" + Base64Url.EncodeToString(Encoding.UTF8.GetBytes(path));

    private async Task<JsonElement> JsonAsync(HttpClient client, string route)
    {
        var separator = route.Contains('?', StringComparison.Ordinal) ? "&" : "?";

        using var response = await client.GetAsync(
            new Uri($"{route}{separator}{Credentials}&f=json", UriKind.Relative),
            Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(Token)).RootElement.Clone();
    }

    /// <summary>
    /// A cover comes no larger than the size asked for, and is scaled once.
    /// </summary>
    /// <remarks>
    /// A stored cover, so neither the archive nor a folder is asked. Removes
    /// what it adds and the copies it caused, because the class shares one
    /// database and the scaler one folder.
    /// </remarks>
    [Fact]
    public async Task ACoverComesNoLargerThanTheSizeAskedForAndIsScaledOnce()
    {
        var made = Path.Combine(_root, "cover-1200x800.jpg");
        Tool("ffmpeg", "-v", "error", "-y", "-f", "lavfi", "-i", "color=c=red:s=1200x800", "-frames:v", "1", made);
        var original = await File.ReadAllBytesAsync(made, Token);
        File.Delete(made);

        var album = new ReleaseGroup { Id = ReleaseGroupId.New(), Title = "Sleeves" };
        var pressing = new Release { Id = ReleaseId.New(), Title = "Sleeves", ReleaseGroupId = album.Id, TrackCount = 1, DiscCount = 1 };
        var recording = new Recording { Id = RecordingId.New(), Title = "Sleeve" };
        var file = new MediaFile
        {
            Id = MediaFileId.New(),
            Path = "Sleeves/01 Sleeve.flac",
            SizeBytes = 1024,
            LastModifiedUtc = DateTimeOffset.Parse("2026-01-03T00:00:00Z", CultureInfo.InvariantCulture),
            RecordingId = recording.Id,
            ReleaseId = pressing.Id,
            ReleaseGroupId = album.Id,
        };
        var cover = new ReleaseCover
        {
            ReleaseId = pressing.Id,
            Bytes = original,
            MediaType = "image/jpeg",
            SavedUtc = DateTimeOffset.Parse("2026-01-03T00:00:00Z", CultureInfo.InvariantCulture),
        };

        await using (var db = PostgresFixture.CreateContext(_connectionString))
        {
            db.ReleaseGroups.Add(album);
            db.Releases.Add(pressing);
            db.Recordings.Add(recording);
            db.MediaFiles.Add(file);
            db.ReleaseCovers.Add(cover);
            await db.SaveChangesAsync(Token);
        }

        var scaler = _factory!.Services.GetRequiredService<Fonoteca.Ingest.FfmpegCoverScaler>();
        byte[]? changed = null;

        try
        {
            using var client = _factory.CreateClient();

            // As it is without a size, and with one larger than the picture.
            Assert.Equal(original, await CoverAsync(client, album.Id, size: null));
            Assert.Equal(original, await CoverAsync(client, album.Id, size: 2000));

            var scaled = Path.Combine(_root, "scaled.jpg");
            await File.WriteAllBytesAsync(scaled, await CoverAsync(client, album.Id, size: 300), Token);
            Assert.Equal(
                "300,200",
                Tool("ffprobe", "-v", "error", "-select_streams", "v:0", "-show_entries", "stream=width,height", "-of", "csv=p=0", scaled).Trim());
            File.Delete(scaled);

            // Size 0 is no size, and a scaled answer carries the unscaled one's ETag.
            Assert.Equal(original, await CoverAsync(client, album.Id, size: 0));
            Assert.Equal(await ETagAsync(client, album.Id, size: null), await ETagAsync(client, album.Id, size: 300));

            // Asked again, the kept copy answers: change it, and the change is what comes back.
            var kept = Path.Combine(scaler.Directory, Fonoteca.Ingest.FfmpegCoverScaler.KeyOf(original, 300) + ".jpg");
            await File.WriteAllBytesAsync(kept, [1, 2, 3], Token);
            var again = await CoverAsync(client, album.Id, size: 300);
            Assert.Equal<byte>([1, 2, 3], again);

            // A changed cover is another picture, so the old copy is not served for it.
            var other = Path.Combine(_root, "cover-800x1200.jpg");
            Tool("ffmpeg", "-v", "error", "-y", "-f", "lavfi", "-i", "color=c=blue:s=800x1200", "-frames:v", "1", other);
            changed = await File.ReadAllBytesAsync(other, Token);
            File.Delete(other);

            await using (var db = PostgresFixture.CreateContext(_connectionString))
            {
                var row = await db.ReleaseCovers.SingleAsync(row => row.ReleaseId == pressing.Id, Token);
                row.Bytes = changed;
                row.SavedUtc = row.SavedUtc.AddDays(1);
                await db.SaveChangesAsync(Token);
            }

            var rescaled = Path.Combine(_root, "rescaled.jpg");
            await File.WriteAllBytesAsync(rescaled, await CoverAsync(client, album.Id, size: 300), Token);
            Assert.Equal(
                "200,300",
                Tool("ffprobe", "-v", "error", "-select_streams", "v:0", "-show_entries", "stream=width,height", "-of", "csv=p=0", rescaled).Trim());
            File.Delete(rescaled);
        }
        finally
        {
            foreach (var picture in changed is null ? new[] { original } : [original, changed])
            {
                foreach (var size in new[] { 300, 2000 })
                {
                    foreach (var kind in new[] { ".jpg", ".as-is" })
                    {
                        File.Delete(Path.Combine(scaler.Directory, Fonoteca.Ingest.FfmpegCoverScaler.KeyOf(picture, size) + kind));
                    }
                }
            }

            await using var db = PostgresFixture.CreateContext(_connectionString);
            db.ReleaseCovers.RemoveRange(await db.ReleaseCovers.Where(row => row.ReleaseId == pressing.Id).ToListAsync(Token));
            db.MediaFiles.RemoveRange(await db.MediaFiles.Where(row => row.ReleaseGroupId == album.Id).ToListAsync(Token));
            db.Releases.Remove(await db.Releases.SingleAsync(row => row.Id == pressing.Id, Token));
            db.ReleaseGroups.Remove(await db.ReleaseGroups.SingleAsync(row => row.Id == album.Id, Token));
            db.Recordings.Remove(await db.Recordings.SingleAsync(row => row.Id == recording.Id, Token));
            await db.SaveChangesAsync(Token);
        }
    }

    private static async Task<byte[]> CoverAsync(HttpClient client, ReleaseGroupId album, int? size)
    {
        var sized = size is { } pixels ? $"&size={pixels}" : string.Empty;

        using var response = await client.GetAsync(
            new Uri($"/rest/getCoverArt.view?{Credentials}&id=al-{album.Value:N}{sized}", UriKind.Relative),
            Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return await response.Content.ReadAsByteArrayAsync(Token);
    }

    private static async Task<string?> ETagAsync(HttpClient client, ReleaseGroupId album, int? size)
    {
        var sized = size is { } pixels ? $"&size={pixels}" : string.Empty;

        using var response = await client.GetAsync(
            new Uri($"/rest/getCoverArt.view?{Credentials}&id=al-{album.Value:N}{sized}", UriKind.Relative),
            Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return response.Headers.ETag?.Tag;
    }

    /// <summary>
    /// An album's motion artwork comes as an animated picture no larger than the
    /// size asked for, converted once; an album without one answers as a
    /// missing cover does.
    /// </summary>
    /// <remarks>
    /// A stand-in video, 320 px square, twenty frames. Removes what it adds and
    /// the copies it caused, as the cover test does.
    /// </remarks>
    [Fact]
    public async Task AnAlbumsMotionComesAnimatedNoLargerThanAskedAndIsConvertedOnce()
    {
        var made = Path.Combine(_root, "motion.mp4");
        Tool("ffmpeg", "-v", "error", "-y", "-f", "lavfi", "-i", "testsrc=size=320x320:rate=10:duration=2", "-c:v", "libx264", "-pix_fmt", "yuv420p", made);
        var video = await File.ReadAllBytesAsync(made, Token);
        File.Delete(made);

        var moving = new ReleaseGroup { Id = ReleaseGroupId.New(), Title = "Moving" };
        var still = new ReleaseGroup { Id = ReleaseGroupId.New(), Title = "Still" };
        var saved = DateTimeOffset.Parse("2026-01-04T00:00:00Z", CultureInfo.InvariantCulture);

        await using (var db = PostgresFixture.CreateContext(_connectionString))
        {
            db.ReleaseGroups.AddRange(moving, still);
            db.AlbumMotions.Add(new AlbumMotion { ReleaseGroupId = moving.Id, Square = video, SavedUtc = saved });

            // A stamp: the shop was asked and had none.
            db.AlbumMotions.Add(new AlbumMotion { ReleaseGroupId = still.Id, SavedUtc = saved });
            await db.SaveChangesAsync(Token);
        }

        var scaler = _factory!.Services.GetRequiredService<Fonoteca.Ingest.FfmpegCoverScaler>();

        try
        {
            using var client = _factory.CreateClient();

            var small = await MotionAsync(client, moving.Id, size: 100);
            Assert.Equal("image/webp", small.MediaType);
            Assert.Equal($"\"{saved.UtcTicks}\"", small.ETag);
            Assert.Equal((100, 100, 20, 0), Animation(small.Bytes));

            // Never larger than the video, with a size or without.
            Assert.Equal((320, 320, 20, 0), Animation((await MotionAsync(client, moving.Id, size: 1000)).Bytes));
            Assert.Equal((320, 320, 20, 0), Animation((await MotionAsync(client, moving.Id, size: null)).Bytes));

            // Asked again, the kept copy answers: change it, and the change is what comes back.
            var kept = Path.Combine(scaler.Directory, Fonoteca.Ingest.FfmpegCoverScaler.KeyOf(video, 100) + ".webp");
            await File.WriteAllBytesAsync(kept, [1, 2, 3], Token);
            var again = await MotionAsync(client, moving.Id, size: 100);
            Assert.Equal<byte>([1, 2, 3], again.Bytes);

            foreach (var none in new[] { $"al-{still.Id.Value:N}", $"al-{ReleaseGroupId.New().Value:N}", "nonsense" })
            {
                var answer = (await JsonAsync(client, $"/rest/getAlbumMotion.view?id={none}&size=100"))
                    .GetProperty("subsonic-response");

                Assert.Equal("failed", answer.GetProperty("status").GetString());
                Assert.Equal(70, answer.GetProperty("error").GetProperty("code").GetInt32());
            }
        }
        finally
        {
            foreach (var size in new[] { 0, 100, 1000 })
            {
                File.Delete(Path.Combine(scaler.Directory, Fonoteca.Ingest.FfmpegCoverScaler.KeyOf(video, size) + ".webp"));
            }

            await using var db = PostgresFixture.CreateContext(_connectionString);
            db.AlbumMotions.RemoveRange(await db.AlbumMotions.Where(row => row.ReleaseGroupId == moving.Id || row.ReleaseGroupId == still.Id).ToListAsync(Token));
            db.ReleaseGroups.RemoveRange(await db.ReleaseGroups.Where(row => row.Id == moving.Id || row.Id == still.Id).ToListAsync(Token));
            await db.SaveChangesAsync(Token);
        }
    }

    private static async Task<(byte[] Bytes, string? MediaType, string? ETag)> MotionAsync(
        HttpClient client,
        ReleaseGroupId album,
        int? size)
    {
        var sized = size is { } pixels ? $"&size={pixels}" : string.Empty;

        using var response = await client.GetAsync(
            new Uri($"/rest/getAlbumMotion.view?{Credentials}&id=al-{album.Value:N}{sized}", UriKind.Relative),
            Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return (
            await response.Content.ReadAsByteArrayAsync(Token),
            response.Content.Headers.ContentType?.MediaType,
            response.Headers.ETag?.Tag);
    }

    /// <summary>An animated WebP's canvas, frames and loop count, read from its chunks.</summary>
    private static (int Width, int Height, int Frames, int Loops) Animation(byte[] webp)
    {
        Assert.Equal("RIFFWEBPVP8X", Encoding.ASCII.GetString(webp, 0, 4) + Encoding.ASCII.GetString(webp, 8, 8));

        int width = 1 + (webp[24] | webp[25] << 8 | webp[26] << 16), height = 1 + (webp[27] | webp[28] << 8 | webp[29] << 16);
        int frames = 0, loops = -1;

        for (var at = 12; at + 8 <= webp.Length;)
        {
            var size = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(webp.AsSpan(at + 4));

            switch (Encoding.ASCII.GetString(webp, at, 4))
            {
                case "ANIM":
                    loops = webp[at + 12] | webp[at + 13] << 8;
                    break;
                case "ANMF":
                    frames++;
                    break;
            }

            at += 8 + size + (size & 1);
        }

        return (width, height, frames, loops);
    }

    /// <summary>Runs ffmpeg or ffprobe and returns what it printed.</summary>
    private static string Tool(string name, params string[] arguments)
    {
        using var tool = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(name, arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        })!;

        var output = tool.StandardOutput.ReadToEnd();
        var error = tool.StandardError.ReadToEnd();
        tool.WaitForExit();

        Assert.True(tool.ExitCode == 0, $"{name} failed: {error}");

        return output;
    }

    private WebApplicationFactory<Program> Factory(string? connectionString = null) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:Fonoteca", connectionString ?? _connectionString);
            builder.UseSetting("Fonoteca:LibraryPath", _root);
            builder.UseSetting("Fonoteca:WarmCandidates", "false");
            builder.UseSetting("Fonoteca:MusicBrainzContact", string.Empty);
        });

    private void WriteFile(string relativePath, string content)
    {
        var absolute = Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(absolute)!);
        File.WriteAllText(absolute, content);
    }
}
