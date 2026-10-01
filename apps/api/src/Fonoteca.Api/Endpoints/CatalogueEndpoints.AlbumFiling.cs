using System.Text.Json;
using Fonoteca.Api.Library;
using Fonoteca.Api.Matching;
using Fonoteca.Data;
using Fonoteca.Domain.Abstractions;
using Fonoteca.Domain.Catalogue;
using Fonoteca.Domain.Events;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace Fonoteca.Api.Endpoints;

/// <summary>
/// The Identify screen's question asked at the album, not at the pressing.
/// </summary>
/// <remarks>
/// <b>A person at a folder knows which album it is far more often than which of
/// its editions.</b> The release-level screen asked them for the pressing and
/// then recorded their pick as one, which is the lie <c>EditionProof</c> exists
/// to stop. Here the answer is the album and a recording per file; the pressing
/// is handed back to the attribution pass, which may prove one of this album's
/// editions from the audio and can never move the folder to another album.
///
/// The files are seated on the album's editions merged into one list, one row
/// per recording, because a rip of the deluxe and a rip of the original are the
/// same album and a list read off either one alone tells the other it holds
/// tracks the album does not have.
/// </remarks>
public static partial class CatalogueEndpoints
{
    /// <summary>Event type for a person filing files under an album with no pressing.</summary>
    private const string FilesFiledUnderAlbumEventType = "matching.files.album";

    /// <summary>
    /// Editions read into one album's merged track list.
    /// </summary>
    /// <remarks>
    /// One lookup each, at MusicBrainz's rate — a minute at the official
    /// server's pace. Nearest to the folder first, so what the cap drops is the
    /// editions least like the rip.
    /// </remarks>
    // ponytail: a cap, not a cache that survives a restart; persist the merged list if much-reissued albums get opened often.
    private const int MaximumAlbumEditions = 40;

    /// <summary>How long a release lookup or an album's edition list is believed here.</summary>
    /// <remarks>
    /// Long enough to page back and forth through the albums found and then file,
    /// which re-reads the editions it seats on. Answers only; a miss is not kept.
    /// </remarks>
    private static readonly TimeSpan EditionCacheDuration = TimeSpan.FromHours(1);

