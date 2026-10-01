using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using Fonoteca.Api.Acquisition;
using Fonoteca.Api.Endpoints;
using Fonoteca.Domain.Acquisition;
using Fonoteca.Domain.Catalogue;
using Fonoteca.Fixtures;
using Fonoteca.Providers.Qobuz;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Fonoteca.Integration.Tests;

/// <summary>
/// The Acquire page's two buttons end to end: a stubbed Qobuz, a real disk and
/// a real catalogue.
/// </summary>
/// <remarks>
/// The failure these exist for: Qobuz names an album <c>Artist/Album</c>, the
/// album it upgrades or completes usually sits in exactly that folder, and the
/// download was refused as a merge of two editions before a track was fetched.
/// </remarks>
[Collection(nameof(PostgresCollection))]
public sealed class QobuzAcquireEndpointTests(PostgresFixture postgres) : IAsyncLifetime
{
    private const string Album = """
        {"id":"1","title":"Rumours","artist":{"name":"Fleetwood Mac"},
         "tracks_count":2,"media_count":1,"maximum_bit_depth":16,"maximum_sampling_rate":44.1,
         "tracks":{"items":[
           {"id":55,"title":"The Chain","track_number":1,"media_number":1,"streamable":true},
           {"id":56,"title":"Dreams","track_number":2,"media_number":1,"streamable":true}]}}
        """;

    private const string FileUrl =
        """{"url":"https://cdn.qobuz.example/a.flac","format_id":6,"mime_type":"audio/flac"}""";

    private string _connectionString = string.Empty;
    private string _root = string.Empty;
    private WebApplicationFactory<Program>? _factory;
    private StubHttpHandler? _qobuz;

    /// <summary>Set to have Qobuz refuse the second track's file, as an expired token would.</summary>
    private bool _refuseSecondTrack;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _connectionString = await postgres.CreateDatabaseAsync(Token);
        _root = Directory.CreateTempSubdirectory("fonoteca-acquire-").FullName;

        var flac = await File.ReadAllBytesAsync(Corpus.Flac, Token);

