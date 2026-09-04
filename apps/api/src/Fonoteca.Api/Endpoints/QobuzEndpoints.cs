using Fonoteca.Api.Acquisition;
using Fonoteca.Api.Configuration;
using Fonoteca.Data;
using Fonoteca.Domain.Abstractions;
using Fonoteca.Domain.Acquisition;
using Fonoteca.Domain.Catalogue;
using Fonoteca.Providers.Qobuz;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Fonoteca.Api.Endpoints;

/// <summary>
/// Manual acquisition from Qobuz: find an album, look at it, fetch it.
/// </summary>
/// <remarks>
/// There is no monitoring, no wanted list and no automatic upgrade — every
/// download here is a person who searched, read a track list and pressed a
/// button, which is the same standard the AcoustID submission path holds itself
/// to and for a stronger reason: this one spends somebody's subscription.
///
/// <c>/upgrades</c> does not change that. It reads the catalogue and says which
/// albums are held in a lossy encoding; it asks Qobuz nothing, and what a person
/// does with a row is the search that was already there.
///
/// Nothing touches the catalogue. Downloads land in the staging root and stop
/// there; a scan does not look at staging, so nothing appears in the library
/// until a person moves it. That import is deliberately not built yet.
/// </remarks>
public static class QobuzEndpoints
{
    public static IEndpointRouteBuilder MapQobuzEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/qobuz").WithTags("Qobuz");

        group.MapGet("/status", GetStatus)
            .WithName("GetQobuzStatus")
            .WithSummary("Whether this instance can search and download, and at what quality.");

        group.MapGet("/upgrades", ListUpgrades)
            .WithName("ListQobuzUpgrades")
            .WithSummary("Albums worth buying: held in a lossy encoding, or held in part.")
            .WithDescription(
                "Two lists, both read out of the catalogue with no request to Qobuz. `items` is "
                + "quality — an album held in something worse than Qobuz sells. `incomplete` is "
                + "completeness — an album whose release prints more tracks than the library "
                + "holds, minus the ones whose folder still has unmatched files in it that could "
                + "account for the gap.");

        group.MapGet("/albums", SearchAlbums)
            .WithName("SearchQobuzAlbums")
            .WithSummary("Search the Qobuz catalogue for albums.");

        group.MapGet("/albums/{albumId}", GetAlbum)
            .WithName("GetQobuzAlbum")
            .WithSummary("One album with its track list.");

        group.MapPost("/albums/{albumId}/download", DownloadAlbum)
            .WithName("DownloadQobuzAlbum")
            .WithSummary("Fetch every streamable track of an album into the library.");

        group.MapPost("/albums/{albumId}/upgrade", UpgradeAlbum)
            .WithName("UpgradeQobuzAlbum")
            .WithSummary("Download an album and retire the one in the library it replaces.")
            .WithDescription(
                "Downloads first, measures what landed, and only then decides. Refuses if fewer "
                + "tracks arrived than the album holds, if what arrived is no better than the best "
                + "file already there, or if nothing has measured the old album. Nothing is "
                + "deleted — the old files are moved to Fonoteca:ReplacedPath keeping their "
                + "layout, and only when Fonoteca:AllowFileReplacement is on.")
            .ProducesProblem(StatusCodes.Status409Conflict);

