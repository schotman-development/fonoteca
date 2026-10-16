using System.Buffers.Text;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Fonoteca.Domain.Abstractions;
using Fonoteca.Domain.Catalogue;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
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
    private const string Password = "a-test-password";

    private string _connectionString = string.Empty;
    private string _root = string.Empty;
    private WebApplicationFactory<Program>? _factory;
    private MediaFileId _filed;
    private MediaFileId _scanned;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _connectionString = await postgres.CreateDatabaseAsync(Token);
        _root = Directory.CreateTempSubdirectory("fonoteca-subsonic-").FullName;

        WriteFile("Bill Evans/Waltz for Debby/01 My Foolish Heart.flac", new string('x', 4096));
        WriteFile("Somebody/A Bootleg/01 Untitled.flac", new string('y', 512));
        WriteFile("Somebody/Scanned Not Filed/01 Nameless.flac", new string('z', 256));

        _factory = Factory(Password);

        using (var warm = _factory.CreateClient())
        {
            using var boot = await warm.GetAsync(new Uri("/health", UriKind.Relative), Token);
        }

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
    /// Without a password the surface does not exist, rather than refusing.
    /// </summary>
    /// <remarks>
    /// The same posture <c>/mcp</c> takes, and for the same reason: a fresh
    /// install should offer nothing to its network, and a 401 would still be an
    /// announcement that there is a library here to log into.
    /// </remarks>
    [Fact]
    public async Task WithNoPasswordConfiguredTheEndpointIsNotThere()
    {
        await using var off = Factory(string.Empty);
        using var client = off.CreateClient();

        using var response = await client.GetAsync(
            new Uri("/rest/ping.view?u=me&p=anything&v=1.16.1&c=test", UriKind.Relative),
            Token);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
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

        Assert.Equal(["songLyrics", "fonotecaAlbumMotion"], extensions);
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
            var rock = new ReleaseGroup { Id = ReleaseGroupId.New(), Title = "Loud", Genres = "rock" };
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

    private WebApplicationFactory<Program> Factory(string password) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:Fonoteca", _connectionString);
            builder.UseSetting("Fonoteca:LibraryPath", _root);
            builder.UseSetting("Fonoteca:WarmCandidates", "false");
            builder.UseSetting("Fonoteca:MusicBrainzContact", string.Empty);
            builder.UseSetting("Fonoteca:SubsonicPassword", password);
        });

    private void WriteFile(string relativePath, string content)
    {
        var absolute = Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(absolute)!);
        File.WriteAllText(absolute, content);
    }
}
