using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Fonoteca.Domain.Catalogue;
using Fonoteca.Fixtures;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Fonoteca.Integration.Tests;

/// <summary>
/// Accounts (ADR 0018): the first visit, signing in, the accounts page, API
/// keys, and hearts that are each account's own.
/// </summary>
[Collection(nameof(PostgresCollection))]
public sealed class AccountEndpointTests(PostgresFixture postgres) : IAsyncLifetime
{
    private const string Password = "A-test-passw0rd";

    private string _connectionString = string.Empty;
    private string _root = string.Empty;
    private WebApplicationFactory<Program>? _factory;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _connectionString = await postgres.CreateDatabaseAsync(Token);
        _root = Directory.CreateTempSubdirectory("fonoteca-accounts-").FullName;

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:Fonoteca", _connectionString);
            builder.UseSetting("Fonoteca:LibraryPath", _root);
            builder.UseSetting("Fonoteca:WarmCandidates", "false");
            builder.UseSetting("Fonoteca:MusicBrainzContact", string.Empty);

            // A session is checked against its account on every request rather
            // than once a minute, so a test sees a reset end it.
            builder.ConfigureTestServices(services => services.Configure<SecurityStampValidatorOptions>(
                stamp => stamp.ValidationInterval = TimeSpan.Zero));
        });

        using var warm = _factory.CreateClient();
        using var boot = await warm.GetAsync(new Uri("/health", UriKind.Relative), Token);
        Assert.Equal(HttpStatusCode.OK, boot.StatusCode);
    }

    public async ValueTask DisposeAsync()
    {
        if (_factory is not null) await _factory.DisposeAsync();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    /// <summary>
    /// The first visit makes the first account and is signed in by it; nothing
    /// else under /api answers before that, and no second setup is allowed.
    /// </summary>
    [Fact]
    public async Task TheFirstVisitMakesTheFirstAccountAndNoOtherCan()
    {
        using var stranger = _factory!.CreateClient();

        Assert.True((await stranger.GetFromJsonAsync<JsonElement>(new Uri("/api/account/setup", UriKind.Relative), Token)).GetProperty("needed").GetBoolean());

        using (var closed = await stranger.GetAsync(new Uri("/api/system/info", UriKind.Relative), Token))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, closed.StatusCode);
        }

        using (var weak = await stranger.PostAsJsonAsync(new Uri("/api/account/setup", UriKind.Relative), new { name = "me", password = "short" }, Token))
        {
            Assert.Equal(HttpStatusCode.BadRequest, weak.StatusCode);
        }

        using var me = (await _factory.SetUpAsync("me", Password, Token)).Client;

        using (var open = await me.GetAsync(new Uri("/api/system/info", UriKind.Relative), Token))
        {
            Assert.Equal(HttpStatusCode.OK, open.StatusCode);
        }

        Assert.Equal("me", (await me.GetFromJsonAsync<JsonElement>(new Uri("/api/account", UriKind.Relative), Token)).GetProperty("name").GetString());
        Assert.False((await stranger.GetFromJsonAsync<JsonElement>(new Uri("/api/account/setup", UriKind.Relative), Token)).GetProperty("needed").GetBoolean());

        using var second = await stranger.PostAsJsonAsync(new Uri("/api/account/setup", UriKind.Relative), new { name = "intruder", password = Password }, Token);
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);

        using var stillClosed = await stranger.GetAsync(new Uri("/api/system/info", UriKind.Relative), Token);
        Assert.Equal(HttpStatusCode.Unauthorized, stillClosed.StatusCode);
    }

    /// <summary>
    /// A wrong password and an unknown name are one answer; five wrong ones
    /// lock the account, and signing out ends the session.
    /// </summary>
    [Fact]
    public async Task SigningInLocksOutAfterFiveWrongPasswordsAndSigningOutEnds()
    {
        using var me = (await _factory!.SetUpAsync("me", Password, Token)).Client;
        using var client = _factory.CreateClient();

        using (var unknown = await SignInAsync(client, "nobody", Password))
        using (var wrong = await SignInAsync(client, "me", "Not-it-0"))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, unknown.StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
            Assert.Equal(await DetailAsync(unknown), await DetailAsync(wrong));
        }

        using (var right = await SignInAsync(client, "ME", Password))
        {
            Assert.Equal(HttpStatusCode.OK, right.StatusCode);
        }

        using (var signedOut = await client.PostAsync(new Uri("/api/account/logout", UriKind.Relative), null, Token))
        {
            Assert.Equal(HttpStatusCode.NoContent, signedOut.StatusCode);
        }

        using (var gone = await client.GetAsync(new Uri("/api/account", UriKind.Relative), Token))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, gone.StatusCode);
        }

        for (var attempt = 0; attempt < 5; attempt++)
        {
            using var wrong = await SignInAsync(client, "me", "Not-it-0");
        }

        using var locked = await SignInAsync(client, "me", Password);
        Assert.Equal(HttpStatusCode.Unauthorized, locked.StatusCode);
        Assert.Contains("locked", await DetailAsync(locked), StringComparison.Ordinal);

        // Subsonic shares the lockout.
        Assert.Equal(40, (await SubsonicAsync(client, $"ping.view?u=me&p={Password}")).GetProperty("error").GetProperty("code").GetInt32());

        // Both ways: Subsonic's wrong passwords count, and its right one starts
        // the count again.
        await me.AddAccountAsync("you", Password, Token);
        for (var attempt = 0; attempt < 4; attempt++) await SubsonicAsync(client, "ping.view?u=you&p=Not-it-0");
        await SubsonicAsync(client, $"ping.view?u=you&p={Password}");
        await SubsonicAsync(client, "ping.view?u=you&p=Not-it-0");

        using (var counted = await SignInAsync(client, "you", Password))
        {
            Assert.Equal(HttpStatusCode.OK, counted.StatusCode);
        }

        for (var attempt = 0; attempt < 5; attempt++) await SubsonicAsync(client, "ping.view?u=you&p=Not-it-0");

        using var lockedByRest = await SignInAsync(client, "you", Password);
        Assert.Contains("locked", await DetailAsync(lockedByRest), StringComparison.Ordinal);
    }

    /// <summary>
    /// One password for both clients: a reset changes it for the web sign-in
    /// and for every Subsonic form at once.
    /// </summary>
    [Fact]
    public async Task APasswordIsTheSameOnTheWebAndOnSubsonicAndAResetChangesBoth()
    {
        using var me = (await _factory!.SetUpAsync("me", Password, Token)).Client;
        var you = await me.AddAccountAsync("you", Password, Token);

        using var client = _factory.CreateClient();
        Assert.Equal("ok", (await SubsonicAsync(client, $"ping.view?u=you&{TokenFor("A-test-passw0rd")}")).GetProperty("status").GetString());

        using var yourBrowser = _factory.CreateClient();
        (await SignInAsync(yourBrowser, "you", Password)).Dispose();

        using (var weak = await me.PostAsJsonAsync(new Uri($"/api/accounts/{you}/password", UriKind.Relative), new { password = "weak" }, Token))
        {
            Assert.Equal(HttpStatusCode.BadRequest, weak.StatusCode);
        }

        using (var reset = await me.PostAsJsonAsync(new Uri($"/api/accounts/{you}/password", UriKind.Relative), new { password = "N3w-password" }, Token))
        {
            Assert.Equal(HttpStatusCode.NoContent, reset.StatusCode);
        }

        // A reset ends the account's other sessions.
        using (var ended = await yourBrowser.GetAsync(new Uri("/api/account", UriKind.Relative), Token))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, ended.StatusCode);
        }

        Assert.Equal(40, (await SubsonicAsync(client, $"ping.view?u=you&p={Password}")).GetProperty("error").GetProperty("code").GetInt32());
        Assert.Equal("ok", (await SubsonicAsync(client, $"ping.view?u=you&{TokenFor("N3w-password")}")).GetProperty("status").GetString());
        Assert.Equal("ok", (await SubsonicAsync(client, "ping.view?u=you&p=N3w-password")).GetProperty("status").GetString());

        using (var old = await SignInAsync(client, "you", Password))
        using (var current = await SignInAsync(client, "you", "N3w-password"))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, old.StatusCode);
            Assert.Equal(HttpStatusCode.OK, current.StatusCode);
        }

        // Resetting one's own password keeps one signed in.
        using (var own = await me.PostAsJsonAsync(new Uri($"/api/accounts/{(await MeAsync(me))}/password", UriKind.Relative), new { password = "An0ther-one" }, Token))
        {
            Assert.Equal(HttpStatusCode.NoContent, own.StatusCode);
        }

        using var still = await me.GetAsync(new Uri("/api/account", UriKind.Relative), Token);
        Assert.Equal(HttpStatusCode.OK, still.StatusCode);
    }

    /// <summary>
    /// An API key signs in alone, names its account, and stops when revoked or
    /// when its account is removed; a key is only ever its own account's.
    /// </summary>
    [Fact]
    public async Task AnApiKeySignsInAloneUntilRevoked()
    {
        using var me = (await _factory!.SetUpAsync("me", Password, Token)).Client;
        using var client = _factory.CreateClient();

        var extensions = await SubsonicAsync(client, "getOpenSubsonicExtensions.view");
        Assert.Contains(
            extensions.GetProperty("openSubsonicExtensions").EnumerateArray(),
            extension => extension.GetProperty("name").GetString() == "apiKeyAuthentication");

        using (var unnamed = await me.PostAsJsonAsync(new Uri("/api/account/keys", UriKind.Relative), new { name = " " }, Token))
        {
            Assert.Equal(HttpStatusCode.BadRequest, unnamed.StatusCode);
        }

        var created = await (await me.PostAsJsonAsync(new Uri("/api/account/keys", UriKind.Relative), new { name = "phone" }, Token))
            .Content.ReadFromJsonAsync<JsonElement>(Token);
        var key = created.GetProperty("key").GetString()!;
        var id = created.GetProperty("id").GetGuid();

        var listed = Assert.Single((await me.GetFromJsonAsync<JsonElement>(new Uri("/api/account/keys", UriKind.Relative), Token)).EnumerateArray());
        Assert.Equal("phone", listed.GetProperty("name").GetString());
        Assert.False(listed.TryGetProperty("key", out _));

        Assert.Equal("ok", (await SubsonicAsync(client, $"ping.view?apiKey={key}")).GetProperty("status").GetString());
        Assert.Equal("me", (await SubsonicAsync(client, $"tokenInfo.view?apiKey={key}")).GetProperty("tokenInfo").GetProperty("username").GetString());

        // A key comes without u=, and its account still owns every playlist.
        Directory.CreateDirectory(Path.Combine(_root, "Bill Evans"));
        Assert.Equal("ok", (await SubsonicAsync(client, $"createPlaylist.view?apiKey={key}&name=Road")).GetProperty("status").GetString());
        var playlist = Assert.Single((await SubsonicAsync(client, $"getPlaylists.view?apiKey={key}")).GetProperty("playlists").GetProperty("playlist").EnumerateArray());
        Assert.Equal("me", playlist.GetProperty("owner").GetString());

        foreach (var beside in new[] { "u=me", "u=", "p=x", "t=x", "s=y", "t=x&s=y" })
        {
            Assert.Equal(43, (await SubsonicAsync(client, $"ping.view?apiKey={key}&{beside}")).GetProperty("error").GetProperty("code").GetInt32());
        }

        Assert.Equal(44, (await SubsonicAsync(client, "ping.view?apiKey=not-a-key")).GetProperty("error").GetProperty("code").GetInt32());

        // Somebody else cannot revoke it.
        var you = await me.AddAccountAsync("you", Password, Token);
        using (var other = _factory.CreateClient())
        {
            using var signedIn = await SignInAsync(other, "you", Password);
            Assert.Empty((await other.GetFromJsonAsync<JsonElement>(new Uri("/api/account/keys", UriKind.Relative), Token)).EnumerateArray());
            using var refused = await other.DeleteAsync(new Uri($"/api/account/keys/{id}", UriKind.Relative), Token);
            Assert.Equal(HttpStatusCode.NotFound, refused.StatusCode);
        }

        using (var revoked = await me.DeleteAsync(new Uri($"/api/account/keys/{id}", UriKind.Relative), Token))
        {
            Assert.Equal(HttpStatusCode.NoContent, revoked.StatusCode);
        }

        Assert.Equal(44, (await SubsonicAsync(client, $"ping.view?apiKey={key}")).GetProperty("error").GetProperty("code").GetInt32());

        // A removed account's keys go with it.
        using var yours = _factory.CreateClient();
        (await SignInAsync(yours, "you", Password)).Dispose();
        var theirs = (await (await yours.PostAsJsonAsync(new Uri("/api/account/keys", UriKind.Relative), new { name = "tablet" }, Token))
            .Content.ReadFromJsonAsync<JsonElement>(Token)).GetProperty("key").GetString()!;
        Assert.Equal("ok", (await SubsonicAsync(client, $"ping.view?apiKey={theirs}")).GetProperty("status").GetString());

        using (var removed = await me.DeleteAsync(new Uri($"/api/accounts/{you}", UriKind.Relative), Token))
        {
            Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
        }

        Assert.Equal(44, (await SubsonicAsync(client, $"ping.view?apiKey={theirs}")).GetProperty("error").GetProperty("code").GetInt32());

        await using var db = PostgresFixture.CreateContext(_connectionString);
        Assert.Equal(0, await db.ApiKeys.CountAsync(Token));
    }

    /// <summary>
    /// A heart is its account's: another account neither sees it nor clears
    /// it, the hearts from before accounts are the first account's, and a
    /// removed account's hearts go with it.
    /// </summary>
    [Fact]
    public async Task HeartsAreEachAccountsOwn()
    {
        await using (var db = PostgresFixture.CreateContext(_connectionString))
        {
            var rec = new Recording { Id = RecordingId.New(), Title = "My Foolish Heart" };
            var artist = new Artist { Id = ArtistId.New(), Name = "Bill Evans" };
            db.Recordings.Add(rec);
            db.Artists.Add(artist);
            db.MediaFiles.Add(new MediaFile
            {
                Id = MediaFileId.New(),
                Path = "Bill Evans/01 My Foolish Heart.flac",
                SizeBytes = 1,
                LastModifiedUtc = DateTimeOffset.Parse("2026-01-01T00:00:00Z", CultureInfo.InvariantCulture),
                RecordingId = rec.Id,
            });

            // Given before there were accounts.
            db.Loves.Add(new Love { Id = Guid.CreateVersion7(), ArtistId = artist.Id, LovedUtc = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync(Token);
        }

        var setUp = await _factory!.SetUpAsync("me", Password, Token);
        using var me = setUp.Client;
        var you = await me.AddAccountAsync("you", Password, Token);
        using var client = _factory.CreateClient();

        Assert.Single(Starred(await SubsonicAsync(client, $"getStarred2.view?u=me&p={Password}"), "artist"));
        Assert.Empty(Starred(await SubsonicAsync(client, $"getStarred2.view?u=you&p={Password}"), "artist"));

        // getUser answers for any account, and only for one.
        Assert.Equal("you", (await SubsonicAsync(client, $"getUser.view?u=me&p={Password}&username=you")).GetProperty("user").GetProperty("username").GetString());
        Assert.Equal(70, (await SubsonicAsync(client, $"getUser.view?u=me&p={Password}&username=nobody")).GetProperty("error").GetProperty("code").GetInt32());

        string song;
        await using (var db = PostgresFixture.CreateContext(_connectionString))
        {
            song = $"tr-{(await db.MediaFiles.SingleAsync(Token)).Id.Value:N}";
        }

        Assert.Equal("ok", (await SubsonicAsync(client, $"star.view?u=you&p={Password}&id={song}")).GetProperty("status").GetString());
        Assert.Empty(Starred(await SubsonicAsync(client, $"getStarred2.view?u=me&p={Password}"), "song"));
        Assert.Single(Starred(await SubsonicAsync(client, $"getStarred2.view?u=you&p={Password}"), "song"));

        Assert.Equal("ok", (await SubsonicAsync(client, $"star.view?u=me&p={Password}&id={song}")).GetProperty("status").GetString());
        Assert.Equal("ok", (await SubsonicAsync(client, $"unstar.view?u=me&p={Password}&id={song}")).GetProperty("status").GetString());
        Assert.Single(Starred(await SubsonicAsync(client, $"getStarred2.view?u=you&p={Password}"), "song"));
        Assert.False((await SubsonicAsync(client, $"getSong.view?u=me&p={Password}&id={song}")).GetProperty("song").TryGetProperty("starred", out _));
        Assert.True((await SubsonicAsync(client, $"getSong.view?u=you&p={Password}&id={song}")).GetProperty("song").TryGetProperty("starred", out _));

        using (var removed = await me.DeleteAsync(new Uri($"/api/accounts/{you}", UriKind.Relative), Token))
        {
            Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
        }

        await using var check = PostgresFixture.CreateContext(_connectionString);
        var left = await check.Loves.IgnoreQueryFilters().ToListAsync(Token);
        Assert.Equal(setUp.Id, Assert.Single(left).AccountId);

        // The last account stays, or the next visit would make one.
        using var last = await me.DeleteAsync(new Uri($"/api/accounts/{setUp.Id}", UriKind.Relative), Token);
        Assert.Equal(HttpStatusCode.Conflict, last.StatusCode);
    }

    /// <summary>
    /// Two accounts removing each other at once leave one, however the two
    /// requests interleave: the last account is never removed.
    /// </summary>
    [Fact]
    public async Task TwoAccountsRemovingEachOtherAtOnceLeaveOne()
    {
        var setUp = await _factory!.SetUpAsync("me", Password, Token);
        var clients = new Dictionary<Guid, HttpClient> { [setUp.Id] = setUp.Client };

        try
        {
            for (var round = 0; round < 3; round++)
            {
                var (keeperId, keeper) = clients.Single();
                var name = $"other{round}";
                var otherId = await keeper.AddAccountAsync(name, Password, Token);
                var other = _factory.CreateClient();
                clients[otherId] = other;
                (await SignInAsync(other, name, Password)).Dispose();

                var removals = await Task.WhenAll(
                    keeper.DeleteAsync(new Uri($"/api/accounts/{otherId}", UriKind.Relative), Token),
                    other.DeleteAsync(new Uri($"/api/accounts/{keeperId}", UriKind.Relative), Token));
                foreach (var removal in removals) removal.Dispose();

                await using var db = PostgresFixture.CreateContext(_connectionString);
                var left = Assert.Single(await db.Users.Select(account => account.Id).ToListAsync(Token));

                foreach (var gone in clients.Keys.Where(id => id != left).ToList())
                {
                    clients[gone].Dispose();
                    clients.Remove(gone);
                }
            }
        }
        finally
        {
            foreach (var client in clients.Values) client.Dispose();
        }
    }

    private static Task<HttpResponseMessage> SignInAsync(HttpClient client, string name, string password) =>
        client.PostAsJsonAsync(new Uri("/api/account/login", UriKind.Relative), new { name, password }, Token);

    private static async Task<string> DetailAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>(Token)).GetProperty("detail").GetString()!;

    private static async Task<Guid> MeAsync(HttpClient signedIn) =>
        (await signedIn.GetFromJsonAsync<JsonElement>(new Uri("/api/account", UriKind.Relative), Token)).GetProperty("id").GetGuid();

    private static async Task<JsonElement> SubsonicAsync(HttpClient client, string route)
    {
        using var response = await client.GetAsync(new Uri($"/rest/{route}&v=1.16.1&c=test&f=json".Replace(".view&", ".view?", StringComparison.Ordinal), UriKind.Relative), Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(Token)).RootElement.GetProperty("subsonic-response").Clone();
    }

    private static IEnumerable<JsonElement> Starred(JsonElement response, string kind) =>
        response.GetProperty("starred2").TryGetProperty(kind, out var list) ? list.EnumerateArray() : [];

#pragma warning disable CA5351 // the protocol's MD5
    private static string TokenFor(string password) =>
        $"t={Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes(password + "s4lt")))}&s=s4lt";
#pragma warning restore CA5351
}
