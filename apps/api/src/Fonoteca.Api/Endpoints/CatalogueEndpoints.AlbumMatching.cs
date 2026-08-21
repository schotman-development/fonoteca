using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Fonoteca.Api.Library;
using Fonoteca.Api.Matching;
using Fonoteca.Data;
using Fonoteca.Domain.Abstractions;
using Fonoteca.Domain.Catalogue;
using Fonoteca.Domain.Events;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace Fonoteca.Api.Endpoints;

/// <summary>
/// Filing files under an album by hand, when nothing about them names one.
/// </summary>
/// <remarks>
/// <b>Everything else on this screen recovers a candidate set. This one has
/// none to recover.</b> The two identification questions are re-asked from the
/// stored fingerprint and the album question is re-gathered from the component's
/// own recordings — both start from something a pass wrote down. The files here
/// are the ones where nothing was written down: measured on the library this was
/// built against, <b>every one of the 696 open file-level questions has a null
/// <c>RecordingId</c></b>, which is exactly why they are open. AcoustID has
/// never heard the audio, or heard it and links it to no recording.
///
/// So <c>ReleaseFit</c> cannot be used, and neither can anything else that keys
/// on an identifier: it matches a file to a slot by the recording MBID the file
/// holds, and these hold none. The only two claims in existence about them are
/// the folder they sit in and the order they sit in it — the same two claims
/// ADR-era prose says not to believe. The difference is who is believing them.
/// A pass reading a folder name is a guess with nobody watching; a person
/// reading it, typing the album into MusicBrainz and approving a pairing they
/// can see is evidence of a different kind, and it is the only kind available
/// for these files.
///
/// Hence the shape: the seating is <b>proposed by the client and committed
/// verbatim</b>. There is no matching rule in here at all, which is deliberate —
/// a rule would be a fourth pass whose worklist is exactly the files the other
/// three refused, ranking the same evidence they already rejected. What is here
/// instead is a search, a track list, and a writer that does what it is told and
/// records who told it.
///
/// It writes identity as well as placement, and that is not scope creep: a file
/// filed under an album with its identification refusal intact stays on the
/// worklist forever, so the screen would appear to do nothing. See
/// <see cref="EnrichmentOutcome.LinkedByPerson"/> for what the catalogue then
/// knows and what it still does not.
/// </remarks>
public static partial class CatalogueEndpoints
{
    /// <summary>Search hits returned when the caller does not say.</summary>
    private const int DefaultSearchResults = 25;

    /// <summary>
    /// Files one filing may seat.
    /// </summary>
    /// <remarks>
    /// Above any real album by a wide margin — the largest folder on the target
    /// library holds 112 files — and there only to keep a malformed body from
    /// turning into an unbounded write inside one transaction.
    /// </remarks>
    private const int MaximumFiledFiles = 500;

    /// <summary>Event type for a person filing a set of files under an album.</summary>
    private const string FilesFiledEventType = "matching.files.filed";

    /// <summary>What the event log calls a set of files a person chose themselves.</summary>
    /// <remarks>
    /// Not <c>component</c>: a component is a set the <i>pass</i> formed and
    /// stamped, and it has an identity in the catalogue. This set is whatever
    /// somebody ticked, so the album is the only durable thing about it and the
    /// subject id is the release MBID.
    /// </remarks>
    private const string FilingSubject = "album-filing";