    private static void MapAlbumFilingEndpoints(IEndpointRouteBuilder group)
    {
        group.MapGet("/matching/albums/search", SearchAlbums)
            .WithName("SearchAlbums")
            .WithSummary("Albums (release groups) matching what somebody typed.")
            .WithDescription(
                "`matching/releases/search` one level up: one row per album, so a page is 25 "
                + "albums rather than 25 pressings of the most reissued one. A release or release "
                + "group MBID or URL pasted into `q` is looked up instead of searched for, and "
                + "comes back as the album it names. Needs MusicBrainz's search index for text, "
                + "as the release search does.")
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        group.MapGet("/matching/albums/{id:guid}/slots", GetAlbumSlots)
            .WithName("GetAlbumSlots")
            .WithSummary("One album's editions merged into one track list.")
            .WithDescription(
                "Every recording on the album's official editions, one row each at its first "
                + "place, the lead edition's first: `release` when it is one of the album's, else "
                + "the one the folder's settled files already hold most of, else the one nearest "
                + "the folder in length. One MusicBrainz lookup per edition, at most "
                + $"{MaximumAlbumEditions}; `editionsRead` below `editionsFound` says the list was "
                + "cut there.\n\n"
                + "`folder` names the album folder: a recording a settled file in it already holds "
                + "under this album comes back naming that file in `heldBy`.")
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        group.MapPost("/matching/files/album", FileFilesUnderAlbum)
            .WithName("FileFilesUnderAlbum")
            .WithSummary("File chosen files under an album, each as the recording of a chosen track.")
            .WithDescription(
                "The commit half of `matching/albums/{id}/slots`. Each pair names a file and a "
                + "position on one edition of `album`; the file takes that position's recording "
                + "and the album, and **no pressing**: the outcome is `AlbumByPerson` and the "
                + "release lookup stamp is cleared, so the attribution pass may prove an edition "
                + "of this album from the audio and can never move the files to another album.\n\n"
                + "Identification and enrichment are answered too, as the release filing answers "
                + "them, or the files would stay on the worklist. Only open files are written. "
                + "Nothing on disk is touched.")
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);
    }

    internal static async Task<Results<Ok<AlbumSearchResponse>, ProblemHttpResult>> SearchAlbums(
        IMusicBrainzCatalogue musicBrainz,
        IMemoryCache cache,
        CancellationToken cancellationToken,
        string? q = null,
        int take = DefaultSearchResults)
    {
        if (string.IsNullOrWhiteSpace(q))
        {
            return TypedResults.Problem(
                title: "Nothing to search for",
                detail: "`q` is required.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var query = q.Trim();

        try
        {
            if (MbidPattern.Match(query) is { Success: true } found)
            {
                var row = await PastedAlbumAsync(
                        musicBrainz, cache, new Mbid(Guid.Parse(found.Value)), cancellationToken)
                    .ConfigureAwait(false);

                return TypedResults.Ok(new AlbumSearchResponse(query, row is null ? 0 : 1, row is null ? [] : [row]));
            }

            var matches = await musicBrainz
                .SearchReleaseGroupsAsync(query, Math.Clamp(take, 1, 100), cancellationToken)
                .ConfigureAwait(false);

            return TypedResults.Ok(new AlbumSearchResponse(
                query,
                matches.Count,
                [.. matches.Select(match => new AlbumSearchRow(
                    match.Group.Id.Value,
                    match.Group.Title,
                    CreditLine(match.Credits.Select(credit => (credit.Name, credit.JoinPhrase))),
                    match.Group.FirstReleaseYear,
                    match.Group.PrimaryType,
                    match.Group.SecondaryTypes,
                    match.Editions,
                    match.Score))]));
        }
        catch (ProviderException error)
        {
            return TypedResults.Problem(
                title: "MusicBrainz did not answer",
                detail: error.Message,
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }

    /// <summary>The album a pasted MBID names, whether it is a release's or the album's own.</summary>
    /// <remarks>
    /// A release first, since that is what a MusicBrainz tab and a file's tags
    /// most often hold; a 404 there makes it an album id to browse.
    /// </remarks>
    private static async Task<AlbumSearchRow?> PastedAlbumAsync(
        IMusicBrainzCatalogue musicBrainz,
        IMemoryCache cache,
        Mbid id,
        CancellationToken cancellationToken)
    {
        var release = await CachedReleaseAsync(musicBrainz, cache, id, cancellationToken)
            .ConfigureAwait(false);

        if (release is not null && release.ReleaseGroupId is null) return null;

        var album = release?.ReleaseGroupId ?? id;
        var editions = await CachedEditionsAsync(musicBrainz, cache, album, cancellationToken, release?.Id)
            .ConfigureAwait(false);

        if (editions.Count == 0) return null;

        var first = editions[0];

        return new AlbumSearchRow(
            album.Value,
            first.ReleaseGroupTitle ?? release?.ReleaseGroupTitle ?? first.Title,
            release is null ? null : CreditLine(release.Credits.Select(credit => (credit.Name, credit.JoinPhrase))),
            editions.Min(edition => edition.ReleasedOn?.Year),
            first.PrimaryType,
            first.SecondaryTypes,
            editions.Count,
            null);
    }

    internal static async Task<Results<Ok<AlbumSlotsResponse>, ProblemHttpResult>> GetAlbumSlots(
        Guid id,
        string? folder,
        Guid? release,
        FonotecaDbContext db,
        IMusicBrainzCatalogue musicBrainz,
        IMemoryCache cache,
        CancellationToken cancellationToken)
    {
        var album = new Mbid(id);
        var prefix = string.IsNullOrWhiteSpace(folder) ? null : folder.TrimEnd('/') + "/";

        try
        {
            var found = await CachedEditionsAsync(musicBrainz, cache, album, cancellationToken)
                .ConfigureAwait(false);

            // Pseudo-releases are translations of a track list, not editions of
            // it. Official where there are any: a bootleg is somebody else's
            // tape, and its songs are not tracks the album is missing.
            var editions = found.Where(edition => edition.Status != "Pseudo-Release").ToList();
            var official = editions.Where(edition => edition.Status == "Official").ToList();
            if (official.Count > 0) editions = official;

            // The edition the files' tags name leads whatever its status: it is
            // the one piece of evidence here that is about these files.
            var named = found.FirstOrDefault(edition => edition.Id.Value == release);
            if (named is not null && !editions.Contains(named)) editions.Add(named);

            if (editions.Count == 0)
            {
                return TypedResults.Problem(
                    title: "No such album",
                    detail: $"MusicBrainz lists no editions of album {id}. It may have been merged.",
                    statusCode: StatusCodes.Status404NotFound);
            }

            var files = prefix is null
                ? 0
                : await db.MediaFiles.CountAsync(file => file.Path.StartsWith(prefix), cancellationToken)
                    .ConfigureAwait(false);

            var ordered = editions
                .OrderByDescending(edition => edition == named)
                .ThenBy(edition => Math.Abs(edition.TrackCount - files))
                .ThenBy(edition => edition.ReleasedOn?.Year ?? int.MaxValue)
                .ThenBy(edition => edition.ReleasedOn?.Month ?? 13)
                .ThenBy(edition => edition.ReleasedOn?.Day ?? 32)
                .ThenBy(edition => edition.Id.Value)
                .Take(MaximumAlbumEditions)
                .ToList();

            var read = new List<MusicBrainzRelease>(ordered.Count);

            foreach (var edition in ordered)
            {
                if (await CachedReleaseAsync(musicBrainz, cache, edition.Id, cancellationToken)
                        .ConfigureAwait(false) is { } looked)
                {
                    read.Add(looked);
                }
            }

            if (read.Count == 0)
            {
                return TypedResults.Problem(
                    title: "No such album",
                    detail: $"None of the editions MusicBrainz lists for album {id} could be read.",
                    statusCode: StatusCodes.Status404NotFound);
            }

            var held = prefix is null
                ? []
                : await HeldRecordingsAsync(db, album, prefix, cancellationToken).ConfigureAwait(false);

            // The edition the folder is most plausibly a rip of: the one its tags
            // name, else the one its settled files already hold most of, else the
            // nearest in length (the fetch order, which a stable sort keeps).
            var lead = read
                .OrderByDescending(edition => edition.Id.Value == release)
                .ThenByDescending(edition => edition.Tracks.Count(track =>
                    track.RecordingId is { } recording && held.ContainsKey(recording.Value)))
                .First();

            // Every edition's recordings, one row each at its first place, the
            // lead's first. Not Editions.Combine: it leaves out a song another
            // performance of the album repeats by title, which is right for
            // "what is this album missing" and wrong here, where the file may be
            // that other performance.
            var order = new List<Guid>();
            var placed = new Dictionary<Guid, (MusicBrainzRelease Edition, MusicBrainzTrack Track)>();
            var carriers = new Dictionary<Guid, int>();

            foreach (var edition in read.OrderByDescending(edition => edition == lead))
            {
                foreach (var recording in edition.Tracks.Select(track => track.RecordingId).OfType<Mbid>().Distinct())
                {
                    carriers[recording.Value] = carriers.GetValueOrDefault(recording.Value) + 1;
                }

                foreach (var track in edition.Tracks)
                {
                    if (track.RecordingId is { } recording && placed.TryAdd(recording.Value, (edition, track)))
                    {
                        order.Add(recording.Value);
                    }
                }
            }

            var rows = order
                .Select(recording =>
                {
                    var (edition, track) = placed[recording];

                    return new AlbumSlotRow(
                        recording,
                        edition.Id.Value,
                        track.DiscNumber,
                        track.Position,
                        track.Number,
                        track.Title,
                        CreditLine(track.Credits.Select(credit => (credit.Name, credit.JoinPhrase))),
                        Format(track.Length),
                        track.Length is { } length ? (int)length.TotalMilliseconds : null,
                        carriers[recording],
                        edition == lead,
                        held.GetValueOrDefault(recording));
                })
                .ToList();

            var first = editions[0];

            return TypedResults.Ok(new AlbumSlotsResponse(
                id,
                lead.ReleaseGroupTitle ?? first.ReleaseGroupTitle ?? lead.Title,
                CreditLine(lead.Credits.Select(credit => (credit.Name, credit.JoinPhrase))),
                found.Min(edition => edition.ReleasedOn?.Year),
                first.PrimaryType,
                first.SecondaryTypes,
                lead.Id.Value,
                lead.Tracks.Select(track => track.DiscNumber).Distinct().Count(),
                lead.Tracks.Count(track => !track.IsPlaceholder
                    || (track.RecordingId is { } recording && held.ContainsKey(recording.Value))),
                read.Count,
                editions.Count,
                rows));
        }
        catch (ProviderException error)
        {
            return TypedResults.Problem(
                title: "MusicBrainz did not answer",
                detail: error.Message,
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }

    /// <summary>
    /// The recordings settled files in this folder already hold under this album,
    /// each with one of those files' names.
    /// </summary>
    /// <remarks>
    /// <c>HeldSlotsAsync</c> one level up: keyed on the recording, since an album
    /// has no positions of its own. Settled means not an open question — an open
    /// file is one of the files being seated, and must not hold a track against
    /// itself.
    /// </remarks>
    private static async Task<Dictionary<Guid, string>> HeldRecordingsAsync(
        FonotecaDbContext db,
        Mbid album,
        string prefix,
        CancellationToken cancellationToken)
    {
        var rows = await db.MediaFiles
            .AsNoTracking()
            .Where(file =>
                file.Path.StartsWith(prefix)
                && file.Recording!.Mbid != null
                && file.ReleaseGroup!.Mbid == album
                && !((file.IdentityDecidedUtc == null
                        && (UnidentifiedOutcomes.Contains(file.AcoustIdOutcome)
                            || UnlinkedOutcomes.Contains(file.EnrichmentOutcome)))
                    || (file.ReleaseDecidedUtc == null
                        && UnattributedOutcomes.Contains(file.AttributionOutcome))))
            .Select(file => new { file.Path, Recording = file.Recording!.Mbid })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var held = new Dictionary<Guid, string>();

        foreach (var row in rows)
        {
            if (row.Recording is { } recording) held[recording.Value] = NameOf(row.Path);
        }

        return held;
    }

    /// <summary>
    /// One person's claim about which album a set of files is, and which track of
    /// it each one is, committed with no pressing.
    /// </summary>
    /// <remarks>
    /// <c>FileFilesUnderRelease</c>'s order of operations: everything that can be
    /// refused is refused before anything is written.
    /// </remarks>
    internal static async Task<Results<Ok<AlbumFilesResponse>, ProblemHttpResult>>
        FileFilesUnderAlbum(
            AlbumFilesRequest request,
            FonotecaDbContext db,
            IMusicBrainzCatalogue musicBrainz,
            IMemoryCache cache,
            IEventLog events,
            LibraryWorkGate gate,
            ICallerContext caller,
            IClock clock,
            CancellationToken cancellationToken)
    {
        if (request is null || request.Pairs is null || request.Pairs.Count == 0)
        {
            return TypedResults.Problem(
                title: "Nothing to file",
                detail: "The request body must name an `album` and at least one pair.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        if (request.Pairs.Count > MaximumFiledFiles)
        {
            return TypedResults.Problem(
                title: "Too many files at once",
                detail: $"{request.Pairs.Count} pairs were sent and the limit is {MaximumFiledFiles}.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        if (request.Pairs.Select(pair => pair.File).Distinct().Count() != request.Pairs.Count)
        {
            return TypedResults.Problem(
                title: "A file was named twice",
                detail: "Each file may be seated on at most one track.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        if (request.Pairs.Select(pair => (pair.Release, pair.Disc, pair.Position)).Distinct().Count()
            != request.Pairs.Count)
        {
            return TypedResults.Problem(
                title: "A track was named twice",
                detail: "Two files cannot be seated on the same track.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        if (!gate.TryEnter(DecisionWorkKind, out var lease))
        {
            return TypedResults.Problem(
                title: "The library is busy",
                detail:
                    $"A {gate.ActiveKind ?? "pass"} is running, and it may clear or rewrite exactly "
                    + "the columns this decision sets. Answer again once it has finished.",
                statusCode: StatusCodes.Status409Conflict);
        }

        using var held = lease;

        var wanted = request.Pairs.Select(pair => new MediaFileId(pair.File)).ToList();

        var rows = await db.MediaFiles
            .Where(file => wanted.Contains(file.Id)
                && ((file.IdentityDecidedUtc == null
                        && (UnidentifiedOutcomes.Contains(file.AcoustIdOutcome)
                            || UnlinkedOutcomes.Contains(file.EnrichmentOutcome)))
                    || (file.ReleaseDecidedUtc == null
                        && UnattributedOutcomes.Contains(file.AttributionOutcome))))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        if (rows.Count == 0)
        {
            return TypedResults.Problem(
                title: "No open files in that set",
                detail:
                    "None of these files is waiting on an answer. Either they were decided while "
                    + "this screen was open, or a scan has moved them — re-read the worklist.",
                statusCode: StatusCodes.Status404NotFound);
        }

        var album = new Mbid(request.Album);
        var releases = new Dictionary<Guid, MusicBrainzRelease>();

        try
        {
            foreach (var id in request.Pairs.Select(pair => pair.Release).Distinct())
            {
                var release = await CachedReleaseAsync(musicBrainz, cache, new Mbid(id), cancellationToken)
                    .ConfigureAwait(false);

                if (release is null)
                {
                    return TypedResults.Problem(
                        title: "No such release",
                        detail:
                            $"MusicBrainz no longer holds release {id}. It has probably been merged; "
                            + "read the album's track list again.",
                        statusCode: StatusCodes.Status404NotFound);
                }

                if (release.ReleaseGroupId != album)
                {
                    return TypedResults.Problem(
                        title: "That edition is of another album",
                        detail: $"“{release.Title}” is not an edition of album {request.Album}.",
                        statusCode: StatusCodes.Status400BadRequest);
                }

                releases[id] = release;
            }
        }
        catch (ProviderException error)
        {
            return TypedResults.Problem(
                title: "MusicBrainz did not answer",
                detail: error.Message,
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        // A position with no recording has nothing for a file to become.
        var invented = request.Pairs
            .Where(pair => !releases[pair.Release].Tracks.Any(track =>
                track.DiscNumber == pair.Disc && track.Position == pair.Position && track.RecordingId is not null))
            .ToList();

        if (invented.Count > 0)
        {
            var first = invented[0];

            return TypedResults.Problem(
                title: "That album has no such track",
                detail:
                    $"“{releases[first.Release].Title}” prints no recording at disc {first.Disc} "
                    + $"track {first.Position}"
                    + (invented.Count > 1 ? $", and {invented.Count - 1} more like it." : ".")
                    + " Read the track list again — it may have changed since the screen loaded.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        // Two editions can print one recording; two files still cannot both be it.
        var recordings = request.Pairs
            .Select(pair => releases[pair.Release].Tracks.First(track =>
                track.DiscNumber == pair.Disc && track.Position == pair.Position).RecordingId)
            .ToList();

        if (recordings.Distinct().Count() != recordings.Count)
        {
            return TypedResults.Problem(
                title: "A track was named twice",
                detail: "Two files cannot be seated on the same recording, whichever edition prints it.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var now = StoreTime.ToStorePrecision(clock.UtcNow);
        var correlationId = Guid.CreateVersion7().ToString("N")[..12];

        var writer = new ReleaseAttributionService.ReleaseWriter(db);
        var written = new Dictionary<Guid, (ReleaseId Release, ReleaseGroupId? Group)>();

        foreach (var (id, release) in releases)
        {
            written[id] = await writer.UpsertAsync(release, null, cancellationToken).ConfigureAwait(false);
        }

        var byId = rows.ToDictionary(row => row.Id);
        var filed = new List<AlbumFilesPair>(request.Pairs.Count);

        foreach (var pair in request.Pairs)
        {
            if (!byId.TryGetValue(new MediaFileId(pair.File), out var row)) continue;

            var edition = written[pair.Release];

            row.RecordingId = writer.RecordingIdAt(edition.Release, pair.Disc, pair.Position);
            row.ReleaseGroupId = edition.Group;
            row.ReleaseId = null;
            row.TrackId = null;
            row.EditionAlternatives = 0;
            row.FolderPosition = null;
            row.OrderOutcome = FolderOrderOutcome.NotChecked;

            row.AcoustIdOutcome = ByCaller(caller, AcoustIdOutcome.IdentifiedByPerson);
            row.IdentityDecidedUtc = now;

            row.EnrichmentOutcome = ByCaller(caller, EnrichmentOutcome.LinkedByPerson);
            row.RecordingLookupUtc = now;

            row.AttributionOutcome = ByCaller(caller, ReleaseAttributionOutcome.AlbumByPerson);
            row.ReleaseDecidedUtc = now;

            // Cleared so the pass proves the pressing, within this album only.
            row.ReleaseLookupUtc = null;

            filed.Add(pair);
        }

        var anyEdition = releases.Values.First();
        var title = anyEdition.ReleaseGroupTitle ?? anyEdition.Title;

        await events.AppendAsync(
            DomainEvent.Create(
                FilesFiledUnderAlbumEventType,
                FilingSubject,
                request.Album.ToString(),
                caller.ActorId,
                now,
                JsonSerializer.Serialize(
                    new AlbumFilesPayload
                    {
                        Album = request.Album,
                        Title = title,
                        Filed = filed.Count,
                        Skipped = request.Pairs.Count - filed.Count,
                        Seats = [.. filed.Select(pair =>
                            $"{pair.File:N}@{pair.Release:N}/{pair.Disc}-{pair.Position}")],
                    },
                    MatchingJson.Default.AlbumFilesPayload),
                correlationId),
            cancellationToken).ConfigureAwait(false);

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        var skipped = request.Pairs.Count - filed.Count;

        return TypedResults.Ok(new AlbumFilesResponse(
            request.Album,
            title,
            filed.Count,
            skipped,
            (skipped == 0
                ? $"{filed.Count} file{(filed.Count == 1 ? "" : "s")} filed under “{title}”"
                : $"{filed.Count} of {request.Pairs.Count} files filed under “{title}”")
            + " with no pressing claimed. The next attribution run may prove one of its editions."
            + (skipped == 0 ? "" : " The rest are no longer open questions and were left as they are.")));
    }

    private static async Task<MusicBrainzRelease?> CachedReleaseAsync(
        IMusicBrainzCatalogue musicBrainz,
        IMemoryCache cache,
        Mbid id,
        CancellationToken cancellationToken)
    {
        var key = ("musicbrainz-release", id);

        if (cache.TryGetValue(key, out MusicBrainzRelease? hit) && hit is not null) return hit;

        var release = await musicBrainz.GetReleaseAsync(id, cancellationToken).ConfigureAwait(false);
        if (release is not null) cache.Set(key, release, EditionCacheDuration);

        return release;
    }

    /// <summary>An album's editions, believed for <see cref="EditionCacheDuration"/>.</summary>
    /// <param name="listing">
    /// An edition known to belong to the album. A cached list that lacks it was
    /// read before it existed, so it is read again rather than believed.
    /// </param>
    /// <remarks>
    /// The seed button sends a person to MusicBrainz to add the very edition they
    /// then paste here. A list read an hour earlier would hide it until expiry,
    /// even though the release's own lookup names this album.
    /// </remarks>
    private static async Task<IReadOnlyList<MusicBrainzReleaseCandidate>> CachedEditionsAsync(
        IMusicBrainzCatalogue musicBrainz,
        IMemoryCache cache,
        Mbid album,
        CancellationToken cancellationToken,
        Mbid? listing = null)
    {
        var key = ("musicbrainz-editions", album);

        if (cache.TryGetValue(key, out IReadOnlyList<MusicBrainzReleaseCandidate>? hit)
            && hit is not null
            && (listing is not { } edition || hit.Any(candidate => candidate.Id == edition)))
        {
            return hit;
        }

        var editions = await musicBrainz.BrowseReleasesForReleaseGroupAsync(album, cancellationToken)
            .ConfigureAwait(false);

        if (editions.Count > 0) cache.Set(key, editions, EditionCacheDuration);

        return editions;
    }
}

/// <summary>Albums matching what somebody typed, in MusicBrainz's own order.</summary>
public sealed record AlbumSearchResponse(string Query, int Total, IReadOnlyList<AlbumSearchRow> Items);

/// <summary>One album a person could be looking for.</summary>
/// <param name="Year">The year of the album's first release.</param>
/// <param name="Editions">How many releases MusicBrainz lists for it, of any status.</param>
/// <param name="Score">MusicBrainz's relevance, 0-100, or null for an album found by identifier.</param>
public sealed record AlbumSearchRow(
    Guid Mbid,
    string Title,
    string? Artist,
    int? Year,
    string? PrimaryType,
    IReadOnlyList<string> SecondaryTypes,
    int Editions,
    int? Score);

/// <summary>One album's editions merged into one track list.</summary>
/// <param name="Lead">The edition whose running order the list follows.</param>
/// <param name="LeadDiscs">Discs on the lead edition, for how a position is printed.</param>
/// <param name="LeadTracks">
/// Tracks on the lead edition, which is what a folder is measured against — a placeholder
/// (<c>TrackTitles</c>) only where a settled file in the folder holds it. Every placeholder is
/// still listed in <paramref name="Tracks"/>, so a file can be seated on one.
/// </param>
/// <param name="EditionsRead">Editions whose track lists are in <paramref name="Tracks"/>.</param>
/// <param name="EditionsFound">Editions considered; above <paramref name="EditionsRead"/> when the list was cut.</param>
public sealed record AlbumSlotsResponse(
    Guid Mbid,
    string Title,
    string? Artist,
    int? Year,
    string? PrimaryType,
    IReadOnlyList<string> SecondaryTypes,
    Guid Lead,
    int LeadDiscs,
    int LeadTracks,
    int EditionsRead,
    int EditionsFound,
    IReadOnlyList<AlbumSlotRow> Tracks);

/// <summary>One recording of an album, at its first place on the editions read.</summary>
/// <param name="Release">The edition <paramref name="DiscNumber"/> and <paramref name="Position"/> are on.</param>
/// <param name="Editions">How many of the editions read carry the recording.</param>
/// <param name="OnLead">Whether the lead edition carries it; its position is the lead's when so.</param>
/// <param name="HeldBy">A settled file in the folder that already holds the recording under this album.</param>
public sealed record AlbumSlotRow(
    Guid Recording,
    Guid Release,
    int DiscNumber,
    int Position,
    string? Number,
    string Title,
    string? Artist,
    string? Duration,
    int? DurationMs,
    int Editions,
    bool OnLead,
    string? HeldBy);

/// <summary>A person's claim: these files are these tracks of this album, on no pressing in particular.</summary>
public sealed record AlbumFilesRequest(Guid Album, IReadOnlyList<AlbumFilesPair> Pairs);

/// <summary>One file, and the edition position whose recording it is.</summary>
public sealed record AlbumFilesPair(Guid File, Guid Release, int Disc, int Position);

/// <summary>What one album filing did.</summary>
/// <param name="Album">The album's MBID.</param>
public sealed record AlbumFilesResponse(Guid Album, string Title, int Filed, int Skipped, string Detail);
