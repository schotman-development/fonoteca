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
            .WithSummary(
                "Albums worth buying: held in a lossy encoding, held in part, or not held at all.")
            .WithDescription(
                "Three lists, all read out of the catalogue with no request to Qobuz. `items` is "
                + "quality — an album held in something worse than Qobuz sells. `incomplete` is "
                + "completeness — an album whose release prints more tracks than the library "
                + "holds, minus the ones whose folder still has unmatched files in it that could "
                + "account for the gap. `missing` is the one that starts from a person rather "
                + "than a file — records MusicBrainz credits to a followed artist that nothing "
                + "here sits under, cut by `Discography.IsGap`. It needs the enrichment pass to "
                + "have browsed those artists; `unbrowsedArtists` says how many it has not.");

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
            .Select(file => new { file.ReleaseGroupId, file.ReleaseId, file.RecordingId, file.Path, file.Quality })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        // How much of each album the library holds, so a folder can tell whether
        // the album it is filed under is actually about it. See the majority
        // test below. In distinct recordings rather than files: two rips of one
        // album each hold all of it, and counted in files they would split it
        // between them and neither folder could be named after it. A file with no
        // recording is only itself.
        var albumSize = files
            .Where(file => file.ReleaseGroupId is not null)
            .GroupBy(file => file.ReleaseGroupId!.Value)
            .ToDictionary(
                album => album.Key,
                album => album.Select(file => file.RecordingId?.Value.ToString() ?? file.Path).Distinct().Count());

        var folders = files
            .Select(file => new Assessed(
                file.ReleaseGroupId,
                file.RecordingId,
                UpgradeScan.Assess(file.Path, file.Quality),
                UpgradeScan.Format(file.Path, file.Quality),
                file.Path))

            // The album folder, always — never the album's catalogue key, and
            // this was a bug rather than a preference. Keying filed files on
            // their release and the rest on their folder put a partly-filed album
            // on the list twice, 125 times over on the target library: eight
            // files under "Don't Rock the Jukebox" and two more under
            // "Alan Jackson/Don't Rock the Jukebox (1991)", one album, one
            // purchase, and neither row saying it holds ten files. A folder is
            // what a person is looking at and what they would replace.
            .GroupBy(file => UpgradeScan.AlbumFolder(file.Path))
            .Select(group => new Grouped(
                Folder: group.Key,

                // The album most of the folder is held to, for a real title and
                // a real billing line. Most rather than any, so a compilation
                // folder holding one stray track is named after the compilation;
                // the id breaks a tie, so a rerun cannot change its mind.
                AlbumId: group
                    .Where(file => file.AlbumId is not null)
                    .GroupBy(file => file.AlbumId!.Value)
                    .OrderByDescending(album => album.Count())
                    .ThenBy(album => album.Key.Value)

                    // ...and only if the two are most of each other. Anthologised
                    // catalogue shows up here from both sides. Brad Paisley's
                    // albums were each filed under one 64-file reissue box, so
                    // four folders named it and a row headed "Original Album
                    // Classics" holding 21 files was really "Time Well Wasted" —
                    // the album being mostly somewhere else. The other way round,
                    // a 152-file compilation folder whose largest album accounts
                    // for thirty of them would be named after those thirty.
                    // Neither title is about the files being counted, and the
                    // folder name at least is.
                    //
                    // Strictly more than half, both ways. "At least half" lets
                    // *both* halves of an even split qualify, which is the same
                    // wrong title arriving by a different route: a two-track
                    // release split one-and-one across two folders had the
                    // "Quitter (2023)" folder titled and searched for as
                    // "Don't Eat Pray Love". An exact tie is not evidence either
                    // way, so neither folder gets to claim it.
                    .Where(album => album.Select(file => file.Recording?.Value.ToString() ?? file.Path).Distinct().Count() * 2
                            > albumSize[album.Key]
                        && album.Count() * 2 > group.Count())
                    .Select(album => (ReleaseGroupId?)album.Key)
                    .FirstOrDefault(),

                // ponytail: files, not distinct tracks — so an album held as ten
                // FLACs and ten MP3s of the same songs reads "10/20" with nothing
                // actually to buy. Zero such files on the target library; count
                // distinct recordings if duplicate rips turn up.
                Files: group.Count(),

                Reason: group.Max(file => file.Reason),
                Upgradable: [.. group.Where(file => file.Reason != UpgradeReason.None)]))
            .ToList();

        var groups = folders
            .Where(group => group.Reason != UpgradeReason.None)
            .ToList();

        // Every folder that an album names, not just the upgradable ones: an
        // album held entirely in hi-res FLAC is nothing to re-buy and can still
        // be missing track 7.
        var ids = folders
            .Where(group => group.AlbumId is not null)
            .Select(group => group.AlbumId!.Value)
            .Distinct()
            .ToList();

        var albums = await db.ReleaseGroups
            .AsNoTracking()
            .Where(album => ids.Contains(album.Id))
            .Select(album => new
            {
                album.Id,
                album.Mbid,
                album.Title,
                album.FirstReleaseYear,
                Artists = album.Credits
                    .OrderBy(credit => credit.Position)
                    .Select(credit => new
                    {
                        credit.CreditedAs,
                        Name = credit.Artist!.LatinName ?? credit.Artist!.Name,
                        credit.JoinPhrase,
                    })
                    .ToList(),
            })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var editions = await CatalogueEndpoints.EditionFactsAsync(db, ids, cancellationToken)
            .ConfigureAwait(false);

        // One heading per album, whichever folder it ends up on: its title, the
        // billing line of the edition that stands for it (the album's own where
        // none is stored), the year it was first released, and the sleeve.
        var byId = albums.ToDictionary(
            album => album.Id,
            album =>
            {
                var display = CatalogueEndpoints.DisplayEdition(editions[album.Id]);
                var billed = display is { Artists.Count: > 0 }
                    ? display.Artists.Select(a => (Name: a.CreditedAs ?? a.Name, First: a.Name, a.JoinPhrase)).ToList()
                    : album.Artists.Select(a => (Name: a.CreditedAs ?? a.Name, First: a.Name, a.JoinPhrase)).ToList();

                return new Heading(
                    album.Id,
                    album.Mbid?.Value,
                    display?.Id.Value,
                    album.Title,

                    // The billing line as printed, join phrases and all. The same
                    // rule the browse screens print, not a second copy of it: two
                    // spellings of one artist across two screens is the sort of
                    // drift nothing ever notices.
                    CatalogueEndpoints.CreditLine(billed.Select(a => (a.Name, a.JoinPhrase))),
                    CatalogueEndpoints.AlbumYear(album.FirstReleaseYear, editions[album.Id]),

                    // The first billed artist and the title, and nothing else.
                    // The line above it is the truth and this is a query: Qobuz
                    // search is full-text, so "Frank Sinatra with Count Basie &
                    // the Orchestra Sinatra at the Sands" is six words of noise
                    // around the two that would have found it.
                    Join(billed.FirstOrDefault().First, album.Title));
            });

        var items = groups
            .Select(group =>
            {
                // Artist/Album, which is how this library is laid out. The
                // fallback rather than the answer, but it is never wrong about
                // which files it covers, which is what the counts are.
                var cut = group.Folder.LastIndexOf('/');
                var title = cut >= 0 ? group.Folder[(cut + 1)..] : group.Folder;
                var artist = cut >= 0 ? group.Folder[..cut] : null;

                // Bracketed noise out of the query and left in the heading: the
                // row has to match the directory somebody is looking at, and
                // "(2023)" against an index of album titles does not help.
                var heading = group.AlbumId is { } id && byId.TryGetValue(id, out var named)
                    ? named
                    : new Heading(
                        null,
                        null,
                        null,
                        title,
                        artist,
                        null,
                        Join(UpgradeScan.Searchable(artist ?? string.Empty), UpgradeScan.Searchable(title)));

                return (
                    group.Reason,
                    Candidate: new UpgradeCandidate(
                        heading.AlbumId?.Value,
                        heading.CoverReleaseId,
                        heading.Mbid,
                        group.Folder,
                        heading.Title,
                        heading.Artist,
                        heading.Year,
                        heading.Query,
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

        // Every stored edition's track list for every album a folder names, so a
        // gap can be named rather than only counted. One query for the whole
        // screen — the alternative is a track list per row.
        var keys = ids.Select(id => (ReleaseGroupId?)id).ToList();

        var slots = ids.Count == 0
            ? []
            : await db.Tracks
                .AsNoTracking()
                .Where(track => keys.Contains(track.Release!.ReleaseGroupId))
                .OrderBy(track => track.DiscNumber)
                .ThenBy(track => track.Position)
                .Select(track => new
                {
                    track.ReleaseId,
                    track.DiscNumber,
                    track.Position,
                    track.Number,
                    track.Title,
                    track.Length,
                    track.RecordingId,
                })
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

        var slotsOf = slots.ToLookup(slot => slot.ReleaseId);

        // What the album's files hold, wherever they sit, and which pressing they
        // are filed under — one pressing for every file is the claimed edition.
        var heldBy = files
            .Where(file => file.ReleaseGroupId is not null)
            .GroupBy(file => file.ReleaseGroupId!.Value)
            .ToDictionary(
                album => album.Key,
                album => (
                    Recordings: album
                        .Where(file => file.RecordingId is not null)
                        .Select(file => file.RecordingId!.Value)
                        .ToHashSet(),
                    Pressings: album.Select(file => file.ReleaseId).Distinct().ToList()));

        // Every folder an album's files sit in, and every file in those folders —
        // the union, so that the loose files and `Held` below are counted over
        // the same files.
        var inFolder = files.ToLookup(file => UpgradeScan.AlbumFolder(file.Path), StringComparer.Ordinal);

        var foldersOf = files
            .Where(file => file.ReleaseGroupId is not null)
            .GroupBy(file => file.ReleaseGroupId!.Value)
            .ToDictionary(
                album => album.Key,
                album => album.Select(file => UpgradeScan.AlbumFolder(file.Path)).Distinct(StringComparer.Ordinal).ToList());

        var incomplete = new List<IncompleteAlbum>();
        var unmatched = 0;

        // One row per album: every count below is album-wide, so a second rip
        // in another folder would be the same row again.
        var measuredAlbums = new HashSet<ReleaseGroupId>();

        foreach (var group in folders)
        {
            if (group.AlbumId is not { } id || !byId.TryGetValue(id, out var heading)) continue;
            if (!measuredAlbums.Add(id)) continue;

            var (held, pressings) = heldBy[id];
            var stored = editions[id]
                .Select(edition => new EditionTracks(
                    edition.Id,
                    [.. slotsOf[edition.Id].Select(slot => new EditionSlot(
                        slot.DiscNumber,
                        slot.Position,
                        slot.Number,
                        slot.Title,
                        slot.Length,
                        slot.RecordingId))]))
                .ToList();

            // The claimed pressing where every file is filed under one; otherwise
            // the edition the files are most plausibly a rip of — never a longer
            // edition they were not, which would report the deluxe's bonus disc as
            // tracks to buy.
            var measured = pressings is [{ } claimed]
                ? stored.FirstOrDefault(edition => edition.Id == claimed)
                : Editions.Nearest(stored, held);

            if (measured is null) continue;

            var facts = editions[id].First(edition => edition.Id == measured.Id);
            var trackCount = facts.TrackCount;
            var holding = measured.Slots.Count(slot => held.Contains(slot.Recording));

            if (trackCount == 0 || holding >= trackCount) continue;

            // The subtraction, and it is the whole reason this list is worth
            // reading. A gap that unfiled files already in the library could fill
            // is a question for the Identify screen and not something to buy:
            // measured on the target library, 128 of the 175 albums short of
            // their track list were exactly that, so a list that skipped this
            // would be four fifths wrong in the expensive direction.
            //
            // Both halves are album-wide, and they have to be. Counted from the
            // one folder that names the album, the loose files are a different
            // set from the one the gap was measured over — and on an album
            // spanning folders that reads as tracks to buy while they sit on disk
            // four folders away. Ray Charles' "The Birth of Soul" was 28/53 with
            // twenty-one to buy and twenty-four unmatched siblings in four
            // neighbouring folders.
            // Loose: in one of the album's folders and on none of the measured
            // edition's tracks — held to no album, or held to this one as a
            // recording the edition does not print. A folder counts once for each
            // album in it, which is right: the same loose file could be a missing
            // track of either.
            var printed = measured.Slots.Select(slot => slot.Recording).ToHashSet();
            var unfiled = foldersOf[id]
                .SelectMany(folder => inFolder[folder])
                .Count(file => file.ReleaseGroupId is null
                    || (file.ReleaseGroupId == id
                        && (file.RecordingId is not { } recording || !printed.Contains(recording))));

            if (trackCount - holding - unfiled <= 0)
            {
                unmatched++;
                continue;
            }

            incomplete.Add(new IncompleteAlbum(
                id.Value,
                measured.Id.Value,
                heading.CoverReleaseId,
                heading.Mbid,
                group.Folder,
                heading.Title,
                heading.Artist,
                heading.Year,
                heading.Query,
                facts.Formats,
                trackCount,
                holding,
                unfiled,
                [.. measured.Slots
                    .Where(slot => !held.Contains(slot.Recording))
                    .Take(MissingShown)
                    .Select(slot => new MissingTrack(slot.Disc, slot.Position, slot.Title))]));
        }

        var (followed, unbrowsed, unmonitored, records) =
            await MissingRecordsAsync(db, cancellationToken).ConfigureAwait(false);

        return TypedResults.Ok(new UpgradeListResponse(
            files.Count,
            items,

            // Nearest to whole first. An album missing one track is one a person
            // can finish; one missing nine is a decision about whether they want
            // it, and there is no reason for the second to be at the top.
            [.. incomplete
                .OrderBy(album => album.TrackCount - album.Held)
                .ThenBy(album => album.Title, StringComparer.OrdinalIgnoreCase)],
            unmatched,
            records,
            followed,
            unbrowsed,
            unmonitored));
    }

    /// <summary>
    /// Records by followed artists that no file here sits under.
    /// </summary>
    /// <remarks>
    /// <b>The third question on this screen, and the only one that is not about
    /// a file.</b> The two above it start from the library and ask what is wrong
    /// with what it holds — an encoding, a gap in a track list. This one starts
    /// from a person: <c>Artist.Followed</c> is the one column in the catalogue
    /// nothing can recompute, and a record by somebody they said they cared
    /// about is worth buying precisely because there is no file to reason from.
    ///
    /// <b>It is <c>CatalogueEndpoints.GetArtist</c>'s discography read over every
    /// followed artist at once</b>, deliberately down to the two-armed
    /// <c>Held</c> test — a file filed under one of the group's releases, or one
    /// pointing straight at the group, which is what attribution writes when it
    /// knows the album and not the pressing. Reading only the first reports a
    /// record as missing while it sits on the artist's own page, and a screen
    /// that offers to sell somebody a record they own is worse than no screen.
    ///
    /// <b>Asks nothing of anybody.</b> Not Qobuz, which is this endpoint's whole
    /// bargain, and not MusicBrainz either: the browse behind these rows is the
    /// enrichment pass's fifth worklist and has already run. So an artist
    /// nobody has followed contributes nothing, the queries are empty and cheap
    /// on a library that has never used the feature, and the cut
    /// (<c>Discography.IsGap</c>) is made here rather than at fetch time —
    /// CLAUDE.md's standing bargain, so changing the rule costs a page load and
    /// not a turn at the rate limit for every followed artist.
    /// </remarks>
    private static async Task<(int Followed, int Unbrowsed, int Unmonitored, List<MissingRecord> Records)>
        MissingRecordsAsync(FonotecaDbContext db, CancellationToken cancellationToken)
    {
        var followed = await db.Artists
            .AsNoTracking()
            .Where(artist => artist.Followed)
            .Select(artist => new
            {
                artist.Id,
                Name = artist.LatinName ?? artist.Name,
                artist.Mbid,
                artist.DiscographyLookupUtc,
            })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        // Followed and never browsed. On the wire for the reason
        // `ArtistDiscography.FetchedAtUtc` is: without it an empty shelf cannot
        // tell "they have released nothing you have not got" from "nobody has
        // asked yet", and those want opposite sentences on the page — one is
        // good news and the other is a pass somebody has to run.
        //
        // **The MBID is part of the question, not a detail.** The browse is keyed
        // on an MBID, so an artist without one is not on
        // `EnrichmentService.UnbrowsedArtist`'s worklist and never will be.
        //
        // This is deliberately the *never asked* half of that worklist and not
        // the whole of it: the pass also re-asks artists whose discography has
        // gone stale (`DiscographyLookupUtc < cutoff`), and those have been
        // browsed — the screen is saying "nobody has looked yet", which a
        // re-browse candidate contradicts. Counted in memory over the followed
        // set that is already loaded above, so `IX_Artists_Unbrowsed` is not
        // involved here either way. Every artist in this
        // catalogue is a byproduct of a credit line, so a followed one with no
        // MBID is ordinary rather than exotic — and the browse is keyed on an
        // MBID, so the pass will never reach them. Counted here without that
        // clause they are permanently "not browsed yet": the shelf tells
        // somebody to run a pass that cannot touch them, it changes nothing, and
        // the line never clears.
        var unbrowsed = followed.Count(artist =>
            artist.DiscographyLookupUtc is null && artist.Mbid is not null);

        var names = followed.ToDictionary(artist => artist.Id, artist => artist.Name);

        if (names.Count == 0) return (0, 0, 0, []);

        var ids = names.Keys.ToList();

        // `ArtistCredit.ReleaseGroupId` is written by the discography browse and
        // by nothing else, so this is the followed set's credited records and
        // not the whole catalogue's.
        var credited = await db.ArtistCredits
            .AsNoTracking()
            .Where(credit => credit.ReleaseGroupId != null && ids.Contains(credit.ArtistId))
            .Select(credit => new { credit.ArtistId, Group = credit.ReleaseGroupId!.Value })
            .Distinct()
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        // The shops' rows for the same followed set, cut by the same rule the
        // artist page cuts them with — at read time, so a record stops being a
        // gap the moment the catalogue gains the album rather than when somebody
        // remembers to clear a row.
        //
        // <b>Read before the early return below, not after.</b> A followed set
        // with no MusicBrainz credits at all is exactly the case the shop half
        // exists for — MusicBrainz learns about a record when an editor adds it,
        // a shop lists it on release day — so returning on an empty `credited`
        // dropped the whole of it precisely where it was the only answer.
        var discovered = await db.DiscoveredRecords
            .AsNoTracking()
            .Where(record => ids.Contains(record.ArtistId))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        if (credited.Count == 0 && discovered.Count == 0)
        {
            return (followed.Count, unbrowsed, 0, []);
        }

        // Who a record is filed under on the shelf. A release group credited to
        // two followed artists is one record and must not be two tiles, so the
        // billing is collapsed to one name — alphabetically, so that a rerun
        // cannot change its mind, which is the tie-break the folder-naming rule
        // above already takes for the same reason.
        var billed = credited
            .GroupBy(credit => credit.Group)
            .ToDictionary(
                group => group.Key,
                group => group
                    .Select(credit => names[credit.ArtistId])
                    .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                    .First());

        // ponytail: two EXISTS per group, evaluated over every release group the
        // followed set is credited with. A person-clicked endpoint, and the set
        // is the followed one rather than the catalogue — push it into a single
        // anti-join if somebody follows enough artists for this to show.
        var groups = await db.ReleaseGroups
            .AsNoTracking()
            .Where(group => billed.Keys.Contains(group.Id))
            .Select(group => new
            {
                group.Id,
                group.Mbid,
                group.Title,
                group.PrimaryType,
                group.SecondaryTypes,
                group.FirstReleaseYear,
                group.Monitored,
                Held = group.Releases.Any(release => release.Files.Count != 0)
                    || db.MediaFiles.Any(file => file.ReleaseGroupId == group.Id),
            })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        // Every gap the rule admits, before monitoring is considered. Both
        // numbers reach the screen, because an empty shelf has two opposite
        // causes: nothing is marked, or nothing is missing. Only the first is
        // something a person can act on, and a shelf that cannot tell them apart
        // reads as broken in exactly the case where it is working.
        var gaps = groups
            .Where(group => !group.Held)
            .Select(group => new
            {
                group.Id,
                group.Mbid,
                group.Title,
                group.PrimaryType,
                group.FirstReleaseYear,
                group.Monitored,
                Artist = billed[group.Id],
                Secondary = group.SecondaryTypes is { Length: > 0 } types
                    ? types.Split(", ", StringSplitOptions.RemoveEmptyEntries)
                    : [],
            })
            .Where(group => Discography.IsGap(new ReleaseGroupFacts(
                group.Title,
                group.PrimaryType,
                group.Secondary,
                group.FirstReleaseYear)))
            .ToList();

        var shopGaps = discovered.Count == 0
            ? []
            : await ShopGapsAsync().ConfigureAwait(false);

        var records = gaps
            // The shelf is the wanted list, not the discography. What fills it
            // by itself is `Discography.IsNewRelease` — records dated at or
            // after `Artist.FollowedUtc` — so a newly followed artist's back
            // catalogue is never on it, however many sources describe it and
            // whenever they are first asked. Anything older reaches the shelf
            // only because a person marked it on the artist page.
            //
            // Note this is no longer "empty until the second browse": follow
            // somebody who put a record out this year and it is here on the
            // first one, which is the point.
            .Where(group => group.Monitored)

            // By artist, then the artist page's own order within one. The two
            // shelves above rank by how big a win each row is; there is no such
            // measure here — one record somebody does not own is not a better
            // buy than another — so the ordering that earns its place is the one
            // that keeps an artist's records adjacent, since the tile names the
            // record and the artist is the thing a person scans for.
            .OrderBy(group => group.Artist, StringComparer.OrdinalIgnoreCase)
            .ThenByDescending(group => group.FirstReleaseYear ?? int.MaxValue)
            .ThenBy(group => group.Title, StringComparer.OrdinalIgnoreCase)
            .Select(group => new MissingRecord(
                group.Id.Value,
                group.Mbid?.Value,
                group.Title,
                group.Artist,
                group.FirstReleaseYear,

                // The artist and the title, which is the shape both shelves
                // above search with — and here it is the billing line as well,
                // since a record nobody owns has no printed credit to prefer.
                Join(group.Artist, group.Title),
                group.PrimaryType,
                group.Secondary))
            .ToList();

        // Interleaved rather than appended: the ordering that earns its place
        // here keeps an artist's records adjacent, and a shop's row is one of
        // that artist's records like any other.
        var wanted = records
            .Concat(shopGaps.Where(record => record.Monitored).Select(record => new MissingRecord(
                record.Id.Value,
                null,
                record.Title,
                names[record.ArtistId],
                record.Year,
                Join(names[record.ArtistId], record.Title),
                null,
                [],
                record.Source,
                record.SourceId,
                record.CoverUrl)))
            .OrderBy(record => record.Artist, StringComparer.OrdinalIgnoreCase)
            .ThenByDescending(record => record.Year ?? int.MaxValue)
            .ThenBy(record => record.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return (
            followed.Count,
            unbrowsed,
            gaps.Count + shopGaps.Count - wanted.Count,
            wanted);

        // Local, because it needs `credited`, `groups` and `discovered` and is
        // read once. Per artist, since the catalogue a shop's row is compared
        // against is that artist's records and not the followed set's.
        async Task<List<DiscoveredRecord>> ShopGapsAsync()
        {
            var byArtist = credited
                .GroupBy(credit => credit.ArtistId)
                .ToDictionary(g => g.Key, g => g.Select(credit => credit.Group).ToHashSet());

            var titles = groups.ToDictionary(
                group => group.Id,
                group => new CataloguedRecord(group.Title, group.FirstReleaseYear));

            var barcodes = (await db.Releases
                    .AsNoTracking()
                    .Where(release => release.ReleaseGroupId != null
                        && billed.Keys.Contains(release.ReleaseGroupId.Value)
                        && release.Barcode != null)
                    .Select(release => new
                    {
                        Group = release.ReleaseGroupId!.Value,
                        Barcode = release.Barcode!,
                    })
                    .ToListAsync(cancellationToken)
                    .ConfigureAwait(false))
                .GroupBy(row => row.Group)
                // Normalised where the set is built, for `Discography.IsGap`'s
                // stated reason: a literal compare between a shop's UPC and
                // MusicBrainz's EAN never matches, and fails silently.
                .ToDictionary(
                    g => g.Key,
                    g => g.Select(row => Barcodes.Normalise(row.Barcode))
                        .OfType<string>()
                        .ToList());

            return discovered
                .Where(record =>
                {
                    var mine = byArtist.TryGetValue(record.ArtistId, out var held)
                        ? held
                        : [];

                    return Discography.IsGap(
                        new DiscoveredRecordFacts(
                            record.Title, record.Year, record.Barcode, record.TrackCount),
                        [.. mine.Where(titles.ContainsKey).Select(id => titles[id])],
                        mine.SelectMany(id => barcodes.TryGetValue(id, out var upcs) ? upcs : [])
                            .ToHashSet(StringComparer.Ordinal));
                })
                .ToList();
        }
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
        ReleaseGroupId? AlbumId,
        RecordingId? Recording,
        UpgradeReason Reason,
        string Format,
        string Path);

    private sealed record Grouped(
        string Folder,
        ReleaseGroupId? AlbumId,
        int Files,
        UpgradeReason Reason,
        IReadOnlyList<Assessed> Upgradable);

    /// <summary>What a row is headed with: the album's, or the folder's own name where no album is held.</summary>
    private sealed record Heading(
        ReleaseGroupId? AlbumId,
        Guid? Mbid,
        Guid? CoverReleaseId,
        string Title,
        string? Artist,
        int? Year,
        string Query);

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
/// <param name="Missing">
/// Records by followed artists that no file here sits under. The third question,
/// and the only one whose unit is not a file — see <c>MissingRecordsAsync</c>.
/// </param>
/// <param name="FollowedArtists">
/// How many artists somebody has followed. The denominator for
/// <paramref name="Missing"/>, and what tells an empty list from an unused
/// feature: nobody following anybody is not the same as following people who
/// have released nothing new.
/// </param>
/// <param name="UnbrowsedArtists">
/// Of those, how many MusicBrainz has never been asked about. The three-state
/// lesson <c>ArtistDiscography.FetchedAtUtc</c> already paid for, counted rather
/// than stamped because this list spans artists: an empty shelf with a non-zero
/// count here means a pass has not run, not that there is nothing to buy.
/// </param>
/// <param name="UnmonitoredGaps">
/// Records by followed artists that the library has not got and nobody has
/// marked. Not on the shelf, and counted so the screen can tell its two empty
/// states apart: nothing marked is a sentence about the artist page, nothing
/// missing is good news, and they look identical from the length of
/// <paramref name="Missing"/> alone.
/// </param>
public sealed record UpgradeListResponse(
    int Files,
    IReadOnlyList<UpgradeCandidate> Items,
    IReadOnlyList<IncompleteAlbum> Incomplete,
    int UnmatchedAlbums,
    IReadOnlyList<MissingRecord> Missing,
    int FollowedArtists,
    int UnbrowsedArtists,
    int UnmonitoredGaps);

/// <summary>A record by a followed artist that the library holds no file of.</summary>
/// <param name="ReleaseGroupId">
/// The catalogue's own id for the release group, or — where
/// <paramref name="Source"/> is set — the discovered record's. There is no page
/// for a record nothing is filed under, so this identifies the row rather than
/// linking anywhere: it is the key the shelf renders on, and the two id spaces
/// are both <c>Guid</c> and never mixed within one row.
/// </param>
/// <param name="Mbid">
/// MusicBrainz's id for the group, and here for the sleeve: the Cover Art
/// Archive redirects a group to whichever of its releases has artwork, which is
/// the only key available when no pressing has been chosen because none has been
/// owned. Null is not expected — the browse that wrote the row is keyed on one.
/// </param>
/// <param name="Artist">
/// Which followed artist this is filed under. One name, not a billing line: a
/// group credited to two followed artists is one record and one tile.
/// </param>
/// <param name="Year">
/// The earliest release in the group, or null where MusicBrainz holds no date —
/// an unreleased or newly announced record, which is worth showing rather than
/// hiding.
/// </param>
/// <param name="Query">What to search Qobuz for — the artist and the title.</param>
/// <param name="PrimaryType">
/// <c>Album</c>, <c>EP</c> — or null, which means nobody has typed it rather
/// than that it is none of them. Printed because <c>Discography.IsGap</c> lets
/// untyped groups through, so a tile has to be able to say so.
/// </param>
/// <param name="Source">
/// Which shop named this record, where the catalogue has never heard of it —
/// and null for a MusicBrainz row, which is how a reader tells the two apart.
/// <b>One shelf and not two, unlike the artist page.</b> That page counts what
/// MusicBrainz knows and must not mix a shop's inventory into the figure; this
/// one is the wanted list, and what somebody wants is one list ordered by artist
/// whatever named each row.
/// </param>
/// <param name="SourceId">
/// The shop's own id for it, so the press can open that album rather than search
/// for its title. Null wherever <paramref name="Source"/> is.
/// </param>
/// <param name="CoverUrl">
/// The sleeve as the shop serves it. The Cover Art Archive is keyed on an MBID
/// and a record it has never heard of has none, so this is the only picture
/// there is for one of these rows — and dropping it, as the old shape did, is
/// what left 13 of one artist's 31 rows permanently showing a monogram.
/// </param>
public sealed record MissingRecord(
    Guid ReleaseGroupId,
    Guid? Mbid,
    string Title,
    string Artist,
    int? Year,
    string Query,
    string? PrimaryType,
    IReadOnlyList<string> SecondaryTypes,
    string? Source = null,
    string? SourceId = null,
    string? CoverUrl = null);

/// <summary>An album held in part, with what is missing from it named.</summary>
/// <param name="AlbumId">The album (release group) the folder's files are held to.</param>
/// <param name="EditionId">
/// The edition the gap is measured against: the claimed pressing where every file
/// is filed under one, otherwise the stored edition the files are most plausibly
/// a rip of. Not a claim that the files are it.
/// </param>
/// <param name="CoverReleaseId">The edition whose stored sleeve stands for the album.</param>
/// <param name="Mbid">The release group's MusicBrainz identifier, for a cover where no sleeve is stored.</param>
/// <param name="Folder">The album folder its files sit in, for a person to recognise it by.</param>
/// <param name="Query">The first billed artist and the title, as on an upgrade row.</param>
/// <param name="MediumFormats">
/// CD, Digital Media, <c>CD+DVD-Video</c>. The honest explanation for a gap
/// nobody can buy their way out of: a release with a video disc on it is missing
/// half its track list on any library that holds only the audio.
/// </param>
/// <param name="TrackCount">What that edition prints. The denominator.</param>
/// <param name="Held">Its tracks the album's files hold, wherever they sit.</param>
/// <param name="Unmatched">
/// Files on none of the measured edition's tracks, in every folder this album's
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
    Guid AlbumId,
    Guid EditionId,
    Guid? CoverReleaseId,
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

/// <param name="AlbumId">
/// The album most of the folder is held to, or null when no pass placed any of
/// it. The <i>row</i> is the folder either way — see <c>ListUpgrades</c> — so
/// this says where the title and the billing line came from rather than what is
/// being counted.
/// </param>
/// <param name="CoverReleaseId">
/// The edition whose stored sleeve stands for that album; null where it has none
/// stored, and wherever <paramref name="AlbumId"/> is, so a folder no pass placed
/// shows a monogram — which is the honest picture of an album nothing has
/// identified.
/// </param>
/// <param name="Mbid">The album's release group id, the Cover Art Archive's key where no sleeve is stored.</param>
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
    Guid? AlbumId,
    Guid? CoverReleaseId,
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
