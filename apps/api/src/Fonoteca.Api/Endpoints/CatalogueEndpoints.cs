using Fonoteca.Api.Library;
using Fonoteca.Data;
using Fonoteca.Domain.Catalogue;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace Fonoteca.Api.Endpoints;

/// <summary>
/// Reading the catalogue back out. The first endpoints that do.
/// </summary>
/// <remarks>
/// Same shape as <see cref="SystemEndpoints"/> and <see cref="LibraryEndpoints"/>:
/// one static class per capability, one <c>MapXEndpoints</c> extension, DTOs at
/// the bottom of the file.
///
/// <b>An artist's tracks come from three places, not one.</b> Being on the
/// printed credit line is only the most obvious way to be responsible for a
/// recording; MusicBrainz bills a classical recording to its composer and leaves
/// the conductor and the orchestra in relationships, and the composer link
/// itself hangs off the <i>work</i> rather than the performance. A query that
/// reads <c>ArtistCredits</c> alone therefore shows Karajan nothing and files
/// every symphony under a man who died in 1827. All three are unioned here, and
/// which one matched is returned as the track's <c>Roles</c> so the answer is
/// legible rather than mysterious.
///
/// <b>Only recordings that have a file are listed.</b> This is a library
/// browser, not a MusicBrainz browser: an artist page that included every
/// recording MusicBrainz links to them would be mostly music the user does not
/// own, and the one question it exists to answer — "what do I have by this
/// artist" — would be the one it could not answer.
/// </remarks>
public static class CatalogueEndpoints
{
    /// <summary>
    /// The most artists one response will carry.
    /// </summary>
    /// <remarks>
    /// Measured, and larger than it first looks like it needs to be. The guess
    /// was "a few hundred" from 159 top-level library directories; the answer
    /// against the real library is <b>2,752</b>, because every songwriter and
    /// lyricist of every pop song is an artist you can browse to — most of them
    /// with one track. A cap of 500 silently truncated the list at the letter D
    /// with no way to reach the rest but the filter.
    ///
    /// So the default carries a library several times this one's size in one
    /// response, and <see cref="MaxTake"/> is the backstop rather than the
    /// working limit. Paging stays in the contract because a hundred-thousand
    /// track library will need it, and because it costs nothing to have it
    /// already there when it does.
    /// </remarks>
    private const int DefaultTake = 10_000;

    private const int MaxTake = 25_000;

    public static IEndpointRouteBuilder MapCatalogueEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/catalogue").WithTags("Catalogue");

        group.MapGet("/artists", GetArtists)
            .WithName("GetArtists")
            .WithSummary("Artists with at least one track in the library.")
            .WithDescription(
                "Ordered by sort name. `query` filters on the artist's name, case-insensitively, "
                + "anywhere in the string. An artist appears here if they are on a recording's "
                + "credit line, are linked to it as a conductor or ensemble, or wrote the work it "
                + "performs — and only when at least one file in the library holds that recording.");

        group.MapGet("/artists/{id:guid}", GetArtist)
            .WithName("GetArtist")
            .WithSummary("One artist and every track of theirs in the library.")
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapGet("/releases", GetReleases)
            .WithName("GetReleases")
            .WithSummary("Albums the library holds at least one track of.")
            .WithDescription(
                "Ordered by title. `query` filters on the release title, case-insensitively, "
                + "anywhere in the string. `held` against `trackCount` is what an incomplete rip "
                + "looks like — though a CD+DVD-Video release is legitimately half missing on an "
                + "audio-only library, which is why the medium formats are returned beside them.");

        group.MapGet("/releases/{id:guid}", GetRelease)
            .WithName("GetRelease")
            .WithSummary("One release and its whole track list, held or not.")
            .WithDescription(
                "The entire track list as MusicBrainz prints it, with each track flagged for "
                + "whether the library holds it — so a missing track is visible as a gap rather "
                + "than as an absence.")
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapGet("/attribution", GetAttributionReport)
            .WithName("GetAttributionReport")
            .WithSummary("How the attributed albums compare with the folders on disk.")
            .WithDescription(
                "The folders play no part in deciding which release a file came from. This is "
                + "where they are used instead: as an independent second opinion. A folder split "
                + "across releases, or a release spanning folders, is a disagreement worth a "
                + "person's attention — and either side may be the wrong one.");