        _qobuz = new StubHttpHandler(request => request.Uri.Host.StartsWith("cdn", StringComparison.Ordinal)
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(flac) }
            : _refuseSecondTrack && request.Uri.Query.Contains("track_id=56", StringComparison.Ordinal)
                ? StubHttpHandler.Json(HttpStatusCode.Unauthorized, """{"status":"error","code":401,"message":"User authentication is required."}""")
                : StubHttpHandler.Json(
                    HttpStatusCode.OK,
                    request.Uri.AbsolutePath.Contains("getFileUrl", StringComparison.Ordinal) ? FileUrl : Album));

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:Fonoteca", _connectionString);
            builder.UseSetting("Fonoteca:LibraryPath", _root);
            builder.UseSetting("Fonoteca:ReplacedPath", _root + "-replaced");
            builder.UseSetting("Fonoteca:AllowFileReplacement", "true");
            builder.UseSetting("Fonoteca:WarmCandidates", "false");
            builder.UseSetting("Fonoteca:IdentifyAfterScan", "false");
            builder.UseSetting("Fonoteca:MusicBrainzContact", string.Empty);
            builder.UseSetting("Fonoteca:Download:TrackDelayMs", "0");
            builder.UseSetting("Fonoteca:Providers:Qobuz:AppId", "app");
            builder.UseSetting("Fonoteca:Providers:Qobuz:AppSecret", "secret");
            builder.UseSetting("Fonoteca:Providers:Qobuz:UserAuthToken", "token");
            builder.UseSetting("Fonoteca:Providers:Qobuz:MinRequestIntervalMs", "0");

            builder.ConfigureTestServices(services =>
            {
                services.AddHttpClient(QobuzOptions.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => _qobuz);
                services.AddHttpClient(QobuzClient.ContentHttpClientName).ConfigurePrimaryHttpMessageHandler(() => _qobuz);
            });
        });

        using var warm = _factory.CreateClient();
    }

    public async ValueTask DisposeAsync()
    {
        if (_factory is not null) await _factory.DisposeAsync();

        foreach (var directory in new[] { _root, _root + "-replaced" })
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task AnUpgradeLandingInTheFolderItReplacesTakesItsPlace()
    {
        Assert.SkipUnless(Corpus.IsAvailable, "ffmpeg is not on PATH; source ~/.local/opt/env.sh.");

        await HeldAsync(Mp3(), "Fleetwood Mac/Rumours/01 - The Chain.mp3", "Fleetwood Mac/Rumours/02 - Dreams.mp3");
        await FileUnderRumoursAsync();

        using var client = _factory!.CreateClient();
        using var response = await client.PostAsJsonAsync(
            new Uri("/api/qobuz/albums/1/upgrade", UriKind.Relative),
            new UpgradeRequest("Fleetwood Mac/Rumours", 2),
            Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<AlbumUpgrade>(Token);
        Assert.Equal(ReplacementVerdict.Replace, body!.Replacement!.Verdict);
        Assert.Equal(2, body.Replacement.Archived);

        var folder = Path.Combine(_root, "Fleetwood Mac", "Rumours");
        Assert.Equal(
            ["01 The Chain.flac", "02 Dreams.flac"],
            Directory.GetFiles(folder).Select(Path.GetFileName).Order(StringComparer.Ordinal));
        Assert.False(Directory.Exists(Path.Combine(_root, ".fonoteca-downloads")));
    }

    [Fact]
    public async Task APlainDownloadOfAnAlbumHeldInPartCompletesIt()
    {
        Assert.SkipUnless(Corpus.IsAvailable, "ffmpeg is not on PATH; source ~/.local/opt/env.sh.");

        // One of two tracks, in the quality Qobuz sells: the Incomplete list's
        // case. Filed under no album, so a person confirms it is this one.
        await HeldAsync(Cd(), "Fleetwood Mac/Rumours/01 - The Chain.flac");

        using var client = _factory!.CreateClient();
        using var asked = await client.PostAsync(
            new Uri("/api/qobuz/albums/1/download", UriKind.Relative), null, Token);

        var question = await asked.Content.ReadFromJsonAsync<AlbumUpgrade>(Token);
        Assert.Equal(ReplacementVerdict.Unconfirmed, question!.Replacement!.Verdict);
        Assert.DoesNotContain(_qobuz!.Requests, request => request.Uri.Host.StartsWith("cdn", StringComparison.Ordinal));

        using var response = await client.PostAsync(
            new Uri("/api/qobuz/albums/1/download?confirmed=true", UriKind.Relative), null, Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<AlbumUpgrade>(Token);
        Assert.Equal(ReplacementVerdict.Replace, body!.Replacement!.Verdict);
        Assert.Equal(
            ["01 The Chain.flac", "02 Dreams.flac"],
            Directory.GetFiles(Path.Combine(_root, "Fleetwood Mac", "Rumours"))
                .Select(Path.GetFileName)
                .Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task AnOfferOfFewerTracksThanTheAlbumHoldsKeepsItAndFetchesNothing()
    {
        await HeldAsync(
            Mp3(),
            "Fleetwood Mac/Rumours/01 - The Chain.mp3",
            "Fleetwood Mac/Rumours/02 - Dreams.mp3",
            "Fleetwood Mac/Rumours/12 - Silver Springs.mp3");
        await FileUnderRumoursAsync();

        using var client = _factory!.CreateClient();
        using var response = await client.PostAsync(
            new Uri("/api/qobuz/albums/1/download", UriKind.Relative), null, Token);

        var body = await response.Content.ReadFromJsonAsync<AlbumUpgrade>(Token);

        Assert.Null(body!.Download);
        Assert.Equal(ReplacementVerdict.Incomplete, body.Replacement!.Verdict);
        Assert.True(File.Exists(Path.Combine(_root, "Fleetwood Mac", "Rumours", "12 - Silver Springs.mp3")));
    }

    [Fact]
    public async Task AnInterruptedDownloadIsResumedRatherThanReplacingItself()
    {
        Assert.SkipUnless(Corpus.IsAvailable, "ffmpeg is not on PATH; source ~/.local/opt/env.sh.");

        // The first track of a plain download, catalogued by a scan since.
        await HeldAsync(Cd(), "Fleetwood Mac/Rumours/01 The Chain.flac");

        using var client = _factory!.CreateClient();
        using var response = await client.PostAsync(
            new Uri("/api/qobuz/albums/1/download", UriKind.Relative), null, Token);

        var body = await response.Content.ReadFromJsonAsync<AlbumUpgrade>(Token);

        Assert.Null(body!.Replacement);
        Assert.Equal(1, body.Download!.Downloaded);
        Assert.False(Directory.Exists(_root + "-replaced"));
        Assert.Equal(
            ["01 The Chain.flac", "02 Dreams.flac"],
            Directory.GetFiles(Path.Combine(_root, "Fleetwood Mac", "Rumours"))
                .Select(Path.GetFileName)
                .Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task AReplacementQobuzRefusesPartWayLeavesNothingHidden()
    {
        Assert.SkipUnless(Corpus.IsAvailable, "ffmpeg is not on PATH; source ~/.local/opt/env.sh.");

        await HeldAsync(Mp3(), "Fleetwood Mac/Rumours/01 - The Chain.mp3", "Fleetwood Mac/Rumours/02 - Dreams.mp3");
        await FileUnderRumoursAsync();
        _refuseSecondTrack = true;

        using var client = _factory!.CreateClient();
        using var response = await client.PostAsync(
            new Uri("/api/qobuz/albums/1/download", UriKind.Relative), null, Token);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.False(Directory.Exists(Path.Combine(_root, ".fonoteca-downloads")));
        Assert.Equal(2, Directory.GetFiles(Path.Combine(_root, "Fleetwood Mac", "Rumours")).Length);
    }

    [Fact]
    public async Task AnAlbumHeldInTwoFoldersIsAPersonsChoiceAndFetchesNothing()
    {
        await HeldAsync(Mp3(), "Fleetwood Mac/Rumours/01 - The Chain.mp3", "Fleetwood Mac/Rumours (1977)/01 - The Chain.mp3");
        await FileUnderRumoursAsync();

        using var client = _factory!.CreateClient();
        using var response = await client.PostAsync(
            new Uri("/api/qobuz/albums/1/download", UriKind.Relative), null, Token);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.DoesNotContain(_qobuz!.Requests, request => request.Uri.Host.StartsWith("cdn", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AnAlbumOfTheReleaseGroupReplacesWhateverItsTracksAreCalled()
    {
        Assert.SkipUnless(Corpus.IsAvailable, "ffmpeg is not on PATH; source ~/.local/opt/env.sh.");

        // Filed under Rumours, titled nothing like the shop's tracks: the
        // album is the release group, and the offer is better.
        await HeldAsync(Mp3(), "Fleetwood Mac/Rumours (1977)/01 - The Chain (Album Version).mp3", "Fleetwood Mac/Rumours (1977)/02 - Songbird.mp3");
        await FileUnderRumoursAsync();

        using var client = _factory!.CreateClient();
        using var response = await client.PostAsync(
            new Uri("/api/qobuz/albums/1/download", UriKind.Relative), null, Token);

        var body = await response.Content.ReadFromJsonAsync<AlbumUpgrade>(Token);

        Assert.Equal(ReplacementVerdict.Replace, body!.Replacement!.Verdict);
        Assert.Equal(2, body.Replacement.Archived);
        Assert.Equal(
            ["01 The Chain.flac", "02 Dreams.flac"],
            Directory.GetFiles(Path.Combine(_root, "Fleetwood Mac", "Rumours (1977)"))
                .Select(Path.GetFileName)
                .Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task ADownloadSaidToBeSeparateLandsBesideTheAlbumHeld()
    {
        Assert.SkipUnless(Corpus.IsAvailable, "ffmpeg is not on PATH; source ~/.local/opt/env.sh.");

        await HeldAsync(Mp3(), "Fleetwood Mac/Rumours (1977)/01 - The Chain.mp3", "Fleetwood Mac/Rumours (1977)/02 - Dreams.mp3");
        await FileUnderRumoursAsync();

        using var client = _factory!.CreateClient();
        using var response = await client.PostAsync(
            new Uri("/api/qobuz/albums/1/download?separate=true", UriKind.Relative), null, Token);

        var body = await response.Content.ReadFromJsonAsync<AlbumUpgrade>(Token);

        Assert.Null(body!.Replacement);
        Assert.Equal("Fleetwood Mac/Rumours", body.Download!.Folder);
        Assert.Equal(2, Directory.GetFiles(Path.Combine(_root, "Fleetwood Mac", "Rumours (1977)")).Length);
    }

    [Fact]
    public async Task AnAlbumOfTheSameTitleBilledOtherwiseIsAskedAboutAndMayBeDownloadedBeside()
    {
        Assert.SkipUnless(Corpus.IsAvailable, "ffmpeg is not on PATH; source ~/.local/opt/env.sh.");

        // Somebody else's Rumours, in CD with three tracks: Qobuz's is no
        // upgrade of it, and may be another album entirely.
        await HeldAsync(
            Cd(),
            "Someone/Rumours (1977)/01 - Intro.flac",
            "Someone/Rumours (1977)/02 - Middle.flac",
            "Someone/Rumours (1977)/03 - End.flac");
        await FileUnderRumoursAsync(artist: "Someone Else");

        using var client = _factory!.CreateClient();
        using var asked = await client.PostAsync(
            new Uri("/api/qobuz/albums/1/download", UriKind.Relative), null, Token);

        var question = await asked.Content.ReadFromJsonAsync<AlbumUpgrade>(Token);
        Assert.Equal(ReplacementVerdict.SameTitle, question!.Replacement!.Verdict);

        using var response = await client.PostAsync(
            new Uri("/api/qobuz/albums/1/download?separate=true", UriKind.Relative), null, Token);

        var body = await response.Content.ReadFromJsonAsync<AlbumUpgrade>(Token);
        Assert.Equal("Fleetwood Mac/Rumours", body!.Download!.Folder);
        Assert.Equal(3, Directory.GetFiles(Path.Combine(_root, "Someone", "Rumours (1977)")).Length);
    }

    /// <summary>Files every held row under a Rumours billed to the artist given, as attribution would.</summary>
    private async Task FileUnderRumoursAsync(string artist = "Fleetwood Mac")
    {
        await using var db = PostgresFixture.CreateContext(_connectionString);

        var billed = new Artist { Id = ArtistId.New(), Name = artist };
        var group = new ReleaseGroup { Id = ReleaseGroupId.New(), Title = "Rumours", FirstReleaseYear = 1977 };

        db.Artists.Add(billed);
        db.ReleaseGroups.Add(group);
        db.ArtistCredits.Add(new ArtistCredit
        {
            Id = Guid.CreateVersion7(),
            ArtistId = billed.Id,
            ReleaseGroupId = group.Id,
            Position = 0,
        });

        foreach (var row in await db.MediaFiles.ToListAsync(Token)) row.ReleaseGroupId = group.Id;

        await db.SaveChangesAsync(Token);
    }

    [Fact]
    public async Task AnOfferNoBetterThanTheAlbumHeldFetchesNothing()
    {
        await HeldAsync(Cd(), "Fleetwood Mac/Rumours/01 - The Chain.flac", "Fleetwood Mac/Rumours/02 - Dreams.flac");
        await FileUnderRumoursAsync();

        // Kept from an earlier attempt to resume, and no longer wanted.
        var kept = Path.Combine(_root, QobuzDownloadService.ReplacementArea, "1");
        Directory.CreateDirectory(kept);
        await File.WriteAllTextAsync(Path.Combine(kept, "01 The Chain.flac.part"), "half", Token);

        using var client = _factory!.CreateClient();
        using var response = await client.PostAsync(
            new Uri("/api/qobuz/albums/1/download", UriKind.Relative), null, Token);

        var body = await response.Content.ReadFromJsonAsync<AlbumUpgrade>(Token);

        Assert.Null(body!.Download);
        Assert.Equal(ReplacementVerdict.NotBetter, body.Replacement!.Verdict);
        Assert.DoesNotContain(_qobuz!.Requests, request => request.Uri.Host.StartsWith("cdn", StringComparison.Ordinal));
        Assert.False(Directory.Exists(kept));
    }

    /// <summary>
    /// Files on disk with rows in the catalogue, measured as the probe pass
    /// would and identified by the title after the name's number and dash.
    /// </summary>
    private async Task HeldAsync(AudioQuality quality, params string[] paths)
    {
        await using var db = PostgresFixture.CreateContext(_connectionString);

        foreach (var path in paths)
        {
            var absolute = Path.Combine(_root, path);
            Directory.CreateDirectory(Path.GetDirectoryName(absolute)!);
            File.Copy(path.EndsWith(".mp3", StringComparison.Ordinal) ? Corpus.Mp3 : Corpus.Flac, absolute);

            var recording = new Recording
            {
                Id = RecordingId.New(),
                Title = System.Text.RegularExpressions.Regex.Replace(
                    Path.GetFileNameWithoutExtension(path), @"^\d+\s*-?\s*", string.Empty),
            };
            db.Recordings.Add(recording);

            db.MediaFiles.Add(new MediaFile
            {
                Id = MediaFileId.New(),
                Path = path,
                SizeBytes = new FileInfo(absolute).Length,
                LastModifiedUtc = DateTimeOffset.Parse("2026-01-01T00:00:00Z", CultureInfo.InvariantCulture),
                Quality = quality,
                RecordingId = recording.Id,
            });
        }

        await db.SaveChangesAsync(Token);
    }

    private static AudioQuality Mp3() => new()
    {
        Codec = "mp3",
        SampleRateHz = 44_100,
        Channels = 2,
        BitrateBps = 320_000,
        IsLossless = false,
    };

    private static AudioQuality Cd() => new()
    {
        Codec = "flac",
        SampleRateHz = 44_100,
        Channels = 2,
        BitDepth = 16,
        BitrateBps = 900_000,
        IsLossless = true,
    };
}
