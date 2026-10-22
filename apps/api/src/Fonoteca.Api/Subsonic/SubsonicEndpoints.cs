using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Fonoteca.Api.Configuration;
using Fonoteca.Api.Endpoints;
using Fonoteca.Api.Library;
using Fonoteca.Data;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using Microsoft.Net.Http.Headers;

namespace Fonoteca.Api.Subsonic;

/// <summary>
/// The library as somebody else's music client sees it.
/// </summary>
/// <remarks>
/// OpenSubsonic, under <c>/rest</c>. ADR 0012 is the design record and the
/// argument; what follows is what a person changing this file has to know.
///
/// <b>The protocol's two browsing modes are this application's two sources of
/// truth.</b> <c>getMusicFolders</c>/<c>getIndexes</c>/<c>getMusicDirectory</c>
/// are answered from the disk and <c>getArtists</c>/<c>getArtist</c>/
/// <c>getAlbum</c>/<c>getAlbumList2</c>/<c>search3</c> from the catalogue —
/// <i>what is there</i> and <i>what it means</i>, which is the same split the
/// file manager already states. Implementing both is cheaper than implementing
/// either one well, because neither then needs a fallback: nothing here has to
/// invent an album for a file no pass has filed, and no file is unreachable.
///
/// <b>This layer holds no rules.</b> Every judgement it needs already exists
/// somewhere and is called rather than copied: containment and media types come
/// from <c>FileEndpoints.GetContent</c>, a billing line from
/// <c>CatalogueEndpoints.CreditLine</c>, a person's corrections from
/// <c>PersonEdits</c>, the cover from <c>CatalogueEndpoints.GetReleaseCover</c>.
/// What is written here is projections and the shape they go out in. A rule
/// appearing in this directory is the drift the rest of the codebase keeps
/// warning about.
///
/// <b>Where one recording has five files, a client sees five songs.</b>
/// <c>AudioQuality.Compare</c> could pick a representative and deliberately does
/// not. Subsonic cannot express "the same performance in five encodings", so
/// collapsing them would not be showing the truth — it would be hiding the
/// defect this application exists to remove, in the one place its owner would
/// have noticed it.
///
/// <b>Every response is HTTP 200, including every refusal</b> — see
/// <see cref="SubsonicResult"/>. Parameters are read off the query string by
/// hand rather than bound, because a bound parameter that fails to parse is a
/// 400 with a problem document, and a client reading that reports the server as
/// unreachable instead of showing the error the protocol has a code for.
/// </remarks>
public static partial class SubsonicEndpoints
{
    internal const string Route = "/rest";

    /// <summary>
    /// The credentials check, as middleware in front of <see cref="Route"/>.
    /// </summary>
    /// <remarks>
    /// The same shape as <c>LibraryTools.Guard</c> and for the same reason:
    /// middleware, so it runs before anything mapped under the route whatever
    /// those routes turn out to be. Here there is a second reason —
    /// <c>/rest/stream</c> answers with bytes and <c>/rest/ping</c> answers with
    /// XML, so a refusal has to be decided before a handler has picked a format.
    ///
    /// <b>An account signs in here with its web name and password</b> (ADR
    /// 0018, the owner's choice), in all three of the protocol's forms — <c>p</c>,
    /// <c>p=enc:</c> and <c>t</c>+<c>s</c> — or with an API key
    /// (<c>apiKeyAuthentication</c>), which must come alone: any of
    /// <c>u</c>, <c>p</c>, <c>t</c>, <c>s</c> beside it is error 43, the
    /// spec's stricter reading. Both password forms are checked against
    /// <see cref="Account.SubsonicPassword"/>, the protected copy the token
    /// form needs anyway, rather than Identity's hash, which is slow on purpose
    /// and would be paid on every cover a client asks for. A wrong password
    /// counts towards Identity's lockout as the web sign-in's does, and a
    /// locked account is refused as a wrong password. A signed-in request acts
    /// as that account: <c>context.User</c> for the journal, and
    /// <c>FonotecaDbContext.AccountId</c> for whose hearts it sees.
    ///
    /// <b>A form body is read as the query</b> (OpenSubsonic's <c>formPost</c>):
    /// a <c>POST</c> of <c>application/x-www-form-urlencoded</c> has its
    /// arguments joined to the query string's before the check, a key in the
    /// body replacing the same key in the URL, so the password can stay out of
    /// the URL and every handler finds the arguments where it always has. Any
    /// other body is not read.
    /// </remarks>
    internal static Task Guard(HttpContext context, RequestDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        if (!context.Request.Path.StartsWithSegments(Route)) return next(context);

        // The one endpoint OpenSubsonic requires to be reachable without
        // credentials, because it is how a client discovers what the server can
        // do before it has any. It names capabilities and nothing about the
        // library.
        //
        // Matched as the whole method name rather than as a substring of the
        // path. Every route under /rest is a literal today, so a substring could
        // not be reached by anything else — but a catch-all lives here now, and
        // the day a parameterised route joins it a substring test is an auth
        // bypass that nothing would fail on.
        return IsFormPost(context.Request) ? AdmitFormAsync(context, next) : AdmitAsync(context, next);
    }

