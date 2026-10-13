using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Fonoteca.Api.Endpoints;
using Fonoteca.Api.Library;
using Fonoteca.Domain.Catalogue;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Fonoteca.Integration.Tests;

/// <summary>
/// Playlists through the real host: the m3u on disk, the songs it follows, and
/// a Subsonic client editing them.
/// </summary>
/// <remarks>
/// The file is what is asserted wherever it can be, because it is what another
/// player reads: a playlist that is right in the database and wrong on disk is
/// the failure this feature exists to prevent.
/// </remarks>
[Collection(nameof(PostgresCollection))]
public sealed class PlaylistEndpointTests(PostgresFixture postgres) : IAsyncLifetime
{
    private const string Password = "a-test-password";
    private const string Credentials = $"u=me&p={Password}&v=1.16.1&c=test&f=json";

    private const string Lossless = "Fleetwood Mac/Rumours (1977)/07 - The Chain.flac";
    private const string Lossy = "Fleetwood Mac/Rumours MP3/07 The Chain.mp3";
    private const string DreamsPath = "Fleetwood Mac/Rumours (1977)/02 - Dreams.flac";
    private const string NamelessPath = "Somebody/01 Nameless.flac";

    private string _root = string.Empty;
    private string _trash = string.Empty;
    private string _connectionString = string.Empty;
    private WebApplicationFactory<Program>? _factory;

    private RecordingId _chain;
    private RecordingId _dreams;
    private MediaFileId _lossless;
    private MediaFileId _lossy;
    private MediaFileId _dreamsFile;
    private MediaFileId _nameless;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _connectionString = await postgres.CreateDatabaseAsync(Token);
        _root = Directory.CreateTempSubdirectory("fonoteca-playlists-").FullName;
        _trash = Directory.CreateTempSubdirectory("fonoteca-playlists-bin-").FullName;

        foreach (var path in new[] { Lossless, Lossy, DreamsPath, NamelessPath }) WriteFile(path, "audio");

        _factory = Factory(_root);

        using (var warm = _factory.CreateClient())
        {
            using var boot = await warm.GetAsync(new Uri("/health", UriKind.Relative), Token);
        }

        await using var db = PostgresFixture.CreateContext(_connectionString);

        var band = new Artist { Id = ArtistId.New(), Name = "Fleetwood Mac" };
        var chain = new Recording { Id = RecordingId.New(), Title = "The Chain" };
        var dreams = new Recording { Id = RecordingId.New(), Title = "Dreams" };

        var lossless = File(Lossless, chain.Id, new AudioQuality
        {
            Codec = "flac",
            SampleRateHz = 44_100,
            Channels = 2,
            BitDepth = 16,
            BitrateBps = 900_000,
            IsLossless = true,
            Duration = TimeSpan.FromSeconds(270.6),
        });

        var lossy = File(Lossy, chain.Id, new AudioQuality
        {
            Codec = "mp3",
            SampleRateHz = 44_100,
            Channels = 2,
            BitrateBps = 320_000,
            IsLossless = false,
            Duration = TimeSpan.FromSeconds(270.6),
        });

        var dreamsFile = File(DreamsPath, dreams.Id, new AudioQuality
        {
            Codec = "flac",
            SampleRateHz = 44_100,
            Channels = 2,
            BitDepth = 16,
            BitrateBps = 900_000,
            IsLossless = true,
            Duration = TimeSpan.FromSeconds(257),
        });

        var nameless = File(NamelessPath, null, null);

        (_chain, _dreams) = (chain.Id, dreams.Id);
        (_lossless, _lossy, _dreamsFile, _nameless) = (lossless.Id, lossy.Id, dreamsFile.Id, nameless.Id);

        db.Artists.Add(band);
        db.Recordings.AddRange(chain, dreams);
        db.MediaFiles.AddRange(lossless, lossy, dreamsFile, nameless);
        db.ArtistCredits.Add(new ArtistCredit { Id = Guid.CreateVersion7(), ArtistId = band.Id, RecordingId = chain.Id, Position = 0 });

