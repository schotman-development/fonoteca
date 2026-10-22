using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Fonoteca.Data;
using Fonoteca.Domain.Catalogue;
using Fonoteca.Fixtures;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
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
    // Sixteen lower-case letters: the shortest password there is, and all it needs.
    private const string Password = "sixteenlowercase";

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

        using (var weak = await stranger.PostAsJsonAsync(new Uri("/api/account/setup", UriKind.Relative), new { name = "me", password = new string('a', 15) }, Token))
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
        Assert.Equal("ok", (await SubsonicAsync(client, $"ping.view?u=you&{TokenFor(Password)}")).GetProperty("status").GetString());

        using var yourBrowser = _factory.CreateClient();
        (await SignInAsync(yourBrowser, "you", Password)).Dispose();

        using (var weak = await me.PostAsJsonAsync(new Uri($"/api/accounts/{you}/password", UriKind.Relative), new { password = "weak" }, Token))
        {
            Assert.Equal(HttpStatusCode.BadRequest, weak.StatusCode);
        }

        using (var reset = await me.PostAsJsonAsync(new Uri($"/api/accounts/{you}/password", UriKind.Relative), new { password = "a-new-passphrase" }, Token))
        {
            Assert.Equal(HttpStatusCode.NoContent, reset.StatusCode);
        }

        // A reset ends the account's other sessions.
        using (var ended = await yourBrowser.GetAsync(new Uri("/api/account", UriKind.Relative), Token))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, ended.StatusCode);
        }

        Assert.Equal(40, (await SubsonicAsync(client, $"ping.view?u=you&p={Password}")).GetProperty("error").GetProperty("code").GetInt32());
        Assert.Equal("ok", (await SubsonicAsync(client, $"ping.view?u=you&{TokenFor("a-new-passphrase")}")).GetProperty("status").GetString());
        Assert.Equal("ok", (await SubsonicAsync(client, "ping.view?u=you&p=a-new-passphrase")).GetProperty("status").GetString());

        using (var old = await SignInAsync(client, "you", Password))
        using (var current = await SignInAsync(client, "you", "a-new-passphrase"))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, old.StatusCode);
            Assert.Equal(HttpStatusCode.OK, current.StatusCode);
        }

        // Resetting one's own password keeps one signed in.
        using (var own = await me.PostAsJsonAsync(new Uri($"/api/accounts/{(await MeAsync(me))}/password", UriKind.Relative), new { password = "another-passphrase" }, Token))
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

        // An admin's getUser answers for any account, and only for one.
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

        // The last admin stays, or nobody could change an account again.
        using var last = await me.DeleteAsync(new Uri($"/api/accounts/{setUp.Id}", UriKind.Relative), Token);
        Assert.Equal(HttpStatusCode.Conflict, last.StatusCode);
    }

    /// <summary>
    /// Two admins removing each other at once leave one, however the two
    /// requests interleave: the last admin is never removed.
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
                var otherId = await keeper.AddAccountAsync(name, Password, Token, "Admin");
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

    /// <summary>
    /// The first account holds every role this server does, a new one
    /// OpenSubsonic's defaults; each role binds on the web, and one given or
    /// taken reaches a signed-in browser without signing it out. The last admin
    /// keeps the role.
    /// </summary>
    [Fact]
    public async Task RolesBindOnTheWebAndAChangeReachesASignedInBrowser()
    {
        // The migration gives every account from before roles this number.
        Assert.Equal(319, (int)Account.Everything);

        var setUp = await _factory!.SetUpAsync("me", Password, Token);
        using var me = setUp.Client;
        Assert.Equal(
            ["Admin", "Settings", "Download", "Upload", "Playlist", "CoverArt", "Stream"],
            Roles(await me.GetFromJsonAsync<JsonElement>(new Uri("/api/account", UriKind.Relative), Token)));

        var you = await me.AddAccountAsync("you", Password, Token);
        using var yours = _factory.CreateClient();
        (await SignInAsync(yours, "you", Password)).Dispose();

        var yourAccount = await yours.GetFromJsonAsync<JsonElement>(new Uri("/api/account", UriKind.Relative), Token);
        Assert.Equal(["Settings", "Stream"], Roles(yourAccount));

        foreach (var (method, route) in new[]
        {
            ("GET", "/api/accounts"),
            ("POST", $"/api/accounts/{you}"),
            ("POST", "/api/library/scan"),
            ("POST", "/api/files/trash"),
            ("POST", "/api/files/upload?path=x.flac"),
            ("POST", "/api/catalogue/folders/tags"),
            ("POST", "/api/qobuz/albums/1/download"),
        })
        {
            using var request = new HttpRequestMessage(new HttpMethod(method), new Uri(route, UriKind.Relative));
            using var refused = await yours.SendAsync(request, Token);
            Assert.True(refused.StatusCode == HttpStatusCode.Forbidden, $"{method} {route}: {refused.StatusCode}");
        }

        // Settings is one's own password.
        using (var own = await yours.PostAsJsonAsync(new Uri("/api/account/password", UriKind.Relative), new { password = "a-new-passphrase" }, Token))
        {
            Assert.Equal(HttpStatusCode.NoContent, own.StatusCode);
        }

        // Stream is the bytes of a song; a cover is anybody's.
        await File.WriteAllBytesAsync(Path.Combine(_root, "01 Song.flac"), [0], Token);
        await File.WriteAllBytesAsync(Path.Combine(_root, "cover.jpg"), [0], Token);
        Assert.Equal(HttpStatusCode.OK, await ContentStatusAsync(yours, "01 Song.flac"));

        using (var taken = await me.PostAsJsonAsync(new Uri($"/api/accounts/{you}", UriKind.Relative), new { roles = new[] { "Settings" }, email = "you@example.org" }, Token))
        {
            Assert.Equal(HttpStatusCode.OK, taken.StatusCode);
            var changed = await taken.Content.ReadFromJsonAsync<JsonElement>(Token);
            Assert.Equal(["Settings"], Roles(changed));
            Assert.Equal("you@example.org", changed.GetProperty("email").GetString());
        }

        Assert.Equal(HttpStatusCode.Forbidden, await ContentStatusAsync(yours, "01 Song.flac"));
        Assert.Equal(HttpStatusCode.OK, await ContentStatusAsync(yours, "cover.jpg"));

        using (var given = await me.PostAsJsonAsync(new Uri($"/api/accounts/{you}", UriKind.Relative), new { roles = new[] { "Admin", "Download" } }, Token))
        {
            Assert.Equal(HttpStatusCode.OK, given.StatusCode);
        }

        Assert.Equal(HttpStatusCode.OK, await ContentStatusAsync(yours, "01 Song.flac"));

        using (var listed = await yours.GetAsync(new Uri("/api/accounts", UriKind.Relative), Token))
        {
            Assert.Equal(HttpStatusCode.OK, listed.StatusCode);
        }

        // An admin without settings still sets its own password; with neither, no password and no keys.
        using (var asAdmin = await yours.PostAsJsonAsync(new Uri("/api/account/password", UriKind.Relative), new { password = "a-third-passphrase" }, Token))
        {
            Assert.Equal(HttpStatusCode.NoContent, asAdmin.StatusCode);
        }

        using (var demoted = await me.PostAsJsonAsync(new Uri($"/api/accounts/{you}", UriKind.Relative), new { roles = Array.Empty<string>() }, Token))
        {
            Assert.Equal(HttpStatusCode.OK, demoted.StatusCode);
        }

        using (var noPassword = await yours.PostAsJsonAsync(new Uri("/api/account/password", UriKind.Relative), new { password = "a-fourth-passphrase" }, Token))
        using (var noKey = await yours.PostAsJsonAsync(new Uri("/api/account/keys", UriKind.Relative), new { name = "phone" }, Token))
        {
            Assert.Equal(HttpStatusCode.Forbidden, noPassword.StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, noKey.StatusCode);
        }

        // The last admin keeps the role.
        using var last = await me.PostAsJsonAsync(new Uri($"/api/accounts/{setUp.Id}", UriKind.Relative), new { roles = new[] { "Settings" } }, Token);
        Assert.Equal(HttpStatusCode.Conflict, last.StatusCode);
        Assert.Contains("Admin", Roles(await me.GetFromJsonAsync<JsonElement>(new Uri("/api/account", UriKind.Relative), Token)));
    }

    /// <summary>
    /// OpenSubsonic's user management: an admin reads, makes, changes and
    /// removes accounts; anybody else reads only their own, changes only their
    /// own password, and is refused what their roles do not give.
    /// </summary>
    [Fact]
    public async Task SubsonicUserManagementFollowsTheRoles()
    {
        using var me = (await _factory!.SetUpAsync("me", Password, Token)).Client;
        using var client = _factory.CreateClient();
        var asMe = $"u=me&p={Password}";

        var mine = (await SubsonicAsync(client, $"getUser.view?{asMe}&username=me")).GetProperty("user");
        Assert.True(mine.GetProperty("adminRole").GetBoolean());
        Assert.True(mine.GetProperty("coverArtRole").GetBoolean());
        Assert.False(mine.GetProperty("jukeboxRole").GetBoolean());
        Assert.False(mine.GetProperty("scrobblingEnabled").GetBoolean());
        Assert.Equal(0, Assert.Single(mine.GetProperty("folder").EnumerateArray()).GetInt32());

        Assert.Equal(10, ErrorCode(await SubsonicAsync(client, $"createUser.view?{asMe}&username=you&password=a-test-passphrase")));
        Assert.Equal(0, ErrorCode(await SubsonicAsync(client, $"createUser.view?{asMe}&username=you&password=short&email=you@example.org")));

        var hex = Convert.ToHexStringLower(Encoding.UTF8.GetBytes("a-test-passphrase"));
        Assert.Equal("ok", Status(await SubsonicAsync(client, $"createUser.view?{asMe}&username=you&password=enc:{hex}&email=you@example.org")));

        var yours = (await SubsonicAsync(client, $"getUser.view?{asMe}&username=you")).GetProperty("user");
        Assert.False(yours.GetProperty("adminRole").GetBoolean());
        Assert.True(yours.GetProperty("settingsRole").GetBoolean());
        Assert.True(yours.GetProperty("streamRole").GetBoolean());
        Assert.False(yours.GetProperty("downloadRole").GetBoolean());
        Assert.Equal("you@example.org", yours.GetProperty("email").GetString());

        Assert.Equal(2, (await SubsonicAsync(client, $"getUsers.view?{asMe}")).GetProperty("users").GetProperty("user").GetArrayLength());

        var asYou = "u=you&p=a-test-passphrase";
        Assert.Equal("you", (await SubsonicAsync(client, $"getUser.view?{asYou}&username=you")).GetProperty("user").GetProperty("username").GetString());

        foreach (var refused in new[] { "getUsers.view?", "getUser.view?username=me&", "startScan.view?", "download.view?id=x&", "deleteUser.view?username=me&", "changePassword.view?username=me&password=a-new-passphrase&", "createUser.view?username=them&password=a-test-passphrase&email=x&", "updateUser.view?username=you&adminRole=true&" })
        {
            Assert.True(50 == ErrorCode(await SubsonicAsync(client, $"{refused}{asYou}")), refused);
        }

        // Without settings or admin, not even one's own password.
        Assert.Equal("ok", Status(await SubsonicAsync(client, $"createUser.view?{asMe}&username=them&password=a-test-passphrase&email=them@example.org&settingsRole=false")));
        Assert.Equal(50, ErrorCode(await SubsonicAsync(client, "changePassword.view?u=them&p=a-test-passphrase&username=them&password=a-new-passphrase")));

        // Stream is a default role: past the check, to the song that is not there.
        Assert.Equal(70, ErrorCode(await SubsonicAsync(client, $"stream.view?{asYou}&id=x")));

        Assert.Equal("ok", Status(await SubsonicAsync(client, $"changePassword.view?{asYou}&username=you&password=a-new-passphrase")));
        asYou = "u=you&p=a-new-passphrase";
        Assert.Equal("ok", Status(await SubsonicAsync(client, $"ping.view?{asYou}")));

        Assert.Equal("ok", Status(await SubsonicAsync(client, $"updateUser.view?{asMe}&username=you&downloadRole=true&maxBitRate=320")));
        yours = (await SubsonicAsync(client, $"getUser.view?{asMe}&username=you")).GetProperty("user");
        Assert.True(yours.GetProperty("downloadRole").GetBoolean());
        Assert.True(yours.GetProperty("streamRole").GetBoolean());
        Assert.Equal(320, yours.GetProperty("maxBitRate").GetInt32());
        Assert.Equal(70, ErrorCode(await SubsonicAsync(client, $"download.view?{asYou}&id=x")));

        // The last admin keeps the role, and a refused update changes nothing, the password included.
        Assert.Equal(0, ErrorCode(await SubsonicAsync(client, $"updateUser.view?{asMe}&username=me&adminRole=false&password=a-changed-passphrase")));
        Assert.Equal("ok", Status(await SubsonicAsync(client, $"ping.view?{asMe}")));
        Assert.True((await SubsonicAsync(client, $"getUser.view?{asMe}")).GetProperty("user").GetProperty("adminRole").GetBoolean());

        Assert.Equal("ok", Status(await SubsonicAsync(client, $"changePassword.view?{asMe}&username=you&password=an-admins-passphrase")));
        Assert.Equal("ok", Status(await SubsonicAsync(client, "ping.view?u=you&p=an-admins-passphrase")));

        Assert.Equal("ok", Status(await SubsonicAsync(client, $"deleteUser.view?{asMe}&username=you")));
        Assert.Equal(70, ErrorCode(await SubsonicAsync(client, $"getUser.view?{asMe}&username=you")));
        Assert.Equal(0, ErrorCode(await SubsonicAsync(client, $"deleteUser.view?{asMe}&username=me")));
    }

    /// <remarks>
    /// The whole map of what changes something on the web and which roles may,
    /// so a role taken off a route, or a new route that changes something and
    /// names none, fails here rather than in a person's browser.
    /// </remarks>
    [Fact]
    public void EveryRouteThatChangesSomethingNamesItsRoles()
    {
        string[] expected =
        [
            "DELETE /api/account/keys/{id:guid} Admin|Settings",
            "DELETE /api/accounts/{id:guid} Admin",
            "DELETE /api/catalogue/artists/{id:guid}/banner CoverArt",
            "DELETE /api/catalogue/artists/{id:guid}/portrait CoverArt",
            "DELETE /api/catalogue/playlists/{file} -",
            "DELETE /api/catalogue/smart-playlists/{id:guid} -",
            "DELETE /api/library/analyse Admin",
            "DELETE /api/library/attribute Admin",
            "DELETE /api/library/enrich Admin",
            "DELETE /api/library/identify Admin",
            "DELETE /api/library/probe Admin",
            "DELETE /api/library/tags Admin",
            "POST /api/account/keys Admin|Settings",
            "POST /api/account/login -",
            "POST /api/account/logout -",
            "POST /api/account/password Admin|Settings",
            "POST /api/account/setup -",
            "POST /api/accounts/ Admin",
            "POST /api/accounts/{id:guid} Admin",
            "POST /api/accounts/{id:guid}/password Admin",
            "POST /api/catalogue/albums/{id:guid}/fingerprints Admin",
            "POST /api/catalogue/albums/{id:guid}/tags Admin",
            "POST /api/catalogue/artists/follow Admin",
            "POST /api/catalogue/artists/{id:guid}/banner CoverArt",
            "POST /api/catalogue/artists/{id:guid}/edits CoverArt",
            "POST /api/catalogue/artists/{id:guid}/follow Admin",
            "POST /api/catalogue/artists/{id:guid}/portrait CoverArt",
            "POST /api/catalogue/artists/{id:guid}/tags Admin",
            "POST /api/catalogue/discovered/{id:guid}/monitor Admin",
            "POST /api/catalogue/folders/tags Admin",
            "POST /api/catalogue/folders/undo Admin",
            "POST /api/catalogue/matching/components/{stamp}/decision Admin",
            "POST /api/catalogue/matching/files/album Admin",
            "POST /api/catalogue/matching/files/release Admin",
            "POST /api/catalogue/matching/folders/album Admin",
            "POST /api/catalogue/matching/folders/reopen Admin",
            "POST /api/catalogue/matching/folders/unreleased Admin",
            "POST /api/catalogue/matching/recordings/{id:guid}/decision Admin",
            "POST /api/catalogue/playlists -",
            "POST /api/catalogue/playlists/{file} -",
            "POST /api/catalogue/playlists/{file}/songs -",
            "POST /api/catalogue/release-groups/{id:guid}/monitor Admin",
            "POST /api/catalogue/releases/{id:guid}/cover CoverArt",
            "POST /api/catalogue/releases/{id:guid}/cover/archive/{imageId:long} CoverArt",
            "POST /api/catalogue/releases/{id:guid}/edits CoverArt",
            "POST /api/catalogue/smart-playlists -",
            "POST /api/catalogue/smart-playlists/preview -",
            "POST /api/catalogue/smart-playlists/{id:guid} -",
            "POST /api/files/move Admin",
            "POST /api/files/trash Admin",
            "POST /api/files/upload Upload",
            "POST /api/library/analyse Admin",
            "POST /api/library/attribute Admin",
            "POST /api/library/enrich Admin",
            "POST /api/library/identify Admin",
            "POST /api/library/probe Admin",
            "POST /api/library/scan Admin",
            "POST /api/library/tags Admin",
            "POST /api/qobuz/albums/{albumId}/download Admin",
            "POST /api/qobuz/albums/{albumId}/upgrade Admin",
        ];

        var actual = _factory!.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Where(endpoint => endpoint.RoutePattern.RawText!.StartsWith("/api/", StringComparison.Ordinal))
            .SelectMany(endpoint => (endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? [])
                .Where(method => method != HttpMethods.Get && method != HttpMethods.Head)
                .Select(method =>
                {
                    var roles = endpoint.Metadata.GetOrderedMetadata<AuthorizationPolicy>()
                        .SelectMany(policy => policy.Requirements.OfType<RolesAuthorizationRequirement>())
                        .SelectMany(requirement => requirement.AllowedRoles)
                        .Order(StringComparer.Ordinal);
                    return $"{method} {endpoint.RoutePattern.RawText} {string.Join("|", roles.DefaultIfEmpty("-"))}";
                }))
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(expected, actual);
    }

    private static string[] Roles(JsonElement account) =>
        [.. account.GetProperty("roles").EnumerateArray().Select(role => role.GetString()!)];

    private static async Task<HttpStatusCode> ContentStatusAsync(HttpClient client, string path)
    {
        using var response = await client.GetAsync(new Uri($"/api/files/content?path={Uri.EscapeDataString(path)}", UriKind.Relative), Token);
        return response.StatusCode;
    }

    private static int ErrorCode(JsonElement response) => response.GetProperty("error").GetProperty("code").GetInt32();

    private static string? Status(JsonElement response) => response.GetProperty("status").GetString();

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
