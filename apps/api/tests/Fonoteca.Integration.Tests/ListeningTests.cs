using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Fonoteca.Data;
using Fonoteca.Domain.Abstractions;
using Fonoteca.Domain.Catalogue;
using Fonoteca.Fixtures;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Fonoteca.Integration.Tests;

/// <summary>Plays: counted from <c>scrobble</c>, served per listener, sent to and seeded from ListenBrainz (client issue #67).</summary>
[Collection(nameof(PostgresCollection))]
public sealed class ListeningTests(PostgresFixture postgres) : IAsyncLifetime
{
    private const string Password = "a-test-passphrase";
    private const string Me = "u=me&p=" + Password + "&v=1.16.1&c=test";
    private const string You = "u=you&p=" + Password + "&v=1.16.1&c=test";
    private const string GoodToken = "good-token";

    private static readonly Mbid Allegro = new(Guid.Parse("11111111-1111-4111-8111-111111111111"));

    private readonly StubListenBrainz _listenBrainz = new();
    private string _connectionString = string.Empty;
    private WebApplicationFactory<Program>? _factory;
    private HttpClient? _web;
    private Guid _me;

    private MediaFileId _allegro;
    private MediaFileId _andante;
    private MediaFileId _allegroOnBestOf;
    private ReleaseGroupId _symphonies;
    private ReleaseGroupId _bestOf;
    private WorkId _symphony;
    private ArtistId _beethoven;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _connectionString = await postgres.CreateDatabaseAsync(Token);

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:Fonoteca", _connectionString);
            builder.UseSetting("Fonoteca:LibraryPath", Directory.CreateTempSubdirectory("fonoteca-listening-").FullName);
            builder.UseSetting("Fonoteca:WarmCandidates", "false");
            builder.UseSetting("Fonoteca:MusicBrainzContact", string.Empty);
            builder.ConfigureTestServices(services => services.AddSingleton<IListenBrainz>(_listenBrainz));
        });

        (_web, _me) = await _factory.SetUpAsync("me", Password, Token);
        await _web.AddAccountAsync("you", Password, Token, "Stream");

        var orchestra = new Artist { Id = ArtistId.New(), Name = "Wiener Philharmoniker" };
        var beethoven = new Artist { Id = ArtistId.New(), Name = "Ludwig van Beethoven", Mbid = new Mbid(Guid.CreateVersion7()) };
        var symphonies = new ReleaseGroup { Id = ReleaseGroupId.New(), Title = "Symphonies", Genres = "classical" };
        var bestOf = new ReleaseGroup { Id = ReleaseGroupId.New(), Title = "Best Of", Genres = "classical" };
        var symphony = new Work { Id = WorkId.New(), Title = "Symphony No. 5", Type = "Symphony" };
        var first = new Work { Id = WorkId.New(), Title = "Symphony No. 5: I. Allegro con brio", ParentId = symphony.Id, Position = 1 };
        var second = new Work { Id = WorkId.New(), Title = "Symphony No. 5: II. Andante con moto", ParentId = symphony.Id, Position = 2 };
        var allegro = new Recording { Id = RecordingId.New(), Title = "Allegro con brio", Mbid = Allegro, WorkId = first.Id, Duration = TimeSpan.FromSeconds(420) };
        var andante = new Recording { Id = RecordingId.New(), Title = "Andante con moto", WorkId = second.Id };

        MediaFile File(string path, Recording recording, ReleaseGroup album) => new()
        {
            Id = MediaFileId.New(),
            Path = path,
            SizeBytes = 1024,
            LastModifiedUtc = DateTimeOffset.Parse("2026-01-01T00:00:00Z", CultureInfo.InvariantCulture),
            RecordingId = recording.Id,
            ReleaseGroupId = album.Id,
            TagTrackNumber = 1,
        };

        var files = new[]
        {
            File("Symphonies/01 Allegro.flac", allegro, symphonies),
            File("Symphonies/02 Andante.flac", andante, symphonies),
            File("Best Of/01 Allegro.flac", allegro, bestOf),
        };

        (_allegro, _andante, _allegroOnBestOf) = (files[0].Id, files[1].Id, files[2].Id);
        (_symphonies, _bestOf, _symphony, _beethoven) = (symphonies.Id, bestOf.Id, symphony.Id, beethoven.Id);

        await using var db = PostgresFixture.CreateContext(_connectionString);
        db.Artists.AddRange(orchestra, beethoven);
        db.ReleaseGroups.AddRange(symphonies, bestOf);
        db.Works.AddRange(symphony, first, second);
        db.Recordings.AddRange(allegro, andante);
        db.MediaFiles.AddRange(files);
        db.ArtistCredits.AddRange(
            new ArtistCredit { Id = Guid.CreateVersion7(), ArtistId = orchestra.Id, RecordingId = allegro.Id, Position = 0 },
            new ArtistCredit { Id = Guid.CreateVersion7(), ArtistId = orchestra.Id, RecordingId = andante.Id, Position = 0 });
        db.Contributions.Add(new Contribution { Id = Guid.CreateVersion7(), WorkId = symphony.Id, Type = "composer", ArtistMbid = beethoven.Mbid!.Value, Name = beethoven.Name, Position = 0 });
        await db.SaveChangesAsync(Token);
    }

    public async ValueTask DisposeAsync()
    {
        _web?.Dispose();
        if (_factory is not null) await _factory.DisposeAsync();
    }

    [Fact]
    public async Task APlayCountsOnEveryCopyOfItsSongOnTheAlbumItWasHeardFromAndOnItsPiece()
    {
        using var client = _factory!.CreateClient();

        // Twice at one second is one play; playing now is none.
        await ScrobbleAsync(client, Me, $"id={Song(_allegro)}&time=1700000000123");
        await ScrobbleAsync(client, Me, $"id={Song(_allegro)}&time=1700000000999");
        await ScrobbleAsync(client, Me, $"id={Song(_andante)}&time=1700000100000&id={Song(_allegroOnBestOf)}&time=1700000200000");
        await ScrobbleAsync(client, Me, $"id={Song(_andante)}&submission=false");

        var song = (await GetAsync(client, Me, $"getSong.view?id={Song(_allegroOnBestOf)}")).GetProperty("song");
        Assert.Equal(2, song.GetProperty("playCount").GetInt32());
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1_700_000_200), song.GetProperty("played").GetDateTimeOffset());
        Assert.Equal(1, (await GetAsync(client, Me, $"getSong.view?id={Song(_andante)}")).GetProperty("song").GetProperty("playCount").GetInt32());

        var album = (await GetAsync(client, Me, $"getAlbum.view?id={Album(_symphonies)}")).GetProperty("album");
        Assert.Equal(2, album.GetProperty("playCount").GetInt32());
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1_700_000_100), album.GetProperty("played").GetDateTimeOffset());

        Assert.Equal([Album(_symphonies), Album(_bestOf)], await AlbumListAsync(client, Me, "frequent"));
        Assert.Equal([Album(_bestOf), Album(_symphonies)], await AlbumListAsync(client, Me, "recent"));

        var piece = (await GetAsync(client, Me, "getCompositions.view")).GetProperty("compositions").GetProperty("composition")
            .EnumerateArray().Single(composition => composition.GetProperty("id").GetString() == $"wk-{_symphony.Value:N}");
        Assert.Equal(3, piece.GetProperty("playCount").GetInt32());

        var composer = (await GetAsync(client, Me, "getComposers.view")).GetProperty("composers").GetProperty("index")
            .EnumerateArray().SelectMany(index => index.GetProperty("composer").EnumerateArray())
            .Single(writer => writer.GetProperty("id").GetString() == $"ar-{_beethoven.Value:N}");
        Assert.Equal(3, composer.GetProperty("playCount").GetInt32());

        // Another listener's are their own.
        Assert.Equal(0, (await GetAsync(client, You, $"getSong.view?id={Song(_allegro)}")).GetProperty("song").GetProperty("playCount").GetInt32());
        Assert.Empty(await AlbumListAsync(client, You, "frequent"));

        // A file filed again since takes its plays to what it holds now.
        await using (var db = PostgresFixture.CreateContext(_connectionString))
        {
            var andante = await db.MediaFiles.SingleAsync(file => file.Id == _andante, Token);
            var copy = await db.MediaFiles.SingleAsync(file => file.Id == _allegroOnBestOf, Token);
            andante.ReleaseGroupId = _bestOf;
            copy.RecordingId = andante.RecordingId;
            await db.SaveChangesAsync(Token);
        }

        Assert.Equal(1, (await GetAsync(client, Me, $"getAlbum.view?id={Album(_symphonies)}")).GetProperty("album").GetProperty("playCount").GetInt32());
        Assert.Equal(2, (await GetAsync(client, Me, $"getAlbum.view?id={Album(_bestOf)}")).GetProperty("album").GetProperty("playCount").GetInt32());
        Assert.Equal(2, (await GetAsync(client, Me, $"getSong.view?id={Song(_andante)}")).GetProperty("song").GetProperty("playCount").GetInt32());
        Assert.Equal(1, (await GetAsync(client, Me, $"getSong.view?id={Song(_allegro)}")).GetProperty("song").GetProperty("playCount").GetInt32());
    }

    [Fact]
    public async Task APlayIsSentToListenBrainzAtItsOwnSecondAndWhatIsPlayingNowAsPlayingNow()
    {
        using (var connected = await _web!.PostAsJsonAsync(new Uri("/api/account/listenbrainz", UriKind.Relative), new { token = GoodToken }, Token))
        {
            connected.EnsureSuccessStatusCode();
        }

        using var client = _factory!.CreateClient();

        await ScrobbleAsync(client, Me, $"id={Song(_allegro)}&submission=false");
        var playing = await _listenBrainz.SentAsync(listen => listen.ListenedAt is null);
        Assert.Equal(("Allegro con brio", "Wiener Philharmoniker", "Symphonies"), (playing.Track, playing.Artist, playing.Release));

        // Heard offline an hour ago, reported now: a listen at its own time.
        await ScrobbleAsync(client, Me, $"id={Song(_allegro)}&time=1700000000999");
        var listen = await _listenBrainz.SentAsync(listen => listen.ListenedAt is not null);

        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1_700_000_000), listen.ListenedAt);
        Assert.Equal(("Allegro con brio", "Wiener Philharmoniker", "Symphonies", Allegro, 1, 420), (listen.Track, listen.Artist, listen.Release, listen.Recording, listen.TrackNumber, listen.DurationSeconds));

        // Plays kept while offline, reported newest first, go oldest first.
        await ScrobbleAsync(client, Me, $"id={Song(_andante)}&time=1700000300000&id={Song(_allegroOnBestOf)}&time=1700000200000");
        await _listenBrainz.SentAsync(sent => sent.ListenedAt == DateTimeOffset.FromUnixTimeSeconds(1_700_000_300));

        Assert.Equal(
            [1_700_000_000L, 1_700_000_200L, 1_700_000_300L],
            _listenBrainz.Sent.Where(sent => sent.ListenedAt is not null).Select(sent => sent.ListenedAt!.Value.ToUnixTimeSeconds()));

        await using var db = PostgresFixture.CreateContext(_connectionString);
        await Until(() => db.Plays.IgnoreQueryFilters().AllAsync(play => play.ForwardedUtc != null, Token));
    }

    [Fact]
    public async Task ConnectingCountsTheListensListenBrainzHoldsOnceAndSendsNothingItHolds()
    {
        using var client = _factory!.CreateClient();

        // Played here before connecting; ListenBrainz holds it too.
        await ScrobbleAsync(client, Me, $"id={Song(_andante)}&time=1700000500000");

        _listenBrainz.Held =
        [
            new(DateTimeOffset.FromUnixTimeSeconds(1_700_000_900), "Somebody", "Not Held Here", null, null),
            new(DateTimeOffset.FromUnixTimeSeconds(1_700_000_800), "wiener philharmoniker", "Andante Con Moto", null, null),
            new(DateTimeOffset.FromUnixTimeSeconds(1_700_000_700), "Vienna Phil", "Allegro", "Best Of", Allegro),
            new(DateTimeOffset.FromUnixTimeSeconds(1_700_000_500), "Wiener Philharmoniker", "Andante con moto", "Symphonies", null),
        ];

        for (var connect = 0; connect < 2; connect++)
        {
            using var connected = await _web!.PostAsJsonAsync(new Uri("/api/account/listenbrainz", UriKind.Relative), new { token = GoodToken }, Token);
            connected.EnsureSuccessStatusCode();

            await using var read = PostgresFixture.CreateContext(_connectionString);
            await Until(async () => (await read.Users.AsNoTracking().SingleAsync(account => account.Id == _me, Token)).ListenBrainzSeededUtc is not null);
        }

        await using var db = PostgresFixture.CreateContext(_connectionString);
        await Until(() => db.Plays.IgnoreQueryFilters().AllAsync(play => play.ForwardedUtc != null, Token));

        var plays = await db.Plays.IgnoreQueryFilters().AsNoTracking().OrderBy(play => play.PlayedUtc).ToListAsync(Token);
        Assert.Equal(
            [(1_700_000_500L, _andante), (1_700_000_700L, _allegroOnBestOf), (1_700_000_800L, _andante)],
            plays.Select(play => (play.PlayedUtc.ToUnixTimeSeconds(), play.MediaFileId!.Value)));

        Assert.Empty(_listenBrainz.Sent);
        Assert.Equal(2, _listenBrainz.PagesAsked);

        var connection = await _web!.GetFromJsonAsync<JsonElement>(new Uri("/api/account/listenbrainz", UriKind.Relative), Token);
        Assert.Equal("lb-me", connection.GetProperty("user").GetString());
        Assert.Equal(3, connection.GetProperty("plays").GetInt32());
        Assert.Equal(0, connection.GetProperty("unsent").GetInt32());
    }

    [Fact]
    public async Task ATokenListenBrainzDoesNotKnowIsRefused()
    {
        using var refused = await _web!.PostAsJsonAsync(new Uri("/api/account/listenbrainz", UriKind.Relative), new { token = "wrong" }, Token);
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);

        var connection = await _web.GetFromJsonAsync<JsonElement>(new Uri("/api/account/listenbrainz", UriKind.Relative), Token);
        Assert.Equal(JsonValueKind.Null, connection.GetProperty("user").ValueKind);
    }

    private static string Song(MediaFileId id) => $"tr-{id.Value:N}";

    private static string Album(ReleaseGroupId id) => $"al-{id.Value:N}";

    private static async Task ScrobbleAsync(HttpClient client, string credentials, string query) =>
        Assert.Equal("ok", (await GetAsync(client, credentials, $"scrobble.view?{query}")).GetProperty("status").GetString());

    private static async Task<List<string?>> AlbumListAsync(HttpClient client, string credentials, string type)
    {
        var list = (await GetAsync(client, credentials, $"getAlbumList2.view?type={type}")).GetProperty("albumList2");

        return list.TryGetProperty("album", out var albums) ? [.. albums.EnumerateArray().Select(album => album.GetProperty("id").GetString())] : [];
    }

    private static async Task<JsonElement> GetAsync(HttpClient client, string credentials, string route)
    {
        var separator = route.Contains('?', StringComparison.Ordinal) ? "&" : "?";
        using var response = await client.GetAsync(new Uri($"/rest/{route}{separator}{credentials}&f=json", UriKind.Relative), Token);

        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(Token)).RootElement.GetProperty("subsonic-response").Clone();
    }

    /// <summary>Waits for the background sender, which nothing else signals.</summary>
    private static async Task Until(Func<Task<bool>> condition)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            if (await condition()) return;
            await Task.Delay(100, Token);
        }

        Assert.Fail("The background sender never got there.");
    }

    private sealed class StubListenBrainz : IListenBrainz
    {
        public IReadOnlyList<HeardListen> Held { get; set; } = [];

        public ConcurrentQueue<ListenToSubmit> Sent { get; } = new();

        public int PagesAsked;

        public Task<string?> UserOfAsync(string token, CancellationToken cancellationToken = default) =>
            Task.FromResult(token == GoodToken ? "lb-me" : null);

        public Task<IReadOnlyList<HeardListen>> ListensAsync(string user, DateTimeOffset? before, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref PagesAsked);

            IReadOnlyList<HeardListen> page = [.. Held.Where(listen => before is null || listen.ListenedAt < before)];
            return Task.FromResult(before is null ? page : []);
        }

        public Task SubmitAsync(string token, IReadOnlyList<ListenToSubmit> listens, CancellationToken cancellationToken = default)
        {
            foreach (var listen in listens) Sent.Enqueue(listen);
            return Task.CompletedTask;
        }

        public async Task<ListenToSubmit> SentAsync(Func<ListenToSubmit, bool> which)
        {
            ListenToSubmit? found = null;
            await Until(() => Task.FromResult((found = Sent.FirstOrDefault(which)) is not null));
            return found!;
        }
    }
}
