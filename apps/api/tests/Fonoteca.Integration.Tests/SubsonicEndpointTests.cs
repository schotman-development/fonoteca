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

    private const string Credentials = "u=me&p=" + Password + "&v=1.16.1&c=test";

    private async Task<string> AlbumIdAsync()
    {
        await using var db = PostgresFixture.CreateContext(_connectionString);

        var release = db.Releases.Single();

        return $"al-{release.Id.Value:N}";
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
