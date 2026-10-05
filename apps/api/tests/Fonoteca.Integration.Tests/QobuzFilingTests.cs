using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using Fonoteca.Api.Acquisition;
using Fonoteca.Api.Endpoints;
using Fonoteca.Api.Library;
using Fonoteca.Domain.Abstractions;
using Fonoteca.Domain.Acquisition;
using Fonoteca.Domain.Catalogue;
using Fonoteca.Fixtures;
using Fonoteca.Providers.Qobuz;
using Fonoteca.Tagging;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Fonoteca.Integration.Tests;

/// <summary>
/// A download filed into the catalogue as the shop described it, and its tags
/// written (ADR 0011): a stubbed Qobuz, a real disk and a real catalogue.
/// </summary>
[Collection(nameof(PostgresCollection))]
public sealed partial class QobuzFilingTests(PostgresFixture postgres) : IAsyncLifetime
{
    private const string Album = """
        {"id":"1","title":"Rumours","artist":{"name":"Fleetwood Mac"},"upc":"0603497941032",
         "label":{"name":"Rhino/Warner Records"},"release_date_original":"1977-02-04",
         "tracks_count":2,"media_count":1,"maximum_bit_depth":16,"maximum_sampling_rate":44.1,
         "tracks":{"items":[
           {"id":55,"title":"The Chain","track_number":1,"media_number":1,"streamable":true,"duration":270,
            "isrc":"USWB10101361","performer":{"name":"Fleetwood Mac"},
            "performers":"Fleetwood Mac, MainArtist - Ken Caillat, Producer - Stevie Nicks, Vocals, Writer"},
           {"id":56,"title":"Dreams","track_number":2,"media_number":1,"streamable":true,"duration":257,
            "isrc":"USWB10101367","performer":{"name":"Fleetwood Mac"},
            "performers":"Fleetwood Mac, MainArtist - Stevie Nicks, Vocals, Writer"}]}}
        """;

    private const string Tusk = """
        {"id":"2","title":"Tusk","artist":{"name":"Fleetwood Mac"},"upc":"0081227946320",
         "release_date_original":"1979-10-12","tracks_count":1,"media_count":1,
         "tracks":{"items":[
           {"id":57,"title":"Over & Over","track_number":1,"media_number":1,"streamable":true,"duration":275,
            "isrc":"USWB19900001","performer":{"name":"Fleetwood Mac"},"performers":"Fleetwood Mac, MainArtist"}]}}
        """;

    /// <summary>An album whose band's name has a comma no rule can tell from a role's, listed whole by the shop.</summary>
    private const string Rosetta = """
        {"id":"3","title":"Into Your Lungs","artist":{"name":"Hey, Rosetta!"},"upc":"0623339900122",
         "artists":[{"id":1,"name":"Hey, Rosetta!","roles":["main-artist"]}],
         "release_date_original":"2008-01-22","tracks_count":1,"media_count":1,
         "tracks":{"items":[
           {"id":58,"title":"Red Heart","track_number":1,"media_number":1,"streamable":true,"duration":245,
            "isrc":"CA0000800001","performer":{"name":"Tim Baker"},"performers":"Hey, Rosetta!, MainArtist - Tim Baker, Composer"}]}}
        """;

    private const string FileUrl =
        """{"url":"https://cdn.qobuz.example/a.flac","format_id":6,"mime_type":"audio/flac"}""";

    private string _connectionString = string.Empty;
    private string _root = string.Empty;
    private StubHttpHandler? _qobuz;

    /// <summary>Set to have Qobuz fail the second track's file, as an outage would.</summary>
    private bool _failSecondTrack;
    private readonly List<WebApplicationFactory<Program>> _factories = [];

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _connectionString = await postgres.CreateDatabaseAsync(Token);
        _root = Directory.CreateTempSubdirectory("fonoteca-filing-").FullName;

        var flac = await File.ReadAllBytesAsync(Corpus.Flac, Token);