        return app;
    }

    private static async Task<Ok<ArtistListResponse>> GetArtists(
        FonotecaDbContext db,
        CancellationToken cancellationToken,
        string? query = null,
        int skip = 0,
        int take = DefaultTake)
    {
        var wanted = Math.Clamp(take, 1, MaxTake);
        var from = Math.Max(skip, 0);

        var matching = db.Artists.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(query))
        {
            // ILIKE, through EF.Functions, so the pg_trgm GIN index on Name can
            // serve it. Contains() would compile to the same operator only for
            // some providers, and being explicit is the difference between an
            // index scan and a sequential one at a hundred thousand rows.
            var pattern = $"%{Escape(query.Trim())}%";
            matching = matching.Where(a => EF.Functions.ILike(a.Name, pattern, "\\"));
        }

        var counts = await CountsByArtistAsync(db, artist: null, cancellationToken).ConfigureAwait(false);

        var named = await matching
            // Ordered here so the collation doing the work is PostgreSQL's, which
            // is the ICU en-US one compose.yaml pins. Sorting these in .NET would
            // quietly use the server process's culture instead.
            //
            // SortName first — "Beatles, The" is what an alphabetical list wants
            // and Name is not — falling back to Name rather than sorting nulls
            // wherever the collation puts them, with the id breaking ties so
            // paging cannot show or skip a row.
            .OrderBy(a => a.SortName ?? a.Name)
            .ThenBy(a => a.Id)
            .Select(a => new
            {
                a.Id,
                a.Name,
                a.SortName,
                a.Disambiguation,
                a.Type,
            })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var withTracks = named
            .Select(a => (Artist: a, Count: counts.GetValueOrDefault(a.Id)))
            .Where(row => row.Count > 0)
            .ToList();

        var page = withTracks
            .Skip(from)
            .Take(wanted)
            .Select(row => new ArtistSummary(
                row.Artist.Id.Value,
                row.Artist.Name,
                row.Artist.SortName,
                row.Artist.Disambiguation,
                row.Artist.Type,
                row.Count))
            .ToList();

        return TypedResults.Ok(new ArtistListResponse(withTracks.Count, page));
    }

    private static async Task<Results<Ok<ArtistDetailResponse>, ProblemHttpResult>> GetArtist(
        Guid id,
        FonotecaDbContext db,
        CancellationToken cancellationToken)
    {
        var artistId = new ArtistId(id);

        var artist = await db.Artists
            .AsNoTracking()
            .FirstOrDefaultAsync(a => a.Id == artistId, cancellationToken)
            .ConfigureAwait(false);

        if (artist is null)
        {
            return TypedResults.Problem(
                title: "No such artist",
                detail: $"The catalogue has no artist with id {id}.",
                statusCode: StatusCodes.Status404NotFound);
        }

        var theirs = await RecordingsOfAsync(db, artistId, cancellationToken).ConfigureAwait(false);

        var tracks = await db.Recordings
            .Where(recording => theirs.Contains(recording.Id))
            .AsNoTracking()
            .Select(recording => new
            {
                recording.Id,
                recording.Title,
                recording.Duration,
                WorkTitle = recording.Work == null ? null : recording.Work.Title,
                Billed = recording.Credits.Any(c => c.ArtistId == artistId),
                Roles = recording.Relationships
                    .Where(r => r.ArtistId == artistId)
                    .Select(r => r.Type)
                    .ToList(),
                WorkRoles = recording.Work == null
                    ? new List<string>()
                    : recording.Work.Relationships
                        .Where(r => r.ArtistId == artistId)
                        .Select(r => r.Type)
                        .ToList(),
                Files = recording.Files
                    .OrderBy(f => f.Path)
                    .Select(f => new
                    {
                        f.Path,
                        f.SizeBytes,
                        f.ReleaseId,
                        ReleaseTitle = f.Release == null ? null : f.Release.Title,
                        ReleaseYear = f.Release == null ? null : f.Release.ReleasedYear,
                    })
                    .ToList(),
            })
            .OrderBy(row => row.Title)
            .ThenBy(row => row.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var rows = tracks
            .Select(row => new TrackRow(
                RecordingId: row.Id.Value,
                Title: row.Title,
                WorkTitle: row.WorkTitle,
                // Formatted here rather than sent as a number: this is a display
                // string, and the alternative — seconds, formatted in the browser
                // — puts the same decision in a second place.
                Duration: Format(row.Duration),
                Roles: Roles(row.Billed, row.Roles, row.WorkRoles),
                Album: row.Files
                    .Where(file => file.ReleaseId != null)
                    .Select(file => new TrackAlbum(
                        file.ReleaseId!.Value.Value,
                        file.ReleaseTitle!,
                        file.ReleaseYear))
                    .FirstOrDefault(),
                Folder: FolderOf(row.Files[0].Path),
                Files: [.. row.Files.Select(file => new FileRow(file.Path, file.SizeBytes))]))
            .ToList();

        return TypedResults.Ok(new ArtistDetailResponse(
            new ArtistSummary(
                artist.Id.Value,
                artist.Name,
                artist.SortName,
                artist.Disambiguation,
                artist.Type,
                rows.Count),
            rows));
    }

    private static async Task<Ok<ReleaseListResponse>> GetReleases(
        FonotecaDbContext db,
        CancellationToken cancellationToken,
        string? query = null,
        int skip = 0,
        int take = DefaultTake)
    {
        take = Math.Clamp(take, 1, MaxTake);
        skip = Math.Max(skip, 0);

        // Only releases something was actually filed under. The attribution pass
        // writes no others, so this is belt and braces — but a release whose only
        // files were later removed by a scan would otherwise linger as an album
        // the library does not have.
        var releases = db.Releases.AsNoTracking().Where(release => release.Files.Any());

        if (!string.IsNullOrWhiteSpace(query))
        {
            var pattern = $"%{Escape(query.Trim())}%";
            releases = releases.Where(release => EF.Functions.ILike(release.Title, pattern, "\\"));
        }

        var total = await releases.CountAsync(cancellationToken).ConfigureAwait(false);

        var rows = await releases
            .OrderBy(release => release.Title)
            .ThenBy(release => release.Id)
            .Skip(skip)
            .Take(take)
            .Select(release => new
            {
                release.Id,
                release.Title,
                release.ReleasedYear,
                release.Country,
                release.Status,
                release.MediumFormats,
                release.DiscCount,
                TrackCount = release.TrackCount ?? release.Tracks.Count,
                Held = release.Files.Where(f => f.TrackId != null).Select(f => f.TrackId).Distinct().Count(),
                Files = release.Files.Count,
                Artists = release.Credits
                    .OrderBy(credit => credit.Position)
                    .Select(credit => new { credit.CreditedAs, credit.JoinPhrase, credit.Artist!.Name })
                    .ToList(),

                // The weakest claim any of its files carries, so a release with
                // one coin-flip in it does not read as certain.
                Certainty = release.Files.Max(file => (int)file.AttributionOutcome),
                Alternatives = release.Files.Max(file => file.EditionAlternatives),
            })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var items = rows
            .Select(row => new ReleaseSummary(
                row.Id.Value,
                row.Title,
                CreditLine(row.Artists.Select(a => (a.CreditedAs ?? a.Name, a.JoinPhrase))),
                row.ReleasedYear,
                row.Country,
                row.Status,
                row.MediumFormats,
                row.DiscCount,
                row.TrackCount,
                row.Held,
                row.Files,
                ((ReleaseAttributionOutcome)row.Certainty).ToString(),
                row.Alternatives))
            .ToList();

        return TypedResults.Ok(new ReleaseListResponse(total, items));
    }

    private static async Task<Results<Ok<ReleaseDetailResponse>, ProblemHttpResult>> GetRelease(
        Guid id,
        FonotecaDbContext db,
        CancellationToken cancellationToken)
    {
        var releaseId = new ReleaseId(id);

        var release = await db.Releases
            .AsNoTracking()
            .Where(r => r.Id == releaseId)
            .Select(r => new
            {
                r.Id,
                r.Title,
                r.ReleasedYear,
                r.Country,
                r.Status,
                r.MediumFormats,
                r.DiscCount,
                TrackCount = r.TrackCount ?? r.Tracks.Count,
                Held = r.Files.Where(f => f.TrackId != null).Select(f => f.TrackId).Distinct().Count(),
                Files = r.Files.Count,
                Artists = r.Credits
                    .OrderBy(credit => credit.Position)
                    .Select(credit => new { credit.CreditedAs, credit.JoinPhrase, credit.Artist!.Name })
                    .ToList(),
                Certainty = r.Files.Max(file => (int)file.AttributionOutcome),
                Alternatives = r.Files.Max(file => file.EditionAlternatives),
            })
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        if (release is null)
        {
            return TypedResults.Problem(
                title: "No such release",
                detail: $"The catalogue has no release with id {id}.",
                statusCode: StatusCodes.Status404NotFound);
        }

        var tracks = await db.Tracks
            .AsNoTracking()
            .Where(track => track.ReleaseId == releaseId)
            .OrderBy(track => track.DiscNumber)
            .ThenBy(track => track.Position)
            .Select(track => new
            {
                track.DiscNumber,
                track.Position,
                track.Number,
                track.Title,
                track.Length,
                RecordingId = track.RecordingId,

                // Files linked to *this track*, not merely holding the same
                // recording. A recording the library owns twice — once here and
                // once on a compilation — must not make both look held.
                Files = db.MediaFiles
                    .Where(file => file.TrackId == track.Id)
                    .OrderBy(file => file.Path)
                    .Select(file => new { file.Path, file.SizeBytes })
                    .ToList(),
            })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var rows = tracks
            .Select(track => new ReleaseTrackRow(
                track.DiscNumber,
                track.Position,
                track.Number,
                track.Title ?? string.Empty,
                Format(track.Length),
                track.RecordingId.Value,
                track.Files.Count > 0,
                [.. track.Files.Select(file => new FileRow(file.Path, file.SizeBytes))]))
            .ToList();

        return TypedResults.Ok(new ReleaseDetailResponse(
            new ReleaseSummary(
                release.Id.Value,
                release.Title,
                CreditLine(release.Artists.Select(a => (a.CreditedAs ?? a.Name, a.JoinPhrase))),
                release.ReleasedYear,
                release.Country,
                release.Status,
                release.MediumFormats,
                release.DiscCount,
                release.TrackCount,
                release.Held,
                release.Files,
                ((ReleaseAttributionOutcome)release.Certainty).ToString(),
                release.Alternatives),
            rows));
    }

    /// <summary>
    /// The one place the folders are read: to disagree with the answer.
    /// </summary>
    /// <remarks>
    /// Attribution never looks at a directory, so the two groupings are genuinely
    /// independent and a disagreement is real evidence rather than a tautology.
    /// It is deliberately not stated which side is wrong. Measured against a real
    /// library, both happen: a folder named "Live From the Royal Albert Hall
    /// (2009)" holds a release MusicBrainz dates to 2010, and a folder named for
    /// a 1979 album holds the audio of its 2015 remaster — while elsewhere a
    /// CD+DVD-Video release really does split a folder in two because three of
    /// its tracks are on the video disc.
    ///
    /// Done in memory over paths, because the folder is not a column: it is the
    /// leading part of <c>MediaFile.Path</c>, and teaching PostgreSQL to group by
    /// it would mean an expression index for a report nobody runs in a loop.
    /// </remarks>
    private static async Task<Ok<AttributionReportResponse>> GetAttributionReport(
        FonotecaDbContext db,
        CancellationToken cancellationToken)
    {
        var outcomes = await db.MediaFiles
            .AsNoTracking()
            .GroupBy(file => file.AttributionOutcome)
            .Select(group => new { Outcome = group.Key, Count = group.Count() })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var placed = await db.MediaFiles
            .AsNoTracking()
            .Where(file => file.ReleaseId != null)
            .Select(file => new
            {
                file.Path,
                ReleaseId = file.ReleaseId!.Value,
                ReleaseTitle = file.Release!.Title,
            })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var byFolder = placed
            .GroupBy(file => FolderOf(file.Path))
            .Select(folder => new
            {
                Folder = folder.Key,
                Releases = folder
                    .GroupBy(file => (file.ReleaseId, file.ReleaseTitle))
                    .Select(release => new AttributionShare(
                        release.Key.ReleaseId.Value,
                        release.Key.ReleaseTitle,
                        release.Count()))
                    .OrderByDescending(share => share.Files)
                    .ThenBy(share => share.Title, StringComparer.Ordinal)
                    .ToList(),
            })
            .ToList();

        var split = byFolder
            .Where(folder => folder.Releases.Count > 1)
            .OrderByDescending(folder => folder.Releases.Count)
            .ThenBy(folder => folder.Folder, StringComparer.Ordinal)
            .Select(folder => new FolderDisagreement(folder.Folder, folder.Releases))
            .ToList();

        var spanning = placed
            .GroupBy(file => (file.ReleaseId, file.ReleaseTitle))
            .Select(release => new
            {
                release.Key,
                Folders = release
                    .GroupBy(file => FolderOf(file.Path))
                    .Select(folder => new AttributionShare(
                        release.Key.ReleaseId.Value,
                        folder.Key,
                        folder.Count()))
                    .OrderByDescending(share => share.Files)
                    .ThenBy(share => share.Title, StringComparer.Ordinal)
                    .ToList(),
            })
            .Where(release => release.Folders.Count > 1)
            .OrderByDescending(release => release.Folders.Count)
            .Select(release => new ReleaseDisagreement(
                release.Key.ReleaseId.Value,
                release.Key.ReleaseTitle,
                release.Folders))
            .ToList();

        var incomplete = await db.Releases
            .AsNoTracking()
            .Where(release => release.Files.Any()
                && release.Files.Where(f => f.TrackId != null).Select(f => f.TrackId).Distinct().Count()
                    < (release.TrackCount ?? 0))
            .OrderBy(release => release.Title)
            .Select(release => new IncompleteRelease(
                release.Id.Value,
                release.Title,
                release.Files.Where(f => f.TrackId != null).Select(f => f.TrackId).Distinct().Count(),
                release.TrackCount ?? 0,
                release.MediumFormats))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return TypedResults.Ok(new AttributionReportResponse(
            Outcomes: [.. outcomes
                .OrderBy(entry => entry.Outcome)
                .Select(entry => new OutcomeCount(entry.Outcome.ToString(), entry.Count))],
            Folders: byFolder.Count,
            FoldersAgreeing: byFolder.Count - split.Count,
            FoldersSplit: split,
            ReleasesSpanningFolders: spanning,
            Incomplete: incomplete));
    }

    /// <summary>
    /// "Beth Hart &amp; Joe Bonamassa" from the parts that printed it.
    /// </summary>
    /// <remarks>
    /// The join phrase belongs to the credit before the gap, so it is appended
    /// rather than inserted between — which is what keeps "feat." and "&amp;" and
    /// ", " each in the place the sleeve put them.
    /// </remarks>
    private static string? CreditLine(IEnumerable<(string Name, string? JoinPhrase)> credits)
    {
        var line = string.Concat(credits.Select(credit => credit.Name + credit.JoinPhrase));
        return string.IsNullOrWhiteSpace(line) ? null : line;
    }

    /// <summary>
    /// Every (artist, recording) link the library can be browsed by.
    /// </summary>
    /// <remarks>
    /// The three ways of being responsible for a recording — on its printed
    /// credit line, linked to it as conductor or ensemble, or a writer of the
    /// work it performs. The third is why a query cannot stop at the first:
    /// MusicBrainz puts the composer on the work, so joining there is what
    /// spreads one "composer" link across every performance of the piece.
    ///
    /// <b>Read from the links inward, not from the recordings outward, and the
    /// difference is eight seconds.</b> Written the obvious way — a predicate
    /// over <c>Recordings</c> with three <c>EXISTS</c> branches OR'd together,
    /// evaluated once per artist — PostgreSQL cannot use an index to find an
    /// artist's recordings, because the OR is a per-row test. On the author's
    /// library that is 2,752 artists x 7,274 recordings, and the artist list
    /// took 7.8 seconds. Starting from <c>ArtistCredits</c> and
    /// <c>Relationships</c>, both of which are indexed by artist, it is three
    /// index scans.
    ///
    /// The union happens in memory rather than in SQL because EF refuses a set
    /// operation over a projection to a type of ours ("unable to translate set
    /// operation after client projection has been applied"). Deduplicating here
    /// matters: an artist who both conducted a recording and is billed on it has
    /// one track, not two.
    ///
    /// <paramref name="artist"/> narrows all three queries when only one artist
    /// is wanted, which is what lets the list and the detail page share one copy
    /// of the rule rather than drifting apart. <c>TheListAndTheDetailPageAgree</c>
    /// asserts they still do.
    /// </remarks>
    private static async Task<HashSet<ArtistTrack>> BrowsableAsync(
        FonotecaDbContext db,
        ArtistId? artist,
        CancellationToken cancellationToken)
    {
        // The library filter: a recording with no file is one the catalogue
        // learned about some other way, and this is a browser for what is owned.
        var present = db.Recordings.Where(recording => recording.Files.Any()).Select(r => r.Id);

        var credits = db.ArtistCredits.AsNoTracking().Where(c => c.RecordingId != null);
        var links = db.Relationships.AsNoTracking().Where(r => r.RecordingId != null);
        var wrote = db.Relationships.AsNoTracking().Where(r => r.WorkId != null);

        // Applied as a separate Where rather than folded in as `artist == null ||
        // …`, which would put a parameter-is-null test in the SQL and cost the
        // planner the index.
        if (artist is { } only)
        {
            credits = credits.Where(c => c.ArtistId == only);
            links = links.Where(r => r.ArtistId == only);
            wrote = wrote.Where(r => r.ArtistId == only);
        }

        var pairs = new HashSet<ArtistTrack>();

        foreach (var pair in await credits
            .Where(c => present.Contains(c.RecordingId!.Value))
            .Select(c => new ArtistTrack(c.ArtistId, c.RecordingId!.Value))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false))
        {
            pairs.Add(pair);
        }

        foreach (var pair in await links
            .Where(r => r.ArtistId != null && present.Contains(r.RecordingId!.Value))
            .Select(r => new ArtistTrack(r.ArtistId!.Value, r.RecordingId!.Value))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false))
        {
            pairs.Add(pair);
        }

        // The composer hop: joined on the work, which is what carries one link
        // across every recording of the piece.
        foreach (var pair in await wrote
            .Where(r => r.ArtistId != null)
            .Join(
                db.Recordings.Where(recording => recording.Files.Any()),
                link => link.WorkId,
                recording => recording.WorkId,
                (link, recording) => new ArtistTrack(link.ArtistId!.Value, recording.Id))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false))
        {
            pairs.Add(pair);
        }

        return pairs;
    }

    /// <summary>How many browsable recordings each artist has.</summary>
    private static async Task<Dictionary<ArtistId, int>> CountsByArtistAsync(
        FonotecaDbContext db,
        ArtistId? artist,
        CancellationToken cancellationToken)
    {
        var pairs = await BrowsableAsync(db, artist, cancellationToken).ConfigureAwait(false);

        var counts = new Dictionary<ArtistId, int>();

        foreach (var pair in pairs)
        {
            counts[pair.ArtistId] = counts.GetValueOrDefault(pair.ArtistId) + 1;
        }

        return counts;
    }

    /// <summary>The recordings one artist is responsible for.</summary>
    private static async Task<HashSet<RecordingId>> RecordingsOfAsync(
        FonotecaDbContext db,
        ArtistId artist,
        CancellationToken cancellationToken)
    {
        var pairs = await BrowsableAsync(db, artist, cancellationToken).ConfigureAwait(false);

        return [.. pairs.Select(pair => pair.RecordingId)];
    }

    /// <summary>Why this artist has this track, strongest claim first.</summary>
    private static IReadOnlyList<string> Roles(
        bool billed,
        List<string> recordingRoles,
        List<string> workRoles)
    {
        var roles = new List<string>(recordingRoles.Count + workRoles.Count + 1);

        if (billed) roles.Add("billed");

        roles.AddRange(recordingRoles);
        roles.AddRange(workRoles);

        return [.. roles.Distinct(StringComparer.Ordinal)];
    }

    /// <summary>
    /// The folder a track's files sit in, which stands in for the album.
    /// </summary>
    /// <remarks>
    /// The filesystem's claim, not MusicBrainz's, and labelled as such in the
    /// UI. Releases are not attributed yet — deciding which of the thirty
    /// releases a recording appears on a given file actually came from is a rule
    /// of its own — and until they are, the directory is the only album-shaped
    /// fact the catalogue holds. It is also usually right.
    /// </remarks>
    private static string FolderOf(string path)
    {
        var cut = path.LastIndexOf('/');
        return cut <= 0 ? string.Empty : path[..cut];
    }

    /// <summary>Duration as <c>m:ss</c>, or <c>h:mm:ss</c> past the hour.</summary>
    private static string? Format(TimeSpan? duration) => duration switch
    {
        null => null,
        { TotalHours: >= 1 } value => $"{(int)value.TotalHours}:{value.Minutes:D2}:{value.Seconds:D2}",
        { } value => $"{(int)value.TotalMinutes}:{value.Seconds:D2}",
    };

    /// <summary>
    /// Neutralises the wildcards in a user's filter.
    /// </summary>
    /// <remarks>
    /// Without this a search for <c>%</c> matches everything and a search for
    /// <c>_</c> matches every single character — not a security hole, since the
    /// value is still parameterised, but a search box that lies about what it
    /// found.
    /// </remarks>
    private static string Escape(string value) => value
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("%", "\\%", StringComparison.Ordinal)
        .Replace("_", "\\_", StringComparison.Ordinal);

    /// <summary>One artist's claim on one recording. Never leaves this file.</summary>
    private readonly record struct ArtistTrack(ArtistId ArtistId, RecordingId RecordingId);
}

