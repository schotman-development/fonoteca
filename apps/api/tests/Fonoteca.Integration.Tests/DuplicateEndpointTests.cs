using System.Globalization;
using System.Net.Http.Json;
using Fonoteca.Api.Endpoints;
using Fonoteca.Domain.Catalogue;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Fonoteca.Integration.Tests;

/// <summary><c>GET /api/catalogue/duplicates</c>, through the real host against a seeded catalogue.</summary>
[Collection(nameof(PostgresCollection))]
public sealed class DuplicateEndpointTests(PostgresFixture postgres) : IAsyncLifetime
{
    private string _connectionString = string.Empty;
    private string _root = string.Empty;
    private WebApplicationFactory<Program>? _factory;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _connectionString = await postgres.CreateDatabaseAsync(Token);
        _root = Directory.CreateTempSubdirectory("fonoteca-duplicates-api-").FullName;

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.SignedIn();
            builder.UseSetting("ConnectionStrings:Fonoteca", _connectionString);
            builder.UseSetting("Fonoteca:LibraryPath", _root);
            builder.UseSetting("Fonoteca:WarmCandidates", "false");
            builder.UseSetting("Fonoteca:MusicBrainzContact", string.Empty);
        });

        // Forces the host to start, which migrates.
        using var warm = _factory.CreateClient();
    }

    public async ValueTask DisposeAsync()
    {
        if (_factory is not null) await _factory.DisposeAsync();

        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    /// <summary>
    /// An album in two folders comes back titled and billed, the FLAC folder
    /// first and marked; a song twice in a folder comes back with its copies;
    /// and a pressing that prints a song on two slots is not on the list.
    /// </summary>
    [Fact]
    public async Task AnAlbumInTwoFoldersAndASongTwiceInOneAreListed()
    {
        await using (var db = PostgresFixture.CreateContext(_connectionString))
        {
            var artist = new Artist { Id = ArtistId.New(), Name = "Fleetwood Mac" };
            var rumours = new ReleaseGroup { Id = ReleaseGroupId.New(), Title = "Rumours" };
            var single = new ReleaseGroup { Id = ReleaseGroupId.New(), Title = "Can't Slow Down" };
            var release = new Release { Id = ReleaseId.New(), Title = "Can't Slow Down", ReleaseGroupId = single.Id };
            var dreams = new Recording { Id = RecordingId.New(), Title = "Dreams" };
            var chain = new Recording { Id = RecordingId.New(), Title = "The Chain" };
            var night = new Recording { Id = RecordingId.New(), Title = "All Night Long" };
            var albumCut = new Track { Id = TrackId.New(), ReleaseId = release.Id, RecordingId = night.Id, DiscNumber = 1, Position = 2 };
            var twelveInch = new Track { Id = TrackId.New(), ReleaseId = release.Id, RecordingId = night.Id, DiscNumber = 1, Position = 9 };

            db.AddRange(artist, rumours, single, release, dreams, chain, night, albumCut, twelveInch);
            db.ArtistCredits.Add(new ArtistCredit { Id = Guid.CreateVersion7(), ArtistId = artist.Id, ReleaseGroupId = rumours.Id, Position = 0 });

            db.MediaFiles.AddRange(
                File("Fleetwood Mac/Rumours/01 Dreams.flac", rumours.Id, dreams.Id, Cd()),
                File("Fleetwood Mac/Rumours/01 Dreams (1).mp3", rumours.Id, dreams.Id, Mp3()),
                File("Fleetwood Mac/Rumours/02 The Chain.flac", rumours.Id, chain.Id, Cd()),
                File("Fleetwood Mac/Rumours (MP3)/01 Dreams.mp3", rumours.Id, dreams.Id, Mp3()),
                File("Fleetwood Mac/Rumours (MP3)/02 The Chain.mp3", rumours.Id, chain.Id, Mp3()),
                File("Lionel Richie/Can't Slow Down/1-02.flac", single.Id, night.Id, Cd(), albumCut.Id),
                File("Lionel Richie/Can't Slow Down/1-09.flac", single.Id, night.Id, Cd(), twelveInch.Id));

            await db.SaveChangesAsync(Token);
        }

        using var client = _factory!.CreateClient();

        var body = await client.GetFromJsonAsync<DuplicatesResponse>(
            new Uri("/api/catalogue/duplicates", UriKind.Relative), Token);

        var album = Assert.Single(body!.Albums);
        Assert.Equal("Rumours", album.Title);
        Assert.Equal("Fleetwood Mac", album.Artist);
        Assert.Equal(["Fleetwood Mac/Rumours", "Fleetwood Mac/Rumours (MP3)"], album.Folders.Select(folder => folder.Path));
        Assert.Equal([false, false], album.Folders.Select(folder => folder.Better));

        // The FLAC folder holds an MP3 too, so its worst file is no better than the other's.
        var flac = album.Folders[0];
        Assert.Equal(3, flac.Files);
        Assert.Equal(2, flac.Songs);
        Assert.Equal(["FLAC 16/44.1", "MP3"], flac.Formats);
        Assert.Equal(3_000, flac.SizeBytes);

        var song = Assert.Single(body.Songs);
        Assert.Equal("Fleetwood Mac/Rumours", song.Folder);
        Assert.Equal("Dreams", song.Title);
        Assert.False(song.SameAudio);
        Assert.Equal(
            [("Fleetwood Mac/Rumours/01 Dreams (1).mp3", "MP3", false), ("Fleetwood Mac/Rumours/01 Dreams.flac", "FLAC 16/44.1", true)],
            song.Copies.Select(copy => (copy.Path, copy.Format, copy.Better)));
    }

    private static MediaFile File(
        string path,
        ReleaseGroupId album,
        RecordingId recording,
        AudioQuality quality,
        TrackId? track = null) => new()
    {
        Id = MediaFileId.New(),
        Path = path,
        SizeBytes = 1_000,
        LastModifiedUtc = DateTimeOffset.Parse("2026-01-01T00:00:00Z", CultureInfo.InvariantCulture),
        RecordingId = recording,
        ReleaseGroupId = album,
        TrackId = track,
        Quality = quality,
        Integrity = IntegrityState.Intact,
    };

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