        return app;
    }

    /// <summary>
    /// What is configured, without asking Qobuz anything.
    /// </summary>
    /// <remarks>
    /// Reports the settings separately because they fail separately: a missing
    /// app secret leaves search and browsing working and only breaks downloads,
    /// which is invisible until somebody clicks. Never returns the credential
    /// values — a status endpoint that echoes an account token is a token in
    /// every browser cache that ever loaded the page. The library path is not a
    /// credential and is worth showing, because it is where the music lands.
    /// </remarks>
    private static QobuzStatusResponse GetStatus(
        IOptions<FonotecaOptions> options,
        QobuzDownloadService downloads)
    {
        var config = options.Value;
        var qobuz = config.Providers.Qobuz;

        return new QobuzStatusResponse(
            Configured: !string.IsNullOrWhiteSpace(qobuz.AppId)
                && !string.IsNullOrWhiteSpace(qobuz.UserAuthToken),
            CanDownload: !string.IsNullOrWhiteSpace(qobuz.AppSecret),
            FormatId: qobuz.FormatId,
            LibraryPath: config.LibraryPath,
            Busy: downloads.IsBusy);
    }

    /// <summary>
    /// Which albums in the library are held in something worse than Qobuz sells.
    /// </summary>
    /// <remarks>
    /// <b>It asks Qobuz nothing.</b> Every number here comes out of the catalogue,
    /// which is what makes it a list a person can open rather than a sweep: the
    /// alternative is one search per album against a reverse-engineered API on a
    /// paid personal account, which is the traffic shape this screen exists to
    /// avoid. The Qobuz half stays exactly what it already was — a person reads a
    /// row, presses Search, and looks at what comes back.
    ///
    /// <b>Two reasons, and the second one is the large one.</b> A lossy container
    /// is knowable from a filename; a 16/44.1 FLAC against a 24/96 master is not,
    /// and needs <c>MediaFile.Quality</c> filled in by <c>ProbeService</c>.
    /// Measured on the target library those halves are <b>87 album folders and
    /// 434</b>, so a library nobody has probed sees about a sixth of its own
    /// upgrades. <see cref="UpgradeScan"/> reports an unprobed lossless file as
    /// no answer rather than as a maybe.
    ///
    /// <b>The unit is the album folder, and the release only names it.</b> The
    /// obvious choice is the release, because that is what Qobuz sells — and it
    /// leaves every unattributed file off a screen whose whole job is to find
    /// things to buy. Grouping the two separately is worse still and was the
    /// first attempt: a partly-attributed album appears twice, 125 times over on
    /// the target library, and neither row states how many files the album
    /// actually holds. One row per folder is one row per thing a person would
    /// replace, with honest counts, and the dominant release supplies the title.
    ///
    /// A folder is the claim this project makes a point of not believing, and the
    /// reason it is allowed here is the reason the by-hand album screen allows
    /// it: a pass reading a folder name is a guess with nobody watching, while a
    /// person reading one and pressing Search is doing the believing themselves.
    /// Nothing here is written to the catalogue.
    ///
    /// <b>The second list is a different question with a different unit.</b>
    /// "Held in something worse than Qobuz sells" is about encoding and its unit
    /// is the folder; "held in part" is about how many tracks there are, and only
    /// a release carries a track list to be short of — so an album no pass has
    /// attributed cannot appear on it at all.
    ///
    /// <b>And it subtracts, which is the only reason it is worth reading.</b> A
    /// release printing more tracks than the library holds is usually not a gap:
    /// measured on the target library, <b>128 of the 175</b> look short only
    /// because files sitting in the same folder have not been matched to a track
    /// yet. Those belong to the Identify screen, so they come off the list and
    /// are counted instead — without that this list would be four fifths wrong
    /// in the direction that costs money.
    /// </remarks>
    private static async Task<Ok<UpgradeListResponse>> ListUpgrades(
        FonotecaDbContext db,
        CancellationToken cancellationToken)
    {
        // ponytail: reads every file and groups in memory — 8k rows here, ~14 MB
        // at 100k files. Push the assessment into SQL if this stops being
        // instant; it is a person-clicked endpoint, not a pass.
        var files = await db.MediaFiles
            .AsNoTracking()
            .Select(file => new { file.ReleaseId, file.TrackId, file.Path, file.Quality })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        // How many files each release holds across the whole library, so a
        // folder can tell whether the release it is filed under is actually
        // about it. See the majority test below.
        var releaseSize = files
            .Where(file => file.ReleaseId is not null)
            .GroupBy(file => file.ReleaseId!.Value)
            .ToDictionary(release => release.Key, release => release.Count());

        var folders = files
            .Select(file => new Assessed(
                file.ReleaseId,
                file.TrackId,
                UpgradeScan.Assess(file.Path, file.Quality),
                UpgradeScan.Format(file.Path, file.Quality),
                file.Path))

            // The album folder, always — never the release, and this was a bug
            // rather than a preference. Keying attributed files on their release
            // and the rest on their folder put a partly-attributed album on the
            // list twice, 125 times over on the target library: eight files
            // under "Don't Rock the Jukebox" and two more under
            // "Alan Jackson/Don't Rock the Jukebox (1991)", one album, one
            // purchase, and neither row saying it holds ten files. A folder is
            // what a person is looking at and what they would replace.
            .GroupBy(file => UpgradeScan.AlbumFolder(file.Path))
            .Select(group => new Grouped(
                Folder: group.Key,

                // The release most of the folder is filed under, for a real
                // title and a real billing line. Most rather than any, so a
                // compilation folder holding one stray track is named after the
                // compilation; the id breaks a tie, so a rerun cannot change its
                // mind.
                ReleaseId: group
                    .Where(file => file.ReleaseId is not null)
                    .GroupBy(file => file.ReleaseId!.Value)
                    .OrderByDescending(release => release.Count())
                    .ThenBy(release => release.Key.Value)

                    // ...and only if the two are most of each other. The
                    // attribution pass's documented weakness is anthologised
                    // catalogue, and it shows up here from both sides. Brad
                    // Paisley's albums are each filed under one 64-file reissue
                    // box, so four folders name it and a row headed "Original
                    // Album Classics" holding 21 files is really "Time Well
                    // Wasted" — that is the release being mostly somewhere else.
                    // The other way round, a 152-file compilation folder whose
                    // largest release accounts for thirty of them would be named
                    // after those thirty. Neither title is about the files being
                    // counted, and the folder name at least is.
                    //
                    // Strictly more than half, both ways. "At least half" lets
                    // *both* halves of an even split qualify, which is the same
                    // wrong title arriving by a different route: a two-track
                    // release split one-and-one across two folders had the
                    // "Quitter (2023)" folder titled and searched for as
                    // "Don't Eat Pray Love". An exact tie is not evidence either
                    // way, so neither folder gets to claim it.
                    .Where(release => release.Count() * 2 > releaseSize[release.Key]
                        && release.Count() * 2 > group.Count())
                    .Select(release => (ReleaseId?)release.Key)
                    .FirstOrDefault(),

                // ponytail: files, not distinct tracks — so an album held as ten
                // FLACs and ten MP3s of the same songs reads "10/20" with nothing
                // actually to buy. Zero such files on the target library; count
                // DISTINCT TrackId if duplicate rips turn up.
                Files: group.Count(),

                // Files sitting in this folder that no pass has seated on a
                // track. Nothing to do with an upgrade; it is the raw material
                // for the subtraction that keeps the incomplete list below
                // honest, which sums it over a release's folders.
                Unfiled: group.Count(file => file.TrackId is null),
                Reason: group.Max(file => file.Reason),
                Upgradable: [.. group.Where(file => file.Reason != UpgradeReason.None)]))
            .ToList();

        var groups = folders
            .Where(group => group.Reason != UpgradeReason.None)
            .ToList();

        // Every folder that a release names, not just the upgradable ones: an
        // album held entirely in hi-res FLAC is nothing to re-buy and can still
        // be missing track 7.
        var ids = folders
            .Where(group => group.ReleaseId is not null)
            .Select(group => group.ReleaseId!.Value)
            .Distinct()
            .ToList();

        var releases = await db.Releases
            .AsNoTracking()
            .Where(release => ids.Contains(release.Id))
            .Select(release => new
            {
                release.Id,
                release.Mbid,
                release.Title,
                release.ReleasedYear,
                release.MediumFormats,

                // MusicBrainz's printed count where it has one, the track list
                // otherwise — the same denominator the album browse prints, so
                // the two screens cannot disagree about whether a rip is whole.
                TrackCount = release.TrackCount ?? release.Tracks.Count,

                // Distinct tracks, never files: five encodings of one song are
                // one track of the album, and counting files makes a
                // half-ripped album read complete.
                Held = release.Files
                    .Where(file => file.TrackId != null)
                    .Select(file => file.TrackId)
                    .Distinct()
                    .Count(),
                Artists = release.Credits
                    .OrderBy(credit => credit.Position)
                    .Select(credit => new { credit.CreditedAs, credit.Artist!.Name, credit.JoinPhrase })
                    .ToList(),
            })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var byId = releases.ToDictionary(release => release.Id);

        var items = groups
            .Select(group =>
            {
                // Artist/Album, which is how this library is laid out. The
                // fallback rather than the answer, but it is never wrong about
                // which files it covers, which is what the counts are.
                var cut = group.Folder.LastIndexOf('/');
                var title = cut >= 0 ? group.Folder[(cut + 1)..] : group.Folder;
                var artist = cut >= 0 ? group.Folder[..cut] : null;
                int? year = null;
                Guid? mbid = null;

                // Bracketed noise out of the query and left in the heading: the
                // row has to match the directory somebody is looking at, and
                // "(2023)" against an index of album titles does not help.
                var query = Join(
                    UpgradeScan.Searchable(artist ?? string.Empty),
                    UpgradeScan.Searchable(title));

                if (group.ReleaseId is { } id && byId.TryGetValue(id, out var release))
                {
                    title = release.Title;

                    // The billing line as printed, join phrases and all. The same
                    // rule the browse screens print, not a second copy of it: two
                    // spellings of one artist across two screens is the sort of
                    // drift nothing ever notices.
                    artist = CatalogueEndpoints.CreditLine(
                        release.Artists.Select(a => (a.CreditedAs ?? a.Name, a.JoinPhrase)));

                    year = release.ReleasedYear;

                    // Only so the screen can show a cover. It is MusicBrainz's
                    // identifier and the Cover Art Archive is keyed on it, so a
                    // folder no pass attributed has no picture to show and gets
                    // the monogram — which is honest: nothing here knows what
                    // that album is. 445 of 520 rows on the target library have
                    // one.
                    mbid = release.Mbid?.Value;

                    // The first billed artist and the title, and nothing else.
                    // The line above it is the truth and this is a query: Qobuz
                    // search is full-text, so "Frank Sinatra with Count Basie &
                    // the Orchestra Sinatra at the Sands" is six words of noise
                    // around the two that would have found it.
                    query = Join(release.Artists.FirstOrDefault()?.Name, release.Title);
                }

                return (
                    group.Reason,
                    Candidate: new UpgradeCandidate(
                        group.ReleaseId?.Value,
                        mbid,
                        group.Folder,
                        title,
                        artist,
                        year,
                        query,
                        group.Reason.ToString(),
                        group.Files,
                        group.Upgradable.Count,
                        [.. group.Upgradable
                            .Select(file => file.Format)
                            // A file with no extension has no format to name, and
                            // an empty badge on the row is worse than one badge
                            // fewer. It is still counted; only the label is
                            // missing.
                            .Where(format => format.Length > 0)
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .Order(StringComparer.OrdinalIgnoreCase)]));
            })
            // By the enum, not by its name. UpgradeReason is ordered by how much
            // better the replacement is and Max() above already relies on that;
            // sorting the string re-derives the same answer from the spelling,
            // and inverts the whole list the day a value is renamed.
            .OrderByDescending(row => row.Reason)
            .ThenByDescending(row => row.Candidate.Upgradable)
            .ThenBy(row => row.Candidate.Title, StringComparer.OrdinalIgnoreCase)
            .Select(row => row.Candidate)
            .ToList();

        // The slots of every album a release names, so a gap can be named
        // rather than only counted. One query for the whole screen — the
        // alternative is a track list per row — and which of them are held is
        // answered in memory, because every TrackId in the library is already
        // in `files`.
        var seated = files
            .Where(file => file.TrackId is not null)
            .Select(file => file.TrackId!.Value)
            .ToHashSet();

        var slots = ids.Count == 0
            ? []
            : await db.Tracks
                .AsNoTracking()
                .Where(track => ids.Contains(track.ReleaseId))
                .OrderBy(track => track.DiscNumber)
                .ThenBy(track => track.Position)
                .Select(track => new
                {
                    track.Id,
                    track.ReleaseId,
                    track.DiscNumber,
                    track.Position,
                    track.Title,
                })
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

        var gaps = slots
            .Where(track => !seated.Contains(track.Id))
            .GroupBy(track => track.ReleaseId)
            .ToDictionary(release => release.Key, release => release.ToList());

        // Loose files in every folder a release's files sit in — the union, so
        // that this and `Held` are counted over the same files. A folder counts
        // once for each release in it, which is right: the same unmatched file
        // could be a missing track of either.
        var unfiledByFolder = folders.ToDictionary(
            group => group.Folder,
            group => group.Unfiled,
            StringComparer.Ordinal);

        var loose = files
            .Where(file => file.ReleaseId is not null)
            .Select(file => (Release: file.ReleaseId!.Value, Folder: UpgradeScan.AlbumFolder(file.Path)))
            .Distinct()
            .GroupBy(pair => pair.Release)
            .ToDictionary(
                release => release.Key,
                release => release.Sum(pair => unfiledByFolder.GetValueOrDefault(pair.Folder)));

        var incomplete = new List<IncompleteAlbum>();
        var unmatched = 0;

        foreach (var group in folders)
        {
            if (group.ReleaseId is not { } id || !byId.TryGetValue(id, out var release)) continue;

            if (release.TrackCount == 0 || release.Held >= release.TrackCount) continue;

            // The subtraction, and it is the whole reason this list is worth
            // reading. A gap that unmatched files already in the library could
            // fill is a question for the Identify screen and not something to
            // buy: measured on the target library, 128 of the 175 albums short
            // of their track list are exactly that, so a list that skipped this
            // would be four fifths wrong in the expensive direction.
            //
            // Both halves are release-wide, and they have to be. Counted from
            // the one folder that names the release, the loose files are a
            // different set from the one the gap was measured over — and on a
            // release spanning folders that reads as tracks to buy while they
            // sit on disk four folders away. Ray Charles' "The Birth of Soul"
            // was 28/53 with twenty-one to buy and twenty-four unmatched
            // siblings in four neighbouring folders.
            var unfiled = loose.GetValueOrDefault(id);

            if (release.TrackCount - release.Held - unfiled <= 0)
            {
                unmatched++;
                continue;
            }

            var missing = gaps.TryGetValue(id, out var open) ? open : [];

            incomplete.Add(new IncompleteAlbum(
                id.Value,
                release.Mbid?.Value,
                group.Folder,
                release.Title,
                CatalogueEndpoints.CreditLine(release.Artists.Select(a => (a.CreditedAs ?? a.Name, a.JoinPhrase))),
                release.ReleasedYear,

                // The first billed artist and the title, for the reason the
                // upgrade rows use it: the printed billing line is the truth and
                // a poor query.
                Join(release.Artists.FirstOrDefault()?.Name, release.Title),
                release.MediumFormats,
                release.TrackCount,
                release.Held,
                unfiled,
                [.. missing
                    .Take(MissingShown)
                    .Select(track => new MissingTrack(
                        track.DiscNumber,
                        track.Position,
                        track.Title))]));
        }

        return TypedResults.Ok(new UpgradeListResponse(
            files.Count,
            items,

            // Nearest to whole first. An album missing one track is one a person
            // can finish; one missing nine is a decision about whether they want
            // it, and there is no reason for the second to be at the top.
            [.. incomplete
                .OrderBy(album => album.TrackCount - album.Held)
                .ThenBy(album => album.Title, StringComparer.OrdinalIgnoreCase)],
            unmatched));
    }

    /// <summary>
    /// How many missing tracks a row names before it stops naming them.
    /// </summary>
    /// <remarks>
    /// A row's honest numbers are <c>held</c> against <c>trackCount</c>; the
    /// names are what turns "you are missing four" into something a person can
    /// decide about. A 71-track box set missing sixty of them would print sixty
    /// titles into a list a person is scanning, so the tail is cut and the count
    /// still says how big it was.
    /// </remarks>
    private const int MissingShown = 8;

    /// <summary>An artist and a title, with neither half required.</summary>
    private static string Join(string? artist, string? title) =>
        string.Join(' ', new[] { artist, title }.Where(part => !string.IsNullOrWhiteSpace(part)));

    /// <summary>One file, once the rule has looked at it.</summary>
    private readonly record struct Assessed(
        ReleaseId? ReleaseId,
        TrackId? TrackId,
        UpgradeReason Reason,
        string Format,
        string Path);

    private sealed record Grouped(
        string Folder,
        ReleaseId? ReleaseId,
        int Files,
        int Unfiled,
        UpgradeReason Reason,
        IReadOnlyList<Assessed> Upgradable);

    private static async Task<Results<Ok<QobuzAlbumSummary[]>, ProblemHttpResult>> SearchAlbums(
        [FromQuery] string query,
        QobuzClient qobuz,
        CancellationToken cancellationToken,
        [FromQuery] int limit = 25)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return TypedResults.Problem(
                "Give something to search for.", statusCode: StatusCodes.Status400BadRequest);
        }

        try
        {
            var albums = await qobuz
                .SearchAlbumsAsync(query, Math.Clamp(limit, 1, 100), cancellationToken)
                .ConfigureAwait(false);

            return TypedResults.Ok(albums.Select(Summarise).ToArray());
        }
        catch (ProviderException cause)
        {
            return Refused(cause);
        }
    }

    private static async Task<Results<Ok<QobuzAlbumResponse>, NotFound, ProblemHttpResult>> GetAlbum(
        string albumId,
        QobuzClient qobuz,
        CancellationToken cancellationToken)
    {
        try
        {
            var album = await qobuz.GetAlbumAsync(albumId, cancellationToken).ConfigureAwait(false);

            if (album is null) return TypedResults.NotFound();

            return TypedResults.Ok(new QobuzAlbumResponse(
                Summarise(album),
                [.. album.Tracks.Select(track => new QobuzTrackResponse(
                    track.Id,
                    track.Title,
                    track.DiscNumber,
                    track.TrackNumber,
                    track.Performer,
                    track.Duration is { } length ? (int)length.TotalSeconds : null,
                    track.Streamable))]));
        }
        catch (ProviderException cause)
        {
            return Refused(cause);
        }
    }

    /// <remarks>
    /// Long-running on purpose — see <see cref="QobuzDownloadService"/>. A
    /// hi-res album is minutes, and the request is held for all of it.
    /// </remarks>
    private static async Task<Results<Ok<AlbumDownload>, ProblemHttpResult>>
        DownloadAlbum(
            string albumId,
            QobuzDownloadService downloads,
            CancellationToken cancellationToken)
    {
        try
        {
            var result = await downloads.DownloadAlbumAsync(albumId, cancellationToken)
                .ConfigureAwait(false);

            return TypedResults.Ok(result);
        }
        catch (DownloadInProgressException cause)
        {
            // A problem document, not TypedResults.Conflict(string). A bare JSON
            // string has no `detail`, so the client's describeError finds
            // nothing to read and falls back to the status line — turning "a
            // download is already running" into "409 Conflict", which is the one
            // failure here a person could otherwise act on immediately.
            return TypedResults.Problem(
                cause.Message,
                statusCode: StatusCodes.Status409Conflict,
                title: "A download is already running");
        }
        catch (ProviderException cause)
        {
            return Refused(cause);
        }
        catch (Exception cause) when (cause is IOException or UnauthorizedAccessException)
        {
            // The disk, not the provider. A full volume and an unwritable
            // staging directory are both ordinary and both used to be a 500
            // with a stack trace — on the one endpoint whose whole job is to
            // put bytes on that disk.
            return TypedResults.Problem(
                cause.Message,
                statusCode: StatusCodes.Status500InternalServerError,
                title: "The download could not be written to staging");
        }
    }

    /// <summary>
    /// Download an album, then retire the one it replaces.
    /// </summary>
    /// <remarks>
    /// <b>One request, and the download half is the ordinary one.</b> Splitting
    /// it — download, then a second call to replace — reads tidier and is worse:
    /// the two would have to agree about which download the replacement is
    /// about, across a gap in which a person can close the tab, and the failure
    /// that leaves is an album downloaded and its predecessor still there with
    /// nothing recording that a swap was intended.
    ///
    /// <b>The folder comes from the caller and is never trusted as a path.</b>
    /// It is matched against <c>MediaFiles.Path</c> as a prefix and used to build
    /// an archive path; a caller sending <c>../</c> or an absolute path must not
    /// reach the filesystem. <c>AlbumReplacementService</c> re-derives everything
    /// from the library root, and a folder the catalogue has no files under is
    /// refused before anything is moved.
    ///
    /// Long-running, for the download's reasons and then some: a hi-res album is
    /// minutes and every track that lands is measured with a decoder afterwards.
    /// </remarks>
    private static async Task<Results<Ok<AlbumUpgrade>, ProblemHttpResult>> UpgradeAlbum(
        string albumId,
        UpgradeRequest request,
        QobuzDownloadService downloads,
        AlbumReplacementService replacements,
        CancellationToken cancellationToken)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.Folder))
        {
            return TypedResults.Problem(
                "Name the album folder this download replaces.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        // `required` in the schema is decorative: minimal APIs run no body
        // validation unless it is wired up, and this application does not wire
        // it. A hand-written request omitting `files` binds to zero, which is
        // the value that skips the count check — so the one field that stops a
        // prefix taking more than the row covered is switchable off by leaving
        // it out. Nothing legitimate sends zero.
        if (request.Files <= 0)
        {
            return TypedResults.Problem(
                "Say how many files that folder holds, so the catalogue can be checked against it.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        // A path, not a folder name. Refused here rather than sanitised, because
        // the only caller that sends one is a broken one and quietly correcting
        // it would replace an album nobody named.
        if (request.Folder.Contains("..", StringComparison.Ordinal)
            || Path.IsPathRooted(request.Folder))
        {
            return TypedResults.Problem(
                "A library folder, relative to the library root.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        try
        {
            var download = await downloads.DownloadAlbumAsync(albumId, cancellationToken)
                .ConfigureAwait(false);

            var replacement = await replacements
                .ReplaceAsync(request.Folder, request.Files, download, cancellationToken)
                .ConfigureAwait(false);

            return TypedResults.Ok(new AlbumUpgrade(download, replacement));
        }
        catch (DownloadInProgressException cause)
        {
            return TypedResults.Problem(
                cause.Message,
                statusCode: StatusCodes.Status409Conflict,
                title: "A download is already running");
        }
        catch (ProviderException cause)
        {
            return Refused(cause);
        }
        catch (Exception cause) when (cause is IOException or UnauthorizedAccessException)
        {
            return TypedResults.Problem(
                cause.Message,
                statusCode: StatusCodes.Status500InternalServerError,
                title: "The upgrade could not be written to disk");
        }
    }

    /// <summary>
    /// The two provider failures, mapped to the two statuses a caller acts on
    /// differently.
    /// </summary>
    /// <remarks>
    /// 502 for unavailable, because it is genuinely upstream and trying again
    /// later is the right response. 400 for rejected, because something here has
    /// to change first — an expired token, a missing secret — and the message
    /// names it.
    /// </remarks>
    private static ProblemHttpResult Refused(ProviderException cause) =>
        TypedResults.Problem(
            cause.Message,
            statusCode: cause is ProviderUnavailableException
                ? StatusCodes.Status502BadGateway
                : StatusCodes.Status400BadRequest,
            title: $"{cause.Provider} could not answer");

    private static QobuzAlbumSummary Summarise(QobuzAlbum album) =>
        new(album.Id,
            album.Title,
            album.Artist,
            album.ReleaseDate,
            album.TrackCount,
            album.DiscCount,
            album.HiRes,
            album.MaximumBitDepth,
            album.MaximumSamplingRate,
            album.Streamable,
            album.CoverUrl);
}

/// <param name="Configured">Whether search and browsing can work at all.</param>
/// <param name="CanDownload">Whether a download could be signed. The app secret is the only extra.</param>
/// <param name="LibraryPath">Where downloads land — they are written into the library directly.</param>
/// <param name="Busy">Whether a download is running. One at a time.</param>
public sealed record QobuzStatusResponse(
    bool Configured,
    bool CanDownload,
    int FormatId,
    string LibraryPath,
    bool Busy);

/// <param name="MaximumSamplingRate">In kHz, as Qobuz report it.</param>
public sealed record QobuzAlbumSummary(
    string Id,
    string Title,
    string? Artist,
    string? ReleaseDate,
    int TrackCount,
    int DiscCount,
    bool HiRes,
    int? MaximumBitDepth,
    double? MaximumSamplingRate,
    bool Streamable,
    string? CoverUrl);

public sealed record QobuzAlbumResponse(QobuzAlbumSummary Album, QobuzTrackResponse[] Tracks);

/// <param name="Folder">
/// The library folder being replaced, relative to the library root — the
/// <c>folder</c> an upgrade row carries.
/// </param>
/// <param name="Files">
/// How many files the row said that folder holds. The catalogue has to agree, or
/// the replacement is refused — see <c>AlbumReplacementService</c>. Must be positive:
/// the endpoint refuses zero rather than treating it as "do not check".
/// </param>
public sealed record UpgradeRequest(string Folder, int Files);

/// <summary>
/// What was fetched, and what happened to what it was meant to replace.
/// </summary>
/// <remarks>
/// Both halves always, including on a refusal: the download happened either way,
/// and a response that reported only the refusal would leave somebody unable to
/// tell whether they now have two copies or none.
/// </remarks>
public sealed record AlbumUpgrade(AlbumDownload Download, AlbumReplacement Replacement);

/// <param name="DurationSeconds">Qobuz's stated length, which is a claim and not a measurement.</param>
public sealed record QobuzTrackResponse(
    long Id,
    string Title,
    int DiscNumber,
    int TrackNumber,
    string? Performer,
    int? DurationSeconds,
    bool Streamable);

/// <summary>Albums worth re-buying, and what the answer was computed against.</summary>
/// <param name="Files">
/// Every file in the library. The denominator, and worth returning: a short list
/// against a large library means either a clean one or an unprobed one, and
/// those are opposite situations.
/// </param>
/// <param name="Incomplete">
/// Albums the library holds part of, worth completing rather than re-encoding.
/// A different question from <paramref name="Items"/> and a different unit: this
/// one needs a track list, so only a release can answer it, where an upgrade row
/// is a folder.
/// </param>
/// <param name="UnmatchedAlbums">
/// How many incomplete albums were left off because the files that would fill
/// the gap are already in the folder, unmatched. They are the Identify screen's
/// question, and saying how many keeps the short list from reading as a claim
/// that the library is nearly whole.
/// </param>
public sealed record UpgradeListResponse(
    int Files,
    IReadOnlyList<UpgradeCandidate> Items,
    IReadOnlyList<IncompleteAlbum> Incomplete,
    int UnmatchedAlbums);

/// <summary>An album held in part, with what is missing from it named.</summary>
/// <param name="Mbid">MusicBrainz's identifier, for the cover. See <see cref="UpgradeCandidate"/>.</param>
/// <param name="Folder">The album folder its files sit in, for a person to recognise it by.</param>
/// <param name="Query">The first billed artist and the title, as on an upgrade row.</param>
/// <param name="MediumFormats">
/// CD, Digital Media, <c>CD+DVD-Video</c>. The honest explanation for a gap
/// nobody can buy their way out of: a release with a video disc on it is missing
/// half its track list on any library that holds only the audio.
/// </param>
/// <param name="TrackCount">What the release prints. The denominator.</param>
/// <param name="Held">Distinct tracks of it the library holds, wherever they sit.</param>
/// <param name="Unmatched">
/// Files that no pass has seated on a track, in every folder this release's
/// files sit in — not only <paramref name="Folder"/>, or it would be counted
/// over a different set of files than <paramref name="Held"/>. Some of the gap
/// may already be on disk; the row is on the list because these cannot account
/// for all of it.
/// </param>
/// <param name="Missing">
/// The unheld slots, earliest first, cut after eight. Shorter than
/// <paramref name="TrackCount"/> minus <paramref name="Held"/> when it was cut,
/// or when nothing has written the release's track list.
/// </param>
public sealed record IncompleteAlbum(
    Guid ReleaseId,
    Guid? Mbid,
    string Folder,
    string Title,
    string? Artist,
    int? Year,
    string Query,
    string? MediumFormats,
    int TrackCount,
    int Held,
    int Unmatched,
    IReadOnlyList<MissingTrack> Missing);

/// <param name="Title">As printed on this release, which can differ from the recording's. Null where MusicBrainz has none.</param>
public sealed record MissingTrack(int Disc, int Position, string? Title);

/// <param name="ReleaseId">
/// The release most of the folder is filed under, or null when no pass
/// attributed any of it. The <i>row</i> is the folder either way — see
/// <c>ListUpgrades</c> — so this says where the title and the billing line came
/// from rather than what is being counted.
/// </param>
/// <param name="Mbid">
/// MusicBrainz's identifier for that release, and the only reason it is here is
/// the cover: the Cover Art Archive is keyed on it. Null wherever
/// <paramref name="ReleaseId"/> is, so a folder no pass attributed shows a
/// monogram — which is the honest picture of an album nothing has identified.
/// </param>
/// <param name="Folder">The album folder every count on this row is about.</param>
/// <param name="Query">
/// What to search Qobuz for — the first billed artist and the title. Not
/// <paramref name="Artist"/>, which is the printed billing line and is a worse
/// query the longer it gets.
/// </param>
/// <param name="Reason"><c>Lossy</c> or <c>BelowHiRes</c>; the worst any of its files carries.</param>
/// <param name="Files">Files held from this album, whatever their encoding.</param>
/// <param name="Upgradable">How many of them are the reason it is on this list.</param>
/// <param name="Formats">
/// What those files are, from the extension plus the measured depth and rate —
/// see <c>UpgradeScan.Format</c>. Empty when nothing in the album has a name to
/// print, which the counts do not depend on.
/// </param>
public sealed record UpgradeCandidate(
    Guid? ReleaseId,
    Guid? Mbid,
    string Folder,
    string Title,
    string? Artist,
    int? Year,
    string Query,
    string Reason,
    int Files,
    int Upgradable,
    IReadOnlyList<string> Formats);