/// <summary>A page of artists, with the total the filter matched.</summary>
/// <remarks>
/// <c>Total</c> is the count before paging, so a client can say "showing 50 of
/// 312" without a second request.
/// </remarks>
public sealed record ArtistListResponse(int Total, IReadOnlyList<ArtistSummary> Items);

/// <param name="Type">Person, Group, Orchestra, Choir. Null when MusicBrainz does not say.</param>
/// <param name="TrackCount">Recordings in the library this artist is credited on.</param>
public sealed record ArtistSummary(
    Guid Id,
    string Name,
    string? SortName,
    string? Disambiguation,
    string? Type,
    int TrackCount);

/// <summary>One artist and every track of theirs the library holds.</summary>
public sealed record ArtistDetailResponse(ArtistSummary Artist, IReadOnlyList<TrackRow> Tracks);

/// <param name="WorkTitle">The composition, when MusicBrainz links one. Usually null outside classical.</param>
/// <param name="Duration">Pre-formatted for display; null when MusicBrainz does not know it.</param>
/// <param name="Roles">Why this artist has this track: "billed", "conductor", "ensemble", "composer".</param>
/// <param name="Album">
/// The release this track was attributed to, when one was. Null where attribution
/// declined, which is a real answer and not a gap to be papered over — the page
/// falls back to <paramref name="Folder"/> there and says which it is showing.
/// </param>
/// <param name="Folder">
/// The directory the files sit in: the filesystem's claim about the album, which
/// attribution deliberately never reads. Still returned, because where the
/// catalogue has no answer it is the only one there is.
/// </param>
/// <param name="Files">
/// Every file holding this recording. More than one is the point rather than a
/// problem: it is the same recording in several encodings, which is what the
/// Recording/MediaFile split exists to express.
/// </param>
public sealed record TrackRow(
    Guid RecordingId,
    string Title,
    string? WorkTitle,
    string? Duration,
    IReadOnlyList<string> Roles,
    TrackAlbum? Album,
    string Folder,
    IReadOnlyList<FileRow> Files);