    private static bool IsFormPost(HttpRequest request) =>
        HttpMethods.IsPost(request.Method)
        && MediaTypeHeaderValue.TryParse(request.ContentType, out var type)
        && type.MediaType.Equals("application/x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase);

    private static async Task AdmitFormAsync(HttpContext context, RequestDelegate next)
    {
        IFormCollection form;

        try
        {
            form = await context.Request.ReadFormAsync(context.RequestAborted).ConfigureAwait(false);
        }
        catch (InvalidDataException)
        {
            // Past the form limits: refused in the envelope, as every answer is.
            await SubsonicResult.Error(0, "The form body could not be read.").ExecuteAsync(context).ConfigureAwait(false);
            return;
        }

        var arguments = new Dictionary<string, StringValues>(context.Request.Query, StringComparer.OrdinalIgnoreCase);

        foreach (var (key, value) in form)
        {
            arguments[key] = value;
        }

        context.Request.Query = new QueryCollection(arguments);

        await AdmitAsync(context, next).ConfigureAwait(false);
    }

    private static async Task AdmitAsync(HttpContext context, RequestDelegate next)
    {
        if (MethodOf(context.Request.Path) != "getOpenSubsonicExtensions"
            && await SignInAsync(context).ConfigureAwait(false) is { } refusal)
        {
            await refusal.ExecuteAsync(context).ConfigureAwait(false);
            return;
        }

        await next(context).ConfigureAwait(false);
    }

    /// <summary>What an API key may not come with, even empty.</summary>
    private static readonly string[] PasswordParameters = ["u", "p", "t", "s"];

    /// <summary>Signs the request in as an account, or answers why not.</summary>
    private static async Task<SubsonicResult?> SignInAsync(HttpContext context)
    {
        var query = context.Request.Query;
        var users = context.RequestServices.GetRequiredService<UserManager<Account>>();
        var db = context.RequestServices.GetRequiredService<FonotecaDbContext>();
        Account? account;

        if (query.ContainsKey("apiKey"))
        {
            if (PasswordParameters.Any(query.ContainsKey))
            {
                return SubsonicResult.Error(43, "Multiple conflicting authentication mechanisms provided.");
            }

            var hash = AccountEndpoints.HashOf(query["apiKey"].ToString());

            var owner = await db.ApiKeys
                .Where(key => key.Hash == hash)
                .Select(key => (Guid?)key.AccountId)
                .FirstOrDefaultAsync(context.RequestAborted)
                .ConfigureAwait(false);

            account = owner is { } id ? await users.FindByIdAsync(id.ToString()).ConfigureAwait(false) : null;

            if (account is null) return SubsonicResult.Error(44, "Invalid API key.");
        }
        else
        {
            // No name is a wrong one: a client that sent no credentials at all
            // is told what a client with the wrong ones is.
            var name = query["u"].ToString();

            account = name.Length == 0 ? null : await users.FindByNameAsync(name).ConfigureAwait(false);

            if (account is null || await users.IsLockedOutAsync(account).ConfigureAwait(false))
            {
                return WrongPassword();
            }

            var password = AccountEndpoints.SubsonicPassword(
                context.RequestServices.GetRequiredService<IDataProtectionProvider>(), account);

            if (password is null || !Authenticated(query, password))
            {
                await users.AccessFailedAsync(account).ConfigureAwait(false);
                return WrongPassword();
            }

            if (account.AccessFailedCount > 0) await users.ResetAccessFailedCountAsync(account).ConfigureAwait(false);
        }

        db.AccountId = account.Id;
        context.User = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, account.Id.ToString()),
                new Claim(ClaimTypes.Name, account.UserName!),
                .. AccountEndpoints.RoleClaims(account.Roles),
            ],
            "Subsonic"));

        return null;

        static SubsonicResult WrongPassword() => SubsonicResult.Error(40, "Wrong username or password.");
    }

    public static IEndpointRouteBuilder MapSubsonicEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        // Not part of the contract packages/api-client is generated from. The
        // precedent is MapMcp: without this every build rewrites openapi.json,
        // regenerates the TypeScript client and turns gen:api:check red.
        //
        // Any origin, and no credentials: a Subsonic client is another
        // program, often a web page served from somewhere else, and its
        // password travels in the request, in the query or a form body, so a
        // browser has nothing of the owner's to attach. Navidrome answers the
        // same. The web UI's policy, credentialed for SignalR, stays on
        // everything else. Anonymous to the authorization middleware, because
        // the guard has signed the request in by then, or refused it.
        var group = app.MapGroup(Route)
            .ExcludeFromDescription()
            .AllowAnonymous()
            .RequireCors(policy => policy.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod());

        Map(group, "ping", Ping);
        Map(group, "getLicense", GetLicense);
        Map(group, "getOpenSubsonicExtensions", GetOpenSubsonicExtensions);

        Map(group, "getMusicFolders", GetMusicFolders);
        Map(group, "getIndexes", GetIndexes);
        Map(group, "getMusicDirectory", GetMusicDirectory);

        Map(group, "getArtists", GetArtists);
        Map(group, "getArtist", GetArtist);
        Map(group, "getAlbum", GetAlbum);
        Map(group, "getArtistInfo2", GetArtistInfo2);
        Map(group, "getAlbumInfo2", GetAlbumInfo2);
        Map(group, "getTopSongs", GetTopSongs);
        Map(group, "getAlbumList2", GetAlbumList2);
        Map(group, "getSong", GetSong);
        Map(group, "getLyrics", GetLyrics);
        Map(group, "getLyricsBySongId", GetLyricsBySongId);
        Map(group, "search3", Search3);
        Map(group, "getGenres", GetGenres);
        Map(group, "getSongsByGenre", GetSongsByGenre);
        Map(group, "getComposers", GetComposers);
        Map(group, "getCompositions", GetCompositions);
        Map(group, "star", Star);
        Map(group, "unstar", Unstar);
        Map(group, "getStarred2", GetStarred2);

        Map(group, "stream", Stream);
        Map(group, "download", Download);
        Map(group, "getCoverArt", GetCoverArt);
        Map(group, "getAlbumMotion", GetAlbumMotion);

        Map(group, "getUser", GetUser);
        Map(group, "getUsers", GetUsers);
        Map(group, "createUser", CreateUser);
        Map(group, "updateUser", UpdateUser);
        Map(group, "deleteUser", DeleteUser);
        Map(group, "changePassword", ChangePassword);
        Map(group, "tokenInfo", TokenInfo);
        Map(group, "getPlaylists", GetPlaylists);
        Map(group, "getPlaylist", GetPlaylist);
        Map(group, "createPlaylist", CreatePlaylist);
        Map(group, "updatePlaylist", UpdatePlaylist);
        Map(group, "deletePlaylist", DeletePlaylist);
        Map(group, "scrobble", Scrobble);
        Map(group, "getScanStatus", GetScanStatus);
        Map(group, "startScan", StartScan);
        Map(group, "getInternetRadioStations", GetInternetRadioStations);
        Map(group, "createInternetRadioStation", CreateInternetRadioStation);
        Map(group, "updateInternetRadioStation", UpdateInternetRadioStation);
        Map(group, "deleteInternetRadioStation", DeleteInternetRadioStation);

        // Anything else, in the protocol's own envelope. A catch-all rather than
        // a 404, because several clients read an HTTP error as "this server is
        // broken" and stop, where error 0 is a method they asked for and did not
        // get. Literal routes win over a catch-all, so this only ever sees what
        // nothing above matched.
        group.MapMethods("/{**method}", Verbs, NotImplemented);

        return app;
    }

    /// <summary>A query string, or the same arguments as a form body (<c>formPost</c>).</summary>
    private static readonly string[] Verbs = [HttpMethods.Get, HttpMethods.Post];

    /// <summary>
    /// Both spellings of one method.
    /// </summary>
    /// <remarks>
    /// The original API's methods all ended in <c>.view</c> and half the client
    /// ecosystem still sends it; the other half does not. Two routes is the
    /// whole of the compatibility story and cheaper than a rewrite rule.
    /// </remarks>
    private static void Map(RouteGroupBuilder group, string method, Delegate handler)
    {
        group.MapMethods($"/{method}", Verbs, handler);
        group.MapMethods($"/{method}.view", Verbs, handler);
    }

    private static IResult Ping() => SubsonicResult.Ok();

    /// <summary>
    /// A method this server does not implement.
    /// </summary>
    /// <remarks>
    /// Named in the message, because the alternative — a bare 404 — is what a
    /// client reports as the server being down, and somebody then goes looking
    /// at their network rather than at a feature that is simply not here.
    /// </remarks>
    private static SubsonicResult NotImplemented(HttpContext http) =>
        SubsonicResult.Error(
            0,
            $"This server does not implement {MethodOf(http.Request.Path)}.");

    /// <summary>Whose API key this is (<c>apiKeyAuthentication</c>); the guard has refused a wrong one.</summary>
    private static IResult TokenInfo(HttpContext http) =>
        SubsonicResult.Ok("tokenInfo", info => info.Attr("username", http.User.Identity!.Name));

    private static IResult GetLicense() =>
        SubsonicResult.Ok("license", license => license.Attr("valid", true));

    /// <summary>
    /// The extensions this server implements: timed lyrics, and Fonoteca's own
    /// album motion, performance dates and compositions.
    /// </summary>
    /// <remarks>
    /// The presence of the response is how a client learns the server is an
    /// OpenSubsonic one at all, and what is missing from it is how it learns
    /// not to ask for transcode offsets. <c>apiKeyAuthentication</c> is API
    /// keys, made on the accounts page, and <c>tokenInfo</c>. <c>songLyrics</c> is
    /// version 1 only: version 2's translations, word cues and singers are
    /// nothing LRCLIB or a tag here holds. <c>fonotecaAlbumMotion</c> is
    /// <c>getAlbumMotion</c>, which only a client that looks for it asks for.
    /// <c>fonotecaPerformance</c> is two fields the protocol has no place for: a
    /// song's <c>recorded</c> (<c>from</c>, <c>to</c>, <c>places</c>) and its
    /// piece's <c>composed</c> (<c>from</c>, <c>to</c>), each date an ItemDate.
    /// <c>fonotecaCompositions</c> is <c>getComposers</c> and
    /// <c>getCompositions</c>: who wrote the classical pieces held, and each
    /// piece with its songs.
    /// </remarks>
    private static IResult GetOpenSubsonicExtensions() =>
        SubsonicResult.OkList(
            "openSubsonicExtensions",
            new (string Name, int[] Versions)[] { ("songLyrics", [1]), ("formPost", [1]), ("apiKeyAuthentication", [1]), ("fonotecaAlbumMotion", [1]), ("fonotecaPerformance", [1]), ("fonotecaCompositions", [1]) },
            (writer, extension) =>
            {
                writer.Attr("name", extension.Name);
                writer.Numbers("versions", extension.Versions);
            });

    /// <summary>
    /// A play, acknowledged and stored nowhere.
    /// </summary>
    /// <remarks>
    /// Within the protocol: the endpoint exists so a client can tell a server
    /// about a play, and a server may do nothing with it. Refusing instead makes
    /// well-behaved clients log an error on every track, and there is no listen
    /// table to write to — play counts are a schema change and a decision of
    /// their own.
    /// </remarks>
    private static IResult Scrobble() => SubsonicResult.Ok();

    private static async Task<IResult> GetScanStatus(
        LibraryScanService scans,
        FonotecaDbContext db,
        CancellationToken cancellationToken) =>
        Scanning(scans, await db.MediaFiles.CountAsync(cancellationToken).ConfigureAwait(false));

    /// <summary>
    /// Starts a scan, and waits for it.
    /// </summary>
    /// <remarks>
    /// The scan runs in the foreground of the request — a walk with no file
    /// reads is seconds even at 100k — so there is nothing to poll and the
    /// answer is the finished state. It is the endpoint handler itself, not a
    /// copy of it, so <c>Fonoteca:IdentifyAfterScan</c> still chains and a
    /// second scan still refuses.
    /// </remarks>
    private static async Task<IResult> StartScan(
        HttpContext http,
        LibraryScanService scans,
        IdentificationService identification,
        IOptions<FonotecaOptions> options,
        FonotecaDbContext db,
        CancellationToken cancellationToken)
    {
        if (!May(http, AccountRoles.Admin)) return NotAuthorized();

        await LibraryEndpoints
            .ScanLibrary(scans, identification, options, cancellationToken)
            .ConfigureAwait(false);

        return Scanning(scans, await db.MediaFiles.CountAsync(cancellationToken).ConfigureAwait(false));
    }

    private static SubsonicResult Scanning(LibraryScanService scans, int count) =>
        SubsonicResult.Ok("scanStatus", status =>
        {
            status.Attr("scanning", scans.IsRunning);
            status.Attr("count", count);
        });

    /// <summary>
    /// The method a request names, with the optional <c>.view</c> taken off.
    /// </summary>
    internal static string MethodOf(PathString path)
    {
        var value = path.Value ?? string.Empty;
        var cut = value.LastIndexOf('/');
        var method = cut < 0 ? value : value[(cut + 1)..];

        return method.EndsWith(".view", StringComparison.OrdinalIgnoreCase)
            ? method[..^".view".Length]
            : method;
    }

    private static bool Authenticated(IQueryCollection query, string password)
    {
        var sent = query["p"].ToString();

        if (sent.Length > 0)
        {
            // "enc:" is hex, not base64, and is the older clients' idea of not
            // sending a password in the clear. It is not, and the protocol says
            // as much; it is accepted because refusing it locks those clients
            // out of a surface they can already reach with p= anyway.
            return Decoded(sent) is { } clear && Same(clear, password);
        }

        var token = query["t"].ToString();
        var salt = query["s"].ToString();

        if (token.Length == 0 || salt.Length == 0) return false;

        // MD5 because the protocol says MD5. It is not a defence against
        // anything and is not treated as one: the password it hashes is held
        // recoverably, because this is the only way a server can check a token
        // it did not compute.
#pragma warning disable CA5351 // MD5 is the protocol's, and is not relied on here.
        var expected = Convert.ToHexStringLower(
            MD5.HashData(Encoding.UTF8.GetBytes(password + salt)));
#pragma warning restore CA5351

        return Same(token.ToLowerInvariant(), expected);
    }

    /// <summary>A password as sent, clear or <c>enc:</c> hex; null where the hex is not hex.</summary>
    private static string? Decoded(string sent)
    {
        if (!sent.StartsWith("enc:", StringComparison.Ordinal)) return sent;

        try
        {
            return Encoding.UTF8.GetString(Convert.FromHexString(sent.AsSpan(4)));
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static bool Same(string sent, string expected) =>
        CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(sent),
            Encoding.UTF8.GetBytes(expected));

    /// <summary>One query parameter, or null where it was not sent.</summary>
    private static string? Text(HttpContext http, string name)
    {
        var value = http.Request.Query[name].ToString();
        return string.IsNullOrEmpty(value) ? null : value;
    }

    /// <summary>
    /// One numeric query parameter, clamped.
    /// </summary>
    /// <remarks>
    /// Unparseable falls back rather than failing. A client sending
    /// <c>size=many</c> is a client with a bug, and answering its bug with the
    /// default list is a better outcome than an error it will render as the
    /// server being down.
    /// </remarks>
    private static int Number(HttpContext http, string name, int fallback, int max)
    {
        var raw = Text(http, name);

        return raw is not null
            && int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
                ? Math.Clamp(value, 0, max)
                : fallback;
    }
}