        _qobuz = new StubHttpHandler(request => request.Uri.Host.StartsWith("cdn", StringComparison.Ordinal)
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(flac) }
            : _failSecondTrack && request.Uri.Query.Contains("track_id=56", StringComparison.Ordinal)
                ? StubHttpHandler.Json(HttpStatusCode.ServiceUnavailable, """{"status":"error","code":503,"message":"Try later."}""")
            : StubHttpHandler.Json(
                HttpStatusCode.OK,
                request.Uri.AbsolutePath.Contains("getFileUrl", StringComparison.Ordinal) ? FileUrl
                    : request.Uri.Query.Contains("album_id=2", StringComparison.Ordinal) ? Tusk
                    : request.Uri.Query.Contains("album_id=3", StringComparison.Ordinal) ? Rosetta
                    : Album));
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var factory in _factories) await factory.DisposeAsync();

        foreach (var directory in new[] { _root, _root + "-replaced" })
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ADownloadArrivesFiledUnderTheShopsAlbumWithItsCredits()
    {
        Assert.SkipUnless(Corpus.IsAvailable, "ffmpeg is not on PATH; source ~/.local/opt/env.sh.");

        var factory = Factory(mutation: false);
        var body = await DownloadAsync(factory);

        Assert.Equal(2, body.Filing!.Filed);
        Assert.Equal("File writing is off, so the tags were not written.", body.Filing.Why);

        await using var db = PostgresFixture.CreateContext(_connectionString);

        var release = await db.Releases.Include(release => release.ReleaseGroup).SingleAsync(Token);
        Assert.Null(release.Mbid);
        Assert.Equal("0603497941032", release.Barcode);
        Assert.Equal("Rhino/Warner Records", release.Label);
        Assert.Equal((1977, 2, 4), (release.ReleasedYear, release.ReleasedMonth, release.ReleasedDay));
        Assert.Equal(body.Filing.AlbumId, release.ReleaseGroupId!.Value.Value);

        var files = await db.MediaFiles.Include(file => file.Recording).Include(file => file.Track).OrderBy(file => file.Path).ToListAsync(Token);
        Assert.Equal(["Fleetwood Mac/Rumours/01 The Chain.flac", "Fleetwood Mac/Rumours/02 Dreams.flac"], files.Select(file => file.Path));

        Assert.All(files, file =>
        {
            Assert.Equal(EnrichmentOutcome.LinkedByProvider, file.EnrichmentOutcome);
            Assert.Equal(ReleaseAttributionOutcome.AttributedByProvider, file.AttributionOutcome);
            Assert.NotNull(file.IdentityDecidedUtc);
            Assert.NotNull(file.ReleaseDecidedUtc);

            // The shop said nothing about the audio, and nobody asked anyone.
            Assert.Null(file.AcoustIdDecidedUtc);
            Assert.Null(file.AcoustIdCheckedUtc);
            Assert.Null(file.RecordingLookupUtc);
            Assert.Null(file.ReleaseLookupUtc);

            var disk = new FileInfo(Path.Combine(_root, file.Path));
            Assert.Equal(disk.Length, file.SizeBytes);
            Assert.Equal(StoreTime.ToStorePrecision(new DateTimeOffset(disk.LastWriteTimeUtc)), file.LastModifiedUtc);
        });

        Assert.Equal("USWB10101361", files[0].Recording!.Isrc);
        Assert.Equal((1, 1), (files[0].Track!.DiscNumber, files[0].Track!.Position));
        Assert.Equal(2, files[1].Track!.Position);

        var billed = await db.ArtistCredits.Include(credit => credit.Artist).Where(credit => credit.RecordingId != null).ToListAsync(Token);
        Assert.Equal(2, billed.Count);
        Assert.All(billed, credit => Assert.Equal("Fleetwood Mac", credit.Artist!.Name));
        Assert.Single(await db.ArtistCredits.Where(credit => credit.ReleaseId == release.Id).ToListAsync(Token));

        // The writer, minted once for the album; the producer is not kept.
        var writers = await db.Relationships.Where(link => link.Type == "composer").ToListAsync(Token);
        Assert.Equal(2, writers.Count);
        Assert.Single(writers.Select(link => link.ArtistId).Distinct());
        Assert.Equal(["Fleetwood Mac", "Stevie Nicks"], await db.Artists.Select(artist => artist.Name).OrderBy(name => name).ToListAsync(Token));

        Assert.Single(await db.DomainEvents.Where(entry => entry.Type == DownloadFiling.FiledEvent).ToListAsync(Token));

        // Fingerprinted still; enriched and attributed by nobody.
        using var scope = factory.Services.CreateScope();
        Assert.Equal(2, await scope.ServiceProvider.GetRequiredService<IdentificationService>().CountPendingAsync(Token));
        Assert.Equal(0, (await scope.ServiceProvider.GetRequiredService<EnrichmentService>().CountPendingAsync(Token)).Files);
        Assert.Equal(0, await scope.ServiceProvider.GetRequiredService<ReleaseAttributionService>().CountPendingAsync(Token));
    }

    [Fact]
    public async Task ANameTheShopListsWholeIsCreditedWhole()
    {
        Assert.SkipUnless(Corpus.IsAvailable, "ffmpeg is not on PATH; source ~/.local/opt/env.sh.");

        await DownloadAsync(Factory(mutation: false), album: "3");

        await using var db = PostgresFixture.CreateContext(_connectionString);
        var billed = await db.ArtistCredits.Include(credit => credit.Artist).Where(credit => credit.RecordingId != null).SingleAsync(Token);

        Assert.Equal("Hey, Rosetta!", billed.Artist!.Name);
        Assert.DoesNotContain("Hey", await db.Artists.Select(artist => artist.Name).ToListAsync(Token));
    }

    /// <summary>
    /// Undo treats a download's tag write as any other: the tags and the name
    /// go back, and the folder is reopened, the shop's filing with it (the
    /// owner's choice).
    /// </summary>
    [Fact]
    public async Task UndoingADownloadsTagWriteReopensItsFolder()
    {
        Assert.SkipUnless(Corpus.IsAvailable, "ffmpeg is not on PATH; source ~/.local/opt/env.sh.");

        var factory = Factory(mutation: true);
        var body = await DownloadAsync(factory);
        Assert.Equal("Fleetwood Mac/Rumours (1977)", body.Filing!.Folder);

        using var client = factory.CreateClient();
        using var response = await client.PostAsJsonAsync(
            new Uri("/api/catalogue/folders/undo", UriKind.Relative), new FolderUndoRequest("Fleetwood Mac/Rumours (1977)"), Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(TagUndoStatus.Undone, (await response.Content.ReadFromJsonAsync<TagUndoResult>(Token))!.Status);

        Assert.True(File.Exists(Path.Combine(_root, "Fleetwood Mac", "Rumours", "01 The Chain.flac")));

        await using var db = PostgresFixture.CreateContext(_connectionString);
        Assert.All(await db.MediaFiles.ToListAsync(Token), file =>
        {
            Assert.Null(file.ReleaseId);
            Assert.Null(file.RecordingId);
            Assert.Equal(ReleaseAttributionOutcome.NotAttempted, file.AttributionOutcome);
        });
    }

    [Fact]
    public async Task TheTagsAreWrittenAndTheAlbumNamedAndAScanAfterwardsChangesNothing()
    {
        Assert.SkipUnless(Corpus.IsAvailable, "ffmpeg is not on PATH; source ~/.local/opt/env.sh.");

        var factory = Factory(mutation: true);
        var body = await DownloadAsync(factory);

        Assert.Equal(2, body.Filing!.TagsWritten);
        Assert.Null(body.Filing.Why);
        Assert.Equal("Fleetwood Mac/Rumours (1977)", body.Filing.Folder);

        var path = Path.Combine(_root, "Fleetwood Mac", "Rumours (1977)", "01 - The Chain.flac");
        var tags = await factory.Services.GetRequiredService<TagReader>()
            .ReadAsync(new LibraryPath("Fleetwood Mac/Rumours (1977)/01 - The Chain.flac"), null, Token);

        Assert.True(File.Exists(path));
        Assert.Equal("The Chain", tags.Fields["TITLE"]);
        Assert.Equal("Rumours", tags.Fields["ALBUM"]);
        Assert.Equal("Fleetwood Mac", tags.Find(CatalogueTags.Artist));
        Assert.Equal("1977", tags.Fields["YEAR"]);

        // Rule 2: the rows carry the bytes as written, so a scan sees no change.
        using var scope = factory.Services.CreateScope();
        var scan = await scope.ServiceProvider.GetRequiredService<LibraryScanService>().ScanAsync(Token);

        Assert.Equal((0, 0, 0, 2), (scan.Summary!.Added, scan.Summary.Updated, scan.Summary.Removed, scan.Summary.Unchanged));

        await using var db = PostgresFixture.CreateContext(_connectionString);
        Assert.All(await db.MediaFiles.ToListAsync(Token), file => Assert.Equal(ReleaseAttributionOutcome.AttributedByProvider, file.AttributionOutcome));
    }

    [Fact]
    public async Task ASecondDownloadOfTheSameBarcodeFindsItsAlbumAgain()
    {
        Assert.SkipUnless(Corpus.IsAvailable, "ffmpeg is not on PATH; source ~/.local/opt/env.sh.");

        var factory = Factory(mutation: false);
        var first = await DownloadAsync(factory);
        var second = await DownloadAsync(factory, "?separate=true");

        Assert.Equal(first.Filing!.AlbumId, second.Filing!.AlbumId);
        Assert.Equal(2, second.Filing.Filed);

        await using var db = PostgresFixture.CreateContext(_connectionString);
        Assert.Single(await db.Releases.ToListAsync(Token));
        Assert.Equal(2, await db.Tracks.CountAsync(Token));
        Assert.Equal(2, await db.MediaFiles.CountAsync(Token));
    }

    [Fact]
    public async Task AnArtistIsLinkedOnlyWhereExactlyOneAnswersToTheName()
    {
        Assert.SkipUnless(Corpus.IsAvailable, "ffmpeg is not on PATH; source ~/.local/opt/env.sh.");

        var band = new Artist { Id = ArtistId.New(), Name = "Fleetwood Mac", Mbid = new Mbid(Guid.Parse("bd13909f-1c29-4c27-a874-d4aaf27c5b1a")) };

        await using (var db = PostgresFixture.CreateContext(_connectionString))
        {
            await db.Database.MigrateAsync(Token);
            db.Artists.AddRange(
                band,
                new Artist { Id = ArtistId.New(), Name = "Stevie Nicks" },
                new Artist { Id = ArtistId.New(), Name = "STEVIE NICKS" });
            await db.SaveChangesAsync(Token);
        }

        await DownloadAsync(Factory(mutation: false));

        await using var check = PostgresFixture.CreateContext(_connectionString);
        Assert.All(await check.ArtistCredits.ToListAsync(Token), credit => Assert.Equal(band.Id, credit.ArtistId));

        // Two answer to "Stevie Nicks", so neither is assumed: a third is minted.
        Assert.Equal(3, await check.Artists.CountAsync(artist => artist.Name.ToLower() == "stevie nicks", Token));
    }

    [Fact]
    public async Task ADownloadIsRefusedWhileAPassRuns()
    {
        var factory = Factory(mutation: false);

        Assert.True(factory.Services.GetRequiredService<LibraryWorkGate>().TryEnter("library.scan", out var lease));

        using (lease)
        {
            using var client = factory.CreateClient();
            using var response = await client.PostAsync(new Uri("/api/qobuz/albums/1/download", UriKind.Relative), null, Token);

            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        }

        Assert.Empty(_qobuz!.Requests);
    }

    [Fact]
    public async Task AReplacedAlbumsRowsGoAndTheNewFilesAreFiledInTheirPlace()
    {
        Assert.SkipUnless(Corpus.IsAvailable, "ffmpeg is not on PATH; source ~/.local/opt/env.sh.");

        var factory = Factory(mutation: false);
        var old = await HeldAsync(mbid: null, "Fleetwood Mac/Rumours/01 - The Chain.mp3", "Fleetwood Mac/Rumours/02 - Dreams.mp3");

        var body = await UpgradeAsync(factory);

        Assert.Equal(ReplacementVerdict.Replace, body.Replacement!.Verdict);
        Assert.Equal(2, body.Filing!.Filed);

        await using var db = PostgresFixture.CreateContext(_connectionString);
        var files = await db.MediaFiles.OrderBy(file => file.Path).ToListAsync(Token);

        Assert.DoesNotContain(files, file => old.Contains(file.Id));
        Assert.Equal(["Fleetwood Mac/Rumours/01 The Chain.flac", "Fleetwood Mac/Rumours/02 Dreams.flac"], files.Select(file => file.Path));
        Assert.All(files, file => Assert.Equal(ReleaseAttributionOutcome.AttributedByProvider, file.AttributionOutcome));
    }

    [Fact]
    public async Task AReplacedAlbumMusicBrainzKnowsIsLeftForThePassesToFile()
    {
        Assert.SkipUnless(Corpus.IsAvailable, "ffmpeg is not on PATH; source ~/.local/opt/env.sh.");

        var factory = Factory(mutation: false);
        await HeldAsync(new Mbid(Guid.Parse("f2b8b0a5-5a56-3a9b-a3f2-4c1b2b2b0d77")), "Fleetwood Mac/Rumours/01 - The Chain.mp3", "Fleetwood Mac/Rumours/02 - Dreams.mp3");

        var body = await UpgradeAsync(factory);

        Assert.Equal(ReplacementVerdict.Replace, body.Replacement!.Verdict);
        Assert.Equal(0, body.Filing!.Filed);
        Assert.NotNull(body.Filing.Why);

        // The old rows went with the old files; the scan adds the new ones.
        await using var db = PostgresFixture.CreateContext(_connectionString);
        Assert.Empty(await db.MediaFiles.ToListAsync(Token));
        Assert.Empty(await db.Releases.Where(release => release.Barcode != null).ToListAsync(Token));
    }

    [Fact]
    public async Task AnInterruptedDownloadIsLeftUnfiledAndResumesWhole()
    {
        Assert.SkipUnless(Corpus.IsAvailable, "ffmpeg is not on PATH; source ~/.local/opt/env.sh.");

        var factory = Factory(mutation: true);

        _failSecondTrack = true;
        var interrupted = await DownloadAsync(factory);

        Assert.Equal(0, interrupted.Filing!.Filed);
        Assert.Contains("did not arrive", interrupted.Filing.Why, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(_root, "Fleetwood Mac", "Rumours", "01 The Chain.flac")));

        await using (var db = PostgresFixture.CreateContext(_connectionString))
        {
            Assert.Empty(await db.MediaFiles.ToListAsync(Token));
        }

        _failSecondTrack = false;
        var resumed = await DownloadAsync(factory);

        Assert.Equal(1, resumed.Download!.Downloaded);
        Assert.Equal(2, resumed.Filing!.Filed);
        Assert.Equal(2, resumed.Filing.TagsWritten);
        Assert.Equal("Fleetwood Mac/Rumours (1977)", resumed.Filing.Folder);
    }

    [Fact]
    public async Task APersonsAnswerAtALandingPathStands()
    {
        Assert.SkipUnless(Corpus.IsAvailable, "ffmpeg is not on PATH; source ~/.local/opt/env.sh.");

        // Already on disk under the name the download gives it, and filed by hand.
        var path = "Fleetwood Mac/Rumours/01 The Chain.flac";
        Directory.CreateDirectory(Path.Combine(_root, "Fleetwood Mac", "Rumours"));
        File.Copy(Corpus.Flac, Path.Combine(_root, path));

        var answered = new MediaFile
        {
            Id = MediaFileId.New(),
            Path = path,
            SizeBytes = new FileInfo(Path.Combine(_root, path)).Length,
            LastModifiedUtc = DateTimeOffset.Parse("2026-01-01T00:00:00Z", CultureInfo.InvariantCulture),
            AttributionOutcome = ReleaseAttributionOutcome.AlbumByPerson,
            ReleaseDecidedUtc = DateTimeOffset.Parse("2026-01-02T00:00:00Z", CultureInfo.InvariantCulture),
        };

        await using (var db = PostgresFixture.CreateContext(_connectionString))
        {
            await db.Database.MigrateAsync(Token);
            db.MediaFiles.Add(answered);
            await db.SaveChangesAsync(Token);
        }

        var body = await DownloadAsync(Factory(mutation: false), "?separate=true");

        Assert.Equal((1, 1), (body.Filing!.Filed, body.Filing.Kept));

        await using var check = PostgresFixture.CreateContext(_connectionString);
        var row = await check.MediaFiles.SingleAsync(file => file.Id == answered.Id, Token);

        Assert.Equal(ReleaseAttributionOutcome.AlbumByPerson, row.AttributionOutcome);
        Assert.Null(row.ReleaseId);
    }

    [Theory]
    [InlineData(1, 2, false)]
    [InlineData(2, 3, true)]
    public async Task AnAlbumIsMusicBrainzsWhereMostOfItsFolderIsFiledUnderOne(int known, int files, bool expected)
    {
        await using (var db = PostgresFixture.CreateContext(_connectionString))
        {
            await db.Database.MigrateAsync(Token);

            var group = new ReleaseGroup { Id = ReleaseGroupId.New(), Title = "Rumours", Mbid = new Mbid(Guid.CreateVersion7()) };
            db.ReleaseGroups.Add(group);

            for (var index = 0; index < files; index++)
            {
                db.MediaFiles.Add(new MediaFile
                {
                    Id = MediaFileId.New(),
                    Path = $"Fleetwood Mac/Rumours/{index:D2}.flac",
                    SizeBytes = 1,
                    LastModifiedUtc = DateTimeOffset.UtcNow,
                    ReleaseGroupId = index < known ? group.Id : null,
                });
            }

            await db.SaveChangesAsync(Token);
        }

        using var scope = Factory(mutation: false).Services.CreateScope();

        Assert.Equal(expected, await scope.ServiceProvider.GetRequiredService<DownloadFiling>().KnownToMusicBrainzAsync("Fleetwood Mac/Rumours", Token));
    }

    private async Task<AlbumUpgrade> DownloadAsync(WebApplicationFactory<Program> factory, string query = "", string album = "1")
    {
        using var client = factory.CreateClient();
        using var response = await client.PostAsync(new Uri($"/api/qobuz/albums/{album}/download{query}", UriKind.Relative), null, Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<AlbumUpgrade>(Token))!;
    }

    private async Task<AlbumUpgrade> UpgradeAsync(WebApplicationFactory<Program> factory)
    {
        using var client = factory.CreateClient();
        using var response = await client.PostAsJsonAsync(
            new Uri("/api/qobuz/albums/1/upgrade", UriKind.Relative),
            new UpgradeRequest("Fleetwood Mac/Rumours", 2, Confirmed: true),
            Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<AlbumUpgrade>(Token))!;
    }

    /// <summary>MP3s held under an album, MusicBrainz's where <paramref name="mbid"/> is given.</summary>
    private async Task<List<MediaFileId>> HeldAsync(Mbid? mbid, params string[] paths)
    {
        await using var db = PostgresFixture.CreateContext(_connectionString);
        await db.Database.MigrateAsync(Token);

        var group = new ReleaseGroup { Id = ReleaseGroupId.New(), Title = "Rumours", Mbid = mbid, FirstReleaseYear = 1977 };
        db.ReleaseGroups.Add(group);

        var ids = new List<MediaFileId>();

        foreach (var path in paths)
        {
            var absolute = Path.Combine(_root, path);
            Directory.CreateDirectory(Path.GetDirectoryName(absolute)!);
            File.Copy(Corpus.Mp3, absolute);

            var recording = new Recording { Id = RecordingId.New(), Title = Path.GetFileNameWithoutExtension(path)[5..] };
            db.Recordings.Add(recording);

            var row = new MediaFile
            {
                Id = MediaFileId.New(),
                Path = path,
                SizeBytes = new FileInfo(absolute).Length,
                LastModifiedUtc = DateTimeOffset.Parse("2026-01-01T00:00:00Z", CultureInfo.InvariantCulture),
                Quality = new AudioQuality { Codec = "mp3", SampleRateHz = 44_100, Channels = 2, BitrateBps = 320_000, IsLossless = false },
                RecordingId = recording.Id,
                ReleaseGroupId = group.Id,
            };

            db.MediaFiles.Add(row);
            ids.Add(row.Id);
        }

        await db.SaveChangesAsync(Token);
        return ids;
    }

    private WebApplicationFactory<Program> Factory(bool mutation, IMusicBrainzCatalogue? musicBrainz = null)
    {
        var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:Fonoteca", _connectionString);
            builder.UseSetting("Fonoteca:LibraryPath", _root);
            builder.UseSetting("Fonoteca:ReplacedPath", _root + "-replaced");
            builder.UseSetting("Fonoteca:AllowFileReplacement", "true");
            builder.UseSetting("Fonoteca:AllowFileMutation", mutation ? "true" : "false");
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
                services.AddHttpClient(QobuzOptions.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => _qobuz!);
                services.AddHttpClient(QobuzClient.ContentHttpClientName).ConfigurePrimaryHttpMessageHandler(() => _qobuz!);

                if (musicBrainz is not null) services.AddSingleton(musicBrainz);

                // An outage is answered at once rather than retried for seconds.
                services.ConfigureAll<Microsoft.Extensions.Http.Resilience.HttpStandardResilienceOptions>(options =>
                {
                    options.Retry.MaxRetryAttempts = 1;
                    options.Retry.Delay = TimeSpan.FromMilliseconds(1);
                    options.Retry.UseJitter = false;
                });
            });
        });

        _factories.Add(factory);
        return factory;
    }
}