/// <summary>The album a track was attributed to, as much of it as a row needs.</summary>
public sealed record TrackAlbum(Guid ReleaseId, string Title, int? Year);

public sealed record FileRow(string Path, long SizeBytes);

/// <summary>A page of releases, with the total the filter matched.</summary>
public sealed record ReleaseListResponse(int Total, IReadOnlyList<ReleaseSummary> Items);

/// <param name="Artist">The release's own billing line, which a compilation's tracks do not share.</param>
/// <param name="Year">
/// The release year alone. MusicBrainz knows no more than that for about half of
/// a real library, and a full date was never available to invent.
/// </param>
/// <param name="Formats">CD, Digital Media, CD+DVD-Video. Why a rip may be legitimately partial.</param>
/// <param name="TrackCount">Tracks MusicBrainz prints. The number a rip is measured against.</param>
/// <param name="Held">
/// Distinct tracks of this release the library holds. Tracks, not files: five
/// encodings of one song are one track of the album, and counting files makes a
/// half-ripped album read as complete.
/// </param>
/// <param name="Files">Files filed under it, which exceeds <paramref name="Held"/> where a track is held twice.</param>
/// <param name="Certainty">
/// The weakest claim any of its files carries — <c>Attributed</c>,
/// <c>AttributedAmbiguously</c> or <c>GroupOnly</c>. Weakest rather than
/// commonest, so one coin-flip in an album does not read as certainty.
/// </param>
/// <param name="EditionAlternatives">How many other pressings fitted exactly as well.</param>
public sealed record ReleaseSummary(
    Guid Id,
    string Title,
    string? Artist,
    int? Year,
    string? Country,
    string? Status,
    string? Formats,
    int? DiscCount,
    int TrackCount,
    int Held,
    int Files,
    string Certainty,
    int EditionAlternatives);