        await db.SaveChangesAsync(Token);
    }

    public async ValueTask DisposeAsync()
    {
        if (_factory is not null) await _factory.DisposeAsync();

        foreach (var directory in new[] { _root, _trash })
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task APlaylistIsAnM3uNamingTheBestCopyOfEachSongRelativeToItself()
    {
        using var client = _factory!.CreateClient();

        using var response = await client.PostAsJsonAsync(
            new Uri("/api/catalogue/playlists", UriKind.Relative),
            new { name = "Road trip", recordingIds = new[] { _chain.Value, _dreams.Value, _chain.Value } },
            Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var playlist = (await response.Content.ReadFromJsonAsync<PlaylistResponse>(Token))!;

        Assert.Equal("Playlists/Road trip.m3u8", playlist.File);
        Assert.Equal([_lossless.Value, _dreamsFile.Value, _lossless.Value], playlist.Entries.Select(entry => entry.FileId!.Value));
        Assert.Equal(Lossless, playlist.Entries[0].Track!.File.Path);
        Assert.Equal("13:18", playlist.Duration);

        Assert.Equal(
            "#EXTM3U\n"
            + "#PLAYLIST:Road trip\n"
            + "#EXTINF:271,Fleetwood Mac - The Chain\n"
            + $"../{Lossless}\n"
            + "#EXTINF:257,Dreams\n"
            + $"../{DreamsPath}\n"
            + "#EXTINF:271,Fleetwood Mac - The Chain\n"
            + $"../{Lossless}\n",
            ReadPlaylist("Road trip"));
    }

    /// <summary>
    /// A folder renamed through the file manager, and the m3u names the new
    /// path after the next tag write — not before, since nothing else writes it
    /// (the owner's choice).
    /// </summary>
    [Fact]
    public async Task AFolderRenamedInTheLibraryIsFollowedAfterTheNextTagWrite()
    {
        using var client = _factory!.CreateClient();
        await CreateAsync(client, "Road trip", _chain);

        using var moved = await client.PostAsJsonAsync(
            new Uri("/api/files/move", UriKind.Relative),
            new { from = "Fleetwood Mac/Rumours (1977)", to = "Fleetwood Mac/Rumours" },
            Token);

        Assert.Equal(HttpStatusCode.OK, moved.StatusCode);
        Assert.Contains($"\n../{Lossless}\n", ReadPlaylist("Road trip"), StringComparison.Ordinal);

        await _factory.Services
            .GetRequiredService<TagWriteService>()
            .WriteFolderAsync("Fleetwood Mac/Rumours", "test", null, Token);

        Assert.Contains("\n../Fleetwood Mac/Rumours/07 - The Chain.flac\n", ReadPlaylist("Road trip"), StringComparison.Ordinal);

        // Nothing moved since: the file is not written again.
        Assert.Equal(0, await SyncAsync());
    }

    /// <summary>
    /// A recording merged away — its file repointed and its row deleted, as
    /// <c>CatalogueWriter</c> and the catch-up do — and the entry follows its
    /// last copy to the survivor.
    /// </summary>
    [Fact]
    public async Task ASongWhoseRecordingIsMergedAwayFollowsItsLastCopy()
    {
        using var client = _factory!.CreateClient();
        var playlist = await CreateAsync(client, "Dreams", _dreams);

        var survivor = new Recording { Id = RecordingId.New(), Title = "Dreams (2004 Remaster)" };

        await using (var db = PostgresFixture.CreateContext(_connectionString))
        {
            db.Recordings.Add(survivor);
            await db.SaveChangesAsync(Token);

            await db.MediaFiles
                .Where(file => file.Id == _dreamsFile)
                .ExecuteUpdateAsync(set => set.SetProperty(file => file.RecordingId, survivor.Id), Token);
            await db.Recordings.Where(recording => recording.Id == _dreams).ExecuteDeleteAsync(Token);
        }

        Assert.Equal(1, await SyncAsync());

        await using (var db = PostgresFixture.CreateContext(_connectionString))
        {
            var entry = await db.PlaylistEntries.SingleAsync(Token);

            Assert.Equal(survivor.Id, entry.RecordingId);
            Assert.Equal(_dreamsFile, entry.MediaFileId);
            Assert.Equal("Dreams (2004 Remaster)", entry.Title);
        }

        var read = await GetAsync(client, playlist.Id);

        Assert.Equal(survivor.Id.Value, read.Entries[0].Track!.RecordingId);
        Assert.Contains("#EXTINF:257,Dreams (2004 Remaster)\n", ReadPlaylist("Dreams"), StringComparison.Ordinal);
    }

    /// <summary>
    /// A file relinked to another recording while the old row stays, as a
    /// person's corrected identification leaves it: the old recording holds no
    /// copy, so the entry follows the file.
    /// </summary>
    [Fact]
    public async Task ASongWhoseOnlyCopyIsRelinkedFollowsTheCopy()
    {
        using var client = _factory!.CreateClient();
        await CreateAsync(client, "Dreams", _dreams);

        var relinked = new Recording { Id = RecordingId.New(), Title = "Dreams (Live)" };

        await using (var db = PostgresFixture.CreateContext(_connectionString))
        {
            db.Recordings.Add(relinked);
            await db.SaveChangesAsync(Token);

            await db.MediaFiles
                .Where(file => file.Id == _dreamsFile)
                .ExecuteUpdateAsync(set => set.SetProperty(file => file.RecordingId, relinked.Id), Token);
        }

        Assert.Equal(1, await SyncAsync());

        await using (var db = PostgresFixture.CreateContext(_connectionString))
        {
            Assert.Equal(relinked.Id, (await db.PlaylistEntries.SingleAsync(Token)).RecordingId);
            Assert.True(await db.Recordings.AnyAsync(recording => recording.Id == _dreams, Token));
        }

        Assert.Equal($"#EXTM3U\n#PLAYLIST:Dreams\n#EXTINF:257,Dreams (Live)\n../{DreamsPath}\n", ReadPlaylist("Dreams"));
    }

    /// <summary>
    /// A file whose link to its recording is withdrawn — a reopened folder, an
    /// outside tagger's bytes — has not been shown to be another song, so the
    /// entry keeps playing it (rule 3).
    /// </summary>
    [Fact]
    public async Task ACopyWhoseIdentificationIsWithdrawnStillPlaysTheSong()
    {
        using var client = _factory!.CreateClient();
        var playlist = await CreateAsync(client, "Dreams", _dreams);

        await using (var db = PostgresFixture.CreateContext(_connectionString))
        {
            await db.MediaFiles
                .Where(file => file.Id == _dreamsFile)
                .ExecuteUpdateAsync(set => set.SetProperty(file => file.RecordingId, (RecordingId?)null), Token);
        }

        Assert.Equal(0, await SyncAsync());
        Assert.Equal($"#EXTM3U\n#PLAYLIST:Dreams\n#EXTINF:257,Dreams\n../{DreamsPath}\n", ReadPlaylist("Dreams"));

        var read = await GetAsync(client, playlist.Id);

        Assert.Equal(_dreamsFile.Value, read.Entries[0].FileId);
        Assert.Equal(DreamsPath, read.Entries[0].Track!.File.Path);
        Assert.Null(read.Entries[0].Track!.Album);
    }

    [Fact]
    public async Task SongsAddedToAPlaylistReachTheFile()
    {
        using var client = _factory!.CreateClient();
        var playlist = await CreateAsync(client, "Road trip", _chain);

        using var added = await client.PostAsJsonAsync(
            new Uri($"/api/catalogue/playlists/{playlist.Id}/songs", UriKind.Relative),
            new { recordingIds = new[] { _dreams.Value } },
            Token);

        Assert.Equal(HttpStatusCode.OK, added.StatusCode);
        Assert.Equal(
            "#EXTM3U\n#PLAYLIST:Road trip\n"
            + $"#EXTINF:271,Fleetwood Mac - The Chain\n../{Lossless}\n"
            + $"#EXTINF:257,Dreams\n../{DreamsPath}\n",
            ReadPlaylist("Road trip"));
    }

    [Fact]
    public async Task ANewOrderIsTheFilesOrder()
    {
        using var client = _factory!.CreateClient();
        var playlist = await CreateAsync(client, "Road trip", _chain, _dreams);

        using var reordered = await client.PostAsJsonAsync(
            new Uri($"/api/catalogue/playlists/{playlist.Id}", UriKind.Relative),
            new { entries = new[] { playlist.Entries[1].Id, playlist.Entries[0].Id } },
            Token);

        Assert.Equal(HttpStatusCode.OK, reordered.StatusCode);
        Assert.Equal(
            "#EXTM3U\n#PLAYLIST:Road trip\n"
            + $"#EXTINF:257,Dreams\n../{DreamsPath}\n"
            + $"#EXTINF:271,Fleetwood Mac - The Chain\n../{Lossless}\n",
            ReadPlaylist("Road trip"));
    }

    /// <summary>The better copy trashed, and the m3u names the one that is left.</summary>
    [Fact]
    public async Task WithTheBestCopyGoneTheNextBestPlays()
    {
        using var client = _factory!.CreateClient();
        await CreateAsync(client, "Road trip", _chain);

        using var trashed = await client.PostAsJsonAsync(
            new Uri("/api/files/trash", UriKind.Relative),
            new { paths = new[] { Lossless } },
            Token);

        Assert.Equal(HttpStatusCode.OK, trashed.StatusCode);
        Assert.Equal(1, await SyncAsync());
        Assert.Contains($"\n../{Lossy}\n", ReadPlaylist("Road trip"), StringComparison.Ordinal);
    }

    /// <summary>
    /// A song the library holds no copy of stays on the playlist and out of
    /// the file, so it returns when a copy does.
    /// </summary>
    [Fact]
    public async Task ASongWithNoCopyStaysOnThePlaylistAndOutOfTheFile()
    {
        using var client = _factory!.CreateClient();
        var playlist = await CreateAsync(client, "Road trip", _chain, _dreams);

        await using (var db = PostgresFixture.CreateContext(_connectionString))
        {
            await db.MediaFiles.Where(file => file.Id == _dreamsFile).ExecuteDeleteAsync(Token);
        }

        Assert.Equal(1, await SyncAsync());

        var read = await GetAsync(client, playlist.Id);

        Assert.Equal(2, read.Entries.Count);
        Assert.Null(read.Entries[1].Track);
        Assert.Null(read.Entries[1].FileId);
        Assert.Equal("Dreams", read.Entries[1].Title);
        Assert.DoesNotContain("Dreams", ReadPlaylist("Road trip"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReorderingRemovingRenamingAndDeletingAllReachTheFile()
    {
        using var client = _factory!.CreateClient();
        var playlist = await CreateAsync(client, "Morning", _chain, _dreams);

        using var patched = await client.PostAsJsonAsync(
            new Uri($"/api/catalogue/playlists/{playlist.Id}", UriKind.Relative),
            new { name = "Evening", entries = new[] { playlist.Entries[1].Id } },
            Token);

        Assert.Equal(HttpStatusCode.OK, patched.StatusCode);
        Assert.False(System.IO.File.Exists(PlaylistPath("Morning")));
        Assert.Equal($"#EXTM3U\n#PLAYLIST:Evening\n#EXTINF:257,Dreams\n../{DreamsPath}\n", ReadPlaylist("Evening"));

        using var deleted = await client.DeleteAsync(new Uri($"/api/catalogue/playlists/{playlist.Id}", UriKind.Relative), Token);

        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.False(System.IO.File.Exists(PlaylistPath("Evening")));
        Assert.Single(Directory.GetFiles(_trash, "Evening.m3u8", SearchOption.AllDirectories));

        using var gone = await client.GetAsync(new Uri($"/api/catalogue/playlists/{playlist.Id}", UriKind.Relative), Token);

        Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
    }

    /// <summary>A person's own m3u is never written over, and two playlists cannot share a file.</summary>
    [Fact]
    public async Task ANameWhoseFileIsTakenIsRefused()
    {
        WriteFile("Playlists/Mine.m3u8", "#EXTM3U\n../somewhere.flac\n");

        using var client = _factory!.CreateClient();

        using var foreign = await client.PostAsJsonAsync(
            new Uri("/api/catalogue/playlists", UriKind.Relative),
            new { name = "Mine" },
            Token);

        Assert.Equal(HttpStatusCode.Conflict, foreign.StatusCode);
        Assert.Equal("#EXTM3U\n../somewhere.flac\n", ReadPlaylist("Mine"));

        await CreateAsync(client, "Road trip", _chain);

        using var twice = await client.PostAsJsonAsync(
            new Uri("/api/catalogue/playlists", UriKind.Relative),
            new { name = "road TRIP" },
            Token);

        Assert.Equal(HttpStatusCode.Conflict, twice.StatusCode);
    }

    [Fact]
    public async Task OnlySongsTheLibraryHoldsCanBeAddedAndAPlaylistNeedsAName()
    {
        using var client = _factory!.CreateClient();

        using var unknown = await client.PostAsJsonAsync(
            new Uri("/api/catalogue/playlists", UriKind.Relative),
            new { name = "Road trip", recordingIds = new[] { Guid.CreateVersion7() } },
            Token);

        using var unnamed = await client.PostAsJsonAsync(
            new Uri("/api/catalogue/playlists", UriKind.Relative),
            new { name = "  " },
            Token);

        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, unnamed.StatusCode);
        Assert.False(Directory.Exists(Path.Combine(_root, "Playlists")));
    }

    /// <summary>
    /// A root with nothing in it is a mount point with nothing behind it, and a
    /// playlist written there would land on the host's own disk (rule 3).
    /// </summary>
    [Fact]
    public async Task NothingIsWrittenWhileTheLibraryLooksUnmounted()
    {
        var empty = Directory.CreateTempSubdirectory("fonoteca-playlists-unmounted-").FullName;

        try
        {
            await using var unmounted = Factory(empty);
            using var client = unmounted.CreateClient();

            using var response = await client.PostAsJsonAsync(
                new Uri("/api/catalogue/playlists", UriKind.Relative),
                new { name = "Road trip", recordingIds = new[] { _chain.Value } },
                Token);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Empty(Directory.EnumerateFileSystemEntries(empty));
        }
        finally
        {
            Directory.Delete(empty, recursive: true);
        }
    }

    [Fact]
    public async Task ASubsonicClientCreatesEditsAndDeletesPlaylists()
    {
        using var client = _factory!.CreateClient();

        var created = await SubsonicAsync(client, $"createPlaylist?name=Phone&songId={Song(_lossy)}&songId={Song(_dreamsFile)}");

        Assert.Equal("ok", created.GetProperty("status").GetString());

        var id = created.GetProperty("playlist").GetProperty("id").GetString()!;

        // A song is played from its best copy whichever copy the client named.
        Assert.Equal([Song(_lossless), Song(_dreamsFile)], Entries(created.GetProperty("playlist")));
        Assert.Contains($"../{Lossless}", ReadPlaylist("Phone"), StringComparison.Ordinal);

        var refused = await SubsonicAsync(client, $"createPlaylist?name=Bad&songId={Song(_nameless)}");

        Assert.Equal("failed", refused.GetProperty("status").GetString());
        Assert.Equal(70, refused.GetProperty("error").GetProperty("code").GetInt32());

        var listed = await SubsonicAsync(client, "getPlaylists");
        var only = Assert.Single(listed.GetProperty("playlists").GetProperty("playlist").EnumerateArray());

        Assert.Equal(id, only.GetProperty("id").GetString());
        Assert.Equal(2, only.GetProperty("songCount").GetInt32());
        Assert.Equal(528, only.GetProperty("duration").GetInt32());

        var updated = await SubsonicAsync(client, $"updatePlaylist?playlistId={id}&songIndexToRemove=0&songIdToAdd={Song(_dreamsFile)}");

        Assert.Equal("ok", updated.GetProperty("status").GetString());
        Assert.Equal(
            [Song(_dreamsFile), Song(_dreamsFile)],
            Entries((await SubsonicAsync(client, $"getPlaylist?id={id}")).GetProperty("playlist")));

        var deleted = await SubsonicAsync(client, $"deletePlaylist?id={id}");

        Assert.Equal("ok", deleted.GetProperty("status").GetString());
        Assert.Empty((await SubsonicAsync(client, "getPlaylists")).GetProperty("playlists").GetProperty("playlist").EnumerateArray());
    }

    /// <summary>
    /// A song with no copy is not shown to a client, and a client replacing the
    /// songs it was shown does not take that one away.
    /// </summary>
    [Fact]
    public async Task ASubsonicReplaceKeepsTheEntriesTheClientCouldNotSee()
    {
        using var client = _factory!.CreateClient();
        var playlist = await CreateAsync(client, "Road trip", _chain, _dreams);

        await using (var db = PostgresFixture.CreateContext(_connectionString))
        {
            await db.MediaFiles.Where(file => file.Id == _dreamsFile).ExecuteDeleteAsync(Token);
        }

        var id = $"pl-{playlist.Id:N}";

        Assert.Equal([Song(_lossless)], Entries((await SubsonicAsync(client, $"getPlaylist?id={id}")).GetProperty("playlist")));

        var replaced = await SubsonicAsync(client, $"createPlaylist?playlistId={id}&songId={Song(_lossless)}&songId={Song(_lossy)}");

        Assert.Equal([Song(_lossless), Song(_lossless)], Entries(replaced.GetProperty("playlist")));

        var read = await GetAsync(client, playlist.Id);

        Assert.Equal(["The Chain", "The Chain", "Dreams"], read.Entries.Select(entry => entry.Title));
        Assert.Null(read.Entries[2].Track);
    }

    private async Task<PlaylistResponse> CreateAsync(HttpClient client, string name, params RecordingId[] songs)
    {
        using var response = await client.PostAsJsonAsync(
            new Uri("/api/catalogue/playlists", UriKind.Relative),
            new { name, recordingIds = songs.Select(song => song.Value).ToArray() },
            Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return (await response.Content.ReadFromJsonAsync<PlaylistResponse>(Token))!;
    }

    private static async Task<PlaylistResponse> GetAsync(HttpClient client, Guid id) =>
        (await client.GetFromJsonAsync<PlaylistResponse>(new Uri($"/api/catalogue/playlists/{id}", UriKind.Relative), Token))!;

    private static async Task<JsonElement> SubsonicAsync(HttpClient client, string route)
    {
        var separator = route.Contains('?', StringComparison.Ordinal) ? "&" : "?";

        using var response = await client.GetAsync(new Uri($"/rest/{route}{separator}{Credentials}", UriKind.Relative), Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(Token))
            .RootElement
            .GetProperty("subsonic-response")
            .Clone();
    }

    private static List<string> Entries(JsonElement playlist) =>
        [.. playlist.GetProperty("entry").EnumerateArray().Select(entry => entry.GetProperty("id").GetString()!)];

    private static string Song(MediaFileId id) => $"tr-{id.Value:N}";

    private Task<int> SyncAsync() => _factory!.Services.GetRequiredService<PlaylistFiles>().SyncAsync(Token);

    private string PlaylistPath(string name) => Path.Combine(_root, "Playlists", $"{name}.m3u8");

    private string ReadPlaylist(string name) => System.IO.File.ReadAllText(PlaylistPath(name));

    private static MediaFile File(string path, RecordingId? recording, AudioQuality? quality) => new()
    {
        Id = MediaFileId.New(),
        Path = path,
        SizeBytes = 5,
        LastModifiedUtc = DateTimeOffset.Parse("2026-01-01T00:00:00Z", CultureInfo.InvariantCulture),
        RecordingId = recording,
        Quality = quality,
    };

    private WebApplicationFactory<Program> Factory(string root) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:Fonoteca", _connectionString);
            builder.UseSetting("Fonoteca:LibraryPath", root);
            builder.UseSetting("Fonoteca:TrashPath", _trash);
            builder.UseSetting("Fonoteca:WarmCandidates", "false");
            builder.UseSetting("Fonoteca:MusicBrainzContact", string.Empty);
            builder.UseSetting("Fonoteca:SubsonicPassword", Password);
        });

    private void WriteFile(string relativePath, string content)
    {
        var absolute = Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(absolute)!);
        System.IO.File.WriteAllText(absolute, content);
    }
}