    /// <summary>An MBID anywhere in what somebody typed or pasted.</summary>
    [GeneratedRegex(
        "[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex MbidPattern { get; }

    private static void MapAlbumMatchingEndpoints(IEndpointRouteBuilder group)
    {
        group.MapGet("/matching/releases/search", SearchReleases)
            .WithName("SearchReleases")
            .WithSummary("Albums matching what somebody typed.")
            .WithDescription(
                "A free-text search of MusicBrainz, for the files no candidate set can be "
                + "recovered for — the ones AcoustID cannot place, whose only remaining evidence "
                + "is a person who knows what the album is. `q` takes MusicBrainz's own query "
                + "syntax, so `artist:` and `date:` work; a MusicBrainz release URL or a bare "
                + "MBID pasted into it is looked up directly instead of searched for, and comes "
                + "back as the single result it is.\n\n"
                + "**This is the one call in the application that needs a search index**, which "
                + "database replication does not cover. Against a self-hosted mirror it fails "
                + "where every other MusicBrainz call works — reported as a provider error rather "
                + "than as an empty list, so 'nothing matches' and 'this server cannot search' "
                + "stay apart. `score` is MusicBrainz's own relevance and is printed, not ranked "
                + "on: the order is theirs.")
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        group.MapGet("/matching/releases/{id:guid}/slots", GetReleaseSlots)
            .WithName("GetReleaseSlots")
            .WithSummary("Every position on one album, filled or not.")
            .WithDescription(
                "The track list a person seats files onto, straight from MusicBrainz and not "
                + "from the catalogue — the release may well not be in the catalogue yet, since "
                + "filing files under it is what puts it there.\n\n"
                + "Every slot the release prints is returned, including the ones nobody holds: "
                + "the empty ones are what a person is choosing *around*, and a list of only the "
                + "occupied positions cannot show a rip that skipped track 7. `durationMs` is "
                + "beside the formatted length so a client can measure a proposed pairing without "
                + "parsing it back.\n\n"
                + "`folder` is optional and is the one thing here read from the catalogue: given "
                + "a library folder, every slot already held by a file *in that folder and filed "
                + "under this same release* comes back naming it in `heldBy`. An album is rarely "
                + "wholly unmatched — the passes place most of a rip and leave the two files they "
                + "could not — so without this the screen offers twenty-one empty positions for "
                + "one leftover file and proposes seating it on track 1.")
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        group.MapPost("/matching/files/release", FileFilesUnderRelease)
            .WithName("FileFilesUnderRelease")
            .WithSummary("File a set of chosen files onto a set of chosen album slots.")
            .WithDescription(
                "The commit half of `matching/releases/search`, and the only write in the "
                + "application whose seating comes from the request rather than from a rule. "
                + "Each pair names one file and one `(disc, position)` on the release; the pairing "
                + "is committed as given, because for these files the person is the evidence — "
                + "none of them holds a recording MBID, so nothing here could check the claim "
                + "against anything.\n\n"
                + "Choosing a release writes it, its group and its whole track list into the "
                + "catalogue exactly as the attribution pass would, then gives each file the "
                + "recording its slot names, the track, the release and the group. All three "
                + "outcomes are set to their by-a-person values and all three decided-stamps are "
                + "written, which is what takes the files off every pass's worklist for good.\n\n"
                + "A file that is not currently an open question is skipped rather than rewritten "
                + "— this endpoint answers refusals, it does not overrule decisions. Nothing on "
                + "disk is touched: no tag write, no `Fonoteca:AllowFileMutation`, no undo "
                + "journal, one decision entry naming every pair.")
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);
    }

    private static async Task<Results<Ok<ReleaseSearchResponse>, ProblemHttpResult>> SearchReleases(
        IMusicBrainzCatalogue musicBrainz,
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
        var wanted = Math.Clamp(take, 1, 100);

        try
        {
            // A pasted URL or a bare MBID is not a query, and searching for one
            // is worse than useless: MusicBrainz indexes titles and credits, not
            // identifiers, so `find` on an MBID returns whatever shares a word
            // with a UUID. Somebody who has the release open in a browser tab
            // has given the strongest possible answer and should get it back.
            if (MbidPattern.Match(query) is { Success: true } found)
            {
                var mbid = new Mbid(Guid.Parse(found.Value));
                var release = await musicBrainz.GetReleaseAsync(mbid, cancellationToken)
                    .ConfigureAwait(false);

                return TypedResults.Ok(new ReleaseSearchResponse(
                    query,
                    release is null ? 0 : 1,
                    release is null ? [] : [RowFor(release)]));
            }

            var matches = await musicBrainz.SearchReleasesAsync(query, wanted, cancellationToken)
                .ConfigureAwait(false);

            return TypedResults.Ok(new ReleaseSearchResponse(
                query,
                matches.Count,
                [.. matches.Select(RowFor)]));
        }
        catch (ProviderException error)
        {
            return TypedResults.Problem(
                title: "MusicBrainz did not answer",
                detail: error.Message,
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }

    private static async Task<Results<Ok<ReleaseSlotsResponse>, ProblemHttpResult>> GetReleaseSlots(
        Guid id,
        string? folder,
        FonotecaDbContext db,
        IMusicBrainzCatalogue musicBrainz,
        CancellationToken cancellationToken)
    {
        MusicBrainzRelease? release;

        try
        {
            release = await musicBrainz.GetReleaseAsync(new Mbid(id), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (ProviderException error)
        {
            return TypedResults.Problem(
                title: "MusicBrainz did not answer",
                detail: error.Message,
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        if (release is null)
        {
            return TypedResults.Problem(
                title: "No such release",
                detail: $"MusicBrainz holds no release {id}. It has probably been merged.",
                statusCode: StatusCodes.Status404NotFound);
        }

        var held = await HeldSlotsAsync(db, release.Id, folder, cancellationToken)
            .ConfigureAwait(false);

        var slots = release.Tracks
            .Select(track => new ReleaseSlotRow(
                track.DiscNumber,
                track.Position,
                track.Number,
                track.Title,
                CreditLine(track.Credits.Select(credit => (credit.Name, credit.JoinPhrase))),
                Format(track.Length),
                track.Length is { } length ? (int)length.TotalMilliseconds : null,
                track.RecordingId?.Value,
                held.GetValueOrDefault((track.DiscNumber, track.Position))))
            .ToList();

        return TypedResults.Ok(new ReleaseSlotsResponse(
            release.Id.Value,
            release.Title,
            CreditLine(release.Credits.Select(credit => (credit.Name, credit.JoinPhrase))),
            release.ReleasedOn?.Year,
            release.Country,
            release.Status,
            release.ReleaseGroupId?.Value,
            release.PrimaryType,
            release.SecondaryTypes,
            slots.Select(slot => slot.DiscNumber).Distinct().Count(),
            slots));
    }

    /// <summary>
    /// One person's claim about which album a set of files came from, committed.
    /// </summary>
    /// <remarks>
    /// The order of operations is the recording decision's, for the same reasons.
    /// Everything that can be refused is refused before anything is written — the
    /// body, the gate, the file set, the release lookup and the slots — so a
    /// failure leaves the decision untaken rather than half-taken, and the same
    /// click works once MusicBrainz does.
    /// </remarks>
    private static async Task<Results<Ok<AlbumFilingResponse>, ProblemHttpResult>>
        FileFilesUnderRelease(
            AlbumFilingRequest request,
            FonotecaDbContext db,
            IMusicBrainzCatalogue musicBrainz,
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
                detail: "The request body must name a `release` and at least one pair.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        if (request.Pairs.Count > MaximumFiledFiles)
        {
            return TypedResults.Problem(
                title: "Too many files at once",
                detail:
                    $"{request.Pairs.Count} pairs were sent and the limit is {MaximumFiledFiles}. "
                    + "An album this size is almost certainly a mistake in how the set was built.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        // Both halves of the pairing have to be injective, and for different
        // reasons. A file named twice would be written twice and the second
        // write would win silently. A slot named twice is the person's error and
        // a real one — two files cannot both be track 4 — and accepting it would
        // file two files on one track link with nothing on the screen or in the
        // log saying which.
        if (request.Pairs.Select(pair => pair.File).Distinct().Count() != request.Pairs.Count)
        {
            return TypedResults.Problem(
                title: "A file was named twice",
                detail: "Each file may be seated on at most one slot.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        if (request.Pairs.Select(pair => (pair.Disc, pair.Position)).Distinct().Count()
            != request.Pairs.Count)
        {
            return TypedResults.Problem(
                title: "A slot was named twice",
                detail: "Two files cannot be seated on the same position of one album.",
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

        // The worklist's own predicate, so what this endpoint will answer and
        // what the screen offered cannot drift apart. A file that is not an open
        // question is skipped rather than rewritten: this answers refusals, and
        // overruling a decision — a pass's or a person's — is a different act
        // that should look different.
        var rows = await db.MediaFiles
            .Where(file => wanted.Contains(file.Id)
                && file.IdentityDecidedUtc == null
                && (UnidentifiedOutcomes.Contains(file.AcoustIdOutcome)
                    || UnlinkedOutcomes.Contains(file.EnrichmentOutcome)))
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

        MusicBrainzRelease? release;

        try
        {
            release = await musicBrainz.GetReleaseAsync(new Mbid(request.Release), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (ProviderException error)
        {
            return TypedResults.Problem(
                title: "MusicBrainz did not answer",
                detail: error.Message,
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        if (release is null)
        {
            return TypedResults.Problem(
                title: "No such release",
                detail:
                    $"MusicBrainz no longer holds release {request.Release}. It has probably been "
                    + "merged; search for it again to see where it went.",
                statusCode: StatusCodes.Status404NotFound);
        }

        var printed = release.Tracks
            .Select(track => (track.DiscNumber, track.Position))
            .ToHashSet();

        // Checked against the release before the writer touches anything. A slot
        // the release does not print cannot be written — `TrackIdAt` would answer
        // null and the file would be filed under the album with no position at
        // all, which reads on every later screen as a track MusicBrainz has since
        // removed rather than as a number somebody made up.
        var invented = request.Pairs
            .Where(pair => !printed.Contains((pair.Disc, pair.Position)))
            .ToList();

        if (invented.Count > 0)
        {
            var first = invented[0];

            return TypedResults.Problem(
                title: "That album has no such position",
                detail:
                    $"“{release.Title}” prints no disc {first.Disc} track {first.Position}"
                    + (invented.Count > 1 ? $", and {invented.Count - 1} more like it." : ".")
                    + " Read the track list again — it may have changed since the screen loaded.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var now = StoreTime.ToStorePrecision(clock.UtcNow);
        var correlationId = Guid.CreateVersion7().ToString("N")[..12];

        var writer = new ReleaseAttributionService.ReleaseWriter(db);

        // Null formats for the reason the component decision passes null: a
        // release lookup carries no medium summaries, and the writer coalesces
        // rather than blanking whatever a browse put there earlier.
        var written = await writer.UpsertAsync(release, null, cancellationToken).ConfigureAwait(false);

        var byId = rows.ToDictionary(row => row.Id);
        var filed = new List<AlbumFilingPair>(request.Pairs.Count);

        foreach (var pair in request.Pairs)
        {
            if (!byId.TryGetValue(new MediaFileId(pair.File), out var row)) continue;

            row.RecordingId = writer.RecordingIdAt(written.Release, pair.Disc, pair.Position);
            row.ReleaseId = written.Release;
            row.ReleaseGroupId = written.Group;
            row.TrackId = writer.TrackIdAt(written.Release, pair.Disc, pair.Position);

            // All three passes are answered at once, because all three refused.
            // A file that keeps any one of these refusals keeps its place on the
            // worklist, and the screen that just filed it would go on offering
            // it — see the type's remarks and `EnrichmentOutcome.LinkedByPerson`.
            row.AcoustIdOutcome = AcoustIdOutcome.IdentifiedByPerson;
            row.IdentityDecidedUtc = now;

            row.EnrichmentOutcome = EnrichmentOutcome.LinkedByPerson;
            row.RecordingLookupUtc = now;

            row.AttributionOutcome = ReleaseAttributionOutcome.AttributedByPerson;
            row.ReleaseLookupUtc = now;
            row.ReleaseDecidedUtc = now;

            // Zero for the reason the component decision states it: the count
            // means "this many other editions fitted exactly as well", and a
            // person choosing is not a tie-break.
            row.EditionAlternatives = 0;

            filed.Add(pair);
        }

        await events.AppendAsync(
            DomainEvent.Create(
                FilesFiledEventType,
                FilingSubject,
                release.Id.Value.ToString(),
                caller.ActorId,
                now,
                JsonSerializer.Serialize(
                    new AlbumFilingPayload
                    {
                        Release = release.Id.Value,
                        Title = release.Title,
                        Filed = filed.Count,
                        Skipped = request.Pairs.Count - filed.Count,
                        Seats = [.. filed.Select(pair =>
                            $"{pair.File:N}@{pair.Disc}-{pair.Position}")],
                    },
                    MatchingJson.Default.AlbumFilingPayload),
                correlationId),
            cancellationToken).ConfigureAwait(false);

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        var skipped = request.Pairs.Count - filed.Count;

        return TypedResults.Ok(new AlbumFilingResponse(
            release.Id.Value,
            release.Title,
            filed.Count,
            skipped,
            skipped == 0
                ? $"All {filed.Count} files filed under “{release.Title}”."
                : $"{filed.Count} of {request.Pairs.Count} files filed under “{release.Title}”. "
                    + "The rest are no longer open questions and were left as they are."));
    }

    private static ReleaseSearchRow RowFor(MusicBrainzReleaseMatch match) =>
        RowFor(
            match.Release,
            CreditLine(match.Credits.Select(credit => (credit.Name, credit.JoinPhrase))),
            match.Score);

    /// <summary>A row for a release fetched by identifier, which carries no score.</summary>
    private static ReleaseSearchRow RowFor(MusicBrainzRelease release) =>
        new(
            release.Id.Value,
            release.Title,
            CreditLine(release.Credits.Select(credit => (credit.Name, credit.JoinPhrase))),
            release.ReleasedOn?.Year,
            release.Country,
            release.Status,
            release.PrimaryType,
            release.SecondaryTypes,
            Formats(release.Tracks.Select(track => track.DiscNumber).Distinct().Count()),
            release.Tracks.Count,
            release.Tracks.Select(track => track.DiscNumber).Distinct().Count(),
            null);

    private static ReleaseSearchRow RowFor(
        MusicBrainzReleaseCandidate release,
        string? artist,
        int? score) =>
        new(
            release.Id.Value,
            release.Title,
            artist,
            release.ReleasedOn?.Year,
            release.Country,
            release.Status,
            release.PrimaryType,
            release.SecondaryTypes,
            MediumFormats(release.Media),
            release.TrackCount,
            release.Media.Count,
            score);

    /// <summary>"CD", "2 x CD", "CD + DVD" — why a rip may legitimately be partial.</summary>
    private static string? MediumFormats(IReadOnlyList<MusicBrainzMediumSummary> media)
    {
        var named = media
            .Select(medium => medium.Format)
            .Where(format => !string.IsNullOrWhiteSpace(format))
            .ToList();

        if (named.Count == 0) return null;

        return string.Join(
            " + ",
            named
                .GroupBy(format => format, StringComparer.Ordinal)
                .Select(group => group.Count() == 1
                    ? group.Key
                    : string.Create(
                        CultureInfo.InvariantCulture,
                        $"{group.Count()} × {group.Key}")));
    }

    /// <summary>What a release lookup can say about its discs, which is only how many.</summary>
    /// <remarks>
    /// A lookup carries tracks and not medium formats — the browse include that
    /// supplies them is on a different call — so this says the honest thing
    /// rather than inventing "CD".
    /// </remarks>
    private static string? Formats(int discs) => discs > 1 ? $"{discs} discs" : null;

    /// <summary>
    /// Which positions of this album a file in this folder already sits on.
    /// </summary>
    /// <remarks>
    /// <b>An album is almost never wholly unmatched, and the worklist cannot show
    /// that.</b> It lists open questions, so a folder whose twenty other files the
    /// passes placed appears on this screen as a one-file album — and the seating
    /// this dialog proposes, files in path order against slots in printed order,
    /// then puts that one leftover on track 1. Measured: a twenty-one file live
    /// album with one <c>Ambiguous</c> file, whose siblings hold every position
    /// except fourteen.
    ///
    /// The catalogue already knows, so this asks it. What comes back is a label
    /// and nothing more — the client greys those options out and seats the open
    /// files on the gaps — which is why it is a filename rather than an id.
    ///
    /// <b>Constrained to files filed under this same release, and that is the
    /// load-bearing part.</b> A sibling filed under a different edition holds a
    /// position on <i>that</i> track list; reporting it against this one would
    /// claim a slot is taken on the strength of a number that means something
    /// else. Where the editions disagree the query simply finds nothing and the
    /// screen behaves as it did before, which is the right way for this to fail.
    ///
    /// Prefixed with a trailing slash so <c>Artist/Album</c> cannot also match
    /// <c>Artist/Album Live</c>, and so the <c>CD1</c> and <c>CD2</c> the client
    /// collapses onto one heading are both included.
    /// </remarks>
    private static async Task<Dictionary<(int Disc, int Position), string>> HeldSlotsAsync(
        FonotecaDbContext db,
        Mbid release,
        string? folder,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(folder))
        {
            return [];
        }

        var prefix = folder.EndsWith('/') ? folder : folder + "/";

        var rows = await db.MediaFiles
            .AsNoTracking()
            .Where(file =>
                file.Track != null
                && file.Release!.Mbid == release
                && file.Path.StartsWith(prefix))
            .Select(file => new { file.Path, file.Track!.DiscNumber, file.Track!.Position })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var held = new Dictionary<(int Disc, int Position), string>();

        // Indexed rather than added: two files on one track is a state the
        // catalogue permits and this endpoint has no business throwing over. The
        // label is there to say "something already sits here", and one name says
        // that as well as two.
        foreach (var row in rows)
        {
            held[(row.DiscNumber, row.Position)] = NameOf(row.Path);
        }

        return held;
    }
}

/// <summary>Albums matching what somebody typed, in MusicBrainz's own order.</summary>
/// <param name="Total">
/// Results returned, which is not the number that exist: this asks for one page
/// and MusicBrainz has more. A person who cannot see their album types a better
/// query rather than paging to it.
/// </param>
public sealed record ReleaseSearchResponse(
    string Query,
    int Total,
    IReadOnlyList<ReleaseSearchRow> Items);

/// <summary>One album a person could be looking for.</summary>
/// <param name="Score">
/// MusicBrainz's relevance, 0-100, or null for a release fetched by identifier —
/// where relevance is not a question anybody asked.
/// </param>
public sealed record ReleaseSearchRow(
    Guid Mbid,
    string Title,
    string? Artist,
    int? Year,
    string? Country,
    string? Status,
    string? PrimaryType,
    IReadOnlyList<string> SecondaryTypes,
    string? Formats,
    int TrackCount,
    int DiscCount,
    int? Score);

/// <summary>One album's every position, for seating files onto.</summary>
public sealed record ReleaseSlotsResponse(
    Guid Mbid,
    string Title,
    string? Artist,
    int? Year,
    string? Country,
    string? Status,
    Guid? ReleaseGroup,
    string? PrimaryType,
    IReadOnlyList<string> SecondaryTypes,
    int DiscCount,
    IReadOnlyList<ReleaseSlotRow> Slots);

/// <summary>
/// One position on an album, as MusicBrainz prints it.
/// </summary>
/// <param name="Number">The printed number, which is not always the position: "A1", "12a".</param>
/// <param name="DurationMs">
/// The printed length in milliseconds, beside the formatted one. Both, because
/// the client shows the first and subtracts with the second, and parsing
/// <c>m:ss</c> back out of a string to do arithmetic is how a rounding rule ends
/// up written twice.
/// </param>
/// <param name="Recording">
/// The recording this position is an appearance of — the identity a file seated
/// here inherits, and the reason this endpoint returns it rather than only a
/// title.
/// </param>
/// <param name="HeldBy">
/// The file already sitting here, when the caller named a folder — see
/// <c>HeldSlotsAsync</c>. Null means the position is free, which is also what
/// every position reads as when no folder was given.
/// </param>
public sealed record ReleaseSlotRow(
    int DiscNumber,
    int Position,
    string? Number,
    string Title,
    string? Artist,
    string? Duration,
    int? DurationMs,
    Guid? Recording,
    string? HeldBy);

/// <summary>
/// A person's claim: these files, on these positions of this album.
/// </summary>
/// <remarks>
/// The pairing arrives whole rather than being derived here from an order or a
/// filename. Deriving it would be a matching rule, and a matching rule over
/// these files is the thing three passes have already declined to write — see
/// the class remarks. What the endpoint owes in exchange is that the pairing it
/// commits is exactly the pairing somebody was shown.
/// </remarks>
public sealed record AlbumFilingRequest(Guid Release, IReadOnlyList<AlbumFilingPair> Pairs);

/// <summary>One file, one position.</summary>
public sealed record AlbumFilingPair(Guid File, int Disc, int Position);

/// <summary>What one filing did.</summary>
/// <param name="Skipped">
/// Pairs naming a file that is no longer an open question. Reported rather than
/// silently dropped: the commonest cause is two tabs, and a count that does not
/// match what was ticked is the only thing that says so.
/// </param>
public sealed record AlbumFilingResponse(
    Guid Release,
    string Title,
    int Filed,
    int Skipped,
    string Detail);