/// <summary>One release and every track on it, held or not.</summary>
public sealed record ReleaseDetailResponse(ReleaseSummary Release, IReadOnlyList<ReleaseTrackRow> Tracks);

/// <param name="Number">The printed number, which is not always the position: "A1", "12a".</param>
/// <param name="Held">Whether the library has a file filed under this track.</param>
public sealed record ReleaseTrackRow(
    int DiscNumber,
    int Position,
    string? Number,
    string Title,
    string? Duration,
    Guid RecordingId,
    bool Held,
    IReadOnlyList<FileRow> Files);

/// <summary>
/// What the folders make of the attribution.
/// </summary>
/// <remarks>
/// A second opinion, not a correction. Attribution never reads a directory, so
/// the two groupings are independent — which is what makes a disagreement worth
/// looking at, and also why neither side is presented as the right one.
/// </remarks>
public sealed record AttributionReportResponse(
    IReadOnlyList<OutcomeCount> Outcomes,

    /// <summary>Folders holding at least one attributed file.</summary>
    int Folders,

    /// <summary>Folders whose files all landed on one release.</summary>
    int FoldersAgreeing,

    IReadOnlyList<FolderDisagreement> FoldersSplit,
    IReadOnlyList<ReleaseDisagreement> ReleasesSpanningFolders,
    IReadOnlyList<IncompleteRelease> Incomplete);

public sealed record OutcomeCount(string Outcome, int Files);

/// <summary>One folder whose files were filed under more than one release.</summary>
public sealed record FolderDisagreement(string Folder, IReadOnlyList<AttributionShare> Releases);

/// <summary>One release whose files came from more than one folder.</summary>
public sealed record ReleaseDisagreement(Guid ReleaseId, string Title, IReadOnlyList<AttributionShare> Folders);

/// <summary>How many files one side of a disagreement accounts for.</summary>
public sealed record AttributionShare(Guid ReleaseId, string Title, int Files);

/// <param name="Formats">
/// Read this before treating the gap as damage: a CD+DVD-Video release is
/// legitimately half missing on a library that holds only the audio.
/// </param>
public sealed record IncompleteRelease(
    Guid ReleaseId,
    string Title,
    int Held,
    int TrackCount,
    string? Formats);
