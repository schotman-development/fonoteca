using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Fonoteca.Api.Configuration;
using Fonoteca.Api.Endpoints;
using Fonoteca.Api.Library;
using Fonoteca.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

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
    /// <b>Without <c>Fonoteca:SubsonicPassword</c> the endpoint does not
    /// exist</b>, answering 404 rather than 401, so a fresh install offers
    /// nothing to its network.
    ///
    /// <b>The password is held recoverably and there is no way round that.</b>
    /// The protocol's own authentication is <c>t = md5(password + salt)</c>,
    /// which the server can only check by computing it, so a hash on this side
    /// is not an option. Containment is that it is one password, for one
    /// surface, that is absent by default.
    ///
    /// <b><c>u</c> is read and ignored.</b> There are no users here —
    /// <c>SingleUserCallerContext</c> is what it would have to resolve against —
    /// and a client that cannot name the user it is logging in as is a client
    /// nobody has.
    /// </remarks>
    internal static Task Guard(HttpContext context, RequestDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        if (!context.Request.Path.StartsWithSegments(Route)) return next(context);

        var password = context.RequestServices
            .GetRequiredService<IOptions<FonotecaOptions>>().Value.SubsonicPassword;

        if (string.IsNullOrWhiteSpace(password))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return Task.CompletedTask;
        }

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
        if (MethodOf(context.Request.Path) == "getOpenSubsonicExtensions")
        {
            return next(context);
        }

        return Authenticated(context.Request.Query, password)
            ? next(context)
            : SubsonicResult
                .Error(40, "Wrong username or password.")
                .ExecuteAsync(context);
    }

    public static IEndpointRouteBuilder MapSubsonicEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        // Not part of the contract packages/api-client is generated from. The
        // precedent is MapMcp: without this every build rewrites openapi.json,
        // regenerates the TypeScript client and turns gen:api:check red.
        var group = app.MapGroup(Route).ExcludeFromDescription();

        Map(group, "ping", Ping);
        Map(group, "getLicense", GetLicense);
        Map(group, "getOpenSubsonicExtensions", GetOpenSubsonicExtensions);

        Map(group, "getMusicFolders", GetMusicFolders);
        Map(group, "getIndexes", GetIndexes);
        Map(group, "getMusicDirectory", GetMusicDirectory);

        Map(group, "getArtists", GetArtists);
        Map(group, "getArtist", GetArtist);
        Map(group, "getAlbum", GetAlbum);
        Map(group, "getAlbumList2", GetAlbumList2);
        Map(group, "getSong", GetSong);
        Map(group, "search3", Search3);

        Map(group, "stream", Stream);
        Map(group, "download", Download);
        Map(group, "getCoverArt", GetCoverArt);

        Map(group, "getUser", GetUser);
        Map(group, "getPlaylists", GetPlaylists);
        Map(group, "scrobble", Scrobble);
        Map(group, "getScanStatus", GetScanStatus);
        Map(group, "startScan", StartScan);

        // Anything else, in the protocol's own envelope. A catch-all rather than
        // a 404, because several clients read an HTTP error as "this server is
        // broken" and stop, where error 0 is a method they asked for and did not
        // get. Literal routes win over a catch-all, so this only ever sees what
        // nothing above matched.
        group.MapGet("/{**method}", NotImplemented);

        return app;
    }

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
        group.MapGet($"/{method}", handler);
        group.MapGet($"/{method}.view", handler);
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

    /// <summary>
    /// The one user, and what it is allowed to do.
    /// </summary>
    /// <remarks>
    /// Several clients call this at login to decide which buttons to draw, and a
    /// missing answer reads to them as a broken server. Every role is stated
    /// rather than assumed, and the three that are true are the three this
    /// surface actually does: stream, download and cover art. Nothing here can
    /// star, rate, upload, share or administer anything, so a client that reads
    /// this draws none of those.
    ///
    /// <c>username</c> is echoed back from the request. There are no users — the
    /// password is the whole credential — and inventing a name the client did
    /// not use is how a client decides it is logged in as somebody else.
    /// </remarks>
    private static IResult GetUser(HttpContext http) =>
        SubsonicResult.Ok("user", user =>
        {
            user.Attr("username", Text(http, "username") ?? Text(http, "u") ?? "fonoteca");
            user.Attr("email", (string?)null);
            user.Attr("scrobblingEnabled", false);
            user.Attr("adminRole", false);
            user.Attr("settingsRole", false);
            user.Attr("downloadRole", true);
            user.Attr("uploadRole", false);
            user.Attr("playlistRole", false);
            user.Attr("coverArtRole", true);
            user.Attr("commentRole", false);
            user.Attr("podcastRole", false);
            user.Attr("streamRole", true);
            user.Attr("jukeboxRole", false);
            user.Attr("shareRole", false);
            user.Attr("videoConversionRole", false);
            user.Attr("folder", 0);
        });

    private static IResult GetLicense() =>
        SubsonicResult.Ok("license", license => license.Attr("valid", true));

    /// <summary>
    /// The extensions this server implements, which is none of them.
    /// </summary>
    /// <remarks>
    /// An empty list rather than a missing endpoint: the presence of the
    /// response is how a client learns the server is an OpenSubsonic one at all,
    /// and the emptiness is how it learns not to ask for transcode offsets or
    /// api keys.
    /// </remarks>
    private static IResult GetOpenSubsonicExtensions() =>
        SubsonicResult.OkList(
            "openSubsonicExtensions",
            Array.Empty<(string Name, int[] Versions)>(),
            (writer, extension) =>
            {
                writer.Attr("name", extension.Name);
                writer.Numbers("versions", extension.Versions);
            });

    /// <summary>
    /// The playlists, of which there are none.
    /// </summary>
    /// <remarks>
    /// Empty rather than absent, because a missing endpoint is an error a client
    /// shows and an empty list is a screen saying there are no playlists, which
    /// is true. Nothing here stores a queue or a playlist — see
    /// <c>PlaybackProvider</c>, which says so in its own words.
    /// </remarks>
    private static IResult GetPlaylists() =>
        SubsonicResult.Ok(
            "playlists",
            playlists => playlists.Children(
                "playlist",
                Array.Empty<object>(),
                (_, _) => { }));

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
        LibraryScanService scans,
        IdentificationService identification,
        IOptions<FonotecaOptions> options,
        FonotecaDbContext db,
        CancellationToken cancellationToken)
    {
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
            if (sent.StartsWith("enc:", StringComparison.Ordinal))
            {
                try
                {
                    sent = Encoding.UTF8.GetString(Convert.FromHexString(sent.AsSpan(4)));
                }
                catch (FormatException)
                {
                    return false;
                }
            }

            return Same(sent, password);
        }

        var token = query["t"].ToString();
        var salt = query["s"].ToString();

        if (token.Length == 0 || salt.Length == 0) return false;

        // MD5 because the protocol says MD5. It is not a defence against
        // anything and is not treated as one: the password it hashes is held in
        // configuration in the clear, because this is the only way a server can
        // check a token it did not compute.
#pragma warning disable CA5351 // MD5 is the protocol's, and is not relied on here.
        var expected = Convert.ToHexStringLower(
            MD5.HashData(Encoding.UTF8.GetBytes(password + salt)));
#pragma warning restore CA5351

        return Same(token.ToLowerInvariant(), expected);
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
