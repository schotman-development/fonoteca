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

    /// <summary>Event type for a person saying a folder is nobody's release.</summary>
    private const string FolderUnreleasedEventType = "matching.folder.unreleased";

    /// <summary>Event type for a person saying a pass got a whole folder wrong.</summary>
    private const string FolderReopenedEventType = "matching.folder.reopened";

    /// <summary>What the event log calls a folder somebody dismissed whole.</summary>
    private const string FolderSubject = "album-folder";

    /// <summary>
    /// How much of a folder path fits in <c>DomainEvent.SubjectId</c>.
    /// </summary>
    /// <remarks>
    /// The column is <c>varchar(200)</c> and a library path is up to 4096 — the
    /// same mismatch that broke a live tagging run at file 76, which is why the
    /// undo journal is keyed by id and never by path. There is no id for a
    /// folder, so the path is the subject and it is cut to fit. The whole one is
    /// in the payload, where nothing constrains it.
    /// </remarks>
    private const int MaximumSubjectId = 200;

    /// <summary>
    /// Most files one folder listing returns.
    /// </summary>
    /// <remarks>
    /// A guard on the path rather than a page size: the screen asks about
    /// <c>Artist/Album</c>, and the largest thing that legitimately is one is a
    /// box set. A path a level higher is a discography, and printing eleven
    /// hundred rows into a disclosure is not the answer to that — the count
    /// still comes back whole, so the screen can say what it is not showing.
    /// </remarks>
    private const int MaximumFolderFiles = 300;

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

        group.MapGet("/matching/folders/files", GetFolderContents)
            .WithName("GetFolderContents")
            .WithSummary("Every file in one folder, and what each of them is matched to.")
            .WithDescription(
                "The whole folder, not the open questions in it — which is the one view of a "
                + "rip nothing else here offers. The worklist lists what the passes refused, so "
                + "a folder of sixteen files that were matched wrongly, minus the three that "
                + "were refused, appears on it as a three-file album; the thirteen that are "
                + "wrong are on no screen at all.\n\n"
                + "Each row says whether the file is still an open question and, when it is not, "
                + "the album, disc, position and track it was filed under, and how certain that "
                + "was. Ordered by path, so the discs of a set come back in the order they sit "
                + "in.\n\n"
                + "A catalogue read and nothing more: no provider call, no file opened, nothing "
                + "written.")
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound);

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

        group.MapPost("/matching/folders/unreleased", MarkFolderUnreleased)
            .WithName("MarkFolderUnreleased")
            .WithSummary("Say a folder is nobody's release, so it stops being asked about.")
            .WithDescription(
                "The other answer to the question `matching/files/release` answers. Some folders "
                + "are somebody's own compilation — tracks pulled off YouTube, a mixtape, a rip of "
                + "a set never issued as an album or a single — and no search will ever find them, "
                + "because there is nothing to find. Left alone they sit on the worklist forever "
                + "and every pass re-asks about them at the rate limit.\n\n"
                + "`folder` is a library-relative path and matches everything beneath it, so "
                + "`Artist/Album` covers its `CD1` and `CD2` and `Artist` covers the lot. Only "
                + "files that are currently open questions are touched: a file the passes placed "
                + "confidently keeps its identity, its release and its track, because this says "
                + "'stop asking', not 'forget what you know'. Each of the three outcomes is set to "
                + "`Unreleased` only where that pass had in fact refused.\n\n"
                + "Nothing on disk is touched — no tag write, no `Fonoteca:AllowFileMutation`, no "
                + "undo journal, one decision entry naming the folder. Undoing it is a hand-written "
                + "`UPDATE`, the same as re-asking a library after a rule change.")
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        group.MapPost("/matching/folders/reopen", ReopenFolder)
            .WithName("ReopenFolder")
            .WithSummary("Say the passes got this folder wrong, and ask it again by hand.")
            .WithDescription(
                "The answer to a *confident* mistake, which is the one kind nothing else here can "
                + "reach. A refused file is on the worklist; a wrongly-matched one is on no screen "
                + "at all — a live set whose tracks AcoustID matched to the studio recordings of "
                + "the same songs reads as a finished album until somebody plays it.\n\n"
                + "Every file under `folder` that a pass placed gives up its recording, track, "
                + "release and release group, and comes back as one folder-shaped question with "
                + "the outcome `ReopenedByPerson`. Files already waiting on an answer are left "
                + "exactly as they are: `Unknown` says something true about the audio that "
                + "`ReopenedByPerson` does not.\n\n"
                + "**No pass will answer it again.** The three lookup stamps are deliberately "
                + "left set, because they record that the providers were asked — which is still "
                + "true, and is what keeps every pass off the file. Clearing them would hand the "
                + "folder back to the rule that got it wrong, with the same evidence and "
                + "therefore the same answer. The fingerprint, the AcoustID and any tag already "
                + "written to disk are all kept; nothing on disk is touched.")
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);
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
    /// What is actually in a folder, whatever the worklist says about it.
    /// </summary>
    /// <remarks>
    /// <b>The screen was showing the leftovers and calling them the album.</b>
    /// Every other list on the matching page is built from refusals, which is
    /// right for deciding what still needs an answer and wrong for deciding
    /// whether an answer already given was any good. The measured example is a
    /// 2019 concert where thirteen of sixteen files went to the studio album of
    /// the same name: the three the pass refused are on the worklist, the
    /// thirteen wrong ones are on nothing, and the folder reads as a three-file
    /// album.
    ///
    /// So this is the folder as it is on disk, matched files included, and it
    /// is what makes <c>matching/folders/reopen</c> a decision somebody can take
    /// with their eyes open rather than a guess.
    ///
    /// <b>It is deliberately not a second worklist.</b> The rows carry no
    /// candidates and nothing here is answerable: a file that is an open
    /// question says so and the screen already knows what to do with one, and a
    /// file that is matched is shown so it can be *read*. Overruling a decision
    /// stays where it is, one folder at a time, because the endpoint that does
    /// it is a bulk write and pretending otherwise on a per-row basis would
    /// promise something no write here delivers.
    /// </remarks>
    private static async Task<Results<Ok<FolderContentsResponse>, ProblemHttpResult>>
        GetFolderContents(
            string? folder,
            FonotecaDbContext db,
            CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(folder))
        {
            return TypedResults.Problem(
                title: "No folder named",
                detail: "`folder` is required, and is a library-relative path.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        // The trailing slash is load-bearing, and it is the same one the held
        // slots query carries: without it `Artist/Album` also matches
        // `Artist/Album Live`, which is a different record.
        var prefix = folder.EndsWith('/') ? folder : folder + "/";

        var files = db.MediaFiles.AsNoTracking().Where(file => file.Path.StartsWith(prefix));

        var total = await files.CountAsync(cancellationToken).ConfigureAwait(false);

        if (total == 0)
        {
            return TypedResults.Problem(
                title: "No files in that folder",
                detail:
                    $"The catalogue holds nothing under “{folder}”. A scan may not have reached "
                    + "it, or the path may not be the one the worklist printed.",
                statusCode: StatusCodes.Status404NotFound);
        }

        // Counted in the database over the whole folder rather than over the page
        // below it. Past the cap the two numbers are about different sets, and
        // "12 waiting, 140 matched" computed from a truncated list would
        // overstate the matched half — on precisely the folders too big to read.
        var open = await files
            .CountAsync(
                file => (file.IdentityDecidedUtc == null
                        && (UnidentifiedOutcomes.Contains(file.AcoustIdOutcome)
                            || UnlinkedOutcomes.Contains(file.EnrichmentOutcome)))
                    || (file.ReleaseDecidedUtc == null
                        && UnattributedOutcomes.Contains(file.AttributionOutcome)),
                cancellationToken)
            .ConfigureAwait(false);

        var rows = await files
            .OrderBy(file => file.Path)
            .Take(MaximumFolderFiles)
            .Select(file => new
            {
                file.Id,
                file.Path,
                file.FingerprintDuration,
                Measured = file.Quality == null ? null : file.Quality.Duration,
                file.AcoustIdOutcome,
                file.EnrichmentOutcome,
                file.AttributionOutcome,
                file.IdentityDecidedUtc,
                file.ReleaseDecidedUtc,
                Recording = file.Recording == null ? null : file.Recording.Title,
                ReleaseId = file.Release == null ? (Guid?)null : file.Release.Id.Value,
                Release = file.Release == null ? null : file.Release.Title,
                Year = file.Release == null ? null : file.Release.ReleasedYear,
                Disc = file.Track == null ? (int?)null : file.Track.DiscNumber,
                Position = file.Track == null ? (int?)null : file.Track.Position,
                Track = file.Track == null ? null : file.Track.Title,
            })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var items = rows
            .Select(row =>
            {
                var unidentified = row.IdentityDecidedUtc is null
                    && (UnidentifiedOutcomes.Contains(row.AcoustIdOutcome)
                        || UnlinkedOutcomes.Contains(row.EnrichmentOutcome));

                // The attribution leg, for the reason the unreleased handler
                // already gives: a folder holding files attribution refused is a
                // folder with an open question in it, and a screen that lists
                // the folder while hiding those rows offers no way to answer
                // them. It is the *only* way to answer some of them — a file
                // whose AcoustID names a recording MusicBrainz lists on no
                // edition of this album cannot be recovered by asking again,
                // because the candidate set is browsed from that same recording.
                var unattributed = row.ReleaseDecidedUtc is null
                    && UnattributedOutcomes.Contains(row.AttributionOutcome);

                var unanswered = unidentified || unattributed;

                return new FolderFileRow(
                    row.Id.Value,
                    NameOf(row.Path),
                    FolderOf(row.Path),
                    Format(row.FingerprintDuration ?? row.Measured),
                    unanswered,

                    // The first pass that refused names the question, exactly as
                    // the worklist folds it: a file AcoustID could not place is
                    // left `NotAttempted` by enrichment, and reading that later
                    // silence would report a consequence instead of a cause.
                    !unanswered
                        ? null
                        : UnidentifiedOutcomes.Contains(row.AcoustIdOutcome)
                            ? row.AcoustIdOutcome.ToString()
                            : unidentified
                                ? row.EnrichmentOutcome.ToString()
                                : row.AttributionOutcome.ToString(),
                    row.Recording,
                    row.ReleaseId,
                    row.Release,
                    row.Year,
                    row.Disc,
                    row.Position,
                    row.Track,
                    row.AttributionOutcome.ToString());
            })
            .ToList();

        return TypedResults.Ok(new FolderContentsResponse(folder, total, open, items));
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

    /// <summary>
    /// One person's claim that a folder is nobody's release, committed.
    /// </summary>
    /// <remarks>
    /// <b>The answer the worklist had no way of taking.</b> Every other chooser
    /// on that screen ends in an identity — a recording, an album, a seating.
    /// This one ends in there being none, and that is not the same as a pass
    /// giving up: a refusal is a question left open, and a person saying "this is
    /// my own compilation" closes it. Without this the folder is asked about by
    /// three passes forever and read by a person on every visit to the screen.
    ///
    /// <b>Only the legs that were refused are written.</b> A folder is rarely
    /// wholly unmatched — the passes place most of a rip — and stamping every
    /// file under a prefix would throw away identities that are correct and
    /// expensive. So each of the three outcomes moves to
    /// <see cref="AcoustIdOutcome.Unreleased"/> only where it currently sits on
    /// that pass's own refusal list, and a file with nothing open is not counted.
    ///
    /// <b>It takes <see cref="LibraryWorkGate"/>, like every other decision
    /// here.</b> Which excludes the three passes and, as documented on the gate,
    /// not a scan — a scan that sees the bytes change clears these columns with
    /// everything else derived, which is the right answer: the claim was about
    /// audio that is no longer there.
    /// </remarks>
    private static async Task<Results<Ok<FolderUnreleasedResponse>, ProblemHttpResult>>
        MarkFolderUnreleased(
            FolderUnreleasedRequest request,
            FonotecaDbContext db,
            IEventLog events,
            LibraryWorkGate gate,
            ICallerContext caller,
            IClock clock,
            CancellationToken cancellationToken)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.Folder))
        {
            return TypedResults.Problem(
                title: "Nothing to mark",
                detail: "The request body must name a library-relative `folder`.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        // Not trimmed: a trailing space is legal in a directory name on every
        // filesystem this runs on, and silently removing one turns a real folder
        // into a prefix that matches nothing.
        var folder = request.Folder;

        // The trailing slash for `HeldSlotsAsync`'s reason — `Artist/Album` must
        // not also match `Artist/Album Live` — and it is what makes a two-disc
        // rip's `CD1` and `CD2` both included from the one heading the client
        // shows. A path is never marked by naming it exactly: this dismisses a
        // folder, and the unit is the folder.
        var prefix = folder.EndsWith('/') ? folder : folder + "/";

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

        // The worklist's own two predicates, unioned and unmodified — `loose` and
        // the component query in `GetOpenQuestions`. What this closes is exactly
        // what that screen shows under this path, which is the only definition
        // that cannot surprise the person who pressed the button.
        //
        // Note what is deliberately *not* here: the filing endpoint narrows its
        // set with `IdentityDecidedUtc == null` and this must not, because the
        // worklist does not either. A file already decided on identity and still
        // refused on enrichment is on the screen, and a set that skipped it would
        // leave a row the button appeared to cover and then 404 on.
        //
        // The attribution leg is included for the folder's sake: a folder holding
        // files the attribution pass refused comes back as a component question
        // with the same path printed on it, and closing only half of a folder is
        // not closing it.
        var rows = await db.MediaFiles
            .Where(file => file.Path.StartsWith(prefix)
                && (UnidentifiedOutcomes.Contains(file.AcoustIdOutcome)
                    || UnlinkedOutcomes.Contains(file.EnrichmentOutcome)
                    || (file.ReleaseDecidedUtc == null
                        && UnattributedOutcomes.Contains(file.AttributionOutcome))))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        if (rows.Count == 0)
        {
            return TypedResults.Problem(
                title: "Nothing open in that folder",
                detail:
                    $"No file under \u201c{folder}\u201d is waiting on an answer. Either the folder "
                    + "was answered while this screen was open, or the path does not match what the "
                    + "catalogue holds \u2014 re-read the worklist.",
                statusCode: StatusCodes.Status404NotFound);
        }

        var now = StoreTime.ToStorePrecision(clock.UtcNow);
        var correlationId = Guid.CreateVersion7().ToString("N")[..12];

        foreach (var row in rows)
        {
            // Leg by leg, and never over a pass that succeeded. A folder of
            // thirty files with two open questions in it keeps twenty-eight
            // identities, twenty-eight recordings and twenty-eight track links.
            if (row.IdentityDecidedUtc == null
                && UnidentifiedOutcomes.Contains(row.AcoustIdOutcome))
            {
                row.AcoustIdOutcome = AcoustIdOutcome.Unreleased;

                // Only if nothing ever asked, for the reason the rejection path
                // states: the stamp means "AcoustID has been put this question",
                // and overwriting a real one with the time somebody answered
                // would misreport when the provider was last consulted.
                row.AcoustIdCheckedUtc ??= now;
                row.IdentityDecidedUtc = now;
            }

            if (UnlinkedOutcomes.Contains(row.EnrichmentOutcome))
            {
                row.EnrichmentOutcome = EnrichmentOutcome.Unreleased;
                row.RecordingLookupUtc ??= now;

                // The identification leg may have been `Identified` and left
                // alone above — this file is open on enrichment, not on identity
                // — but the decided stamp is what keeps enrichment's own worklist
                // off it, so it is written here too.
                row.IdentityDecidedUtc ??= now;
            }

            if (row.ReleaseDecidedUtc == null
                && UnattributedOutcomes.Contains(row.AttributionOutcome))
            {
                row.AttributionOutcome = ReleaseAttributionOutcome.Unreleased;
                row.ReleaseLookupUtc ??= now;
                row.ReleaseDecidedUtc = now;
            }
        }

        await events.AppendAsync(
            DomainEvent.Create(
                FolderUnreleasedEventType,
                FolderSubject,
                Fit(folder),
                caller.ActorId,
                now,
                JsonSerializer.Serialize(
                    new FolderUnreleasedPayload
                    {
                        Folder = folder,
                        Closed = rows.Count,
                    },
                    MatchingJson.Default.FolderUnreleasedPayload),
                correlationId),
            cancellationToken).ConfigureAwait(false);

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return TypedResults.Ok(new FolderUnreleasedResponse(
            folder,
            rows.Count,
            $"{rows.Count} file{(rows.Count == 1 ? "" : "s")} under \u201c{folder}\u201d marked as "
            + "coming from no release. Nothing on disk was touched."));
    }

    /// <summary>
    /// One person's claim that a pass got a whole folder wrong, committed.
    /// </summary>
    /// <remarks>
    /// <b>The one mistake no worklist can show.</b> Everything else on the
    /// matching screen is a question a pass declined to answer. This is the
    /// opposite shape: the pass answered, confidently, and was wrong — and the
    /// files are therefore identified, linked, filed and on no screen. The case
    /// it was built for is a live album AcoustID matched to the studio
    /// recordings of the same songs, which reads as a finished album on every
    /// screen this application has until somebody plays it.
    ///
    /// <b>What is cleared is what a pass derived, and nothing that was
    /// measured.</b> The recording, the track, the release and the release group
    /// go, along with both decided-stamps and the edition count. The
    /// fingerprint, the AcoustID, the cached provider answers and any tag
    /// already on disk all stay: they are facts about audio that has not
    /// changed, and re-deriving them would cost a decode and a turn at the rate
    /// limit to arrive at the same values.
    ///
    /// <b>The three lookup stamps stay set, and that is the load-bearing
    /// half.</b> <see cref="MediaFile.AcoustIdCheckedUtc"/>,
    /// <see cref="MediaFile.RecordingLookupUtc"/> and
    /// <see cref="MediaFile.ReleaseLookupUtc"/> mean "this provider has been put
    /// this question", which stays true after somebody disagrees with the
    /// answer — and each is its pass's worklist. Clearing them, which is what
    /// "reopen" sounds like it should do, hands the folder straight back to the
    /// rule that got it wrong, on the next run, with the same evidence and so
    /// the same answer. The file is reopened for a <i>person</i>;
    /// <see cref="AcoustIdOutcome.ReopenedByPerson"/> is how the worklist sees
    /// it.
    ///
    /// <b>Files already open are left alone.</b> A folder is rarely wholly
    /// wrong: the three files in the worked example that AcoustID had never
    /// heard are <see cref="AcoustIdOutcome.Unknown"/>, which says something true
    /// about the audio that <c>ReopenedByPerson</c> does not, and they are
    /// already on the same screen under the same folder heading. Overwriting
    /// them would lose the distinction and change nothing a person can see.
    /// </remarks>
    private static async Task<Results<Ok<FolderReopenResponse>, ProblemHttpResult>>
        ReopenFolder(
            FolderReopenRequest request,
            FonotecaDbContext db,
            IEventLog events,
            LibraryWorkGate gate,
            ICallerContext caller,
            IClock clock,
            CancellationToken cancellationToken)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.Folder))
        {
            return TypedResults.Problem(
                title: "Nothing to reopen",
                detail: "The request body must name a library-relative `folder`.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        // Untrimmed, and slash-terminated, for the reasons the dismissal above
        // states: a directory name may legally end in a space, and `Artist/Album`
        // must not also match `Artist/Album Live`.
        var folder = request.Folder;
        var prefix = folder.EndsWith('/') ? folder : folder + "/";

        if (!gate.TryEnter(DecisionWorkKind, out var lease))
        {
            return TypedResults.Problem(
                title: "The library is busy",
                detail:
                    $"A {gate.ActiveKind ?? "pass"} is running, and it may rewrite exactly the "
                    + "columns this clears. Answer again once it has finished.",
                statusCode: StatusCodes.Status409Conflict);
        }

        using var held = lease;

        // "Placed by something" rather than "not currently a question", because
        // those differ on the row that matters. A file can carry a recording and
        // still be open — identified, linked, and refused by attribution — and
        // that file belongs to the album question being reopened just as much as
        // the ones that were filed. The set is therefore everything under the
        // path with a derived link or a decision on it, and the reopen is a
        // no-op for a row that has neither.
        var rows = await db.MediaFiles
            .Where(file => file.Path.StartsWith(prefix)
                && (file.RecordingId != null
                    || file.ReleaseId != null
                    || file.TrackId != null
                    || file.ReleaseGroupId != null
                    || file.IdentityDecidedUtc != null
                    || file.ReleaseDecidedUtc != null))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        if (rows.Count == 0)
        {
            return TypedResults.Problem(
                title: "Nothing matched in that folder",
                detail:
                    $"No file under \u201c{folder}\u201d has an identity, a release or a decision "
                    + "to give up \u2014 either the folder is already one open question, or the "
                    + "path does not match what the catalogue holds.",
                statusCode: StatusCodes.Status404NotFound);
        }

        var now = StoreTime.ToStorePrecision(clock.UtcNow);
        var correlationId = Guid.CreateVersion7().ToString("N")[..12];

        foreach (var row in rows)
        {
            row.RecordingId = null;
            row.TrackId = null;
            row.ReleaseId = null;
            row.ReleaseGroupId = null;
            row.EditionAlternatives = 0;

            row.IdentityDecidedUtc = null;
            row.ReleaseDecidedUtc = null;

            row.AcoustIdOutcome = AcoustIdOutcome.ReopenedByPerson;

            // Both of the later legs go back to "no answer", which is what they
            // now are: the recording they were about is gone. They are not moved
            // to a refusal — no pass refused anything here — and NotAttempted is
            // not on any worklist, which is right. The question is the
            // identification one, asked once for the folder, and answering it
            // through `matching/files/release` writes all three legs again.
            row.EnrichmentOutcome = EnrichmentOutcome.NotAttempted;
            row.AttributionOutcome = ReleaseAttributionOutcome.NotAttempted;
        }

        await events.AppendAsync(
            DomainEvent.Create(
                FolderReopenedEventType,
                FolderSubject,
                Fit(folder),
                caller.ActorId,
                now,
                JsonSerializer.Serialize(
                    new FolderReopenedPayload
                    {
                        Folder = folder,
                        Reopened = rows.Count,
                    },
                    MatchingJson.Default.FolderReopenedPayload),
                correlationId),
            cancellationToken).ConfigureAwait(false);

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return TypedResults.Ok(new FolderReopenResponse(
            folder,
            rows.Count,
            $"{rows.Count} file{(rows.Count == 1 ? "" : "s")} under \u201c{folder}\u201d gave up "
            + "the recording and album a pass chose, and are now one question for you. No pass "
            + "will answer them again. Nothing on disk was touched."));
    }

    /// <summary>A folder path cut to what <c>DomainEvent.SubjectId</c> holds.</summary>
    /// <remarks>
    /// One character back if the cut lands between the halves of a surrogate
    /// pair, which a library full of non-Latin folder names will find eventually.
    /// A lone surrogate is not text: it survives the serialiser as a replacement
    /// character and reaches PostgreSQL as bytes nobody meant, for a log key.
    /// </remarks>
    private static string Fit(string folder) =>
        folder.Length <= MaximumSubjectId
            ? folder
            : folder[..(char.IsHighSurrogate(folder[MaximumSubjectId - 1])
                ? MaximumSubjectId - 1
                : MaximumSubjectId)];

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

/// <summary>
/// A person's claim: nothing under this folder came from a release.
/// </summary>
/// <remarks>
/// The whole request, because the whole claim is the folder. There is nowhere
/// for a reason to go that anything would read back, and the folder name is
/// already the most legible one available.
/// </remarks>
public sealed record FolderUnreleasedRequest(string Folder);

/// <summary>
/// A person's claim: the passes matched this folder to the wrong thing.
/// </summary>
/// <remarks>
/// The folder and nothing else, like the dismissal beside it. There is no field
/// for which album it should have been, because that is the next question rather
/// than part of this one — reopening puts the folder on the worklist, and the
/// worklist is where it gets an album.
/// </remarks>
public sealed record FolderReopenRequest(string Folder);

/// <summary>What reopening one folder did.</summary>
/// <param name="Reopened">
/// Files that gave up a derived identity or album. Not the size of the folder:
/// files that were already open questions are left as they are, since their
/// refusal says something true that "somebody disagreed" does not.
/// </param>
public sealed record FolderReopenResponse(string Folder, int Reopened, string Detail);

/// <summary>What marking one folder did.</summary>
/// <param name="Closed">
/// Files that had an open question and now do not. Not the size of the folder:
/// everything the passes had already placed was left exactly as it was.
/// </param>
public sealed record FolderUnreleasedResponse(string Folder, int Closed, string Detail);

/// <summary>One folder as it sits on disk, answered questions and all.</summary>
/// <param name="Files">
/// Files under the path, whole — not the length of <paramref name="Items"/>,
/// which stops at <c>MaximumFolderFiles</c>. A listing cut short says so by the
/// two disagreeing, which is the only honest way for a capped list to read.
/// </param>
/// <param name="Open">
/// How many of them can still be answered, counted over the whole folder rather
/// than over <paramref name="Items"/> — which stops at the cap, and would
/// otherwise report the two halves of a large folder against different sets.
/// Beside <paramref name="Files"/> it is the gap that is the whole point of the
/// endpoint.
/// </param>
public sealed record FolderContentsResponse(
    string Folder,
    int Files,
    int Open,
    IReadOnlyList<FolderFileRow> Items);

/// <summary>One file in a folder, and whatever it has been matched to.</summary>
/// <param name="Folder">
/// The file's own folder, which is not the one that was asked about: a two-disc
/// rip is one album question and two directories, and a person checking a
/// pairing needs to see which disc a row is on.
/// </param>
/// <param name="Open">
/// Whether this file can still be answered here — <c>FileFilesUnderRelease</c>'s
/// own predicate, which is the useful one for a row that offers a tick: what
/// this claims and what a filing will actually accept cannot drift apart.
///
/// It is one condition narrower than the worklist's, which does not exclude
/// <see cref="MediaFile.IdentityDecidedUtc"/>. Nothing reaches that difference
/// today — every path that stamps it also moves the file out of the enrichment
/// refusals — but if one ever did, this would call the file matched while the
/// worklist went on asking about it, and the tick is the half that has to be
/// honest.
/// </param>
/// <param name="Reason">The refusal, when it is open. Null when it is not.</param>
/// <param name="Certainty">
/// <c>ReleaseAttributionOutcome</c>'s own name. <c>Attributed</c> and
/// <c>AttributedByPerson</c> are both matched and only one of them was a rule's
/// doing, which is exactly the distinction somebody hunting a wrong match is
/// looking for.
/// </param>
public sealed record FolderFileRow(
    Guid MediaFileId,
    string Name,
    string Folder,
    string? Length,
    bool Open,
    string? Reason,
    string? Recording,
    Guid? ReleaseId,
    string? Release,
    int? Year,
    int? Disc,
    int? Position,
    string? Track,
    string Certainty);
