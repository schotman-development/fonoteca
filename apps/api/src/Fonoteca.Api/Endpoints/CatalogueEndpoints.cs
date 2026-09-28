using System.Globalization;
using System.Text.Json;
using Fonoteca.Api.Configuration;
using Fonoteca.Api.Library;
using Fonoteca.Api.Matching;
using Fonoteca.Data;
using Fonoteca.Domain.Abstractions;
using Fonoteca.Domain.Acquisition;
using Fonoteca.Domain.Catalogue;
using Fonoteca.Domain.Events;
using Fonoteca.Domain.Identification;
using Fonoteca.Tagging;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

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
public static partial class CatalogueEndpoints
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

    /// <summary>
    /// The most open questions one response will carry.
    /// </summary>
    /// <remarks>
    /// Two orders of magnitude below <see cref="DefaultTake"/>, and not for the
    /// same reason. An artist is a row; a question is a card that a person is
    /// meant to read one of, and a worklist nobody could finish reading is a
    /// worklist nobody starts. The tail is long — measured against the target
    /// library, 697 files carry a refusal — so paging here is the working limit
    /// rather than the backstop it is above.
    /// </remarks>
    private const int DefaultQuestions = 200;

    private const int MaxQuestions = 1_000;

    /// <summary>
    /// The most recordings one file's candidate set will name.
    /// </summary>
    /// <remarks>
    /// A cap on requests rather than on reading. Every candidate past the
    /// AcoustID answer costs a MusicBrainz lookup at the gate, and a fingerprint
    /// matching a much-compiled track can name dozens of recordings — most of
    /// them the same performance under different MBIDs. The full size comes back
    /// as <c>Total</c>, so a truncated set says so instead of looking complete.
    ///
    /// Six because each one is the heaviest lookup this application makes.
    /// <c>GetRecordingAsync</c> asks for artists, credits, releases, release
    /// groups, media, ISRCs and two kinds of relationship — the request measured
    /// at 10.3 seconds cold, and the reason the attempt timeout is 30s rather
    /// than the standard 10. Twelve of those behind one gate is a minute of
    /// somebody's afternoon and a minute stolen from any pass that is running.
    ///
    /// Six is also enough for the refusals this exists for. They are near-ties:
    /// an ambiguous file has two or three clusters and a handful of recordings
    /// behind them, and anything past the first few is not what the rule
    /// hesitated over. Measured against the real library, the worked example
    /// returns five.
    /// </remarks>
    private const int MaxCandidates = 6;

    /// <summary>Releases printed against one candidate before it starts counting.</summary>
    /// <remarks>
    /// A reading limit, not a request limit — the whole list arrived with the
    /// lookup. Six candidates times twenty-five releases is a hundred and fifty
    /// rows on one screen, and the ones past the first few are compilations
    /// nobody is choosing between. <c>Appearances</c> carries the full count, so
    /// a cut list says so.
    /// </remarks>
    private const int MaxAppearances = 8;

    /// <summary>Uncredited performers printed against one candidate.</summary>
    /// <remarks>
    /// Enough for a conductor, an orchestra, a choir and a handful of soloists,
    /// which is the case this exists for. A big band's full personnel is a
    /// different screen.
    /// </remarks>
    private const int MaxPerformers = 8;

    /// <summary>ISRCs printed against one candidate.</summary>
    /// <remarks>
    /// A recording carries several when it has been licensed into several
    /// territories, and they are all equally the answer — the point is whether
    /// one of them is in the file's tags, not which.
    /// </remarks>
    private const int MaxIsrcs = 6;

    /// <summary>
    /// How long a stored answer from AcoustID or MusicBrainz is believed.
    /// </summary>
    /// <remarks>
    /// A week, and the number is a judgement about how fast the two databases
    /// move against how much a fresh answer costs. AcoustID gains submissions
    /// daily and MusicBrainz merges recordings constantly, so an answer does go
    /// out of date — but not in an afternoon, and the alternative was paying one
    /// AcoustID turn plus up to six MusicBrainz recording lookups <i>every time
    /// somebody opened the same question</i>. Measured cold, that is around a
    /// minute of waiting to re-read something the application had already been
    /// told.
    ///
    /// Nothing expires it in the background. A stale entry is simply rebuilt by
    /// the next reader, which is the only moment anybody is waiting for it
    /// anyway, and <c>?refresh=true</c> is there for the case where a person has
    /// reason to believe the answer has moved.
    /// </remarks>
    internal static readonly TimeSpan CacheDuration = TimeSpan.FromDays(7);

    /// <summary>
    /// Identification refusals a person could answer.
    /// </summary>
    /// <remarks>
    /// <see cref="AcoustIdOutcome.Unfingerprintable"/> is deliberately absent.
    /// The other three are questions about the music — nobody has submitted this
    /// audio, two clusters were too close to call, nothing matched well enough —
    /// and all three have an answer a person could supply. A file the decoder
    /// could not read has none: the follow-up is to check whether the file is
    /// intact, which is a different screen and a different fix. Listing it here
    /// would put work in a queue that answering the question cannot clear.
    ///
    /// <see cref="AcoustIdOutcome.ReopenedByPerson"/> is here for the opposite
    /// reason: it is not a refusal at all, but a person saying a confident
    /// answer was wrong. It has to appear on the worklist or reopening a folder
    /// does nothing visible, and it has to be in <i>this</i> set rather than
    /// only in the worklist query, because the same set is what
    /// <c>FileFilesUnderRelease</c> accepts and what "not a release" can close.
    /// A file that can be reopened and then not answered is worse than one that
    /// was never reopened.
    /// </remarks>
    private static readonly AcoustIdOutcome[] UnidentifiedOutcomes =
    [
        AcoustIdOutcome.Unknown,
        AcoustIdOutcome.Ambiguous,
        AcoustIdOutcome.BelowThreshold,
        AcoustIdOutcome.ReopenedByPerson,
        AcoustIdOutcome.ReopenedByAgent,
    ];

    /// <summary>Enrichment refusals: the audio is known, the recording is not.</summary>
    private static readonly EnrichmentOutcome[] UnlinkedOutcomes =
        [EnrichmentOutcome.NoRecording, EnrichmentOutcome.RecordingNotFound];

    /// <summary>Attribution refusals: the recording is known, the edition is not.</summary>
    private static readonly ReleaseAttributionOutcome[] UnattributedOutcomes =
    [
        ReleaseAttributionOutcome.NoConfidentFit,
        ReleaseAttributionOutcome.NoCandidate,
        ReleaseAttributionOutcome.OrderContradicted,
    ];

    /// <summary>"Not from a release", by a person or an agent, on each of the three legs.</summary>
    private static readonly AcoustIdOutcome[] UnreleasedIdentities =
        [AcoustIdOutcome.Unreleased, AcoustIdOutcome.UnreleasedByAgent];

    private static readonly EnrichmentOutcome[] UnreleasedRecordings =
        [EnrichmentOutcome.Unreleased, EnrichmentOutcome.UnreleasedByAgent];

    private static readonly ReleaseAttributionOutcome[] UnreleasedAttributions =
        [ReleaseAttributionOutcome.Unreleased, ReleaseAttributionOutcome.UnreleasedByAgent];

    /// <summary>
    /// What a decision takes the library work gate as.
    /// </summary>
    /// <remarks>
    /// Named like the passes' own kinds, because it appears in the same place
    /// they do — the message a pass gets when it tries to start mid-decision
    /// reads "a matching.decide is running", and a person reading a log should
    /// not have to work out that a request held the gate.
    /// </remarks>
    private const string DecisionWorkKind = "matching.decide";

    /// <summary>
    /// The outcome a decision records: the by-a-person value given, or its agent
    /// twin when the caller is an agent working through <c>/mcp</c>.
    /// </summary>
    /// <remarks>
    /// One mapping per enum and in one place, so every decision endpoint gets the
    /// distinction by naming the person's value and nothing more. A value with no
    /// twin throws rather than being recorded as the person's.
    /// </remarks>
    private static AcoustIdOutcome ByCaller(ICallerContext caller, AcoustIdOutcome decided) =>
        caller is not AgentCallerContext ? decided : decided switch
        {
            AcoustIdOutcome.IdentifiedByPerson => AcoustIdOutcome.IdentifiedByAgent,
            AcoustIdOutcome.RejectedByPerson => AcoustIdOutcome.RejectedByAgent,
            AcoustIdOutcome.Unreleased => AcoustIdOutcome.UnreleasedByAgent,
            AcoustIdOutcome.ReopenedByPerson => AcoustIdOutcome.ReopenedByAgent,
            _ => throw new ArgumentOutOfRangeException(nameof(decided), decided, "No agent twin."),
        };

    private static EnrichmentOutcome ByCaller(ICallerContext caller, EnrichmentOutcome decided) =>
        caller is not AgentCallerContext ? decided : decided switch
        {
            EnrichmentOutcome.LinkedByPerson => EnrichmentOutcome.LinkedByAgent,
            EnrichmentOutcome.Unreleased => EnrichmentOutcome.UnreleasedByAgent,
            _ => throw new ArgumentOutOfRangeException(nameof(decided), decided, "No agent twin."),
        };

    private static ReleaseAttributionOutcome ByCaller(
        ICallerContext caller,
        ReleaseAttributionOutcome decided) =>
        caller is not AgentCallerContext ? decided : decided switch
        {
            ReleaseAttributionOutcome.AttributedByPerson => ReleaseAttributionOutcome.AttributedByAgent,
            ReleaseAttributionOutcome.NoReleaseByPerson => ReleaseAttributionOutcome.NoReleaseByAgent,
            ReleaseAttributionOutcome.Unreleased => ReleaseAttributionOutcome.UnreleasedByAgent,
            ReleaseAttributionOutcome.AlbumByPerson => ReleaseAttributionOutcome.AlbumByAgent,
            _ => throw new ArgumentOutOfRangeException(nameof(decided), decided, "No agent twin."),
        };

    /// <summary>Event type for a person naming which recording a file holds.</summary>
    private const string RecordingDecidedEventType = "matching.recording.decided";

    /// <summary>Event type for a person rejecting every candidate a file had.</summary>
    private const string RecordingRejectedEventType = "matching.recording.rejected";

    /// <summary>Event type for a person naming which release a component came from.</summary>
    private const string ComponentDecidedEventType = "matching.component.decided";

    /// <summary>Event type for a person rejecting every candidate release a component had.</summary>
    private const string ComponentRejectedEventType = "matching.component.rejected";

    /// <summary>What the event log calls a set of files decided together.</summary>
    /// <remarks>
    /// Not <c>file</c>, even though every file in the set is changed by the
    /// decision. The subject of an attribution decision is the component — one
    /// answer, taken once, about a set that a single file cannot name on its own
    /// — and writing it thirty-one times under thirty-one subjects would turn
    /// one decision into thirty-one indistinguishable ones in the log. The stamp
    /// is the component's identity, which is what <c>ReleaseLookupUtc</c> is.
    /// </remarks>
    private const string ComponentSubject = "component";

    /// <summary>
    /// <c>tag</c> when no write was attempted at all.
    /// </summary>
    /// <remarks>
    /// Not a <see cref="TagWriteStatus"/> value, and deliberately outside that
    /// enum: every member of it describes something a writer did or declined to
    /// do about a specific plan, and this is the case where there was nothing to
    /// plan — no cluster to write. Adding it to the enum would oblige the writer
    /// to have an opinion about a situation it never sees.
    /// </remarks>
    private const string NotTagged = "NotAttempted";

    /// <summary>
    /// <c>tag</c> when the file could not be read well enough to be changed safely.
    /// </summary>
    /// <remarks>
    /// Outside the enum for the same reason: the failure is in
    /// <c>TagReader</c>, before a plan exists.
    /// </remarks>
    private const string TagUnreadable = "Unreadable";

    public static IEndpointRouteBuilder MapCatalogueEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/catalogue").WithTags("Catalogue");

        group.MapGet("/artists", GetArtists)
            .WithName("GetArtists")
            .WithSummary("Album artists in the library, or everyone credited on one.")
            .WithDescription(
                "Ordered by sort name, or by `sort=tracks` for the most-held first — ties keep "
                + "the alphabetical order. `query` filters on the artist's name, case-insensitively, "
                + "anywhere in the string. By default the list is the artists an album is billed "
                + "to: the release's own credit line, collaborators included, plus anyone billed on "
                + "every track of an album — which is how a conductor and an orchestra are found on "
                + "a release the sleeve bills to the composer — and the track's own credit line for "
                + "files no release has been attributed to yet. "
                + "`scope=all` widens it to everyone the catalogue can reach a track through: "
                + "composers and lyricists of the work, conductors, ensembles and guest features, "
                + "which on a real library is an order of magnitude more names.");

        group.MapGet("/artists/{id:guid}", GetArtist)
            .WithName("GetArtist")
            .WithSummary("One artist and every track of theirs in the library.")
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPost("/artists/{id:guid}/follow", SetArtistFollowed)
            .WithName("SetArtistFollowed")
            .WithSummary("Follow or unfollow an artist the catalogue already holds.")
            .WithDescription(
                "Following is the one fact on an artist that is not derived from the library or "
                + "from a provider, and it is deliberately unrelated to what is held: an artist "
                + "with two hundred tracks may be unfollowed, and a followed artist may have no "
                + "files at all.\n\n"
                + "A followed artist appears on `artists` whatever their track count and whether "
                + "or not they are an album artist — `scope=following` narrows the list to them. "
                + "Unfollowing keeps the discography already fetched, so re-following spends no "
                + "provider requests.")
                .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPost("/artists/follow", FollowArtistByMbid)
            .WithName("FollowArtistByMbid")
            .WithSummary("Follow an artist by MusicBrainz id, whether or not the library holds them.")
            .WithDescription(
                "The only way to reach an artist nothing in the library is by. Every artist in "
                + "this catalogue is otherwise a byproduct of a file, so there is no row to "
                + "toggle.\n\n"
                + "`artist` is a MusicBrainz artist id or any URL containing one, which is "
                + "detected before anything else happens. There is no artist search and this is "
                + "why: the only free-text call this application makes is for releases, it needs "
                + "a Solr index, and it fails against a self-hosted mirror — see ADR 0006.\n\n"
                + "An artist already in the catalogue is followed without a lookup. A new one is "
                + "minted from the MusicBrainz lookup with their name, sort name and type, and is "
                + "left on the enrichment pass's worklist so that pass can fill in the genres and "
                + "band relations this lookup does not carry.")
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPost("/release-groups/{id:guid}/monitor", SetReleaseGroupMonitored)
            .WithName("SetReleaseGroupMonitored")
            .WithSummary("Mark a record as one you want, or stop wanting it.")
            .WithDescription(
                "The second fact in this catalogue that is not derived from anything — "
                + "`Artist.Followed` is the other — and, like it, a rescan must never touch it.\n\n"
                + "It is a filter and not an instruction. Nothing searches for a monitored "
                + "record and nothing buys one: acquisition is still a person who searched, read "
                + "a track list and pressed a button. What it decides is which records the "
                + "acquire screen's shelf will show, because a followed artist's whole "
                + "discography is not a list anybody reads once a few dozen artists are "
                + "followed.\n\n"
                + "Everything a first discography browse writes is unmonitored: that browse is "
                + "the baseline, so marking is opt-in. Records that appear on a *later* browse "
                + "are monitored automatically, which is what \"released after you followed "
                + "them\" means here.")
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPost("/discovered/{id:guid}/monitor", SetDiscoveredRecordMonitored)
            .WithName("SetDiscoveredRecordMonitored")
            .WithSummary("Mark a record a shop named as one you want, or stop wanting it.")
            .WithDescription(
                "`SetReleaseGroupMonitored` for a record only a shop has heard of. Two "
                + "endpoints rather than one because they address different things: a release "
                + "group is an album the catalogue knows, a discovered record is a question a "
                + "shop raised, and the whole point of keeping them apart is that a question "
                + "cannot be mistaken for a catalogue entry.\n\n"
                + "The wanted flag is the one column on a discovered record a person owns. "
                + "Every later browse restates the shop's own facts — title, year, barcode, "
                + "sleeve — and leaves this alone.")
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapGet("/albums", GetAlbums)
            .WithName("GetAlbums")
            .WithSummary("Albums the library holds at least one track of.")
            .WithDescription(
                "An album is a release group; `editionId` names the pressing only where every "
                + "file is filed under the same one. Ordered by title, or by `sort=year` "
                + "(newest first, undated last), `sort=artist` (the first billed name, "
                + "uncredited last) or `sort=added` (whichever album gained a file most "
                + "recently). `query` filters on the album title, case-insensitively, anywhere "
                + "in the string. `held` against `trackCount` is what an incomplete rip looks "
                + "like, and is only given against a claimed pressing — though a CD+DVD-Video "
                + "release is legitimately half missing on an audio-only library, which is why "
                + "the medium formats are returned beside them.\n\n"
                + "`noRelease` lists the album folders answered as coming from no release, which "
                + "have no release group: unpaged, outside `total`, filtered on the folder name.");

        group.MapGet("/albums/{id:guid}", GetAlbum)
            .WithName("GetAlbum")
            .WithSummary("One album: every stored edition's tracks at once, held or not.")
            .WithDescription(
                "The claimed pressing's track list when there is one, with what the album's "
                + "other stored editions print beside it, each track flagged for whether the "
                + "library holds it — so a missing track is visible as a gap rather than as an "
                + "absence. Files held to the album whose recording no edition prints are "
                + "listed apart, as are the album folders they sit in.")
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapGet("/attribution", GetAttributionReport)
            .WithName("GetAttributionReport")
            .WithSummary("How the attributed albums compare with the folders on disk.")
            .WithDescription(
                "The folder's boundary decides which files are considered together; its name "
                + "plays no part in deciding which album they came from. This is where the two "
                + "are compared, album by album (release group), whether or not a pressing is "
                + "claimed. An album spanning folders is the disagreement worth a person's "
                + "attention — the same album ripped twice, or one of the two filed wrongly — and "
                + "either side may be the wrong one.");

        group.MapGet("/matching", GetOpenQuestions)
            .WithName("GetOpenQuestions")
            .WithSummary("What the passes refused to decide, as questions for a person.")
            .WithDescription(
                "One entry per open question: a file no pass could identify, or a set of files no "
                + "release explained. Refusals that are nobody's decision are left out — a lookup "
                + "that did not answer is transient and stays on the worklist, a file the pass has "
                + "not reached is a queue position, and a file the decoder could not read is a "
                + "question about the file rather than about the music. No candidate answers come "
                + "back here, because nothing records them — they are recovered on demand, per "
                + "question: `matching/recordings/{id}/candidates` re-asks AcoustID from the "
                + "file's stored fingerprint, and `matching/components/{stamp}/candidates` asks "
                + "MusicBrainz again for a whole refused set.");

        group.MapGet("/matching/files/{id:guid}", GetSubjectFile)
            .WithName("GetMatchingFile")
            .WithSummary("Everything one file can be told to say about itself.")
            .WithDescription(
                "What a person needs in front of them while deciding which recording a file "
                + "holds, and the half the worklist could not carry. Half of it is catalogue — "
                + "path, size, the length `fpcalc` measured, what each pass concluded — and half "
                + "of it is read from the bytes on demand: codec, bitrate, sample rate, bit "
                + "depth, channels, and whatever the file's own tags still claim.\n\n"
                + "**Separate from the candidate set on purpose.** This costs one header read "
                + "and no provider request at all, so it answers for every file on the worklist "
                + "— including the ones whose candidates cannot be recovered, which are most of "
                + "them. The measured properties are written back to the catalogue as a side "
                + "effect, since nothing else populates them yet.\n\n"
                + "Nothing here fails the request. An unmounted volume, a moved file or a "
                + "decoder that refuses the container all come back as an absent `audio` block "
                + "and a `note` saying which, because a file nothing could read is itself worth "
                + "knowing about on this screen.")
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapGet("/matching/files/{id:guid}/art", GetSubjectFileArtwork)
            .WithName("GetMatchingFileArtwork")
            .WithSummary("The cover art embedded in one file, as it is stored.")
            .WithDescription(
                "The bytes of the file's own front cover, served unchanged — no scaling, no "
                + "re-encoding. It is the one claim about an album that a person can check "
                + "against a candidate at a glance, and unlike the folder name it travels "
                + "inside the file.\n\n"
                + "**404 is the ordinary answer, not an error.** A file with no picture, a file "
                + "no tag parser will open and an unmounted volume all come back the same way, "
                + "because the screen draws the same monogram for all three.")
            .Produces(
                StatusCodes.Status200OK,
                contentType: "image/jpeg",
                additionalContentTypes: "image/png")
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapGet("/matching/recordings/{id:guid}/candidates", GetRecordingCandidates)
            .WithName("GetRecordingCandidates")
            .WithSummary("The recordings one file could be, re-asked from its stored fingerprint.")
            .WithDescription(
                "The candidate set identification weighed and did not record. It is recovered "
                + "rather than read: the fingerprint is in the catalogue, so AcoustID can be asked "
                + "the same question again for one request and no disk access at all.\n\n"
                + "**Answered from the catalogue where it can be.** The identification and "
                + "enrichment passes store AcoustID's whole answer as they go, and the assembled "
                + "candidate document is stored the first time anybody asks for it; both are "
                + "believed for a week. So the ordinary case is a single row read, and the "
                + "expensive case — one AcoustID turn plus a MusicBrainz recording lookup per "
                + "candidate, measured at 10.3 seconds each cold — is paid once per file rather "
                + "than once per click. `refresh=true` skips the stored answer and asks again; "
                + "`asOfUtc` and `fromCache` say which of the two happened.")
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        group.MapPost("/matching/recordings/{id:guid}/decision", DecideRecording)
            .WithName("DecideRecordingMatch")
            .WithSummary("A person's answer to one file's identification question.")
            .WithDescription(
                "The commit half of `matching/recordings/{id}/candidates`. `answer` is either "
                + "`recording`, with the MBID chosen, or `none`, which is the person saying the "
                + "audio is none of the recordings AcoustID named — a stronger claim than the "
                + "rule was in a position to make, and the only one that closes the question "
                + "without an identity. Either way the file is marked as decided by a person, "
                + "which takes it out of the reach of the identification and enrichment "
                + "worklists: re-asking a library after a rule change must not silently undo an "
                + "answer somebody gave.\n\n"
                + "Choosing a recording links it, writes its artists, works and credits into the "
                + "catalogue exactly as the enrichment pass would, re-asks AcoustID for the "
                + "cluster that names it, and writes that cluster into the file's tags — subject "
                + "to `Fonoteca:AllowFileMutation`, whose refusal comes back as `tag: Refused` "
                + "rather than as an error. The file then joins the attribution worklist by the "
                + "same rule every enriched file does.")
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        group.MapGet("/matching/components/{stamp}/candidates", GetComponentCandidates)
            .WithName("GetComponentCandidates")
            .WithSummary("The albums one refused component could be, gathered again on demand.")
            .WithDescription(
                "The album-shaped half of the worklist, which used to have no answer at all. "
                + "`stamp` is the component's identity — the `UtcTicks` of the "
                + "`ReleaseLookupUtc` every file in it shares, which is the number in the "
                + "worklist item's `release:` id.\n\n"
                + "**Gathered live, and nothing caches it.** No candidate set is recorded "
                + "anywhere, so this browses MusicBrainz for each of the component's distinct "
                + "recordings, ranks the releases those browses name by how much of the set each "
                + "holds, and fetches the track lists of the best few to score them with the same "
                + "`ReleaseFit` the pass uses. Expect seconds rather than milliseconds: it is "
                + "one gated request per recording plus one per release offered. `browsed` and "
                + "`recordings` say whether every recording was asked about, and `total` says how "
                + "many releases were seen before the list was cut.\n\n"
                + "It takes no lease and writes nothing, so a slow gather refuses no pass.")
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        group.MapPost("/matching/components/{stamp}/decision", DecideComponent)
            .WithName("DecideComponentMatch")
            .WithSummary("A person's answer to one component's album question.")
            .WithDescription(
                "The commit half of `matching/components/{stamp}/candidates`. `answer` is either "
                + "`release`, with the MBID chosen, or `none` — the person saying these files "
                + "came from none of the albums MusicBrainz offered, which closes the question "
                + "where the pass's refusal leaves it open forever.\n\n"
                + "Choosing a release writes it, its group and its **whole** track list into the "
                + "catalogue exactly as the pass would, then links each file the release explains "
                + "to its own track. Files in the component the release does not explain are left "
                + "alone and stay on the worklist — a set that was refused as one is not "
                + "necessarily one album, and filing the remainder under an album that does not "
                + "list them would be the invention this pass exists to avoid.\n\n"
                + "Nothing on disk is touched: an album is a catalogue fact, so there is no tag "
                + "write, no `Fonoteca:AllowFileMutation` and no undo journal — only the decision "
                + "entry. Every file answered is marked as decided by a person, which takes it "
                + "out of the attribution worklist for good.")
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        // The by-hand half: search MusicBrainz for an album, then file a set of
        // files a person picked under a slot each. In its own partial because it
        // starts from a folder rather than from anything the passes wrote down —
        // see CatalogueEndpoints.AlbumMatching.cs.
        MapAlbumMatchingEndpoints(group);

        // The one thing here that talks back to a provider rather than asking
        // it. Its own partial for that reason alone — see
        // CatalogueEndpoints.Fingerprints.cs.
        MapFingerprintEndpoints(group);
        MapTaggingEndpoints(group);

        // The step before any of the above can help: the album is not in
        // MusicBrainz at all. See CatalogueEndpoints.ReleaseSeed.cs.
        MapReleaseSeedEndpoints(group);

        // One album folder at a time, for the Identify screen. See
        // CatalogueEndpoints.Identify.cs.
        MapIdentifyEndpoints(group);

        // Album covers, stored rather than hot-linked. See
        // CatalogueEndpoints.Cover.cs.
        MapCoverEndpoints(group);
        MapPortraitEndpoints(group);
        MapProfileEndpoints(group);

        return app;
    }

    internal static async Task<Ok<ArtistListResponse>> GetArtists(
        FonotecaDbContext db,
        CancellationToken cancellationToken,
        string? query = null,
        string? sort = null,
        string? scope = null,
        int skip = 0,
        int take = DefaultTake)
    {
        var wanted = Math.Clamp(take, 1, MaxTake);
        var from = Math.Max(skip, 0);
        var everyone = scope == "all";

        // The third shelf, and the only one that is not a rule about the
        // catalogue: the other two ask what the library holds, this one asks
        // what somebody said they cared about.
        var following = scope == "following";

        var matching = db.Artists.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(query))
        {
            // ILIKE, through EF.Functions, so the pg_trgm GIN index on Name can
            // serve it. Contains() would compile to the same operator only for
            // some providers, and being explicit is the difference between an
            // index scan and a sequential one at a hundred thousand rows.
            var pattern = $"%{Escape(query.Trim())}%";

            // Both names, because the list prints `LatinName ?? Name` and a
            // filter over the other one is a search box that cannot find what
            // the screen is showing: typing "Tchaikovsky" would match nothing
            // while the row reading "Pyotr Ilyich Tchaikovsky" sits in the
            // results behind it. The native name stays searchable too — somebody
            // pasting "Чайковский" out of a filename means that row.
            matching = matching.Where(a =>
                EF.Functions.ILike(a.Name, pattern, "\\")
                || (a.LatinName != null && EF.Functions.ILike(a.LatinName, pattern, "\\")));
        }

        var theirs = await RecordingsByArtistAsync(db, artist: null, cancellationToken)
            .ConfigureAwait(false);

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
            // The whole row rather than the five columns a card used to print.
            // This query loads every matching artist whatever the page size —
            // the track count is not a column, so paging cannot happen in SQL —
            // and against that, eleven scalars per row instead of five is not
            // the expensive part. What it buys is that `Describe` is one
            // function taking one entity, rather than the list and the detail
            // page each writing out fourteen fields.
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var shelf = everyone || following
            ? null
            : await AlbumArtistsAsync(db, cancellationToken).ConfigureAwait(false);

        var withTracks = named
            .Select(a => (Artist: a, Recordings: theirs.GetValueOrDefault(a.Id) ?? []))
            // A followed artist survives both cuts below, and has to survive
            // both. An artist with no recordings is exactly what following
            // somebody the library holds nothing by produces, and the album
            // shelf is a rule about held releases that such an artist can never
            // satisfy — so either filter alone makes the follow button look
            // broken: press it, and the artist you just followed is not there.
            //
            // Following is a person's explicit act and outranks both heuristics,
            // which is the whole difference between this and the other shelves.
            .Where(row => row.Recordings.Count > 0 || row.Artist.Followed)
            .Where(row => shelf is null || shelf.Contains(row.Artist.Id) || row.Artist.Followed)
            .Where(row => !following || row.Artist.Followed)
            .ToList();

        // Sorted here rather than in SQL because the list is already in memory:
        // the track count is not a column, it is the size of the recording set
        // `RecordingsByArtistAsync` built, so PostgreSQL has nothing to order by.
        //
        // `OrderByDescending` is stable in LINQ to Objects, so artists sharing a
        // count keep the sort-name order the collation above put them in — which
        // is the only reason this can drop the ICU ordering and still be
        // alphabetical within a tie.
        var ordered = sort == "tracks"
            ? withTracks.OrderByDescending(row => row.Recordings.Count).ToList()
            : withTracks;

        var page = ordered
            .Skip(from)
            .Take(wanted)
            .Select(row => Describe(row.Artist, row.Recordings.Count))
            .ToList();

        return TypedResults.Ok(new ArtistListResponse(withTracks.Count, page));
    }

    internal static async Task<Results<Ok<ArtistDetailResponse>, ProblemHttpResult>> GetArtist(
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

        // The groups this artist played in, as a set of local ids. One query for
        // the whole page: the question below is asked of every album and the
        // answer does not vary by album, so a subquery in the projection would be
        // the same rows read once per track.
        //
        // Read as bare `TargetId` guids rather than joined back to `Artists`,
        // because nothing here needs the band's name — the comparison is against
        // ids the release credits already carry, and a join would buy a string no
        // caller reads.
        var memberOf = await db.Relationships
            .AsNoTracking()
            .Where(r => r.ArtistId == artistId && r.Type == RelationshipTargets.Member)
            .Select(r => r.TargetId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        // Named, not merely counted. The screen groups a band shelf by the group,
        // and grouping on the release's printed credit splits one band across two
        // headings the moment two sleeves disagree — this library prints both
        // "The Robert Cray Band" and "Robert Cray Band". The artist row's own
        // name is the one spelling all of them share.
        //
        // A second query rather than a join: `Relationship.TargetId` is a bare
        // `Guid` and `Artist.Id` is value-converted, so EF can translate a
        // comparison against a list of typed ids and cannot translate
        // `a.Id.Value` inside one.
        var typed = memberOf.ConvertAll(id => new ArtistId(id));

        var bands = (await db.Artists
            .AsNoTracking()
            .Where(a => typed.Contains(a.Id))
            .Select(a => new { a.Id, Name = a.LatinName ?? a.Name })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false))
            .ToDictionary(a => a.Id.Value, a => a.Name);

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

                // Who plays it, where this artist is not the one playing: the
                // rest of the billing line, then the ensembles and conductors
                // linked to the recording. On a composer's page that is the
                // performance; on a musician's own track it is usually empty.
                OthersBilled = recording.Credits
                    .Where(c => c.ArtistId != artistId)
                    .OrderBy(c => c.Position)
                    .Select(c => c.CreditedAs ?? c.Artist!.LatinName ?? c.Artist!.Name)
                    .ToList(),
                Players = recording.Relationships
                    .Where(r => r.ArtistId != artistId)
                    .Select(r => new
                    {
                        r.Type,
                        Name = db.Artists
                            .Where(a => a.Id == r.ArtistId)
                            .Select(a => a.LatinName ?? a.Name)
                            .FirstOrDefault(),
                    })
                    .ToList(),
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
                        f.Quality,
                        f.Integrity,
                        f.ReleaseId,
                        f.ReleaseGroupId,
                        AlbumTitle = f.ReleaseGroup == null ? null : f.ReleaseGroup.Title,
                        AlbumYear = f.ReleaseGroup == null
                            ? null
                            : f.ReleaseGroup.FirstReleaseYear ?? (f.Release == null ? null : f.Release.ReleasedYear),
                        AlbumMbid = f.ReleaseGroup == null ? null : f.ReleaseGroup.Mbid,

                        // The billing line of the pressing the file is filed
                        // under, or the album's own where no pressing is claimed —
                        // a different claim from the recording's, and the whole
                        // point of the two fields below.
                        AlbumCredits = f.Release != null
                            ? f.Release.Credits
                                .OrderBy(credit => credit.Position)
                                .Select(credit => new
                                {
                                    credit.ArtistId,
                                    credit.CreditedAs,
                                    credit.JoinPhrase,
                                    ArtistName = credit.Artist!.LatinName ?? credit.Artist!.Name,
                                })
                                .ToList()
                            : f.ReleaseGroup == null
                                ? null
                                : f.ReleaseGroup.Credits
                                    .OrderBy(credit => credit.Position)
                                    .Select(credit => new
                                    {
                                        credit.ArtistId,
                                        credit.CreditedAs,
                                        credit.JoinPhrase,
                                        ArtistName = credit.Artist!.LatinName ?? credit.Artist!.Name,
                                    })
                                    .ToList(),
                    })
                    .ToList(),
            })
            // The work's title where there is one, the recording's where there is
            // not — one alphabetical stream either way, with a symphony's
            // movements landing consecutively under it rather than scattered by
            // whatever each movement happens to be called. That consecutiveness
            // is what the page's work grouping folds on; ordering by work *then*
            // title instead would move every unworked track to the end, which is
            // a rearrangement of a pop artist's page to serve a classical one.
            .OrderBy(row => row.WorkTitle ?? row.Title)
            .ThenBy(row => row.Title)
            .ThenBy(row => row.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        // Where no pressing is claimed, the sleeve and the billing line are the
        // display edition's — the album list's rule, so a card here shows the
        // same cover and credit as the album it links to.
        var unclaimed = tracks
            .SelectMany(row => row.Files)
            .Where(file => file.ReleaseGroupId != null && file.ReleaseId == null)
            .Select(file => file.ReleaseGroupId!.Value)
            .Distinct()
            .ToList();

        var displays = (await EditionFactsAsync(db, unclaimed, cancellationToken).ConfigureAwait(false))
            .ToDictionary(entry => entry.Key, entry => DisplayEdition(entry.Value));

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
                    .Where(file => file.ReleaseGroupId != null)
                    .OrderBy(file => file.ReleaseId == null)
                    .Select(file =>
                    {
                        var display = file.ReleaseId == null ? displays.GetValueOrDefault(file.ReleaseGroupId!.Value) : null;
                        var credits = display is { Artists.Count: > 0 }
                            ? display.Artists.Select(credit => (credit.ArtistId, Name: credit.CreditedAs ?? credit.Name, credit.JoinPhrase)).ToList()
                            : file.AlbumCredits!.Select(credit => (credit.ArtistId, Name: credit.CreditedAs ?? credit.ArtistName, credit.JoinPhrase)).ToList();

                        return new TrackAlbum(
                        file.ReleaseGroupId!.Value.Value,
                        file.AlbumMbid?.Value,
                        file.ReleaseId?.Value,
                        file.ReleaseId?.Value ?? display?.Id.Value,
                        file.AlbumTitle!,
                        file.AlbumYear,
                        CreditLine(credits.Select(credit => (credit.Name, credit.JoinPhrase))),
                        // Null where the release records no credit at all, and
                        // that is not the same answer as false. Eight of this
                        // library's 644 releases are in that state, and reading
                        // their silence as "not the album artist" would move an
                        // artist's own record off their discography on the
                        // strength of a row nobody wrote.
                        credits.Count == 0
                            ? null
                            : credits.Exists(credit => credit.ArtistId == artistId),
                        // The act on the sleeve, where it is a band this artist
                        // was in. Null both when the release names somebody else
                        // and when no credit is recorded at all: unlike `Billed`
                        // those are one answer here, because both mean "no
                        // evidence of membership" and leave the album where it
                        // already was.
                        credits
                            .Where(credit => bands.ContainsKey(credit.ArtistId.Value))
                            .Select(credit => new TrackBand(
                                credit.ArtistId.Value,
                                bands[credit.ArtistId.Value]))
                            .FirstOrDefault());
                    })
                    .FirstOrDefault(),
                Folder: FolderOf(row.Files[0].Path),
                Files: [.. row.Files.Select(file => FileRowOf(file.Path, file.SizeBytes, file.Quality, file.Integrity))],
                Performers: Performers(row.OthersBilled, row.Players.Select(player => (player.Type, player.Name)))))
            .ToList();

        // Release groups credited to this artist. `ArtistCredit.ReleaseGroupId`
        // has been a wired column with no writer since the first migration; the
        // discography browse is the only thing that fills it, so this query is
        // empty on every artist that browse has not reached — which was once
        // everybody nobody had followed, and is now everybody the pass has not
        // yet got to. The distinction matters when reading an empty page: it is
        // an unfetched answer, never a filtered one.
        var credited = await db.ArtistCredits
            .AsNoTracking()
            .Where(credit => credit.ArtistId == artistId && credit.ReleaseGroupId != null)
            .Select(credit => credit.ReleaseGroupId!.Value)
            .Distinct()
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var groups = credited.Count == 0
            ? []
            : await db.ReleaseGroups
                .AsNoTracking()
                .Where(group => credited.Contains(group.Id))
                .Select(group => new
                {
                    group.Id,
                    group.Mbid,
                    group.Title,
                    group.PrimaryType,
                    group.SecondaryTypes,
                    group.FirstReleaseYear,
                    group.Monitored,

                    // Two ways to hold a record, and both count. A file filed
                    // under one of the group's releases is the ordinary one; a
                    // file pointing straight at the group is what attribution
                    // writes when it knows the album and not the pressing.
                    // Reading only the first would report a GroupOnly album as
                    // missing while it sits on the artist's own page.
                    Held = group.Releases.Any(release => release.Files.Count != 0)
                        || db.MediaFiles.Any(file => file.ReleaseGroupId == group.Id),
                })
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

        // **What a shop stocks is not part of a discography, and is not on
        // this page.** It was, briefly, as a second shelf — and a second shelf
        // is a duplicate whether it is interleaved or folded away, because the
        // two catalogues name one record differently and nothing they share
        // settles which rows are the same. Measured here: shop barcodes match
        // the catalogue's 106 times in 94,660, a shop selling the digital
        // edition under its own UPC. Whether a record can be bought belongs to
        // the acquire shelf, which is where wanting one is expressed.
        //
        // `DiscoveredRecords` is still written and still read — by
        // `QobuzEndpoints`, and as the raw material for enriching these rows
        // with what a shop knows and MusicBrainz does not. It may enrich a row
        // here; it may not add one.

        var missing = groups
            .Where(group => !group.Held)
            .Select(group => new
            {
                group.Id,
                group.Mbid,
                group.Title,
                group.PrimaryType,
                group.FirstReleaseYear,
                group.Monitored,
                Secondary = group.SecondaryTypes is { Length: > 0 } types
                    ? types.Split(", ", StringSplitOptions.RemoveEmptyEntries)
                    : [],
            })
            // The cut is made here rather than in the browse, so changing the
            // rule costs a page load instead of a turn at the rate limit for
            // every followed artist. CLAUDE.md's standing bargain: what is
            // cached is answers, never rankings.
            .Where(group => Discography.IsGap(new ReleaseGroupFacts(
                group.Title,
                group.PrimaryType,
                group.Secondary,
                group.FirstReleaseYear)))
            .OrderByDescending(group => group.FirstReleaseYear ?? int.MaxValue)
            .ThenBy(group => group.Title)
            .Select(group => new DiscographyRow(
                group.Id.Value,
                group.Mbid?.Value,
                group.Title,
                group.PrimaryType,
                group.Secondary,
                group.FirstReleaseYear,
                group.Monitored,
                Editions: 1))
            .ToList();

        // Different performances of one work, folded. Separated by primary
        // type, so an album and the single named after it stay two rows — see
        // `Discography.Collapse` for what this can and cannot take away.
        missing =
        [
            .. Discography
                .Collapse(
                    missing,
                    row => row.Title,
                    row => row.PrimaryType,
                    row => row.FirstReleaseYear,
                    row => row.Monitored)
                .Select(edition => edition.Row with { Editions = edition.Count })
                // Sorted *after* folding, not before. The folded row carries a
                // representative's year, which is rarely the year of the row
                // that happened to sort first — left in place the shelf read
                // 2014, 2010, 2022, 2008.
                .OrderByDescending(row => row.FirstReleaseYear ?? int.MaxValue)
                .ThenBy(row => row.Title, StringComparer.CurrentCulture),
        ];

        // Both directions of the one artist-to-artist link stored: the groups
        // this artist was in, and — for a group — who was in it.
        var members = await db.Relationships
            .AsNoTracking()
            .Where(r => r.Type == RelationshipTargets.Member && r.TargetId == id && r.ArtistId != null)
            .Select(r => r.ArtistId!.Value)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var related = members.Concat(typed).Distinct().ToList();

        var people = related.Count == 0
            ? []
            : await db.Artists
                .AsNoTracking()
                .Where(a => related.Contains(a.Id))
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

        IReadOnlyList<RelatedArtist> Cards(List<ArtistId> ids) =>
        [
            .. people
                .Where(person => ids.Contains(person.Id))
                .Select(person => Describe(person, 0))
                .Select(person => new RelatedArtist(person.Id, person.Name, person.Portrait))
                .OrderBy(person => person.Name, StringComparer.CurrentCulture),
        ];

        var edits = PersonEdits.Read(artist.EditsJson);

        // Asked for rather than joined: the bytes are megabytes and the page
        // wants only which kinds have any.
        var uploaded = await db.ArtistImages
            .Where(row => row.ArtistId == artist.Id)
            .Select(row => row.Kind)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var profile = new ArtistProfile(
            artist.Mbid?.Value,
            PersonEdits.Apply(edits, "name", artist.Name) ?? artist.Name,
            PersonEdits.Apply(edits, "latinName", artist.LatinName),
            artist.PortraitLookupUtc,
            uploaded.Contains(ArtistImageKind.Portrait.Name),
            PersonEdits.Apply(edits, "banner", artist.BannerUrl),
            artist.BannerLookupUtc,
            uploaded.Contains(ArtistImageKind.Banner.Name),
            Written(edits, "biography", artist.BiographyText, artist.BiographyUrl),
            artist.BiographyLookupUtc,
            artist.DiscographyLookupUtc,
            [.. edits.Keys.Order(StringComparer.Ordinal)],
            Cards(members),
            Cards(typed));

        return TypedResults.Ok(new ArtistDetailResponse(
            Describe(artist, rows.Count),
            rows,
            new ArtistDiscography(
                groups.Count,
                groups.Count(group => group.Held),
                artist.DiscographyLookupUtc,
                missing),
            profile));
    }

    /// <summary>
    /// Who performs a recording, in one line: the billing line first, then ensembles, then conductors.
    /// </summary>
    private static string? Performers(
        IEnumerable<string> billed,
        IEnumerable<(string Type, string? Name)> players)
    {
        var names = billed
            .Concat(players
                .Where(player => player.Type is "ensemble" or "conductor" && player.Name is not null)
                .OrderBy(player => player.Type == "ensemble" ? 0 : 1)
                .ThenBy(player => player.Name, StringComparer.Ordinal)
                .Select(player => player.Name!))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        return names.Count == 0 ? null : string.Join(", ", names);
    }

    private static FileRow FileRowOf(
        string path,
        long sizeBytes,
        AudioQuality? quality,
        IntegrityState integrity) =>
        new(
            path,
            sizeBytes,
            quality is null
                ? null
                : new FileQuality(
                    quality.Codec.ToUpperInvariant(),
                    quality.IsLossless,
                    quality.BitDepth,
                    quality.SampleRateHz,
                    (int)(quality.BitrateBps / 1000)),
            integrity.ToString());

    /// <summary>Follow or unfollow an artist the catalogue already holds.</summary>
    internal static async Task<Results<Ok<ArtistFollowResponse>, ProblemHttpResult>> SetArtistFollowed(
        Guid id,
        ArtistFollowRequest request,
        FonotecaDbContext db,
        IClock clock,
        CancellationToken cancellationToken)
    {
        if (request is null)
        {
            return TypedResults.Problem(
                title: "Nothing to set",
                detail: "The request body must say whether to `follow`.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var artistId = new ArtistId(id);

        var artist = await db.Artists
            .FirstOrDefaultAsync(a => a.Id == artistId, cancellationToken)
            .ConfigureAwait(false);

        if (artist is null)
        {
            return TypedResults.Problem(
                title: "No such artist",
                detail: $"The catalogue has no artist with id {id}.",
                statusCode: StatusCodes.Status404NotFound);
        }

        artist.Followed = request.Follow;

        // The line "new release" is measured from, drawn once. `??=` rather than
        // an assignment is the whole of the unfollow story: re-following keeps
        // the original date, so the records released while they were unfollowed
        // are still new ones — which is the reading the note below already
        // describes, now written where it is actually decided.
        if (request.Follow) artist.FollowedUtc ??= StoreTime.ToStorePrecision(clock.UtcNow);

        // DiscographyLookupUtc is deliberately left alone in both directions.
        // Unfollowing keeps what was already fetched, so re-following costs no
        // provider turns; and clearing it on a follow would re-browse an artist
        // somebody had merely toggled twice.
        //
        // **It no longer decides monitoring, and that separation is the fix for
        // a measured bug.** It used to: a null stamp meant "first browse, lay
        // the baseline". But the stamp is MusicBrainz's, so the first time a
        // second source was asked about an artist MusicBrainz had already
        // answered for, that source's whole answer read as arrivals — 1,224 rows
        // across 28 followed artists, every one of them a back catalogue, marked
        // wanted in one run. `FollowedUtc` above is the line now, and it is a
        // date records are compared against rather than a record of which of our
        // own passes ran first.
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return TypedResults.Ok(new ArtistFollowResponse(artist.Id.Value, artist.Followed));
    }

    /// <summary>Mark a record as wanted, or stop wanting it.</summary>
    /// <remarks>
    /// <see cref="SetArtistFollowed"/>'s counterpart one level down, and
    /// deliberately the same shape: a named field rather than a toggle, so two
    /// open tabs cannot each believe they know the answer.
    /// </remarks>
    internal static async Task<Results<Ok<ReleaseGroupMonitorResponse>, ProblemHttpResult>>
        SetReleaseGroupMonitored(
            Guid id,
            ReleaseGroupMonitorRequest request,
            FonotecaDbContext db,
            CancellationToken cancellationToken)
    {
        if (request is null)
        {
            return TypedResults.Problem(
                title: "Nothing to set",
                detail: "The request body must say whether to `monitor`.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var groupId = new ReleaseGroupId(id);

        var group = await db.ReleaseGroups
            .FirstOrDefaultAsync(g => g.Id == groupId, cancellationToken)
            .ConfigureAwait(false);

        if (group is null)
        {
            return TypedResults.Problem(
                title: "No such record",
                detail: $"The catalogue has no release group with id {id}.",
                statusCode: StatusCodes.Status404NotFound);
        }

        group.Monitored = request.Monitor;

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return TypedResults.Ok(new ReleaseGroupMonitorResponse(group.Id.Value, group.Monitored));
    }

    /// <summary>Mark a record only a shop knows about as wanted, or stop wanting it.</summary>
    /// <remarks>
    /// <see cref="SetReleaseGroupMonitored"/>'s twin, kept separate because the
    /// two address different kinds of thing — see the endpoint's description.
    /// </remarks>
    internal static async Task<Results<Ok<ReleaseGroupMonitorResponse>, ProblemHttpResult>>
        SetDiscoveredRecordMonitored(
            Guid id,
            ReleaseGroupMonitorRequest request,
            FonotecaDbContext db,
            CancellationToken cancellationToken)
    {
        if (request is null)
        {
            return TypedResults.Problem(
                title: "Nothing to set",
                detail: "The request body must say whether to `monitor`.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var recordId = new DiscoveredRecordId(id);

        var record = await db.DiscoveredRecords
            .FirstOrDefaultAsync(r => r.Id == recordId, cancellationToken)
            .ConfigureAwait(false);

        if (record is null)
        {
            return TypedResults.Problem(
                title: "No such record",
                detail: $"No shop has named a record with id {id}.",
                statusCode: StatusCodes.Status404NotFound);
        }

        record.Monitored = request.Monitor;

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return TypedResults.Ok(new ReleaseGroupMonitorResponse(record.Id.Value, record.Monitored));
    }

    /// <summary>Follow an artist by MusicBrainz id, minting them if need be.</summary>
    /// <remarks>
    /// <b>The only way to reach an artist the library holds nothing by.</b>
    /// Every artist in this catalogue is otherwise a byproduct of a file, so
    /// there is no row to toggle and nothing to search — <c>SearchReleasesAsync</c>
    /// is the one free-text call here and it is about releases, needs a Solr
    /// index, and fails against a mirror. A pasted MBID or MusicBrainz URL is a
    /// lookup, works everywhere, and is the strongest answer somebody can give.
    /// </remarks>
    internal static async Task<Results<Ok<ArtistFollowResponse>, ProblemHttpResult>> FollowArtistByMbid(
        ArtistFollowByMbidRequest request,
        FonotecaDbContext db,
        IMusicBrainzCatalogue musicBrainz,
        IClock clock,
        CancellationToken cancellationToken)
    {
        if (request is null
            || string.IsNullOrWhiteSpace(request.Artist)
            || MbidPattern.Match(request.Artist) is not { Success: true } found)
        {
            return TypedResults.Problem(
                title: "Not a MusicBrainz artist",
                detail:
                    "`artist` must be a MusicBrainz artist id, or a URL containing one — "
                    + "https://musicbrainz.org/artist/<id>. There is no artist search: the only "
                    + "free-text call this application makes is for releases, and it needs a "
                    + "search index a mirror does not have.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var mbid = new Mbid(Guid.Parse(found.Value));

        var existing = await db.Artists
            .FirstOrDefaultAsync(a => a.Mbid == mbid, cancellationToken)
            .ConfigureAwait(false);

        if (existing is not null)
        {
            // Already in the catalogue as a byproduct of some file. Following is
            // then a flag on a row that already exists, and no lookup is spent.
            existing.Followed = true;
            existing.FollowedUtc ??= StoreTime.ToStorePrecision(clock.UtcNow);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

            return TypedResults.Ok(new ArtistFollowResponse(existing.Id.Value, true));
        }

        var described = await musicBrainz.GetArtistAsync(mbid, cancellationToken).ConfigureAwait(false);

        if (described is null)
        {
            return TypedResults.Problem(
                title: "No such artist",
                detail:
                    $"MusicBrainz has no artist {mbid.Value}. The id was read out of what you "
                    + "pasted without checking what kind of thing it names, so the commonest "
                    + "cause is a release or recording URL rather than an artist one — check the "
                    + "address says /artist/. Otherwise the artist has been merged away.",
                statusCode: StatusCodes.Status404NotFound);
        }

        // Minted with the little this lookup is asked for, and LookupUtc left
        // null on purpose: the enrichment pass's artist stage asks for genres and
        // band relations that this call does not include, so stamping it here
        // would save one turn and lose those permanently. The row is on that
        // stage's worklist from the moment it is saved.
        var artist = new Artist
        {
            Id = ArtistId.New(),
            Name = described.Name,
            SortName = described.SortName,
            Type = described.Type,
            Disambiguation = described.Disambiguation,
            Mbid = mbid,
            Followed = true,
            FollowedUtc = StoreTime.ToStorePrecision(clock.UtcNow),
        };

        db.Artists.Add(artist);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return TypedResults.Ok(new ArtistFollowResponse(artist.Id.Value, true));
    }

    internal static async Task<Ok<AlbumListResponse>> GetAlbums(
        FonotecaDbContext db,
        CancellationToken cancellationToken,
        string? query = null,
        string? sort = null,
        int skip = 0,
        int take = DefaultTake)
    {
        take = Math.Clamp(take, 1, MaxTake);
        skip = Math.Max(skip, 0);

        // An album is a release group some file is held to. Every filed file
        // carries its group whether or not an edition of it is claimed, so this
        // also lists the albums an edition-keyed list could not: those whose
        // files are known to be the album without it being known which pressing.
        var albums = db.ReleaseGroups.AsNoTracking().Where(g => g.Files.Any());

        if (!string.IsNullOrWhiteSpace(query))
        {
            var pattern = $"%{Escape(query.Trim())}%";
            albums = albums.Where(g => EF.Functions.ILike(g.Title, pattern, "\\"));
        }

        var total = await albums.CountAsync(cancellationToken).ConfigureAwait(false);

        // In SQL, because this endpoint pages in SQL: sorting the returned page
        // would order 200 of 600 albums and call it a sort.
        //
        // The artist key is the first billed credit on the display edition —
        // the same rungs DisplayEdition applies in memory below, written again
        // here because EF reads a helper taking the row as a closure. Undated
        // albums go last on newest-first via the coalesce: an absent year is not
        // year zero, and it is not this year either.
        var sorted = sort switch
        {
            "year" => albums
                .OrderByDescending(g => g.FirstReleaseYear
                    ?? g.Releases.Where(r => r.Status == "Official").Min(r => r.ReleasedYear)
                    ?? g.Releases.Min(r => r.ReleasedYear)
                    ?? 0)
                .ThenBy(g => g.Title),
            "artist" => albums
                .OrderBy(g => g.Releases
                    .OrderByDescending(r => r.Files.Count != 0)
                    .ThenByDescending(r => db.ReleaseCovers.Any(c => c.ReleaseId == r.Id && c.Bytes != null))
                    .ThenByDescending(r => r.Status == "Official")
                    .ThenBy(r => r.ReleasedYear ?? int.MaxValue)
                    .ThenBy(r => r.Id)
                    .Select(r => r.Credits
                        .OrderBy(credit => credit.Position)
                        .Select(credit => credit.CreditedAs ?? credit.Artist!.LatinName ?? credit.Artist!.Name)
                        .FirstOrDefault())
                    .FirstOrDefault()
                    ?? g.Credits
                        .OrderBy(credit => credit.Position)
                        .Select(credit => credit.CreditedAs ?? credit.Artist!.LatinName ?? credit.Artist!.Name)
                        .FirstOrDefault())
                .ThenBy(g => g.Title),
            // "Added" is not a column and does not need to be: MediaFileId is a
            // UUIDv7, so the id a row was minted with is when the scan first saw
            // the file, and PostgreSQL orders uuids bytewise over the big-endian
            // millisecond those lead with. The newest file decides, so a bonus
            // disc ripped later brings its album back to the top. Within one
            // millisecond the rest of the id is randomness, so albums inserted by
            // one scan come back shuffled, stably. Ordered rather than Max'd,
            // because PostgreSQL has no max(uuid).
            "added" => albums
                .OrderByDescending(g => g.Files
                    .OrderByDescending(file => file.Id)
                    .Select(file => file.Id)
                    .FirstOrDefault()),
            _ => albums.OrderBy(g => g.Title),
        };

        var rows = await sorted
            .ThenBy(g => g.Id)
            .Skip(skip)
            .Take(take)
            .Select(g => new AlbumFacts(
                g.Id,
                g.Mbid,
                g.Title,
                g.FirstReleaseYear,
                g.Files.Count,
                g.Files.Select(file => file.ReleaseId).Distinct().Count(),
                g.Files.Select(file => file.ReleaseId).FirstOrDefault(),
                g.Files.Where(file => file.TrackId != null).Select(file => file.TrackId).Distinct().Count(),

                // Every claim its files carry, so the weakest can be named and an
                // album with one coin-flip in it does not read as certain.
                g.Files.Select(file => file.AttributionOutcome).Distinct().ToList(),
                g.Files.Max(file => file.EditionAlternatives),
                g.Credits
                    .OrderBy(credit => credit.Position)
                    .Select(credit => new EditionCredit(
                        credit.ArtistId,
                        credit.CreditedAs,
                        credit.JoinPhrase,
                        credit.Artist!.LatinName ?? credit.Artist!.Name))
                    .ToList()))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var editions = await EditionFactsAsync(db, [.. rows.Select(row => row.Id)], cancellationToken)
            .ConfigureAwait(false);

        var items = rows
            .Select(row => AlbumSummaryOf(row, editions[row.Id]))
            .ToList();

        var noRelease = (await NoReleaseAlbumsAsync(db, cancellationToken).ConfigureAwait(false))
            .Where(album => string.IsNullOrWhiteSpace(query)
                || album.Title.Contains(query.Trim(), StringComparison.OrdinalIgnoreCase))
            .OrderBy(album => sort == "artist" ? album.Folder : album.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return TypedResults.Ok(new AlbumListResponse(total, items, noRelease));
    }

    /// <summary>
    /// The album folders somebody said are nobody's release, where nothing else
    /// makes them an album.
    /// </summary>
    /// <remarks>
    /// <b>Albums everywhere else are release groups, and these have none.</b> A
    /// folder answered "not from a release" is still one of the library's albums —
    /// a set of covers, a concert recording — and leaving it out of the count read
    /// as though the folder were missing. Counted by <see cref="AlbumFolder"/>,
    /// the unit every other album question is asked in, so a folder of six
    /// singles is one album and not six.
    ///
    /// <b>Only where nothing else claims the folder.</b> One with a file filed
    /// under an album already counts as that album, and one with a file still on
    /// the Identify queue is unanswered rather than unreleased.
    /// </remarks>
    internal static async Task<List<NoReleaseAlbum>> NoReleaseAlbumsAsync(
        FonotecaDbContext db,
        CancellationToken cancellationToken)
    {
        var unfiled = await db.MediaFiles
            .AsNoTracking()
            .Where(file => file.ReleaseGroupId == null)
            .Select(file => new
            {
                file.Path,
                Unreleased = UnreleasedIdentities.Contains(file.AcoustIdOutcome)
                    || UnreleasedRecordings.Contains(file.EnrichmentOutcome)
                    || UnreleasedAttributions.Contains(file.AttributionOutcome),
            })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var open = (await db.MediaFiles
                .AsNoTracking()
                .Where(OpenOnIdentify)
                .Select(file => file.Path)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false))
            .Select(AlbumFolder.Of)
            .ToHashSet(StringComparer.Ordinal);

        var albums = new List<NoReleaseAlbum>();

        foreach (var folder in unfiled
            .GroupBy(file => AlbumFolder.Of(file.Path), StringComparer.Ordinal)
            .Where(folder => folder.Key.Length > 0
                && !open.Contains(folder.Key)
                && folder.Any(file => file.Unreleased)))
        {
            // ponytail: one query per candidate folder, tens in a real library;
            // batch into one if a library ever holds hundreds.
            //
            // The prefix narrows, the album folder decides: a file loose under an
            // artist has the artist's folder as its album, and every album below
            // it shares the prefix.
            var prefix = folder.Key + "/";

            var filed = await db.MediaFiles
                .AsNoTracking()
                .Where(file => file.ReleaseGroupId != null && file.Path.StartsWith(prefix))
                .Select(file => file.Path)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            if (filed.Any(path => AlbumFolder.Of(path) == folder.Key)) continue;

            var artist = AlbumFolder.ParentOf(folder.First().Path);

            albums.Add(new NoReleaseAlbum(
                folder.Key,
                folder.Key[(folder.Key.LastIndexOf('/') + 1)..],
                artist.Length > 0 ? artist : null,
                folder.Count()));
        }

        return albums;
    }

    internal static async Task<Results<Ok<AlbumDetailResponse>, ProblemHttpResult>> GetAlbum(
        Guid id,
        FonotecaDbContext db,
        CancellationToken cancellationToken)
    {
        var groupId = new ReleaseGroupId(id);

        var album = await db.ReleaseGroups
            .AsNoTracking()
            .Where(g => g.Id == groupId)
            .Select(g => new
            {
                Facts = new AlbumFacts(
                    g.Id,
                    g.Mbid,
                    g.Title,
                    g.FirstReleaseYear,
                    g.Files.Count,
                    g.Files.Select(file => file.ReleaseId).Distinct().Count(),
                    g.Files.Select(file => file.ReleaseId).FirstOrDefault(),
                    g.Files.Where(file => file.TrackId != null).Select(file => file.TrackId).Distinct().Count(),
                    g.Files.Select(file => file.AttributionOutcome).Distinct().ToList(),
                    g.Files.Max(file => (int?)file.EditionAlternatives) ?? 0,
                    g.Credits
                        .OrderBy(credit => credit.Position)
                        .Select(credit => new EditionCredit(
                            credit.ArtistId,
                            credit.CreditedAs,
                            credit.JoinPhrase,
                            credit.Artist!.LatinName ?? credit.Artist!.Name))
                        .ToList()),
                g.PrimaryType,
                g.SecondaryTypes,
                g.Monitored,
                g.ReviewText,
                g.ReviewUrl,
                g.ReviewLookupUtc,
                LookedUp = g.Files.Max(file => file.ReleaseLookupUtc),
                Probed = g.Files.Max(file => file.LastVerifiedUtc),
            })
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        if (album is null)
        {
            return TypedResults.Problem(
                title: "No such album",
                detail: $"The catalogue has no album with id {id}.",
                statusCode: StatusCodes.Status404NotFound);
        }

        var editions = (await EditionFactsAsync(db, [groupId], cancellationToken).ConfigureAwait(false))[groupId];
        var summary = AlbumSummaryOf(album.Facts, editions);
        var display = DisplayEdition(editions);
        var claimed = summary.EditionId is { } claim ? new ReleaseId(claim) : (ReleaseId?)null;

        var ids = editions.Select(edition => edition.Id).ToList();

        var tracks = await db.Tracks
            .AsNoTracking()
            .Where(track => ids.Contains(track.ReleaseId))
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

                // The composition the recording performs, so the track list can
                // gather four movements under one heading. Null on everything
                // that is not classical, which is most of a library — the page
                // reads the absence and stays a flat list.
                WorkTitle = track.Recording!.Work == null ? null : track.Recording.Work.Title,

                // The recording's own billing line and typed links, which the
                // credits below are rolled up from.
                Billed = track.Recording.Credits
                    .OrderBy(credit => credit.Position)
                    .Select(credit => new
                    {
                        credit.ArtistId,
                        Name = credit.CreditedAs ?? credit.Artist!.LatinName ?? credit.Artist!.Name,
                        credit.JoinPhrase,
                    })
                    .ToList(),
                Links = track.Recording.Relationships
                    .Where(link => link.ArtistId != null)
                    .Select(link => new { ArtistId = link.ArtistId!.Value, link.Type, link.Attribute })
                    .ToList(),
                Writers = track.Recording.Work == null
                    ? null
                    : track.Recording.Work.Relationships
                        .Where(link => link.ArtistId != null)
                        .Select(link => new { ArtistId = link.ArtistId!.Value, link.Type })
                        .ToList(),
            })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var files = await db.MediaFiles
            .AsNoTracking()
            .Where(file => file.ReleaseGroupId == groupId)
            .Select(file => new
            {
                file.Path,
                file.SizeBytes,
                file.Quality,
                file.Integrity,
                file.RecordingId,
                file.ReleaseId,
                Recording = file.Recording == null ? null : file.Recording.Title,
                Disc = file.Track == null ? (int?)null : file.Track.DiscNumber,
                Position = file.Track == null ? (int?)null : file.Track.Position,
                file.FolderPosition,
                file.OrderOutcome,
                file.TagDiscNumber,
                file.TagTrackNumber,
            })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var byEdition = tracks.ToLookup(track => track.ReleaseId);
        var slotsOf = ids.ToDictionary(
            edition => edition,
            edition => new EditionTracks(
                edition,
                [.. byEdition[edition].Select(track => new EditionSlot(
                    track.DiscNumber,
                    track.Position,
                    track.Number,
                    track.Title,
                    track.Length,
                    track.RecordingId))]));
        var holding = files.Where(file => file.RecordingId is not null).Select(file => file.RecordingId!.Value).ToHashSet();

        // The claimed edition sets the running order; the rest add what it does
        // not print. Without a claim, an edition files are filed under, then the
        // one the files are nearest to — the shortest holding what they hold, so
        // a deluxe's bonus tracks follow the album and a vinyl-and-CD box does
        // not set the order for a download, official where there is one — then
        // official, longest first.
        var official = editions.Where(edition => edition.Status == "Official").Select(edition => slotsOf[edition.Id]).ToList();
        var nearest = Editions.Nearest(official.Count > 0 ? official : [.. slotsOf.Values], holding)?.Id;
        var order = editions
            .OrderByDescending(edition => edition.Id == claimed)
            .ThenByDescending(edition => edition.HasFiles)
            .ThenByDescending(edition => edition.Id == nearest)
            .ThenByDescending(edition => edition.Status == "Official")
            .ThenByDescending(edition => edition.TrackCount)
            .ThenBy(edition => edition.Id.Value)
            .Select(edition => edition.Id)
            .ToList();

        var combined = Editions.Combine(
            [.. order.Select(edition => slotsOf[edition])], holding, firstIsClaimed: claimed is not null);

        // What each recording says about itself, whichever edition it came from.
        var recordings = tracks
            .GroupBy(track => track.RecordingId)
            .ToDictionary(group => group.Key, group => group.First());

        // A file sits under the row printing its recording — two rips of one
        // album, in two folders, are two files under one row — except under a
        // claimed pressing, where each file sits on its own slot, a recording
        // the pressing prints twice included. A file that has lost its slot (a
        // re-read track list moved it) goes back to its recording's first row
        // rather than off the page.
        var placed = combined
            .Select(row => row.Slot.Recording)
            .ToHashSet();

        var filesOf = files
            .Where(file => file.RecordingId is { } recording && placed.Contains(recording))
            .OrderBy(file => AlbumFolder.SortKey(file.Path), StringComparer.Ordinal)
            .ThenBy(file => file.Path, StringComparer.Ordinal)
            .ToLookup(file => file.RecordingId!.Value);

        var seated = files
            .Where(file => file.Disc is not null && file.Position is not null)
            .OrderBy(file => AlbumFolder.SortKey(file.Path), StringComparer.Ordinal)
            .ThenBy(file => file.Path, StringComparer.Ordinal)
            .ToLookup(file => (Disc: file.Disc!.Value, Position: file.Position!.Value));

        var credit = CreditLine(ArtistsOf(display, album.Facts).Select(a => (a.CreditedAs ?? a.Name, a.JoinPhrase)));
        var firstRow = new HashSet<RecordingId>();

        var rows = combined
            .Select(row =>
            {
                var facts = recordings[row.Slot.Recording];
                var own = CreditLine(facts.Billed.Select(billed => (billed.Name, billed.JoinPhrase)));
                var first = firstRow.Add(row.Slot.Recording);
                var mine = claimed is { } pressing
                    ? seated[(row.Slot.Disc, row.Slot.Position)]
                        .Where(_ => row.On[0] == pressing)
                        .Concat(filesOf[row.Slot.Recording].Where(file => first && file.Position is null))
                    : filesOf[row.Slot.Recording];

                return new AlbumTrackRow(
                    row.Slot.Disc,
                    row.Slot.Position,
                    row.Slot.Number,
                    row.Slot.Title ?? string.Empty,
                    facts.WorkTitle,
                    Format(row.Slot.Length),
                    row.Slot.Recording.Value,
                    row.Held,
                    [.. mine.Select(file => FileRowOf(file.Path, file.SizeBytes, file.Quality, file.Integrity))],
                    // Only where it says something the album's line does not:
                    // a guest on one song, a compilation's own artist.
                    own is not null && own != credit ? own : null,
                    [.. row.On.Select(edition => edition.Value)]);
            })
            .ToList();

        // Files held to the album whose recording no stored edition prints — a
        // file the folder says is this album and MusicBrainz does not.
        var unplaced = files
            .Where(file => file.RecordingId is not { } recording || !placed.Contains(recording))
            .OrderBy(file => AlbumFolder.SortKey(file.Path), StringComparer.Ordinal)
            .ThenBy(file => file.Path, StringComparer.Ordinal)
            .Select(file => new AlbumFileRow(
                FileRowOf(file.Path, file.SizeBytes, file.Quality, file.Integrity),
                file.Recording,
                null,
                null,
                file.TagDiscNumber,
                file.TagTrackNumber))
            .ToList();

        // Each folder in its own order: the claimed pressing's where the folder
        // is one, otherwise the order the pass settled on, otherwise the numbers
        // its file names carry. A position is only given against a pressing the
        // folder's files are known to be; the files' own numbers ride beside it.
        var titles = editions.ToDictionary(edition => edition.Id, edition => edition.Title);
        var folders = files
            .GroupBy(file => AlbumFolder.Of(file.Path))
            .OrderBy(folder => folder.Key, StringComparer.Ordinal)
            .Select(folder =>
            {
                var pressings = folder.Select(file => file.ReleaseId).Distinct().ToList();
                var edition = pressings is [{ } only] ? only : (ReleaseId?)null;

                // One run decides a folder whole, so its files agree; a file
                // added since has not been checked and does not outvote them.
                var checkedOrder = folder
                    .Select(file => file.OrderOutcome)
                    .FirstOrDefault(outcome => outcome != FolderOrderOutcome.NotChecked);

                return new AlbumFolderRow(
                    folder.Key,
                    edition?.Value,
                    edition is { } e ? titles.GetValueOrDefault(e) : null,
                    checkedOrder.ToString(),
                    [.. folder
                        .OrderBy(file => edition is null ? 0 : file.Disc ?? int.MaxValue)
                        .ThenBy(file => edition is null ? 0 : file.Position ?? int.MaxValue)
                        .ThenBy(file => file.FolderPosition ?? int.MaxValue)
                        .ThenBy(file => AlbumFolder.SortKey(file.Path), StringComparer.Ordinal)
                        .ThenBy(file => file.Path, StringComparer.Ordinal)
                        .Select(file => new AlbumFileRow(
                            FileRowOf(file.Path, file.SizeBytes, file.Quality, file.Integrity),
                            file.Recording,
                            edition is null ? null : file.Disc,
                            edition is null ? null : file.Position,
                            file.TagDiscNumber,
                            file.TagTrackNumber))]);
            })
            .ToList();

        // Its own query rather than a column on the projection above: the
        // predicate is shared with the endpoint that acts on it, and EF cannot
        // take an Expression into a subquery inside a Select without reading it
        // as a closure over the row.
        var contributable = await db.MediaFiles
            .Where(file => file.ReleaseGroupId == groupId)
            .Where(Contributable)
            .CountAsync(cancellationToken)
            .ConfigureAwait(false);

        var albumArtists = ArtistsOf(display, album.Facts);

        // Everybody the credits name, read once, for the names the links do not
        // carry and the portraits the cards show.
        var named = albumArtists.Select(a => a.ArtistId)
            .Concat(recordings.Values.SelectMany(track => track.Billed.Select(billed => billed.ArtistId)))
            .Concat(recordings.Values.SelectMany(track => track.Links.Select(link => link.ArtistId)))
            .Concat(recordings.Values.SelectMany(track => track.Writers?.Select(link => link.ArtistId) ?? []))
            .Distinct()
            .ToList();

        var people = (await db.Artists
            .AsNoTracking()
            .Where(a => named.Contains(a.Id))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false))
            .ToDictionary(a => a.Id, a => Describe(a, 0));

        // Rolled up over the claimed pressing's tracks, or over the tracks held
        // where none is claimed — not over every edition's, which would credit a
        // guest on a deluxe's bonus disc to a rip that has no bonus disc. Every
        // row still takes its place, so a credit's track numbers are positions
        // in the list the page shows.
        var credits = AlbumCredits(
            [.. albumArtists.Select(a => a.ArtistId)],
            combined
                .Select(row => (Row: row, Track: recordings[row.Slot.Recording]))
                .Select(entry => (claimed is { } pressing ? entry.Row.On.Contains(pressing) : entry.Row.Held)
                    ? new TrackCredits(
                        [.. entry.Track.Billed.Select(billed => billed.ArtistId)],
                        [.. entry.Track.Links.Select(link => (link.ArtistId, Role(link.Type, link.Attribute)))],
                        [.. (entry.Track.Writers ?? []).Select(link => (link.ArtistId, link.Type))])
                    : new TrackCredits([], [], []))
                .ToList(),
            people);

        var coverId = display?.Id;
        var cover = coverId is not { } covered
            ? null
            : await db.ReleaseCovers
                .AsNoTracking()
                .Where(row => row.ReleaseId == covered)
                .Select(row => new
                {
                    Held = row.Bytes != null,
                    row.ArchiveImageId,
                    row.QobuzAlbumId,
                    row.SavedUtc,
                })
                .FirstOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false);

        // Other albums the library holds under the first name on this one's
        // billing line — the "more by" shelf.
        var lead = albumArtists.Select(a => (ArtistId?)a.ArtistId).FirstOrDefault();

        var others = lead is not { } leadId
            ? []
            : await db.ReleaseGroups
                .AsNoTracking()
                .Where(g => g.Id != groupId
                    && g.Files.Any()
                    && (g.Credits.Any(c => c.ArtistId == leadId)
                        || g.Releases.Any(r => r.Files.Count != 0 && r.Credits.Any(c => c.ArtistId == leadId))))
                .OrderBy(g => g.FirstReleaseYear ?? int.MaxValue)
                .ThenBy(g => g.Title)
                .Take(12)
                .Select(g => new { g.Id, g.Mbid, g.Title, g.FirstReleaseYear })
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

        var otherEditions = await EditionFactsAsync(db, [.. others.Select(other => other.Id)], cancellationToken)
            .ConfigureAwait(false);

        var moreBy = others
            .Select(other => new AlbumCard(
                other.Id.Value,
                other.Mbid?.Value,
                DisplayEdition(otherEditions[other.Id])?.Id.Value,
                other.Title,
                other.FirstReleaseYear))
            .ToList();

        // The hero is the lead artist's banner, with their own corrections laid
        // over it; no banner is a plain band, never a blown-up sleeve.
        var leadRow = lead is not { } leading
            ? null
            : await db.Artists
                .AsNoTracking()
                .Where(a => a.Id == leading)
                .Select(a => new { a.BannerUrl, a.EditsJson })
                .FirstOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false);

        // What only a pressing has — its label, its barcode, its day of release —
        // is shown for the pressing the files are proven to be and for no other.
        // A display edition's barcode on an album whose edition nobody knows
        // would be the very claim this page exists not to make.
        var pressing = claimed is not { } proven
            ? null
            : await db.Releases
                .AsNoTracking()
                .Where(r => r.Id == proven)
                .Select(r => new
                {
                    r.Mbid,
                    r.Disambiguation,
                    r.ReleasedYear,
                    r.ReleasedMonth,
                    r.ReleasedDay,
                    r.Label,
                    r.CatalogNumber,
                    r.Barcode,
                })
                .FirstOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false);

        var edits = PersonEdits.Read(display?.EditsJson);
        string? Field(string field, string? provider) => PersonEdits.Apply(edits, field, provider);

        // A person's correction to a pressing's fact is still a fact about that
        // pressing, so it is shown on the same terms: only where the files are
        // known to be it.
        string? Pressing(string field, string? provider) => pressing is null ? null : Field(field, provider);

        var about = new ReleaseAbout(
            groupId.Value,
            album.Facts.Mbid?.Value,
            lead?.Value,
            leadRow is null
                ? null
                : PersonEdits.Apply(PersonEdits.Read(leadRow.EditsJson), "banner", leadRow.BannerUrl),
            pressing?.Mbid?.Value,
            Pressing("disambiguation", pressing?.Disambiguation),
            Field("primaryType", album.PrimaryType),
            Split(Field("secondaryTypes", album.SecondaryTypes)),
            Integer(Field("firstReleaseYear", Number(album.Facts.FirstReleaseYear))),
            Integer(Pressing("releasedYear", Number(pressing?.ReleasedYear))),
            Integer(Pressing("releasedMonth", Number(pressing?.ReleasedMonth))),
            Integer(Pressing("releasedDay", Number(pressing?.ReleasedDay))),
            Pressing("label", pressing?.Label),
            Pressing("catalogNumber", pressing?.CatalogNumber),
            Pressing("barcode", pressing?.Barcode),
            album.Monitored,
            cover is not { Held: true }
                ? null
                : cover.ArchiveImageId is not null ? "archive"
                : cover.QobuzAlbumId is not null ? "qobuz"
                : "upload",
            cover?.SavedUtc,
            Written(edits, "review", album.ReviewText, album.ReviewUrl),
            album.ReviewLookupUtc,
            album.LookedUp,
            album.Probed,
            [.. edits.Keys.Order(StringComparer.Ordinal)]);

        var stored = editions.ToDictionary(edition => edition.Id);

        return TypedResults.Ok(new AlbumDetailResponse(
            summary,
            rows,
            [.. order.Select(id => stored[id]).Select(edition => new AlbumEditionRow(
                edition.Id.Value,
                edition.Title,
                edition.Year,
                edition.Country,
                edition.Formats,
                edition.Status))],
            unplaced,
            folders,
            contributable,
            about,
            credits,
            moreBy));
    }

    /// <summary>What the album list and page read of one release group and its files.</summary>
    /// <param name="Editions">
    /// Distinct <c>ReleaseId</c> values among its files, null counted as one — so
    /// one edition and no nulls is every file filed under the same pressing.
    /// </param>
    /// <param name="Held">Distinct tracks of that pressing the files sit on; read only when there is one.</param>
    private sealed record AlbumFacts(
        ReleaseGroupId Id,
        Mbid? Mbid,
        string Title,
        int? FirstReleaseYear,
        int Files,
        int Editions,
        ReleaseId? AnyEdition,
        int Held,
        List<ReleaseAttributionOutcome> Outcomes,
        int Alternatives,
        List<EditionCredit> Artists);

    /// <summary>One stored edition of an album, as the album list and page read it.</summary>
    internal sealed record EditionFacts(
        ReleaseId Id,
        ReleaseGroupId? GroupId,
        Mbid? Mbid,
        string Title,
        int? Year,
        string? Country,
        string? Status,
        string? Formats,
        int? DiscCount,
        int TrackCount,
        bool HasFiles,
        bool HasCover,
        string? EditsJson,
        List<EditionCredit> Artists);

    internal sealed record EditionCredit(ArtistId ArtistId, string? CreditedAs, string? JoinPhrase, string Name);

    /// <summary>Every stored edition of each album, by album; an album with none maps to an empty list.</summary>
    internal static async Task<Dictionary<ReleaseGroupId, List<EditionFacts>>> EditionFactsAsync(
        FonotecaDbContext db,
        List<ReleaseGroupId> groups,
        CancellationToken cancellationToken)
    {
        var keys = groups.Select(group => (ReleaseGroupId?)group).ToList();

        var editions = await db.Releases
            .AsNoTracking()
            .Where(r => keys.Contains(r.ReleaseGroupId))
            .Select(r => new EditionFacts(
                r.Id,
                r.ReleaseGroupId,
                r.Mbid,
                r.Title,
                r.ReleasedYear,
                r.Country,
                r.Status,
                r.MediumFormats,
                r.DiscCount,
                r.TrackCount ?? r.Tracks.Count,
                r.Files.Any(),
                db.ReleaseCovers.Any(c => c.ReleaseId == r.Id && c.Bytes != null),
                r.EditsJson,
                r.Credits
                    .OrderBy(credit => credit.Position)
                    .Select(credit => new EditionCredit(
                        credit.ArtistId,
                        credit.CreditedAs,
                        credit.JoinPhrase,
                        credit.Artist!.LatinName ?? credit.Artist!.Name))
                    .ToList()))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var byGroup = editions.ToLookup(edition => edition.GroupId);

        return groups.Distinct().ToDictionary(group => group, group => byGroup[group].ToList());
    }

    /// <summary>
    /// The edition an album borrows its billing line and its sleeve from.
    /// </summary>
    /// <remarks>
    /// Not a claim about the files — only <see cref="AlbumSummary.EditionId"/> is
    /// that. An album's name and picture have to come from somewhere, and the
    /// edition the files were filed under is the best source; then one whose
    /// sleeve is already stored, so a page keeps the picture a person may have
    /// chosen; then the official one; then the earliest. The artist sort in
    /// <see cref="GetAlbums"/> applies the same rungs in SQL.
    /// </remarks>
    internal static EditionFacts? DisplayEdition(IEnumerable<EditionFacts> editions) =>
        editions
            .OrderByDescending(edition => edition.HasFiles)
            .ThenByDescending(edition => edition.HasCover)
            .ThenByDescending(edition => edition.Status == "Official")
            .ThenBy(edition => edition.Year ?? int.MaxValue)
            .ThenBy(edition => edition.Id.Value)
            .FirstOrDefault();

    /// <summary>The outcomes an album's files can carry, strongest claim first.</summary>
    /// <remarks>
    /// Stated rather than read off the enum's numbers, which are storage and grow
    /// by appending: a new outcome would otherwise rank by when it was added.
    /// </remarks>
    private static readonly ReleaseAttributionOutcome[] StrongestFirst =
    [
        ReleaseAttributionOutcome.Attributed,
        ReleaseAttributionOutcome.AttributedAmbiguously,
        ReleaseAttributionOutcome.GroupOnly,
        ReleaseAttributionOutcome.AttributedByPerson,
        ReleaseAttributionOutcome.AlbumByPerson,
        ReleaseAttributionOutcome.AttributedByAgent,
        ReleaseAttributionOutcome.AlbumByAgent,
        ReleaseAttributionOutcome.OnNoEdition,
    ];

    /// <summary>The weakest claim among an album's files; an outcome not ranked counts as weaker than any that is.</summary>
    private static ReleaseAttributionOutcome Weakest(IEnumerable<ReleaseAttributionOutcome> outcomes) =>
        outcomes
            .OrderByDescending(outcome => Array.IndexOf(StrongestFirst, outcome) is var rank and >= 0
                ? rank
                : StrongestFirst.Length + (int)outcome)
            .FirstOrDefault();

    /// <summary>
    /// When an album was first released: MusicBrainz's own year for the group,
    /// else the earliest official edition stored, else the earliest of any.
    /// </summary>
    /// <remarks>The year sort in <see cref="GetAlbums"/> applies the same rungs in SQL.</remarks>
    internal static int? AlbumYear(int? firstReleaseYear, IEnumerable<EditionFacts> editions) =>
        firstReleaseYear
        ?? editions.Where(edition => edition.Status == "Official").Min(edition => edition.Year)
        ?? editions.Min(edition => edition.Year);

    /// <summary>The billing line: the display edition's, or the album's own where no edition is stored.</summary>
    private static List<EditionCredit> ArtistsOf(EditionFacts? display, AlbumFacts album) =>
        display is { Artists.Count: > 0 } ? display.Artists : album.Artists;

    private static AlbumSummary AlbumSummaryOf(AlbumFacts album, List<EditionFacts> editions)
    {
        var display = DisplayEdition(editions);

        // Every file under one pressing, and no file under none.
        var claimed = album.Editions == 1 && album.AnyEdition is { } only
            ? editions.FirstOrDefault(edition => edition.Id == only)
            : null;

        var summary = new AlbumSummary(
            album.Id.Value,
            album.Mbid?.Value,
            album.Title,
            CreditLine(ArtistsOf(display, album).Select(a => (a.CreditedAs ?? a.Name, a.JoinPhrase))),
            AlbumYear(album.FirstReleaseYear, editions),
            claimed?.Id.Value,
            display?.Id.Value,
            claimed?.Country,
            claimed?.Status,
            claimed?.Formats,
            claimed?.DiscCount,
            claimed?.TrackCount,
            claimed is null ? null : album.Held,
            album.Files,
            Weakest(album.Outcomes).ToString(),
            album.Alternatives);

        return WithEdits(summary, display?.EditsJson);
    }

    /// <summary>One track's credits, by artist, for <see cref="AlbumCredits"/>.</summary>
    private sealed record TrackCredits(
        IReadOnlyList<ArtistId> Billed,
        IReadOnlyList<(ArtistId Artist, string Role)> Links,
        IReadOnlyList<(ArtistId Artist, string Role)> Writers);

    /// <summary>A relationship as a person would name the role.</summary>
    /// <remarks>An ensemble's attribute is its artist type, so "Orchestra" reads as "orchestra".</remarks>
    private static string Role(string type, string? attribute) =>
        type == "ensemble" && attribute is { Length: > 0 }
            ? attribute.ToLowerInvariant()
            : type;

    /// <summary>
    /// The album's credits, rolled up from its tracks, one card per artist and group.
    /// </summary>
    /// <remarks>
    /// <b>Only what the catalogue stores</b>: the release's billing line, each
    /// recording's billing line, the conductors and ensembles linked to a
    /// recording, and the writers linked to its work. No instruments and no
    /// production roles — MusicBrainz has them, and nothing here asks for them —
    /// so there is never a Production group.
    ///
    /// The billed artists are Main and appear nowhere else, with whatever roles
    /// the tracks give them; everyone else lands in Performers or Composition
    /// with the running-order places they are credited on, or null where that
    /// is every track.
    /// </remarks>
    private static List<AlbumCredit> AlbumCredits(
        List<ArtistId> main,
        List<TrackCredits> tracks,
        Dictionary<ArtistId, ArtistSummary> people)
    {
        var roles = new Dictionary<(string Group, ArtistId Artist), (SortedSet<string> Roles, SortedSet<int> Places)>();

        void Add(string group, ArtistId artist, string role, int place)
        {
            if (main.Contains(artist)) group = "Main";

            if (!roles.TryGetValue((group, artist), out var entry))
            {
                roles[(group, artist)] = entry = (new SortedSet<string>(StringComparer.Ordinal), []);
            }

            entry.Roles.Add(role);
            entry.Places.Add(place);
        }

        for (var index = 0; index < tracks.Count; index++)
        {
            var place = index + 1;
            var track = tracks[index];

            foreach (var artist in track.Billed) Add("Performers", artist, "credited", place);
            foreach (var (artist, role) in track.Links) Add("Performers", artist, role, place);
            foreach (var (artist, role) in track.Writers) Add("Composition", artist, role, place);
        }

        var credits = new List<AlbumCredit>();

        foreach (var artist in main.Distinct())
        {
            if (!people.TryGetValue(artist, out var person)) continue;

            var theirs = roles.GetValueOrDefault(("Main", artist)).Roles?
                .Where(role => role != "credited")
                .ToList() ?? [];

            credits.Add(new AlbumCredit(
                artist.Value,
                person.Name,
                theirs.Count == 0 ? "album artist" : string.Join(", ", theirs),
                "Main",
                null,
                person.Portrait));
        }

        foreach (var group in new[] { "Performers", "Composition" })
        {
            credits.AddRange(roles
                .Where(entry => entry.Key.Group == group && people.ContainsKey(entry.Key.Artist))
                .Select(entry => new AlbumCredit(
                    entry.Key.Artist.Value,
                    people[entry.Key.Artist].Name,
                    string.Join(", ", entry.Value.Roles),
                    group,
                    entry.Value.Places.Count == tracks.Count ? null : [.. entry.Value.Places],
                    people[entry.Key.Artist].Portrait))
                .OrderBy(credit => credit.Tracks is null ? 0 : 1)
                .ThenBy(credit => credit.Name, StringComparer.CurrentCulture));
        }

        return credits;
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

        // Placed on an album, whether or not a pressing of it is claimed.
        var placed = await db.MediaFiles
            .AsNoTracking()
            .Where(file => file.ReleaseGroupId != null)
            .Select(file => new
            {
                file.Path,
                AlbumId = file.ReleaseGroupId!.Value,
                AlbumTitle = file.ReleaseGroup!.Title,
            })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var byFolder = placed
            .GroupBy(file => FolderOf(file.Path))
            .Select(folder => new
            {
                Folder = folder.Key,
                Albums = folder
                    .GroupBy(file => (file.AlbumId, file.AlbumTitle))
                    .Select(album => new AttributionShare(
                        album.Key.AlbumId.Value,
                        album.Key.AlbumTitle,
                        album.Count()))
                    .OrderByDescending(share => share.Files)
                    .ThenBy(share => share.Title, StringComparer.Ordinal)
                    .ToList(),
            })
            .ToList();

        var split = byFolder
            .Where(folder => folder.Albums.Count > 1)
            .OrderByDescending(folder => folder.Albums.Count)
            .ThenBy(folder => folder.Folder, StringComparer.Ordinal)
            .Select(folder => new FolderDisagreement(folder.Folder, folder.Albums))
            .ToList();

        var spanning = placed
            .GroupBy(file => (file.AlbumId, file.AlbumTitle))
            .Select(album => new
            {
                album.Key,
                Folders = album
                    .GroupBy(file => FolderOf(file.Path))
                    .Select(folder => new AttributionShare(
                        album.Key.AlbumId.Value,
                        folder.Key,
                        folder.Count()))
                    .OrderByDescending(share => share.Files)
                    .ThenBy(share => share.Title, StringComparer.Ordinal)
                    .ToList(),
            })
            .Where(album => album.Folders.Count > 1)
            .OrderByDescending(album => album.Folders.Count)
            .Select(album => new AlbumDisagreement(
                album.Key.AlbumId.Value,
                album.Key.AlbumTitle,
                album.Folders))
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
            AlbumsSpanningFolders: spanning,
            Incomplete: incomplete));
    }

    /// <summary>
    /// Everything the passes declined to decide, as a worklist.
    /// </summary>
    /// <remarks>
    /// <b>The unit differs by kind, and that is the whole shape of this
    /// endpoint.</b> Identification and enrichment refuse one file at a time, so
    /// each refused file is one question. Attribution refuses a <i>component</i> —
    /// a set of files that shared candidate releases and were decided together —
    /// because a single file cannot name its release, and answering for one file
    /// of a rip while leaving its nine siblings behind is not an answer.
    ///
    /// <b>Components are recovered from <c>ReleaseLookupUtc</c>, which is not a
    /// coincidence.</b> The attribution pass reads the clock once per component
    /// and stamps that one value on every file it decides, so the timestamp is
    /// the component's identity written down. It is already how the same question
    /// gets asked of the live database by hand; this endpoint asks it in the
    /// application instead.
    ///
    /// <b>The reason is the first pass that refused, not the last.</b> A file
    /// AcoustID could not place has <c>EnrichmentOutcome.NotAttempted</c> after
    /// it, and reporting the later silence would describe a consequence rather
    /// than a cause. Within one component the outcomes may differ file by file;
    /// the lowest wins, so a component where anything at all had releases to
    /// compare against reads as "no release fitted" rather than "nothing to
    /// compare against".
    ///
    /// <b>No candidates come back.</b> <c>ReleaseFit</c> is computed inside the
    /// attribution pass and discarded once it has ranked, and the AcoustID
    /// clusters behind an ambiguous file are not stored either — so this endpoint
    /// can say what is open and how much hangs on it, and nothing else. Both are
    /// recovered on demand instead, by the question's own endpoint: from the
    /// stored fingerprint for a file, and from a fresh MusicBrainz gather for a
    /// component.
    /// </remarks>
    internal static async Task<Ok<MatchingQueueResponse>> GetOpenQuestions(
        FonotecaDbContext db,
        CancellationToken cancellationToken,
        int skip = 0,
        int take = DefaultQuestions)
    {
        var wanted = Math.Clamp(take, 1, MaxQuestions);
        var from = Math.Max(skip, 0);

        // One component per distinct stamp. Grouped on the timestamp alone
        // rather than on (timestamp, outcome): a component routinely holds both
        // refusals at once — some of its files had releases to compare against
        // and some had none — and splitting it in two would invent a second
        // question about the same rip.
        var components = await db.MediaFiles
            .AsNoTracking()
            .Where(file => file.ReleaseLookupUtc != null
                && UnattributedOutcomes.Contains(file.AttributionOutcome))
            .GroupBy(file => file.ReleaseLookupUtc!.Value)
            .Select(group => new
            {
                DecidedUtc = group.Key,
                Files = group.Count(),
                Outcome = group.Min(file => (int)file.AttributionOutcome),
            })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        // Biggest first: the number of files hanging on an answer is the only
        // ranking the catalogue can offer, and it is the right one — a ten-file
        // rip that landed nowhere is worth a person's attention before a single
        // stray track is.
        var ordered = components
            .OrderByDescending(component => component.Files)
            .ThenBy(component => component.DecidedUtc)
            .ToList();

        var loose = db.MediaFiles
            .AsNoTracking()
            .Where(file => UnidentifiedOutcomes.Contains(file.AcoustIdOutcome)
                || UnlinkedOutcomes.Contains(file.EnrichmentOutcome));

        // Counted by reason rather than merely counted, because the reason is
        // what a worklist of this length has to be read by. Four hundred rows
        // saying "known audio, no recording" are one fact and one decision, and
        // a client that only knew the total would have to fetch all of them to
        // find that out.
        var looseReasons = await loose
            .GroupBy(file => new { file.AcoustIdOutcome, file.EnrichmentOutcome })
            .Select(group => new
            {
                group.Key.AcoustIdOutcome,
                group.Key.EnrichmentOutcome,
                Files = group.Count(),
            })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var looseTotal = looseReasons.Sum(reason => reason.Files);

        // Components come first as a block, so the paging arithmetic is one
        // subtraction rather than a merge: a page either lands wholly in the
        // components, wholly in the files, or straddles the one boundary.
        var componentPage = ordered.Skip(from).Take(wanted).ToList();
        var fileSkip = Math.Max(0, from - ordered.Count);
        var fileTake = wanted - componentPage.Count;

        var stamps = componentPage.Select(component => component.DecidedUtc).ToList();

        var componentFiles = stamps.Count == 0
            ? []
            : await db.MediaFiles
                .AsNoTracking()
                .Where(file => file.ReleaseLookupUtc != null
                    && stamps.Contains(file.ReleaseLookupUtc!.Value)
                    && UnattributedOutcomes.Contains(file.AttributionOutcome))
                .OrderBy(file => file.Path)
                .Select(file => new
                {
                    DecidedUtc = file.ReleaseLookupUtc!.Value,
                    file.Path,

                    // Present by construction — attribution only ever asks about
                    // files enrichment has already linked — but left nullable so
                    // a half-migrated row renders as its filename instead of
                    // throwing.
                    Title = file.Recording!.Title,
                })
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

        var byComponent = componentFiles
            .GroupBy(file => file.DecidedUtc)
            .ToDictionary(group => group.Key, group => group.ToList());

        var files = fileTake <= 0
            ? []
            : await loose
                .OrderBy(file => file.Path)
                .Skip(fileSkip)
                .Take(fileTake)
                .Select(file => new
                {
                    file.Id,
                    file.Path,
                    file.AcoustIdOutcome,
                    file.EnrichmentOutcome,
                    file.SizeBytes,
                    file.FingerprintDuration,

                    // The other length, and on this worklist it is not the rare
                    // one: a third of the target library's file-level questions
                    // have no fingerprint duration at all, because their
                    // AcoustID was adopted from a tag the file already carried
                    // and `fpcalc` never ran. Without this those rows print a
                    // size and a container and no length forever, which is the
                    // half of the request this list exists to answer.
                    Measured = file.Quality!.Duration,
                })
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

        var items = new List<OpenQuestion>(componentPage.Count + files.Count);

        foreach (var component in componentPage)
        {
            var rows = byComponent.GetValueOrDefault(component.DecidedUtc) ?? [];

            var titles = rows
                .Select(row => row.Title ?? NameOf(row.Path))
                .Distinct(StringComparer.Ordinal)
                .ToList();

            items.Add(new OpenQuestion(
                $"release:{component.DecidedUtc.UtcTicks}",
                "release",
                ((ReleaseAttributionOutcome)component.Outcome).ToString(),
                Subject(titles),
                [.. rows.Select(row => FolderOf(row.Path)).Distinct(StringComparer.Ordinal)],
                component.Files,

                // A set of files has a total runtime and a total size, and
                // neither is a fact about the music. See the DTO's remarks.
                null,
                null,
                null));
        }

        foreach (var file in files)
        {
            items.Add(new OpenQuestion(
                $"recording:{file.Id.Value}",
                "recording",
                UnidentifiedOutcomes.Contains(file.AcoustIdOutcome)
                    ? file.AcoustIdOutcome.ToString()
                    : file.EnrichmentOutcome.ToString(),
                NameOf(file.Path),
                [FolderOf(file.Path)],
                1,
                Format(file.FingerprintDuration ?? file.Measured),
                Bytes(file.SizeBytes),
                ExtensionOf(file.Path)));
        }

        var componentFileCount = components.Sum(component => component.Files);

        // Attribution's refusals first, then the file-level ones, each block
        // largest first — the order `Items` is in, and for the same reason. By
        // size alone the four hundred files that share one gap in AcoustID's
        // links would lead, and the album-shaped question a person can actually
        // finish would sit under all of them. A client rendering these in order
        // gets the small important block above the large repetitive one.
        var reasons = components
            .GroupBy(component => component.Outcome)
            .Select(group => new OpenQuestionCount(
                ((ReleaseAttributionOutcome)group.Key).ToString(),
                group.Count(),
                group.Sum(component => component.Files)))
            .OrderByDescending(count => count.Files)
            .ThenBy(count => count.Name, StringComparer.Ordinal)

            // The same fold the item loop does, applied to the grouped counts:
            // the first pass that refused names the question, so a file AcoustID
            // could not place is counted under that and not under the enrichment
            // silence that followed it.
            .Concat(looseReasons
                .Select(group => new
                {
                    Reason = UnidentifiedOutcomes.Contains(group.AcoustIdOutcome)
                        ? group.AcoustIdOutcome.ToString()
                        : group.EnrichmentOutcome.ToString(),
                    group.Files,
                })
                .GroupBy(group => group.Reason, StringComparer.Ordinal)
                .Select(group => new OpenQuestionCount(
                    group.Key,
                    group.Sum(entry => entry.Files),
                    group.Sum(entry => entry.Files)))
                .OrderByDescending(count => count.Files)
                .ThenBy(count => count.Name, StringComparer.Ordinal))
            .ToList();

        return TypedResults.Ok(new MatchingQueueResponse(
            ordered.Count + looseTotal,
            [
                new OpenQuestionCount("release", ordered.Count, componentFileCount),
                new OpenQuestionCount("recording", looseTotal, looseTotal),
            ],
            reasons,
            items));
    }

    /// <summary>
    /// One file, described as fully as it can be described.
    /// </summary>
    /// <remarks>
    /// <b>The half the worklist could not carry, and the reason the screen was
    /// unusable.</b> A person asked to choose between two recordings was being
    /// shown a filename and a folder — while the two facts that actually settle
    /// it, how long the audio runs and what the file's own tags still say, were
    /// either in the catalogue and not being read or on disk and never asked
    /// for. Candidate rows print a length each; without the file's own length
    /// beside them the whole column is decoration.
    ///
    /// <b>Two sources, and the split is deliberate.</b> Path, size, the length
    /// <c>fpcalc</c> measured and what each of the three passes concluded are
    /// catalogue reads and always available. Codec, bitrate, sample rate, bit
    /// depth, channels and the tag fields are read from the bytes, here, now —
    /// because nothing populates <see cref="MediaFile.Quality"/>: the probing
    /// pass does not exist, and the column has been in the schema unwritten
    /// since the first migration. What it measures is written back, so the
    /// second person to open the file — and the worklist row behind them — reads
    /// it without touching the disk.
    ///
    /// <b>Not part of the candidate set, and not behind it.</b> Candidates cost
    /// an AcoustID turn and a MusicBrainz lookup each and can only be recovered
    /// for two of the seven refusals. This costs one header read and answers for
    /// all of them — which matters, because the refusals with no candidates are
    /// the majority of the worklist and were the ones showing nothing at all.
    ///
    /// <b>It cannot fail on the file.</b> An unmounted volume, a file moved
    /// since the last scan and a container the decoder will not parse are all
    /// ordinary states here — a file nothing can read is disproportionately a
    /// file nothing could identify — so they come back as an absent
    /// <c>audio</c> block and a note. The only 404 is a media file id the
    /// catalogue has never heard of.
    ///
    /// <b>It is a GET that starts a subprocess and writes a row, and takes no
    /// lease for either.</b> Worth stating rather than leaving to be discovered.
    /// The subprocess decodes the whole file: measured on this library, a median
    /// of 0.28 seconds, 1.3s at the 25-file worst case and 2.3s on the largest
    /// file on the worklist. That is a person's click, not a pass, and there is
    /// no cap on how many of them can be in flight. The write is one row's
    /// quality columns and it does not need <c>LibraryWorkGate</c>: no pass
    /// writes them, and a scan that sees the bytes change clears them along with
    /// everything else derived — so the worst a race can do is store a
    /// measurement of audio that has just been replaced, which the scan then
    /// removes. Taking the gate would instead refuse the screen for the whole
    /// length of any running pass, which is the wrong trade for a read.
    /// </remarks>
    internal static async Task<Results<Ok<SubjectFileResponse>, ProblemHttpResult>> GetSubjectFile(
        Guid id,
        FonotecaDbContext db,
        AudioFileDescriber describer,
        CancellationToken cancellationToken)
    {
        var mediaFileId = new MediaFileId(id);

        // Tracked, because a successful reading is written back.
        var file = await db.MediaFiles
            .Include(row => row.Recording)
            .Include(row => row.Release)
            .FirstOrDefaultAsync(row => row.Id == mediaFileId, cancellationToken)
            .ConfigureAwait(false);

        if (file is null)
        {
            return TypedResults.Problem(
                title: "No such file",
                detail: $"The catalogue has no media file with id {id}.",
                statusCode: StatusCodes.Status404NotFound);
        }

        var reading = await describer
            .DescribeAsync(new LibraryPath(file.Path), cancellationToken)
            .ConfigureAwait(false);

        if (reading is { Quality: { } measured, DecodedCleanly: true })
        {
            // Two conditions, and the second is the one worth stating. Only a
            // clean decode is written down: `AudioQuality` exists to decide
            // which duplicate to keep and whether a candidate is an upgrade, and
            // a number the decoder itself objected to on the way past has no
            // business being an input to either. Measured across sixty real
            // library files, that withholds roughly one — and that one has a
            // defect. The reading is still returned; it is just not remembered.
            //
            // Only ever filled in, never blanked. A read that failed is a fact
            // about this moment — the volume was not mounted — and throwing away
            // a measurement taken when it was would make the column flicker with
            // the mount state rather than with the file.
            file.Quality = measured;
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        var quality = reading.Quality ?? file.Quality;

        return TypedResults.Ok(new SubjectFileResponse(
            id,
            file.Path,
            FolderOf(file.Path),
            NameOf(file.Path),
            ExtensionOf(file.Path),
            file.SizeBytes,
            Bytes(file.SizeBytes),
            Format(file.FingerprintDuration),
            Format(reading.Duration ?? quality?.Duration),
            quality is null ? null : new AudioQualityRow(
                quality.Codec,
                (int)(quality.BitrateBps / 1000),
                quality.SampleRateHz,
                quality.BitDepth,
                quality.Channels,
                quality.IsLossless,
                quality.Tier.ToString()),
            file.AcoustIdOutcome.ToString(),
            file.EnrichmentOutcome.ToString(),
            file.AttributionOutcome.ToString(),
            file.AcoustId?.Value,
            file.AcoustIdTaggedUtc is not null,
            file.Fingerprint is { Length: > 0 },
            file.Integrity.ToString(),
            file.LastScannedUtc,
            file.Recording?.Title,
            file.Release?.Title,
            [.. reading.Tags.Select(tag => new FileTagRow(tag.Name, tag.Value))],
            reading.Note));
    }

    /// <summary>
    /// The picture the file carries, for somebody comparing it with an album cover.
    /// </summary>
    /// <remarks>
    /// <b>Its own request, and binary, because that is what an <c>&lt;img&gt;</c>
    /// takes.</b> The alternative — a data URI on
    /// <see cref="SubjectFileResponse"/> — would put a base64 megabyte into a
    /// JSON document that the worklist reads for every file, to show a picture
    /// on the one file somebody opened. A URL is fetched only when the dialog
    /// paints, and costs the response nothing on the files nobody opens.
    ///
    /// <b>No cache headers, deliberately.</b> Answering is a tag-block read —
    /// measured at 8ms against the target library, on files up to 475 MB — so
    /// there is nothing here worth an <c>ETag</c> and a staleness rule. An
    /// <c>ETag</c> over bytes that a rescan may have replaced is the kind of
    /// cache this project has already paid to clear twice.
    ///
    /// <b>Nothing decides anything from this.</b> A cover is the weakest claim
    /// in the file — it is copied from a web search about as often as it is
    /// ripped — so it is evidence for a person's eye and never an input to a
    /// rule, in exactly the way the folder name is not.
    /// </remarks>
    private static async Task<Results<FileContentHttpResult, ProblemHttpResult>>
        GetSubjectFileArtwork(
            Guid id,
            FonotecaDbContext db,
            AudioFileDescriber describer,
            CancellationToken cancellationToken)
    {
        var mediaFileId = new MediaFileId(id);

        var path = await db.MediaFiles
            .AsNoTracking()
            .Where(row => row.Id == mediaFileId)
            .Select(row => row.Path)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        if (path is null)
        {
            return TypedResults.Problem(
                title: "No such file",
                detail: $"The catalogue has no media file with id {id}.",
                statusCode: StatusCodes.Status404NotFound);
        }

        var art = await describer
            .ReadArtworkAsync(new LibraryPath(path), cancellationToken)
            .ConfigureAwait(false);

        if (art is null)
        {
            return TypedResults.Problem(
                title: "No embedded cover art",
                detail: "This file carries no picture, or none that could be read from it.",
                statusCode: StatusCodes.Status404NotFound);
        }

        return TypedResults.File(art.Bytes, art.MimeType);
    }

    /// <summary>
    /// The recordings one file could be, asked again rather than remembered.
    /// </summary>
    /// <remarks>
    /// <b>This is a re-ask, and that is the only reason it can exist.</b> The
    /// identification pass computes a candidate set, picks from it and keeps
    /// only the verdict — so the worklist can say a file was refused and not
    /// what it was refused between. What makes the loss recoverable is that
    /// <see cref="MediaFile.Fingerprint"/> is stored: the expensive half of
    /// identification is decoding audio, and that half is already paid. Asking
    /// AcoustID the same question a second time costs one request and touches no
    /// file, which is also why re-asking a whole library after a rule change
    /// costs turns at the rate limit rather than hours of <c>fpcalc</c>.
    ///
    /// <b>It is deliberately not free, and deliberately not on the list.</b> One
    /// AcoustID request, then one MusicBrainz request per recording named — at
    /// 340ms and whatever the server earns, that is seconds for one file and an
    /// afternoon for a page of them. So this answers about a single file, when
    /// somebody asks about that file.
    ///
    /// <b>The ranking is <see cref="RecordingCandidates"/>, and it is not the
    /// rule that refused.</b> Worth stating plainly, because the two collapse in
    /// opposite directions: <c>AcoustIdSelection</c> compares <i>clusters</i>,
    /// and its own remarks record that reaching for <c>RecordingCandidates</c>
    /// there breaks 64 of 200 files that identify confidently today. So this is
    /// the recording-level view of the same evidence, not a replay of the
    /// refusal — which is exactly why <c>Clusters</c> comes back beside it,
    /// unaggregated. The scores the rule actually weighed are there to be read.
    ///
    /// No threshold is applied, for the reason <c>RecordingCandidates</c> gives
    /// for not applying one: where to stop believing a score depends on what
    /// happens next, and here nothing happens next. Refusing is what the pass
    /// did, and the point of showing the set is that a person can see how close
    /// it was.
    ///
    /// <b>A MusicBrainz failure degrades a row, it does not fail the request.</b>
    /// The identity — the MBID, the score, how many clusters and submissions
    /// stand behind it — comes from AcoustID and is the part that explains an
    /// ambiguous refusal. The title is decoration by comparison, and returning
    /// the set without one is far more use than returning nothing.
    /// </remarks>
    internal static async Task<Results<Ok<RecordingCandidatesResponse>, ProblemHttpResult>>
        GetRecordingCandidates(
            Guid id,
            FonotecaDbContext db,
            IAcoustIdLookup acoustId,
            IMusicBrainzCatalogue musicBrainz,
            IClock clock,
            CancellationToken cancellationToken,
            bool refresh = false)
    {
        var mediaFileId = new MediaFileId(id);

        // Tracked, because a miss fills the cache. The read is one row by
        // primary key either way.
        var file = await db.MediaFiles
            .FirstOrDefaultAsync(row => row.Id == mediaFileId, cancellationToken)
            .ConfigureAwait(false);

        if (file is null)
        {
            return TypedResults.Problem(
                title: "No such file",
                detail: $"The catalogue has no media file with id {id}.",
                statusCode: StatusCodes.Status404NotFound);
        }

        // Floored to whole microseconds before it is stored or returned, which
        // is the scan's oldest lesson reaching a second place: `timestamptz`
        // keeps microseconds and .NET keeps 100ns ticks, so a stamp written at
        // tick precision comes back rounded. Here it would make `asOfUtc` differ
        // between the answer that filled the cache and the identical answer read
        // out of it — a caller diffing the two would see a document that changed
        // without changing.
        var now = StoreTime.ToStorePrecision(clock.UtcNow);

        // The whole document, assembled from both providers and kept. Returned
        // as it was stored, with only the freshness fields rewritten — the
        // caller is entitled to know it is reading a week-old answer, and
        // nothing else about it changes.
        if (!refresh
            && IsFresh(file.RecordingCandidatesUtc, now)
            && Stored(file.RecordingCandidatesJson) is { } cached)
        {
            return TypedResults.Ok(cached with
            {
                AsOfUtc = file.RecordingCandidatesUtc!.Value,
                FromCache = true,
            });
        }

        IReadOnlyList<AcoustIdMatch>? matches = null;

        // Somebody else's answer, kept separately and re-usable on its own: the
        // MusicBrainz half above may be stale or absent while this is fine, and
        // rebuilding the document from it costs no AcoustID turn at all.
        if (!refresh
            && IsFresh(file.AcoustIdMatchesUtc, now)
            && AcoustIdEvidence.Deserialise(file.AcoustIdMatchesJson) is { } remembered)
        {
            matches = remembered;
        }

        if (matches is null)
        {
            // Not a 404: the file is here, the thing to ask with is not. It is
            // the ordinary state of a file the pass has not reached, and of one
            // the decoder could not read — two different problems, neither of
            // them answerable by asking AcoustID nothing.
            if (file.Fingerprint is null || file.FingerprintDuration is null)
            {
                return TypedResults.Problem(
                    title: "Nothing to ask with",
                    detail:
                        "This file has no stored fingerprint, so there is no question to put to "
                        + "AcoustID. Run the identification pass over it first.",
                    statusCode: StatusCodes.Status409Conflict);
            }

            try
            {
                matches = await acoustId
                    .LookupAsync(
                        new AudioFingerprint(file.Fingerprint, file.FingerprintDuration.Value),
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (ProviderException error)
            {
                return TypedResults.Problem(
                    title: "AcoustID did not answer",
                    detail: error.Message,
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }

            file.AcoustIdMatchesJson = AcoustIdEvidence.Serialise(matches);
            file.AcoustIdMatchesUtc = now;

            // Committed before the MusicBrainz half rather than with it. That
            // half is up to six lookups under this request's cancellation token,
            // and a caller who closes the dialog part-way through would otherwise
            // throw away a turn at AcoustID's rate limit that has already been
            // spent — the one thing this whole cache exists to stop happening
            // twice.
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        var ranked = RecordingCandidates.From(matches);

        // How many clusters point at each recording. The number that explains an
        // ambiguous refusal better than either score does: two near-tied
        // clusters naming one recording are one answer arriving twice, and a
        // person reading two scores alone cannot tell that from a real
        // disagreement.
        var clusterCount = ranked.ToDictionary(
            candidate => candidate.Id,
            candidate => matches.Count(match =>
                match.Recordings.Any(recording => recording.Id == candidate.Id)));

        var rows = new List<RecordingCandidateRow>();

        foreach (var candidate in ranked.Take(MaxCandidates))
        {
            MusicBrainzRecording? recording = null;

            try
            {
                recording = await musicBrainz
                    .GetRecordingAsync(candidate.Id, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (ProviderException)
            {
                // Left null on purpose. See the remarks: the row is still worth
                // returning without its title.
            }

            // Earliest first, so the original sits above the compilations that
            // reprinted it. Ordered on the parts MusicBrainz stated rather than
            // on a widened date, for the reason `ReleaseDate` is not a
            // `DateOnly`: 1969 turned into 1969-01-01 sorts a reissue ahead of
            // the pressing it reissues. Undated releases go last rather than
            // first — an absent date is not year zero.
            var appearances = recording is null
                ? []
                : recording.Appearances
                    .OrderBy(appearance => appearance.ReleasedOn is null)
                    .ThenBy(appearance => appearance.ReleasedOn?.Year ?? 0)
                    .ThenBy(appearance => appearance.ReleasedOn?.Month ?? 0)
                    .ThenBy(appearance => appearance.ReleasedOn?.Day ?? 0)
                    .ThenBy(appearance => appearance.ReleaseTitle, StringComparer.Ordinal)
                    .ToList();

            rows.Add(new RecordingCandidateRow(
                candidate.Id.Value,
                recording?.Title,
                recording is null ? null : CreditLine(recording.Credits),
                recording?.Disambiguation,
                Format(recording?.Length),
                candidate.Score,
                candidate.Sources,
                clusterCount.GetValueOrDefault(candidate.Id, 1),
                appearances is [var first, ..] ? first.ReleaseTitle : null,
                Drift(file.FingerprintDuration, recording?.Length),
                Earliest(appearances)?.ToString(),
                recording?.WorkTitle,
                recording is null ? [] : [.. recording.Isrcs.Take(MaxIsrcs)],
                recording is null ? [] : Performers(recording.Relations),
                appearances.Count,
                [.. appearances.Take(MaxAppearances).Select(appearance => new CandidateAppearance(
                    appearance.ReleaseId.Value,
                    appearance.ReleaseTitle,
                    appearance.ReleasedOn?.ToString(),
                    appearance.Country,
                    appearance.Status,
                    appearance.PrimaryType,
                    appearance.DiscNumber,
                    appearance.TrackPosition,
                    appearance.TrackNumber,
                    appearance.TrackCount))]));
        }

        var assembled = new RecordingCandidatesResponse(
            id,
            Format(file.FingerprintDuration) ?? "—",
            [.. matches.Select(match =>
                new AcoustIdClusterRow(match.AcoustId, match.Score, match.Recordings.Count))],
            ranked.Count,
            rows,
            now,
            false);

        // Stored last, so a MusicBrainz failure part-way through is cached as
        // exactly what it produced — rows without titles are still the real
        // candidate set, and the alternative is asking again for the ones that
        // did answer. A caller who thinks it went badly has `refresh`.
        file.RecordingCandidatesJson =
            JsonSerializer.Serialize(assembled, MatchingJson.Default.RecordingCandidatesResponse);

        file.RecordingCandidatesUtc = now;

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return TypedResults.Ok(assembled);
    }

    /// <summary>Whether a cached answer is still inside <see cref="CacheDuration"/>.</summary>
    /// <remarks>
    /// A null stamp is never fresh, and a stamp in the future is — a clock that
    /// went backwards should not invalidate a whole library's evidence.
    /// </remarks>
    private static bool IsFresh(DateTimeOffset? takenUtc, DateTimeOffset now) =>
        takenUtc is { } taken && now - taken < CacheDuration;

    /// <summary>A stored candidate document, or null if it cannot be read.</summary>
    /// <remarks>
    /// Malformed JSON is a cache miss rather than a failure. A cache must not be
    /// able to break the thing it makes faster; the worst a bad entry may cost
    /// is the request it was there to save.
    /// </remarks>
    private static RecordingCandidatesResponse? Stored(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;

        try
        {
            var document =
                JsonSerializer.Deserialize(json, MatchingJson.Default.RecordingCandidatesResponse);

            return document is not null && Complete(document) ? document : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Whether a stored document has every field this version of the shape has.
    /// </summary>
    /// <remarks>
    /// <b>A document written before a field existed is a cache miss, not a null
    /// list.</b> Deserialising last week's JSON into today's record succeeds and
    /// leaves the new collections null, which then serialise back to the client
    /// as <c>null</c> where the generated schema promises an array — so the
    /// screen that the cache exists to make fast is the one it breaks, and only
    /// for the files somebody had already looked at. It costs one rebuild per
    /// file, once, the first time a shape changes.
    ///
    /// Checked rather than versioned because the check is the thing that
    /// actually matters and a version number is a second fact to keep in step
    /// with it. Same rule as the <c>catch</c> above: the most a stale entry may
    /// cost is the request it was there to save.
    /// </remarks>
    private static bool Complete(RecordingCandidatesResponse document) =>
        // Non-nullable scalars as well as collections. A scalar deserialises to
        // null or default rather than to an absent field, so the next required
        // string added to this shape would sail through a collections-only check
        // and reach a client that was promised it — the same failure, one field
        // later. `Measured` is the one that exists today.
        document.Measured is not null
        && document.Clusters is not null
        && document.Candidates is not null
        && document.Candidates.All(row =>
            row.Isrcs is not null && row.Performers is not null && row.Releases is not null);

    /// <summary>
    /// A person's answer to one file's identification question, written down.
    /// </summary>
    /// <remarks>
    /// <b>The half that was missing, and the reason the worklist read as a
    /// dead end.</b> The three passes are all allowed to refuse, and refusing is
    /// the behaviour being protected rather than a bug to route around — but a
    /// refusal nothing could answer was a list of complaints. This is where an
    /// answer goes.
    ///
    /// <b>Two answers, and the second is not a cancel button.</b> <c>none</c> is
    /// a person saying the audio is not any of the recordings AcoustID named,
    /// which is a stronger claim than either the rule or the provider is in a
    /// position to make: AcoustID answered, offered recordings, and somebody who
    /// listened rejected them. It closes the question without an identity, and
    /// <see cref="AcoustIdOutcome.RejectedByPerson"/> keeps it distinguishable
    /// from <see cref="AcoustIdOutcome.Unknown"/> forever.
    ///
    /// <b>It resolves the cluster itself rather than trusting the caller for
    /// it.</b> The candidate list a person chose from names recordings, not
    /// clusters, and the cluster is what gets written into the file's bytes —
    /// the one irreversible thing this application does. Taking that identifier
    /// from a form post would mean the only unrecoverable write in the system
    /// was authorised by whatever the client happened to send. So the choice
    /// that crosses the boundary is the recording, which is a claim about music,
    /// and the cluster is resolved here from the server's own evidence:
    /// <c>MediaFile.AcoustIdMatchesJson</c> if it is inside
    /// <see cref="CacheDuration"/>, and a live lookup otherwise. Which of the
    /// two it was does not change the guarantee — neither is the caller's.
    ///
    /// <b>The recording is linked whether or not a cluster survives that.</b>
    /// AcoustID's data moves between the question and the answer, and a cluster
    /// that no longer names the chosen recording is not grounds for discarding a
    /// person's decision — the link is a fact about MusicBrainz and stands on its
    /// own. What is lost is only the tag, which comes back as
    /// <c>tag: NotAttempted</c> with the reason.
    ///
    /// <b>It holds <see cref="LibraryWorkGate"/> for its whole run, and that
    /// excludes the three passes — not a scan.</b> Worth stating precisely,
    /// because the obvious reading is wrong: <c>LibraryScanService</c> guards
    /// itself with a private interlocked flag and has never taken this gate, so
    /// a <c>POST /api/library/scan</c> arriving mid-decision still runs and
    /// still clears every derived column it decides a file changed. What the
    /// lease does buy is real — identification, enrichment and attribution all
    /// take it, and any of them may hold this file in an in-flight page and
    /// write to it — and it is taken rather than merely checked because reading
    /// <c>ActiveKind</c> and carrying on only narrows the window: the two
    /// lookups and the tag write all come after the check.
    ///
    /// Taken <i>after</i> the request is validated, so a malformed body cannot
    /// hold it even briefly, and released by <c>using</c> on every exit — the
    /// four 400s are already past, and the 404s, the 503s, both 200s and any
    /// exception all unwind through it.
    ///
    /// It is held across up to two MusicBrainz requests, whose configured
    /// timeout is 90 seconds each. Ordinarily that is a click and a second;
    /// against a degraded mirror one decision can hold the gate for minutes and
    /// refuse every pass in that window. Acceptable because the alternative is
    /// deciding a file while a pass rewrites it, and because the person holding
    /// it is the person who would have started the pass.
    ///
    /// The order at the end is identification's, for identification's reason:
    /// <c>ApplyAsync</c> commits the file before <c>SaveChanges</c> commits the
    /// row. A crash in the gap leaves a file carrying a verified tag and a row
    /// that does not say so, which the next pass reads and converges on. The
    /// reverse leaves a row claiming a tag the file does not carry, which nothing
    /// can detect.
    /// </remarks>
    internal static async Task<Results<Ok<RecordingDecisionResponse>, ProblemHttpResult>>
        DecideRecording(
            Guid id,
            RecordingDecisionRequest request,
            FonotecaDbContext db,
            IAcoustIdLookup acoustId,
            IMusicBrainzCatalogue musicBrainz,
            AcoustIdTagWriter tagWriter,
            IEventLog events,
            LibraryWorkGate gate,
            ICallerContext caller,
            IClock clock,
            CancellationToken cancellationToken)
    {
        if (request is null)
        {
            return TypedResults.Problem(
                title: "No decision",
                detail: "The request body is required.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var chose = string.Equals(
            request.Answer, RecordingDecisionRequest.ChoseRecording, StringComparison.OrdinalIgnoreCase);

        var rejected = string.Equals(
            request.Answer, RecordingDecisionRequest.ChoseNone, StringComparison.OrdinalIgnoreCase);

        // Named rather than inferred from whether a recording arrived. An empty
        // body would otherwise deserialise into a person's rejection, which is a
        // real decision recorded on a client's bug.
        if (!chose && !rejected)
        {
            return TypedResults.Problem(
                title: "No such answer",
                detail:
                    $"`answer` must be `{RecordingDecisionRequest.ChoseRecording}` or "
                    + $"`{RecordingDecisionRequest.ChoseNone}`.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        if (chose && request.Recording is null)
        {
            return TypedResults.Problem(
                title: "No recording chosen",
                detail: $"`answer: {RecordingDecisionRequest.ChoseRecording}` needs a `recording`.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        if (rejected && request.Recording is not null)
        {
            return TypedResults.Problem(
                title: "Two answers at once",
                detail:
                    $"`answer: {RecordingDecisionRequest.ChoseNone}` means none of the candidates, "
                    + "so it cannot carry one.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        // Taken, not merely read. Checking `ActiveKind` and carrying on would be
        // a check with nothing behind it: this handler then spends seconds on a
        // MusicBrainz lookup, an AcoustID lookup and a tag write, and a scan
        // starting a millisecond after the check would run through all of it —
        // which is the whole race being guarded against. The lease is why
        // `TryEnter` returns one.
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

        var mediaFileId = new MediaFileId(id);

        var row = await db.MediaFiles
            .FirstOrDefaultAsync(file => file.Id == mediaFileId, cancellationToken)
            .ConfigureAwait(false);

        if (row is null)
        {
            return TypedResults.Problem(
                title: "No such file",
                detail: $"The catalogue has no media file with id {id}.",
                statusCode: StatusCodes.Status404NotFound);
        }

        // Floored for the reason above, and for the extra one this handler has:
        // the same value goes into the row and into the tag journal's entry, and
        // those two must agree after a round trip.
        var now = StoreTime.ToStorePrecision(clock.UtcNow);
        var correlationId = Guid.CreateVersion7().ToString("N")[..12];

        if (rejected)
        {
            row.AcoustIdOutcome = ByCaller(caller, AcoustIdOutcome.RejectedByPerson);

            // Only if nothing ever asked. The stamp means "AcoustID has been put
            // this question", and overwriting a real one with the time somebody
            // answered would misreport when the provider was last consulted.
            row.AcoustIdCheckedUtc ??= now;
            row.IdentityDecidedUtc = now;

            await Journal(events, row, null, null, caller, now, correlationId, cancellationToken)
                .ConfigureAwait(false);

            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

            return TypedResults.Ok(new RecordingDecisionResponse(
                id,
                row.AcoustIdOutcome.ToString(),
                row.EnrichmentOutcome.ToString(),
                null,
                null,
                null,
                NotTagged,
                "Recorded as none of the candidates. The file was not opened or changed."));
        }

        var mbid = new Mbid(request.Recording!.Value);

        MusicBrainzRecording? recording;
        MusicBrainzWork? work = null;

        try
        {
            recording = await musicBrainz.GetRecordingAsync(mbid, cancellationToken).ConfigureAwait(false);

            // The recording the person chose has been merged or deleted since the
            // candidate list was drawn. Nothing here can repair that, and writing
            // the link anyway would put an MBID in the catalogue that resolves to
            // nothing.
            if (recording is null)
            {
                return TypedResults.Problem(
                    title: "No such recording",
                    detail:
                        $"MusicBrainz no longer holds recording {mbid}. It has probably been "
                        + "merged; ask for the candidates again to see where it went.",
                    statusCode: StatusCodes.Status404NotFound);
            }

            if (recording.WorkId is { } workId)
            {
                work = await musicBrainz.GetWorkAsync(workId, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (ProviderException error)
        {
            return TypedResults.Problem(
                title: "MusicBrainz did not answer",
                detail: error.Message,
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        AcoustId? cluster = null;

        // The stored answer first, which is almost always the one the person was
        // just shown: `GET .../candidates` fills this cache, and a decision
        // follows a look at the candidates by seconds. Using it is not a weaker
        // check than asking again — the evidence is still the server's own,
        // never the caller's — and it keeps a click off the rate limit.
        IReadOnlyList<AcoustIdMatch>? matches =
            IsFresh(row.AcoustIdMatchesUtc, now)
                ? AcoustIdEvidence.Deserialise(row.AcoustIdMatchesJson)
                : null;

        // Both halves or neither: a fingerprint covers the leading two minutes,
        // so without the duration beside it there is no question to put to
        // AcoustID at all. The entity keeps them together for that reason, and
        // the two branches below have to be able to say which case they are in.
        var asked = matches is not null
            || (row.Fingerprint is { Length: > 0 } && row.FingerprintDuration is not null);

        if (matches is null
            && row.Fingerprint is { Length: > 0 } fingerprint
            && row.FingerprintDuration is { } duration)
        {
            try
            {
                matches = await acoustId
                    .LookupAsync(new AudioFingerprint(fingerprint, duration), cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (ProviderException error)
            {
                // Before anything has been written. The decision is not lost —
                // it was never taken — so the question is still on the worklist
                // and the same click will work when AcoustID does.
                return TypedResults.Problem(
                    title: "AcoustID did not answer",
                    detail: error.Message,
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }

            row.AcoustIdMatchesJson = AcoustIdEvidence.Serialise(matches);
            row.AcoustIdMatchesUtc = now;
        }

        if (matches is not null)
        {
            // Highest score among the clusters that name the chosen recording,
            // ties broken by id. The same order `RecordingCandidates` collapses
            // by, and for the same reason: a rerun must not be able to change its
            // mind about which of two equal answers it took.
            cluster = matches
                .Where(match => match.Recordings.Any(link => link.Id == mbid))
                .OrderByDescending(match => match.Score)
                .ThenBy(match => match.AcoustId)
                .Select(match => (AcoustId?)new AcoustId(match.AcoustId))
                .FirstOrDefault();
        }

        // Discarded: the count belongs to a pass's summary, and one decision has
        // no summary to report it in.
        var artists = new HashSet<Mbid>();

        row.RecordingId = await new CatalogueWriter(db, artists)
            .UpsertAsync(recording, work, cancellationToken)
            .ConfigureAwait(false);

        row.RecordingLookupUtc = now;
        row.EnrichmentOutcome = EnrichmentOutcome.Linked;
        row.AcoustIdCheckedUtc ??= now;
        row.AcoustIdOutcome = ByCaller(caller, AcoustIdOutcome.IdentifiedByPerson);
        row.IdentityDecidedUtc = now;

        var tag = NotTagged;

        string? detail = asked
            ? "AcoustID links no cluster to this recording, so there was nothing to write into "
                + "the file. The link is recorded."
            : "This file has no stored fingerprint and no stored answer, so no cluster could be "
                + "resolved. The link is recorded.";

        if (cluster is { } identified)
        {
            row.AcoustId = identified;

            try
            {
                var plan = await tagWriter
                    .PlanAsync(new LibraryPath(row.Path), identified.Value, cancellationToken)
                    .ConfigureAwait(false);

                var write = await tagWriter
                    .ApplyAsync(plan, row.Id.ToString(), correlationId, caller.ActorId, cancellationToken)
                    .ConfigureAwait(false);

                tag = write.Status.ToString();
                detail = write.Detail;

                if (write.Status is TagWriteStatus.Written or TagWriteStatus.NothingToDo)
                {
                    row.AcoustIdTaggedUtc = now;
                }

                // The loop the identification pass documents, reached by a second
                // route: the bytes changed, and a catalogue still holding the old
                // size and mtime reads that as "this file was modified" on the
                // next scan and discards everything derived from it.
                if (write.Committed is { } facts)
                {
                    row.SizeBytes = facts.SizeBytes;
                    row.LastModifiedUtc = StoreTime.ToStorePrecision(facts.LastModifiedUtc);
                    row.ContentHash = null;
                }
            }
            catch (TagReadFailedException cause)
            {
                // Decided, but not writable — the forty FLACs carrying a
                // prepended ID3v2 header land here. The link above is kept: what
                // failed is reading the file well enough to change it safely, not
                // the decision.
                tag = TagUnreadable;
                detail =
                    $"{cause.Library} could not read this file ({cause.CauseType}), so it was left "
                    + "alone. The link is recorded.";
            }
        }

        await Journal(events, row, mbid, cluster, caller, now, correlationId, cancellationToken)
            .ConfigureAwait(false);

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return TypedResults.Ok(new RecordingDecisionResponse(
            id,
            row.AcoustIdOutcome.ToString(),
            row.EnrichmentOutcome.ToString(),
            mbid.Value,
            recording.Title,
            cluster?.Value,
            tag,
            detail));
    }

    /// <summary>
    /// The albums one refused component could be, gathered again on demand.
    /// </summary>
    /// <remarks>
    /// <b>The question the screen used to say it could not put back to
    /// anybody.</b> Attribution refuses a <i>set</i> of files, and its candidate
    /// releases are computed inside the pass and discarded once they have
    /// ranked — so the worklist could name the refusal and nothing else. The
    /// stated reason it stayed that way was that recovering the set means "a
    /// MusicBrainz browse per recording in the component, which is a pass rather
    /// than a request". That is true of <i>forming</i> a component and not of
    /// re-asking about one that already exists.
    ///
    /// <b>The gather is the expensive half, and it is already paid for.</b> The
    /// pass browses every recording in the album folder and fetches the track
    /// list of each release worth one — bounded by the folder, which runs to
    /// about two hundred files at the worst on this library. None of it is
    /// needed here: the component's members are written down, in the
    /// <c>ReleaseLookupUtc</c> they share. What is left is one browse per
    /// distinct recording and one lookup per release actually offered, which is
    /// bounded, small, and a wait rather than a job.
    ///
    /// <b>Nothing is cached and nothing is written.</b> A recording's candidate
    /// set is cached because a person returns to a file; a component is opened,
    /// decided and gone. Skipping the cache also keeps the honest property the
    /// recording endpoint had to work for — what comes back is what MusicBrainz
    /// says now, not what it said a week ago.
    ///
    /// <b>The prune is by support, and it is a ranking rather than a gate.</b>
    /// The pass's <c>WorthFetching</c> does discard, and is careful about it
    /// because two live runs paid for the care. Here nothing is discarded: the
    /// releases are ordered by how many of the component's recordings each holds
    /// and the tail is simply not looked up. <c>Total</c> says how long the tail
    /// was. That asymmetry is deliberate — it is what makes this endpoint able to
    /// answer a question the pass pruned itself out of.
    /// </remarks>
    internal static async Task<Results<Ok<ComponentCandidatesResponse>, ProblemHttpResult>>
        GetComponentCandidates(
            string stamp,
            FonotecaDbContext db,
            IMusicBrainzCatalogue musicBrainz,
            IOptions<FonotecaOptions> options,
            IClock clock,
            CancellationToken cancellationToken,
            bool refresh = false)
    {
        if (!Component(stamp, out var decidedUtc))
        {
            return TypedResults.Problem(
                title: "No such component",
                detail: $"\u201c{stamp}\u201d is not a component stamp.",
                statusCode: StatusCodes.Status404NotFound);
        }

        var rows = await db.MediaFiles
            .AsNoTracking()
            .Where(file => file.ReleaseLookupUtc == decidedUtc
                && file.ReleaseDecidedUtc == null
                && UnattributedOutcomes.Contains(file.AttributionOutcome)
                && file.Recording!.Mbid != null)
            .OrderBy(file => file.Path)
            .Select(file => new ComponentMember(
                file.Id,
                file.Path,
                file.Recording!.Title,
                file.Recording!.Mbid!.Value,
                file.Quality!.Duration ?? file.FingerprintDuration,
                file.SizeBytes))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        if (rows.Count == 0)
        {
            return TypedResults.Problem(
                title: "No such component",
                detail:
                    "No files are waiting on this component. Either it was never refused, or it "
                    + "has already been answered — re-read the worklist.",
                statusCode: StatusCodes.Status404NotFound);
        }

        // Floored, because it is stored beside the document and read back out of
        // it: the answer that filled the cache and the identical answer read from
        // it must not report different `asOfUtc` values. The scan's oldest lesson.
        var now = StoreTime.ToStorePrecision(clock.UtcNow);

        // The stored answer first, and on an ordinary library it is almost always
        // there: the attribution pass writes one for every component it refuses,
        // out of the gather it had already paid for. What is left for the live
        // path below is a component decided before this existed, one whose file
        // set has moved since, and `?refresh=true`.
        if (!refresh)
        {
            var cached = await db.ReleaseCandidateSets
                .AsNoTracking()
                .FirstOrDefaultAsync(set => set.ComponentUtc == decidedUtc, cancellationToken)
                .ConfigureAwait(false);

            if (StoredComponent(cached?.DocumentJson) is { } document
                && ComponentCandidates.Usable(document, rows.Count, now))
            {
                return TypedResults.Ok(document with { FromCache = true });
            }
        }

        var recordings = rows
            .Select(row => row.Recording)
            .Distinct()
            .OrderBy(recording => recording.Value)
            .ToList();

        var asked = recordings.Take(ComponentCandidates.MaxBrowses).ToList();

        // Which of *this component's* recordings each release is known to hold.
        // The only measure available before a track list has been fetched, which
        // is why this path needs `Support` and the pass does not.
        var hits = new Dictionary<Mbid, HashSet<Mbid>>();
        var summaries = new Dictionary<Mbid, MusicBrainzReleaseCandidate>();

        try
        {
            foreach (var recording in asked)
            {
                var candidates = await musicBrainz
                    .BrowseReleasesForRecordingAsync(recording, cancellationToken)
                    .ConfigureAwait(false);

                foreach (var candidate in candidates)
                {
                    summaries[candidate.Id] = candidate;

                    if (!hits.TryGetValue(candidate.Id, out var holding))
                    {
                        holding = [];
                        hits[candidate.Id] = holding;
                    }

                    holding.Add(recording);
                }
            }
        }
        catch (ProviderException error)
        {
            return TypedResults.Problem(
                title: "MusicBrainz did not answer",
                detail: error.Message,
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        var shortlist = hits
            .OrderByDescending(entry => ComponentCandidates.Support(
                summaries[entry.Key].TrackCount, asked.Count, entry.Value.Count))
            .ThenByDescending(entry => entry.Value.Count)
            .ThenBy(entry => entry.Key.Value)
            .Take(ComponentCandidates.Shown)
            .Select(entry => entry.Key)
            .ToList();

        var fetched = new List<MusicBrainzRelease>(shortlist.Count);

        try
        {
            foreach (var id in shortlist)
            {
                var release = await musicBrainz
                    .GetReleaseAsync(id, cancellationToken)
                    .ConfigureAwait(false);

                // Merged away between the browse and the lookup, which is
                // ordinary rather than exceptional on a mirror mid-replication.
                if (release is not null) fetched.Add(release);
            }
        }
        catch (ProviderException error)
        {
            return TypedResults.Problem(
                title: "MusicBrainz did not answer",
                detail: error.Message,
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        var built = ComponentCandidates.Build(
            stamp,
            now,
            rows,
            fetched,
            fetched.ToDictionary(
                release => release.Id,
                release => ReleaseAttributionService.Formats(summaries[release.Id])),
            recordings.Count,
            asked.Count,
            hits.Count,
            options.Value.ReleaseMinimumCoverage,
            options.Value.ReleaseMaximumDriftMs);

        await StoreComponentAsync(db, decidedUtc, built, cancellationToken).ConfigureAwait(false);

        return TypedResults.Ok(built);
    }

    /// <summary>
    /// The component a stamp names, if it names one.
    /// </summary>
    /// <remarks>
    /// <b>A string on the wire, and it has to be.</b> The stamp is
    /// <c>DateTimeOffset.UtcTicks</c> — around 6.4 × 10^17 — and JavaScript's
    /// number type is exact only to 9 × 10^15. Routed as a <c>long</c> it would
    /// arrive in a browser rounded to the nearest few hundred ticks, and every
    /// request would ask about a component that does not exist. So the id the
    /// worklist prints is a string, it is carried as a string, and this is the
    /// one place it becomes a number again.
    /// </remarks>
    private static bool Component(string stamp, out DateTimeOffset decidedUtc)
    {
        decidedUtc = default;

        if (!long.TryParse(stamp, NumberStyles.None, CultureInfo.InvariantCulture, out var ticks)) return false;
        if (ticks < DateTimeOffset.MinValue.UtcTicks || ticks > DateTimeOffset.MaxValue.UtcTicks) return false;

        decidedUtc = new DateTimeOffset(ticks, TimeSpan.Zero);
        return true;
    }

    /// <summary>
    /// Marks a component's stored candidate document for deletion, if it has one.
    /// </summary>
    /// <remarks>
    /// Tracked rather than <c>ExecuteDelete</c>, so it joins the decision's own
    /// transaction: a document outliving the answer it was gathered for is a row
    /// that can only ever mislead, and it must go or not go with the outcomes.
    /// </remarks>
    private static async Task Forget(
        FonotecaDbContext db,
        DateTimeOffset component,
        CancellationToken cancellationToken)
    {
        var row = await db.ReleaseCandidateSets
            .FirstOrDefaultAsync(set => set.ComponentUtc == component, cancellationToken)
            .ConfigureAwait(false);

        if (row is not null) db.ReleaseCandidateSets.Remove(row);
    }

    /// <summary>
    /// A candidate document read back out of the catalogue, or null if it cannot be.
    /// </summary>
    /// <remarks>
    /// A malformed entry is a cache miss and never a failure — the most a bad row
    /// may cost is the request it was there to save, and here that request is two
    /// minutes of somebody else's rate limit.
    /// </remarks>
    private static ComponentCandidatesResponse? StoredComponent(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;

        try
        {
            return JsonSerializer.Deserialize(json, MatchingJson.Default.ComponentCandidatesResponse);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Keeps a gathered document, so the next person to open this question does not pay for it.
    /// </summary>
    /// <remarks>
    /// An upsert on the component's stamp. Written from a GET, which this file
    /// already does for <c>MediaFile.Quality</c> and for the recording candidate
    /// cache, and for the same reason: the moment somebody is waiting for an
    /// answer is the only moment anybody is waiting for it.
    ///
    /// <c>FromCache</c> is stored as it was built — false. It describes how
    /// <i>this</i> response was answered, not the document, and a stored true
    /// would come back true on the miss that rebuilt it.
    /// </remarks>
    private static async Task StoreComponentAsync(
        FonotecaDbContext db,
        DateTimeOffset component,
        ComponentCandidatesResponse document,
        CancellationToken cancellationToken)
    {
        var row = await db.ReleaseCandidateSets
            .FirstOrDefaultAsync(set => set.ComponentUtc == component, cancellationToken)
            .ConfigureAwait(false);

        var json = JsonSerializer.Serialize(document, MatchingJson.Default.ComponentCandidatesResponse);

        if (row is null)
        {
            db.ReleaseCandidateSets.Add(new ReleaseCandidateSet
            {
                ComponentUtc = component,
                DocumentJson = json,
                Files = document.Files,
                GatheredUtc = document.AsOfUtc,
            });
        }
        else
        {
            row.DocumentJson = json;
            row.Files = document.Files;
            row.GatheredUtc = document.AsOfUtc;
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// A person's answer to one component's album question, written down.
    /// </summary>
    /// <remarks>
    /// <b>Simpler than the recording decision beside it, and the difference is
    /// the point.</b> Identification's answer is written into the file's bytes,
    /// so that handler carries a tag plan, an undo journal, a mutation flag and
    /// two providers. An album is a catalogue fact — nothing on disk says which
    /// pressing a rip came from and this application does not put one there — so
    /// this opens no file, takes no mutation flag and writes one decision entry.
    ///
    /// <b>It writes the release's whole track list, not the part the library
    /// holds.</b> The same rule the pass follows and the same
    /// <c>ReleaseWriter</c> enforcing it, reused rather than restated: the
    /// dictionaries in that class are what keep a component naming one release
    /// group twice from dying on a unique index, and EF's change tracker is
    /// still invisible to EF's queries here.
    ///
    /// <b>Files the chosen release does not explain are left open.</b> A
    /// component is a set that shared a candidate set, which is not the same as
    /// a set that shares an album — the seven hundred-file worklist has
    /// components holding two rips at once. Stamping the remainder with a release
    /// that does not list them would be exactly the invention the pass refuses
    /// to make, so they keep their refusal and their place in the queue, and the
    /// response says how many.
    ///
    /// <b>It takes <see cref="LibraryWorkGate"/>.</b> One MusicBrainz lookup and
    /// one transaction, against a pass that may be holding these very rows in an
    /// in-flight page. Taken rather than read, for the reason the recording
    /// decision gives at length: the lookup and the write both come after the
    /// check.
    /// </remarks>
    internal static async Task<Results<Ok<ComponentDecisionResponse>, ProblemHttpResult>>
        DecideComponent(
            string stamp,
            ComponentDecisionRequest request,
            FonotecaDbContext db,
            IMusicBrainzCatalogue musicBrainz,
            IEventLog events,
            LibraryWorkGate gate,
            ICallerContext caller,
            IClock clock,
            CancellationToken cancellationToken)
    {
        if (request is null)
        {
            return TypedResults.Problem(
                title: "No decision",
                detail: "The request body is required.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var chose = string.Equals(
            request.Answer, ComponentDecisionRequest.ChoseRelease, StringComparison.OrdinalIgnoreCase);

        var rejected = string.Equals(
            request.Answer, ComponentDecisionRequest.ChoseNone, StringComparison.OrdinalIgnoreCase);

        // Named rather than inferred, for the reason the recording decision
        // names it: an empty body would otherwise deserialise into a person
        // rejecting every album, which is a real decision recorded on a bug.
        if (!chose && !rejected)
        {
            return TypedResults.Problem(
                title: "No such answer",
                detail:
                    $"`answer` must be `{ComponentDecisionRequest.ChoseRelease}` or "
                    + $"`{ComponentDecisionRequest.ChoseNone}`.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        if (chose && request.Release is null)
        {
            return TypedResults.Problem(
                title: "No release chosen",
                detail: $"`answer: {ComponentDecisionRequest.ChoseRelease}` needs a `release`.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        if (rejected && request.Release is not null)
        {
            return TypedResults.Problem(
                title: "Two answers at once",
                detail:
                    $"`answer: {ComponentDecisionRequest.ChoseNone}` means none of the candidates, "
                    + "so it cannot carry one.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        if (!Component(stamp, out var decidedUtc))
        {
            return TypedResults.Problem(
                title: "No such component",
                detail: $"“{stamp}” is not a component stamp.",
                statusCode: StatusCodes.Status404NotFound);
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

        // Tracked, not AsNoTracking: these are the rows being written. The
        // recording is included rather than left to lazy loading, which is off:
        // its MBID is what `ReleaseFit` matches on, and an unloaded navigation
        // reads as null — so every file would look like one holding no recording
        // and the fit would explain nothing.
        var rows = await db.MediaFiles
            .Include(file => file.Recording)
            .Where(file => file.ReleaseLookupUtc == decidedUtc
                && file.ReleaseDecidedUtc == null
                && UnattributedOutcomes.Contains(file.AttributionOutcome)
                // The same filter the candidate screen applies. Unreachable
                // today — the pass only stamps files it has a recording for —
                // but without it a file with no MBID counts towards `stillOpen`
                // and is reported as holding a recording the album does not
                // list, which is not why it is still open.
                && file.Recording!.Mbid != null)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        if (rows.Count == 0)
        {
            return TypedResults.Problem(
                title: "No such component",
                detail:
                    "No files are waiting on this component. Either it was never refused, or it "
                    + "has already been answered — re-read the worklist.",
                statusCode: StatusCodes.Status404NotFound);
        }

        // Floored for the reason every stamp in this application is: the value
        // goes into the rows and into the event log, and the two have to agree
        // after a round trip through PostgreSQL's microseconds.
        var now = StoreTime.ToStorePrecision(clock.UtcNow);
        var correlationId = Guid.CreateVersion7().ToString("N")[..12];

        if (rejected)
        {
            foreach (var row in rows)
            {
                row.AttributionOutcome = ByCaller(caller, ReleaseAttributionOutcome.NoReleaseByPerson);
                row.ReleaseDecidedUtc = now;
            }

            await Journal(events, stamp, null, rows.Count, rows.Count, caller, now, correlationId, cancellationToken)
                .ConfigureAwait(false);

            // Nothing will ask this again, so the candidate document goes with
            // the question. Same `SaveChanges` as the outcomes it explains.
            await Forget(db, decidedUtc, cancellationToken).ConfigureAwait(false);

            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

            return TypedResults.Ok(new ComponentDecisionResponse(
                stamp,
                ByCaller(caller, ReleaseAttributionOutcome.NoReleaseByPerson).ToString(),
                null,
                null,
                rows.Count,
                0,
                "Recorded as none of the candidate albums. No file was opened or changed."));
        }

        var mbid = new Mbid(request.Release!.Value);

        MusicBrainzRelease? release;

        try
        {
            release = await musicBrainz.GetReleaseAsync(mbid, cancellationToken).ConfigureAwait(false);
        }
        catch (ProviderException error)
        {
            // Before anything has been written, so the decision is not lost — it
            // was never taken, and the same click will work when MusicBrainz does.
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
                    $"MusicBrainz no longer holds release {mbid}. It has probably been merged; "
                    + "ask for the candidates again to see where it went.",
                statusCode: StatusCodes.Status404NotFound);
        }

        // `Quality.Duration ?? FingerprintDuration`, which is what the candidate
        // screen and the pass scored with — measuring one thing on screen and
        // another on commit is how a person is shown drift figures the write did
        // not use.
        var files = rows
            .Where(row => row.Recording?.Mbid is not null)
            .Select(row => new AttributionFile(
                row.Id,
                row.Recording!.Mbid!.Value,
                row.Quality?.Duration ?? row.FingerprintDuration))
            .ToList();

        var fit = files.Count == 0 ? null : ReleaseFit.For(release, files);

        if (fit is null)
        {
            // Not a 500 and not a silent no-op: the person chose a release that
            // lists none of this component's recordings, which is a real answer
            // to give back rather than a state to write.
            return TypedResults.Problem(
                title: "That release explains none of these files",
                detail:
                    $"“{release.Title}” lists none of the recordings these files hold, so there is "
                    + "nothing to file under it. Ask for the candidates again if the list looks wrong.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var writer = new ReleaseAttributionService.ReleaseWriter(db);

        // Null formats: the writer reads them from the lookup's own media.
        var written = await writer.UpsertAsync(release, null, cancellationToken).ConfigureAwait(false);

        var byId = rows.ToDictionary(row => row.Id);
        var decided = 0;

        foreach (var match in fit.Matches)
        {
            if (!byId.TryGetValue(match.File, out var row)) continue;

            row.ReleaseId = written.Release;
            row.ReleaseGroupId = written.Group;
            row.TrackId = writer.TrackIdAt(written.Release, match.Slot.DiscNumber, match.Slot.Position);
            row.AttributionOutcome = ByCaller(caller, ReleaseAttributionOutcome.AttributedByPerson);

            // Zero, and stated rather than left as it was. The count means "this
            // many other editions fitted exactly as well and one was taken by a
            // tie-break"; a person choosing is not a tie-break, and carrying the
            // rule's old number would report their answer as a coin flip.
            row.EditionAlternatives = 0;
            row.ReleaseDecidedUtc = now;
            decided++;
        }

        await Journal(events, stamp, mbid, decided, rows.Count, caller, now, correlationId, cancellationToken)
            .ConfigureAwait(false);

        // Even on a partial answer. What is left of the component is a smaller
        // question than the document describes, and the stored file count would
        // refuse it on the next read anyway — dropping it says so in one place
        // rather than leaving a row that only ever misses.
        await Forget(db, decidedUtc, cancellationToken).ConfigureAwait(false);

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return TypedResults.Ok(new ComponentDecisionResponse(
            stamp,
            ByCaller(caller, ReleaseAttributionOutcome.AttributedByPerson).ToString(),
            mbid.Value,
            release.Title,
            decided,
            rows.Count - decided,
            rows.Count - decided == 0
                ? $"All {decided} files filed under “{release.Title}”."
                : $"{decided} of {rows.Count} files filed under “{release.Title}”. The rest hold "
                    + "recordings it does not list, so they stay on the worklist."));
    }

    /// <summary>One person-made album decision, in the event log.</summary>
    /// <remarks>
    /// One entry per decision rather than one per file, because the decision was
    /// taken once about a set — see <see cref="ComponentSubject"/>. There is no
    /// undo entry beside it: nothing on disk changed, so there is nothing to
    /// reverse.
    /// </remarks>
    private static Task Journal(
        IEventLog events,
        string stamp,
        Mbid? release,
        int decided,
        int files,
        ICallerContext caller,
        DateTimeOffset now,
        string correlationId,
        CancellationToken cancellationToken) =>
        events.AppendAsync(
            DomainEvent.Create(
                release is null ? ComponentRejectedEventType : ComponentDecidedEventType,
                ComponentSubject,
                stamp,
                caller.ActorId,
                now,
                JsonSerializer.Serialize(
                    new ComponentDecisionPayload
                    {
                        Stamp = stamp,
                        Release = release?.Value,
                        Decided = decided,
                        Files = files,
                        Outcome = ByCaller(
                                caller,
                                release is null
                                    ? ReleaseAttributionOutcome.NoReleaseByPerson
                                    : ReleaseAttributionOutcome.AttributedByPerson)
                            .ToString(),
                    },
                    MatchingJson.Default.ComponentDecisionPayload),
                correlationId),
            cancellationToken);

    /// <summary>
    /// The decision itself, in the event log — separate from the tag write's undo entry.
    /// </summary>
    /// <remarks>
    /// They answer different questions. The undo journal records what a file's
    /// tags were before a byte changed, and exists so a write can be reversed;
    /// this records that a person overrode a rule, and exists so that "why does
    /// this file say it is decided when the pass refused it" has an answer with
    /// a date and an actor on it. A rejection writes one of these and no undo
    /// entry at all, because nothing was opened.
    ///
    /// Both share the correlation id, so one decision is one indexed query.
    /// </remarks>
    private static Task Journal(
        IEventLog events,
        MediaFile row,
        Mbid? recording,
        AcoustId? cluster,
        ICallerContext caller,
        DateTimeOffset now,
        string correlationId,
        CancellationToken cancellationToken) =>
        events.AppendAsync(
            DomainEvent.Create(
                recording is null ? RecordingRejectedEventType : RecordingDecidedEventType,
                AcoustIdTagWriter.FileSubject,
                row.Id.ToString(),
                caller.ActorId,
                now,
                JsonSerializer.Serialize(
                    new RecordingDecisionPayload
                    {
                        Path = row.Path,
                        Recording = recording?.Value,
                        AcoustId = cluster?.Value,
                        Outcome = row.AcoustIdOutcome.ToString(),
                    },
                    MatchingJson.Default.RecordingDecisionPayload),
                correlationId),
            cancellationToken);

    /// <summary>
    /// The people on the recording the credit line does not name.
    /// </summary>
    /// <remarks>
    /// <b>The half that tells two identical-looking candidates apart.</b> On a
    /// classical file the billed artist is the composer and every candidate row
    /// says "Ludwig van Beethoven"; the conductor and the orchestra are in the
    /// relationships, and they are the only thing on the screen that differs. On
    /// a jazz file it is the sidemen.
    ///
    /// Deduplicated by (role, name) rather than by artist, because one person
    /// legitimately appears twice with two instruments and both are worth
    /// printing — and because MusicBrainz repeats the same relation across
    /// mediums often enough that an undeduplicated list reads as a stutter.
    ///
    /// The attribute is folded into the role where there is one, so "performer"
    /// plus "trumpet" prints as one phrase rather than two columns, and
    /// <c>PrimaryCredits</c>'s narrowing is deliberately <i>not</i> applied: that
    /// rule decides which artists a track is browsable under, and this is a
    /// person reading evidence, where an engineer is worth a line.
    /// </remarks>
    private static List<CandidatePerformer> Performers(
        IReadOnlyList<MusicBrainzRelation> relations)
    {
        var seen = new HashSet<(string, string)>();
        var performers = new List<CandidatePerformer>();

        foreach (var relation in relations)
        {
            var role = string.IsNullOrWhiteSpace(relation.Attribute)
                ? relation.Type
                : $"{relation.Type} ({relation.Attribute})";

            if (string.IsNullOrWhiteSpace(relation.Name)) continue;
            if (!seen.Add((role, relation.Name))) continue;

            performers.Add(new CandidatePerformer(role, relation.Name, relation.ArtistType));

            if (performers.Count == MaxPerformers) break;
        }

        return performers;
    }

    /// <summary>The billing line as printed, join phrases and all.</summary>
    internal static string? CreditLine(IReadOnlyList<MusicBrainzCredit> credits)
    {
        if (credits.Count == 0) return null;

        var line = string.Concat(credits.Select(credit => credit.Name + (credit.JoinPhrase ?? "")));
        return string.IsNullOrWhiteSpace(line) ? null : line;
    }

    /// <summary>What a set of files is about, in one line.</summary>
    /// <remarks>
    /// Titles rather than the folder, even though the folder is shorter and
    /// usually right. A worklist row headed by a directory name would be reading
    /// the one claim attribution exists not to believe back to the person who is
    /// about to check it — and on the documented failure, the folder says 1979
    /// while the audio is the 2015 remaster. The folder still travels with the
    /// question; it is just not what the question is called.
    /// </remarks>
    private static string Subject(List<string> titles) => titles switch
    {
        [] => "Files with no recording",
        [var only] => only,
        [var first, ..] => $"{first} and {titles.Count - 1} other{(titles.Count == 2 ? "" : "s")}",
    };

    /// <summary>The filename, which is all a single unidentified file has to be called.</summary>
    private static string NameOf(string path)
    {
        var cut = path.LastIndexOf('/');
        return cut < 0 ? path : path[(cut + 1)..];
    }

    /// <summary>
    /// "Beth Hart &amp; Joe Bonamassa" from the parts that printed it.
    /// </summary>
    /// <remarks>
    /// The join phrase belongs to the credit before the gap, so it is appended
    /// rather than inserted between — which is what keeps "feat." and "&amp;" and
    /// ", " each in the place the sleeve put them.
    /// </remarks>
    internal static string? CreditLine(IEnumerable<(string Name, string? JoinPhrase)> credits)
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
    ///
    /// <b>A fourth source, and it is a hop rather than a link: the bands the
    /// artist played in.</b> The three above all require a row naming the artist
    /// on the recording or its work, and for a band member there usually is not
    /// one — MusicBrainz credits a Dire Straits recording to the <i>group</i>.
    /// What reached the page before was whatever the artist happened to be
    /// credited as writer of, which is a fact about MusicBrainz's editing
    /// coverage rather than about the music: measured here, Mark Knopfler had a
    /// writer credit on all 11 Dire Straits albums and Robert Cray on 3 of his
    /// band's 8, so one page looked complete and the other looked like a man who
    /// wandered through his own group. Membership is the claim that the whole
    /// discography is his.
    ///
    /// One hop, not a closure. A band that is itself a member of something does
    /// not carry its members further, which is a real limit and not one this
    /// catalogue has an example of.
    /// </remarks>
    private static async Task<HashSet<ArtistTrack>> BrowsableAsync(
        FonotecaDbContext db,
        ArtistId? artist,
        CancellationToken cancellationToken)
    {
        var pairs = await CreditedAsync(
            db,
            artist is { } only ? [only] : null,
            cancellationToken).ConfigureAwait(false);

        var memberships = db.Relationships
            .AsNoTracking()
            .Where(r => r.Type == RelationshipTargets.Member && r.ArtistId != null);

        if (artist is { } member) memberships = memberships.Where(r => r.ArtistId == member);

        var bands = await memberships
            .Select(r => new { Member = r.ArtistId!.Value, r.TargetId })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        if (bands.Count == 0) return pairs;

        // The same rule, asked about the bands — so what a member inherits is
        // exactly what the band's own page shows, and the two cannot drift.
        var theirs = await CreditedAsync(
            db,
            [.. bands.Select(b => new ArtistId(b.TargetId)).Distinct()],
            cancellationToken).ConfigureAwait(false);

        var byBand = new Dictionary<ArtistId, List<RecordingId>>();

        foreach (var pair in theirs)
        {
            if (!byBand.TryGetValue(pair.ArtistId, out var recordings))
            {
                recordings = [];
                byBand[pair.ArtistId] = recordings;
            }

            recordings.Add(pair.RecordingId);
        }

        foreach (var band in bands)
        {
            if (!byBand.TryGetValue(new ArtistId(band.TargetId), out var recordings)) continue;

            foreach (var recording in recordings)
            {
                pairs.Add(new ArtistTrack(band.Member, recording));
            }
        }

        return pairs;
    }

    /// <summary>The three ways a row can name an artist on a recording.</summary>
    /// <remarks>
    /// Split out of <see cref="BrowsableAsync"/> so the membership hop can ask
    /// the identical question about a band. Two copies of these three queries is
    /// how a member's shelf would come to hold something the band's own page
    /// does not.
    /// </remarks>
    private static async Task<HashSet<ArtistTrack>> CreditedAsync(
        FonotecaDbContext db,
        IReadOnlyList<ArtistId>? artists,
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
        if (artists is { Count: > 0 } only)
        {
            credits = credits.Where(c => only.Contains(c.ArtistId));
            links = links.Where(r => r.ArtistId != null && only.Contains(r.ArtistId.Value));
            wrote = wrote.Where(r => r.ArtistId != null && only.Contains(r.ArtistId.Value));
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

    /// <summary>Which browsable recordings each artist has.</summary>
    /// <remarks>
    /// The recordings rather than a count of them, because the list is what a
    /// picture for the artist is worked out from and the count falls out of it.
    /// Two dictionaries built from one pass over the same pairs would be free;
    /// two passes that could disagree about which artists exist would not.
    /// </remarks>
    private static async Task<Dictionary<ArtistId, List<RecordingId>>> RecordingsByArtistAsync(
        FonotecaDbContext db,
        ArtistId? artist,
        CancellationToken cancellationToken)
    {
        var pairs = await BrowsableAsync(db, artist, cancellationToken).ConfigureAwait(false);

        var byArtist = new Dictionary<ArtistId, List<RecordingId>>();

        foreach (var pair in pairs)
        {
            if (!byArtist.TryGetValue(pair.ArtistId, out var recordings))
            {
                recordings = [];
                byArtist[pair.ArtistId] = recordings;
            }

            recordings.Add(pair.RecordingId);
        }

        return byArtist;
    }

    /// <summary>
    /// Who the albums in the library are by — the default artist list, and the
    /// set worth spending a rationed press photograph on.
    /// </summary>
    /// <remarks>
    /// <b>The union reaches 2,860 artists and 2,157 of them arrive as writers of
    /// a work</b> — every songwriter of every pop song, most with one track. A
    /// front page of that is a phone book, so the default is who the records are
    /// by and <c>scope=all</c> is the rest.
    ///
    /// Three branches, and each exists because the one before it is silently
    /// incomplete:
    ///
    /// <list type="bullet">
    /// <item><b>Billed on an album the library holds a file of</b> — on any
    /// stored edition of it, or on the album itself. The ordinary case, and the
    /// whole chain has to exist: an album nothing is held of is not one anybody
    /// owns. Keyed on the album rather than the pressing, because most files are
    /// held to an album without a proven pressing.</item>
    /// <item><b>Credited on every recording the library holds of an album.</b>
    /// The classical case. MusicBrainz bills a Solti Ring to <i>Wagner</i>, so
    /// reading release credits alone puts the sleeve's composer on the shelf and
    /// leaves the conductor off it. <b>Every</b> track rather than any: on an
    /// anthology a guest is credited on one recording out of many, and three of
    /// three against one of three is the only thing that separates them. The
    /// count is the tracks the library <i>holds</i>, not the ones the release
    /// prints — a box set held one disc of is still an album to whoever is
    /// browsing it.</item>
    /// <item><b>Credited on a recording in a file no album was attributed
    /// to.</b> Measured, 987 of 8,411 files are in that position — without this
    /// a refused album takes its artist off the page with nothing to say why.
    /// </item>
    /// </list>
    ///
    /// <b>All three read <c>ArtistCredits</c> and none reads a relationship</b>,
    /// which is what keeps the two exclusions out: a composer reached through
    /// the work hop, and an orchestra linked to a recording it is not billed on.
    /// <see cref="PrimaryCredits"/>'s wider union is right for "what is this
    /// artist's page", and it is the wrong rule for "whose records are these".
    ///
    /// Membership is all this answers. An artist on the shelf with nothing to
    /// show is still dropped by the caller's track-count filter, which is why
    /// Wagner is in this set and off the page.
    /// </remarks>
    internal static async Task<HashSet<ArtistId>> AlbumArtistsAsync(
        FonotecaDbContext db,
        CancellationToken cancellationToken)
    {
        var billed = await db.ArtistCredits
            .AsNoTracking()
            .Where(credit => (credit.ReleaseId != null
                    && db.Releases.Any(release => release.Id == credit.ReleaseId
                        && release.ReleaseGroup!.Files.Any()))
                || (credit.ReleaseGroupId != null
                    && db.MediaFiles.Any(file => file.ReleaseGroupId == credit.ReleaseGroupId)))
            .Select(credit => credit.ArtistId)
            .Distinct()
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var shelf = new HashSet<ArtistId>(billed);

        // Distinct pairs, not files: a track held in five encodings is one track
        // of one album, and counting files would let the best-ripped song decide
        // whether somebody was on the whole record.
        var held = await db.MediaFiles
            .AsNoTracking()
            .Where(file => file.RecordingId != null)
            .Select(file => new { Album = file.ReleaseGroupId, Recording = file.RecordingId!.Value })
            .Distinct()
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        if (held.Count == 0) return shelf;

        // Recording credits, for the two branches that cannot be answered from a
        // release credit. One query rather than one per release: the set is the
        // library's recordings, which is the same order as the browse pairs the
        // caller already materialises.
        var recordings = held.Select(row => row.Recording).Distinct().ToList();

        var credits = await db.ArtistCredits
            .AsNoTracking()
            .Where(credit => credit.RecordingId != null
                && recordings.Contains(credit.RecordingId!.Value))
            .Select(credit => new { Recording = credit.RecordingId!.Value, credit.ArtistId })
            .Distinct()
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var creditedOn = credits
            .GroupBy(credit => credit.Recording)
            .ToDictionary(group => group.Key, group => group.Select(c => c.ArtistId).ToHashSet());

        foreach (var album in held.GroupBy(row => row.Album))
        {
            var tracks = album.Select(row => row.Recording).Distinct().ToList();

            if (album.Key is null)
            {
                // No album was attributed, so there is none to be on every track
                // of. Anybody billed on the recording counts.
                foreach (var track in tracks)
                {
                    if (creditedOn.TryGetValue(track, out var everyone)) shelf.UnionWith(everyone);
                }

                continue;
            }

            // On every track the library holds of this album, or a guest.
            var onAll = tracks
                .Select(track => creditedOn.GetValueOrDefault(track) ?? [])
                .Aggregate(
                    (IEnumerable<ArtistId>?)null,
                    (common, artists) => common is null ? artists : common.Intersect(artists));

            shelf.UnionWith(onAll ?? []);
        }

        return shelf;
    }

    /// <summary>The recordings one artist is responsible for.</summary>
    /// <remarks>
    /// Internal rather than private because the tag-write pass scopes an
    /// artist's button by it: the files it rewrites are exactly the tracks the
    /// artist page lists, and a second definition of "this artist's recordings"
    /// would put a composer's symphonies in one and not the other.
    /// </remarks>
    internal static async Task<HashSet<RecordingId>> RecordingsOfAsync(
        FonotecaDbContext db,
        ArtistId artist,
        CancellationToken cancellationToken)
    {
        var pairs = await BrowsableAsync(db, artist, cancellationToken).ConfigureAwait(false);

        return [.. pairs.Select(pair => pair.RecordingId)];
    }

    /// <summary>Why this artist has this track, strongest claim first.</summary>
    /// <remarks>
    /// <b>Empty means the membership hop reached it, and that is a reason rather
    /// than the absence of one.</b> The three direct sources each imply a role —
    /// a credit is "billed", a relationship carries its own type — so a pair with
    /// nothing to say for itself can only have arrived through a band the artist
    /// played in, where MusicBrainz names the group on the recording and not the
    /// player. 527 of this library's 11,252 artist/track rows are in that
    /// position.
    ///
    /// Naming it matters twice over. The card prints these as the answer to "why
    /// is this record theirs", and a blank there reads as a bug; and a screen
    /// asking whether every role is <c>composer</c> gets <c>true</c> from an
    /// empty list, which quietly filed two of Neville Marriner's albums under
    /// music of his that somebody else recorded.
    /// </remarks>
    private static IReadOnlyList<string> Roles(
        bool billed,
        List<string> recordingRoles,
        List<string> workRoles)
    {
        var roles = new List<string>(recordingRoles.Count + workRoles.Count + 1);

        if (billed) roles.Add("billed");

        roles.AddRange(recordingRoles);
        roles.AddRange(workRoles);

        return roles.Count == 0 ? ["member"] : [.. roles.Distinct(StringComparer.Ordinal)];
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
    internal static string? Format(TimeSpan? duration) => duration switch
    {
        null => null,
        { TotalHours: >= 1 } value => $"{(int)value.TotalHours}:{value.Minutes:D2}:{value.Seconds:D2}",
        { } value => $"{(int)value.TotalMinutes}:{value.Seconds:D2}",
    };

    /// <summary>The container, upper-cased: <c>FLAC</c>, <c>MP3</c>.</summary>
    /// <remarks>
    /// The extension rather than the codec, and both are returned because they
    /// are different claims: the extension is what the filesystem says and the
    /// codec is what the decoder found, and an <c>.m4a</c> holding ALAC is the
    /// ordinary case where they differ in a way worth seeing.
    /// </remarks>
    private static string ExtensionOf(string path)
    {
        var name = NameOf(path);
        var dot = name.LastIndexOf('.');
        return dot <= 0 ? "" : name[(dot + 1)..].ToUpperInvariant();
    }

    /// <summary>
    /// A file size a person can read, in binary units and labelled as such.
    /// </summary>
    /// <remarks>
    /// <c>MiB</c> rather than <c>MB</c>, because the divisor is 1024 and calling
    /// that a megabyte is the small lie that makes a size disagree with every
    /// file manager it is checked against. Formatted here for the reason
    /// durations are: one rounding rule, applied once, rather than one per
    /// screen.
    /// </remarks>
    private static string Bytes(long size)
    {
        string[] units = ["B", "KiB", "MiB", "GiB", "TiB"];

        double value = size;
        var unit = 0;

        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0
            ? $"{size} B"
            : string.Create(CultureInfo.InvariantCulture, $"{value:0.#} {units[unit]}");
    }

    /// <summary>
    /// How far a candidate's printed length is from what the file measures.
    /// </summary>
    /// <remarks>
    /// <b>The single most decisive number on the screen, and the one the rest of
    /// this application already leans on.</b> Attribution tells the 2015
    /// remaster of <i>Off the Wall</i> from three earlier pressings on exactly
    /// this comparison — 0.00s against 0.76s, on identical track lists — and a
    /// person choosing between two candidates deserves the same evidence rather
    /// than two lengths to subtract in their head.
    ///
    /// Signed, and the sign is information: a file longer than the recording
    /// MusicBrainz prints is usually a different master or a track with the
    /// applause left on, and a file shorter is usually a radio edit.
    ///
    /// Null when either side is unknown, never zero — "we did not measure" and
    /// "they agree exactly" are the two answers a person most needs kept apart.
    /// </remarks>
    private static string? Drift(TimeSpan? measured, TimeSpan? printed)
    {
        if (measured is not { } file || printed is not { } candidate) return null;

        var seconds = (file - candidate).TotalSeconds;

        // A sign on a number that rounds to zero reads as a direction that is
        // not there: "-0.00s" says the file is shorter, and it is not.
        if (Math.Abs(seconds) < 0.005) return "0.00s";

        return string.Create(CultureInfo.InvariantCulture, $"{seconds:+0.00;-0.00;0.00}s");
    }

    /// <summary>The earliest date any of these releases carries.</summary>
    /// <remarks>
    /// Ordered on the parts MusicBrainz actually stated, never on a widened
    /// date: <c>ReleaseDate</c> keeps "1969" as a year with no month because
    /// turning it into 1969-01-01 is what makes a reissue outrank an original.
    /// Sorting on (year, month ?? 0, day ?? 0) keeps a bare year ahead of a
    /// dated release in the same year, which is the order that reads right — the
    /// year is the album and the dated ones are its pressings.
    /// </remarks>
    private static ReleaseDate? Earliest(IEnumerable<MusicBrainzAppearance> appearances) =>
        appearances
            .Select(appearance => appearance.ReleasedOn)
            .Where(date => date is not null)
            .OrderBy(date => date!.Value.Year)
            .ThenBy(date => date!.Value.Month ?? 0)
            .ThenBy(date => date!.Value.Day ?? 0)
            .FirstOrDefault();

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

    /// <summary>
    /// One artist row, as the list and the detail page both send it.
    /// </summary>
    /// <remarks>
    /// Shared rather than written twice, unlike <c>BrowsableAsync</c>'s browse
    /// rule above — that one is duplicated because EF refuses the alternatives,
    /// and this one has no such excuse. Ten fields written out at two call sites
    /// is ten chances for the card and the page to disagree about the same
    /// artist.
    ///
    /// <c>Genres</c> is split here and stored joined, which is the one place
    /// that decision is made. A client that received the delimited string would
    /// have to know the delimiter, and then the wire format would be a fact
    /// about how the column happens to be stored.
    /// </remarks>
    /// <remarks>A person's corrections are laid over the providers' answers here — see <c>PersonEdits</c>.</remarks>
    private static ArtistSummary Describe(Artist artist, int trackCount)
    {
        var edits = PersonEdits.Read(artist.EditsJson);
        string? Field(string field, string? provider) => PersonEdits.Apply(edits, field, provider);

        var name = Field("name", artist.Name) ?? artist.Name;

        return new(
            artist.Id.Value,
            Field("latinName", artist.LatinName) ?? name,
            Field("sortName", artist.SortName),
            Field("disambiguation", artist.Disambiguation),
            Field("type", artist.Type),
            trackCount,
            Field("portrait", artist.PortraitUrl),
            Field("country", artist.Country),
            Field("gender", artist.Gender),
            Integer(Field("beganYear", Number(artist.BeganYear))),
            Integer(Field("endedYear", Number(artist.EndedYear))),
            Field("ended", Flag(artist.Ended)) == "true",
            Split(Field("genres", artist.Genres)),
            artist.LookupUtc,
            artist.Followed);
    }

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
/// <param name="Cover">
/// A release of theirs to draw as their picture, by MusicBrainz id — the album
/// most of their tracks were filed under. Null when nothing of theirs has been
/// attributed yet, which is a card with a monogram on it rather than a gap.
/// It is a *release*, not a portrait: there is no photograph of an artist to be
/// had, since MusicBrainz holds none and the Cover Art Archive is keyed on
/// releases.
/// </param>
/// <param name="Country">ISO 3166-1 code MusicBrainz primarily associates them with.</param>
/// <param name="BeganYear">Born, or formed. Null until the enrichment pass has asked.</param>
/// <param name="EndedYear">Died, or dissolved.</param>
/// <param name="Ended">
/// Whether the life span is over, which is <b>not</b> <c>EndedYear != null</c>:
/// a band known to have split but never dated carries this with no year.
/// </param>
/// <param name="Genres">MusicBrainz's curated genres, most-voted first. Empty until asked.</param>
/// <param name="DescribedAtUtc">
/// When MusicBrainz was last asked about them, or null when nobody has.
/// </param>
/// <remarks>
/// Everything from <paramref name="Country"/> down arrives from the enrichment
/// pass's artist stage and is absent on a catalogue it has not run over — which
/// is why <paramref name="DescribedAtUtc"/> is on the wire beside them. Without
/// it a screen cannot tell "MusicBrainz holds no country for this orchestra"
/// from "nobody has asked yet", and those want opposite things on the page.
/// </remarks>
public sealed record ArtistSummary(
    Guid Id,
    string Name,
    string? SortName,
    string? Disambiguation,
    string? Type,
    int TrackCount,

    /// <summary>
    /// A picture of the artist, on whichever provider's CDN answered.
    /// </summary>
    /// <remarks>
    /// A full URL rather than an identifier, because four different services
    /// answer here and only they know how their addresses are shaped — a
    /// Commons file name is percent-encoded text, a Deezer one carries the
    /// rendition's dimensions in the path. <c>coverArt.ts</c> resizes it.
    ///
    /// <b>Null means "no picture", and the client draws a monogram.</b> There
    /// used to be an album beside this to fall back on, and it was removed:
    /// the artists reaching the fallback are the ones no picture source has
    /// heard of, which is very nearly the set of artists who are not on the
    /// front of their own sleeves — so the fallback showed a conductor, a
    /// session player or a guest wearing somebody else's album cover, stated as
    /// confidently as a photograph.
    /// </remarks>
    string? Portrait,

    string? Country,
    string? Gender,
    int? BeganYear,
    int? EndedYear,
    bool Ended,
    IReadOnlyList<string> Genres,
    DateTimeOffset? DescribedAtUtc,

    /// <summary>
    /// Whether somebody has said they care about this artist.
    /// </summary>
    /// <remarks>
    /// The only field on this record that is not derived from the library or
    /// from a provider — so it is also the only one that means the same thing
    /// on an artist with two hundred tracks and on one with none.
    /// </remarks>
    bool Following);

/// <summary>One artist, every track of theirs the library holds, and what it does not.</summary>
public sealed record ArtistDetailResponse(
    ArtistSummary Artist,
    IReadOnlyList<TrackRow> Tracks,
    ArtistDiscography Discography,
    ArtistProfile Profile);

/// <summary>What MusicBrainz says this artist released, against what is held.</summary>
/// <param name="Known">
/// Release groups MusicBrainz credits them with, before any rule is applied.
/// Zero on an artist nobody has followed, which is not the same as an artist
/// who released nothing — <paramref name="FetchedAtUtc"/> is what tells them
/// apart.
///
/// <b>MusicBrainz's figure and nothing else's.</b> A shop's inventory is counted
/// in <paramref name="Discovered"/>, because the two are different claims and
/// folding them made this number and <paramref name="Held"/> both wrong — on one
/// artist here, 39 known against MusicBrainz's 26, under a sentence on the page
/// naming MusicBrainz as the source.
/// </param>
/// <param name="Held">How many of those the library holds at least one file of.</param>
/// <param name="FetchedAtUtc">
/// When MusicBrainz was last asked, or null when nobody has. On the wire for the
/// same reason <c>ArtistSummary.DescribedAtUtc</c> is: without it a screen
/// cannot tell "this artist released nothing else" from "we never looked", and
/// those want opposite things on the page.
/// </param>
/// <param name="Missing">
/// The records worth telling somebody they do not own — <c>Discography.IsGap</c>
/// applied to the unheld remainder. A cut list, so <paramref name="Known"/> and
/// <paramref name="Held"/> travel beside it rather than being inferable from its
/// length.
/// </param>
/// <remarks>
/// <b>One source, and no shop rows.</b> What a shop stocks was briefly a second
/// list here and a second list is a duplicate however it is presented: the two
/// catalogues name one record differently and nothing they share settles which
/// rows are the same. Whether a record can be bought is the acquire shelf's
/// question, and `DiscoveredRecords` is kept to enrich these rows rather than to
/// add to them.
/// </remarks>
public sealed record ArtistDiscography(
    int Known,
    int Held,
    DateTimeOffset? FetchedAtUtc,
    IReadOnlyList<DiscographyRow> Missing);

/// <summary>One record a shop says the artist made that MusicBrainz has not named.</summary>
/// <remarks>
/// <b>A separate list from <c>Missing</c> rather than more nullable columns on
/// it, because the two are different kinds of claim.</b> A row there is
/// MusicBrainz's: it carries an MBID, a type and a sleeve the Cover Art Archive
/// holds against that very record. A row here is a shop's inventory — no MBID
/// to be had, no type stated, and a picture from the shop's own CDN. Folded into
/// one list they were indistinguishable on the wire, which is how
/// <c>DiscographyRow.Mbid</c> came to be null on 13 of one artist's 31 rows
/// under a doc saying null was not expected.
/// </remarks>
/// <param name="Id">This row's own id — what <c>SetDiscoveredRecordMonitored</c> takes.</param>
/// <param name="Source">Which shop said so.</param>
/// <param name="SourceId">Their id for it, for a caller that wants to go and buy it.</param>
/// <param name="CoverUrl">
/// The sleeve as the shop serves it, fetched by the browser rather than proxied
/// — the same bargain <c>releaseArt</c> takes against the Cover Art Archive.
/// </param>
/// <param name="TrackCount">What the shop says, or null where it did not say.</param>
/// <param name="Monitored">Somebody wants this record. See <c>ReleaseGroup.Monitored</c>.</param>
/// <param name="Editions">
/// Rows the shop listed for this record, 1 where it listed one. A shop sells a
/// record and its bonus-track reissue as two products; folding them is
/// <see cref="Discography.Collapse"/>'s job and this is what it folded.
/// </param>
public sealed record DiscoveredRow(
    Guid Id,
    string Source,
    string SourceId,
    string Title,
    int? Year,
    string? CoverUrl,
    int? TrackCount,
    bool Monitored,
    int Editions);

/// <summary>One record the artist made that the library has no file of.</summary>
/// <param name="Mbid">
/// MusicBrainz's id for the release group. Null is not expected — the browse
/// that wrote the row is keyed on one — but the column is nullable because a
/// group can also be minted by the attribution pass from a file.
/// </param>
/// <param name="Monitored">
/// Whether somebody has said they want this record. The acquire screen's shelf
/// is the monitored subset; this page is where the whole discography is, and
/// where the flag is set. False on everything the first browse wrote — see
/// <c>ReleaseGroup.Monitored</c> for why the default decides the feature.
/// </param>
/// <param name="Editions">
/// Recordings MusicBrainz names under this title and type, 1 where it names
/// one. Vivaldi's shelf carries 132 rows called "The Four Seasons" — different
/// performances, correctly stored, indistinguishable on screen. The count is
/// printed rather than the rows dropped silently: somebody after a particular
/// performance has to be told the others are there.
/// </param>
public sealed record DiscographyRow(
    Guid Id,
    Guid? Mbid,
    string Title,
    string? PrimaryType,
    IReadOnlyList<string> SecondaryTypes,
    int? FirstReleaseYear,
    bool Monitored,
    int Editions);

/// <param name="Follow">True to follow, false to unfollow.</param>
/// <remarks>
/// A named field rather than an inference from the route, so that following and
/// unfollowing are the same request shape and a client cannot express "toggle" —
/// which, from two open tabs, means neither of them knows the answer.
/// </remarks>
public sealed record ArtistFollowRequest(bool Follow);

/// <param name="Monitor">True to want this record, false to stop wanting it.</param>
/// <remarks>
/// Named rather than inferred, for <see cref="ArtistFollowRequest"/>'s reason: a
/// client cannot express "toggle", which from two open tabs means neither of
/// them knows what the answer will be.
/// </remarks>
public sealed record ReleaseGroupMonitorRequest(bool Monitor);

/// <summary>Where a record stands after the request.</summary>
public sealed record ReleaseGroupMonitorResponse(Guid Id, bool Monitored);

/// <param name="Artist">
/// A MusicBrainz artist id, or any URL containing one. Matched anywhere in the
/// string, so pasting the address bar works.
/// </param>
public sealed record ArtistFollowByMbidRequest(string Artist);

/// <summary>Where an artist stands after the request.</summary>
/// <param name="Id">
/// The catalogue's own id, which for a newly minted artist is the only way the
/// caller learns it.
/// </param>
public sealed record ArtistFollowResponse(Guid Id, bool Following);

/// <param name="WorkTitle">The composition, when MusicBrainz links one. Usually null outside classical.</param>
/// <param name="Duration">Pre-formatted for display; null when MusicBrainz does not know it.</param>
/// <param name="Roles">
/// Why this artist has this track: "billed", "conductor", "ensemble", "composer",
/// or "member" where the only reason is a band they played in.
/// </param>
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
    IReadOnlyList<FileRow> Files,

    /// <summary>
    /// Who performs it, where that is somebody other than this artist — the
    /// rest of the billing line, then ensembles, then conductors. What a
    /// composer's page prints beside each recording of their music.
    /// </summary>
    string? Performers);

/// <summary>The album a track was attributed to, as much of it as a row needs.</summary>
/// <param name="AlbumId">The release group, which is what the artist page groups these rows into albums by.</param>
/// <param name="Mbid">
/// The release group's MusicBrainz id — the Cover Art Archive's key for an album
/// with no pressing claimed, so the page draws a cover for it rather than a
/// monogram.
/// </param>
/// <param name="EditionId">The pressing the file is filed under; null when none is claimed.</param>
/// <param name="CoverReleaseId">
/// Whose stored sleeve the page draws: the claimed pressing's, else the album's
/// display edition's, as on the album list. Null where the album has no stored
/// edition at all.
/// </param>
/// <param name="Artist">
/// The pressing's own billing line, or the display edition's where no pressing
/// is claimed, or the album's own where it has no stored edition
/// — "Joe Bonamassa" for a B.B. King tribute
/// album, whatever the tracks on it are credited to. A compilation's tracks do
/// not share it, which is exactly why it is worth sending.
/// </param>
/// <param name="Billed">
/// Whether the artist this page is about is on that billing line.
///
/// <b>Not the same question as the track's "billed" role, and conflating them
/// is what put a B.B. King tribute album into Marc Broussard's discography.</b>
/// He is billed on one recording of it; the album is Joe Bonamassa's. Measured
/// on the target library, 649 artist/album pairs are billed on a recording and
/// absent from the release's own credit — every one of them is an appearance.
///
/// Three states, deliberately. Null means the release records no credit at all,
/// which is the catalogue not knowing rather than the artist not being there,
/// and a screen must not demote an album on the strength of it.
/// </param>
/// <param name="Band">
/// The act credited on the release, where it is a group this artist was a
/// member of.
///
/// <b>Neither billing nor authorship can answer this.</b> MusicBrainz credits a
/// Dire Straits recording to the group, so Mark Knopfler is not on the release's
/// billing line, not on the recording's, and reaches the catalogue only as the
/// composer of the work — which reads as "somebody else recorded their music"
/// about the man who played and sang it. The membership relation is the only
/// thing in MusicBrainz that says otherwise, and it rides on the artist lookup
/// the enrichment pass already makes.
///
/// Null until that pass has run with <c>ArtistRelationships</c> included, and
/// that is a safe default: it is exactly the behaviour of the version before
/// this field existed.
///
/// The group's <i>own</i> name, never the release's printed credit. A screen
/// grouping by this heads one shelf per band; keyed on what the sleeve printed,
/// "The Robert Cray Band" and "Robert Cray Band" are two.
/// </param>
public sealed record TrackAlbum(
    Guid AlbumId,
    Guid? Mbid,
    Guid? EditionId,
    Guid? CoverReleaseId,
    string Title,
    int? Year,
    string? Artist,
    bool? Billed,
    TrackBand? Band);

/// <summary>A group the artist belongs to, as much of it as a shelf heading needs.</summary>
public sealed record TrackBand(Guid Id, string Name);

/// <param name="Quality">What the probe pass measured, or null until it has decoded the file.</param>
/// <param name="Integrity">Unchecked, Intact, Corrupt, Truncated or Unreadable.</param>
public sealed record FileRow(string Path, long SizeBytes, FileQuality? Quality, string Integrity);

/// <summary>A probed file's format, as much as a badge needs.</summary>
public sealed record FileQuality(
    string Codec,
    bool Lossless,
    int? BitDepth,
    int SampleRateHz,
    int BitrateKbps);

/// <summary>A page of albums, with the total the filter matched.</summary>
/// <param name="Total">Release groups the filter matched; what <paramref name="Items"/> pages through.</param>
/// <param name="NoRelease">
/// Album folders answered as coming from no release, matching the filter. Not
/// paged and not in <paramref name="Total"/>: they have no release group to list.
/// </param>
public sealed record AlbumListResponse(
    int Total,
    IReadOnlyList<AlbumSummary> Items,
    IReadOnlyList<NoReleaseAlbum> NoRelease);

/// <summary>An album folder somebody said comes from no release.</summary>
/// <param name="Folder">Library-relative, as the Files screen takes it.</param>
/// <param name="Title">The folder's own name: there is no release to take one from.</param>
/// <param name="Artist">The folder it sits in, which is a shelf and not a credit.</param>
/// <param name="Files">Every file in the folder.</param>
public sealed record NoReleaseAlbum(string Folder, string Title, string? Artist, int Files);

/// <param name="Id">The release group: an album, whichever pressing the files are.</param>
/// <param name="Mbid">The release group's MusicBrainz id.</param>
/// <param name="Artist">The billing line, borrowed from the display edition.</param>
/// <param name="Year">
/// When the album was first released. The year alone: MusicBrainz knows no more
/// than that for about half of a real library, and a full date was never
/// available to invent.
/// </param>
/// <param name="EditionId">
/// The pressing the files are, where every file is filed under the same one.
/// Null means the album is known and the pressing is not, which is a statement,
/// not a gap to fill with a guess.
/// </param>
/// <param name="CoverReleaseId">
/// The edition whose stored sleeve stands for the album. Not a claim about the
/// files; null only when no edition of the album is stored.
/// </param>
/// <param name="Formats">CD, Digital Media, CD+DVD-Video, of the claimed pressing. Why a rip may be legitimately partial.</param>
/// <param name="TrackCount">Tracks the claimed pressing prints. The number a rip is measured against.</param>
/// <param name="Held">
/// Distinct tracks of the claimed pressing the library holds. Tracks, not files:
/// five encodings of one song are one track of the album, and counting files
/// makes a half-ripped album read as complete.
/// </param>
/// <param name="Files">Files held to the album, which exceeds <paramref name="Held"/> where a track is held twice.</param>
/// <param name="Certainty">
/// The weakest claim any of its files carries. Weakest rather than commonest, so
/// one coin-flip in an album does not read as certainty.
/// </param>
/// <param name="EditionAlternatives">How many other pressings fitted exactly as well.</param>
public sealed record AlbumSummary(
    Guid Id,
    Guid? Mbid,
    string Title,
    string? Artist,
    int? Year,
    Guid? EditionId,
    Guid? CoverReleaseId,
    string? Country,
    string? Status,
    string? Formats,
    int? DiscCount,
    int? TrackCount,
    int? Held,
    int Files,
    string Certainty,
    int EditionAlternatives);

/// <summary>One album: its stored editions' tracks at once, and the files held to it.</summary>
/// <param name="Tracks">
/// The claimed pressing's running order, whole; otherwise, one row per
/// recording, that of an edition files are filed under, else of the edition the
/// files are nearest to — official where there is one. Either way followed by
/// whatever the album's other stored editions print that it does not.
/// </param>
/// <param name="Editions">
/// The album's stored editions, in the order the track list was assembled —
/// what a track's <c>On</c> refers to. Listing one is not a claim that the files
/// are it; only <see cref="AlbumSummary.EditionId"/> is that.
/// </param>
/// <param name="Unplaced">Files held to the album whose recording no stored edition prints.</param>
/// <param name="Folders">The album folders its files sit in, and the pressing each is filed under.</param>
/// <param name="Contributable">
/// Files of this album whose recording a person chose by hand and whose
/// fingerprint has not been offered to AcoustID yet. Zero on an ordinary album,
/// because the pass took its answer from AcoustID in the first place. See
/// <c>CatalogueEndpoints.Fingerprints.cs</c>.
/// </param>
public sealed record AlbumDetailResponse(
    AlbumSummary Album,
    IReadOnlyList<AlbumTrackRow> Tracks,
    IReadOnlyList<AlbumEditionRow> Editions,
    IReadOnlyList<AlbumFileRow> Unplaced,
    IReadOnlyList<AlbumFolderRow> Folders,
    int Contributable,
    ReleaseAbout About,
    IReadOnlyList<AlbumCredit> Credits,
    IReadOnlyList<AlbumCard> MoreBy);

/// <summary>One stored edition of an album, as much as naming it needs.</summary>
public sealed record AlbumEditionRow(
    Guid Id,
    string Title,
    int? Year,
    string? Country,
    string? Formats,
    string? Status);

/// <param name="Recording">The title of the recording the file was identified as, if any.</param>
/// <param name="Disc">Where the folder's claimed pressing prints it; null where no pressing is claimed.</param>
/// <param name="TaggedDisc">The disc number the file's own tags carry, as the attribution pass last read them.</param>
/// <param name="TaggedTrack">
/// The track number the file's own tags carry. What the file says about itself,
/// not a position on any pressing — shown where no pressing is claimed.
/// </param>
public sealed record AlbumFileRow(
    FileRow File,
    string? Recording,
    int? Disc,
    int? Position,
    int? TaggedDisc,
    int? TaggedTrack);

/// <param name="Path">The album folder, library-relative.</param>
/// <param name="EditionId">The pressing every file in it is filed under, or null.</param>
/// <param name="Order">
/// <c>FolderOrderOutcome</c>'s own name: how the folder's own order compared
/// with the order the album's editions print.
/// </param>
/// <param name="Files">
/// Its files in the folder's own order: the pressing's, else the order the
/// attribution pass settled on, else the numbers in their names.
/// </param>
public sealed record AlbumFolderRow(
    string Path,
    Guid? EditionId,
    string? Edition,
    string Order,
    IReadOnlyList<AlbumFileRow> Files);

/// <summary>Everything the album page shows that the list does not need.</summary>
/// <param name="GroupId">The release group, which is what "Want" marks.</param>
/// <param name="ArtistId">The first artist on the billing line, whose other albums "More by" lists.</param>
/// <param name="ArtistBanner">That artist's banner, the page's hero; null draws a plain band.</param>
/// <param name="EditionMbid">
/// The claimed pressing's MusicBrainz id. This and every field only a pressing
/// has — the edition note, its date, label, catalogue number and barcode — are
/// null unless the files are known to be one pressing.
/// </param>
/// <param name="CoverSource">"archive", "qobuz" or "upload"; null when there is no picture.</param>
/// <param name="CoverLookupUtc">When the stored picture or the "neither has one" stamp was written; null when nobody has looked.</param>
/// <param name="ReleaseLookupUtc">The latest attribution stamp among its files.</param>
/// <param name="ProbedUtc">The latest probe among its files.</param>
/// <param name="Edited">The fields a person changed.</param>
public sealed record ReleaseAbout(
    Guid? GroupId,
    Guid? GroupMbid,
    Guid? ArtistId,
    string? ArtistBanner,
    Guid? EditionMbid,
    string? Disambiguation,
    string? PrimaryType,
    IReadOnlyList<string> SecondaryTypes,
    int? FirstReleaseYear,
    int? ReleasedYear,
    int? ReleasedMonth,
    int? ReleasedDay,
    string? Label,
    string? CatalogNumber,
    string? Barcode,
    bool Monitored,
    string? CoverSource,
    DateTimeOffset? CoverLookupUtc,
    WrittenText? Review,
    DateTimeOffset? ReviewLookupUtc,
    DateTimeOffset? ReleaseLookupUtc,
    DateTimeOffset? ProbedUtc,
    IReadOnlyList<string> Edited);

/// <summary>One artist's part in an album.</summary>
/// <param name="Role">"album artist", "credited", "conductor", "orchestra", "composer" — joined where several.</param>
/// <param name="Group">Main, Performers or Composition.</param>
/// <param name="Tracks">1-based places in the running order; null for every track.</param>
public sealed record AlbumCredit(
    Guid ArtistId,
    string Name,
    string Role,
    string Group,
    IReadOnlyList<int>? Tracks,
    string? Portrait);

/// <summary>Another album, as much as a card needs.</summary>
/// <param name="CoverReleaseId">The edition whose stored sleeve stands for it, or null.</param>
public sealed record AlbumCard(Guid Id, Guid? Mbid, Guid? CoverReleaseId, string Title, int? Year);

/// <param name="Number">The printed number, which is not always the position: "A1", "12a".</param>
/// <param name="WorkTitle">
/// The composition this track performs, when MusicBrainz links one — usually
/// null outside classical, where it is the heading four movements sit under.
/// </param>
/// <param name="Held">Whether the album's files hold this recording.</param>
/// <param name="On">The stored editions that print it.</param>
public sealed record AlbumTrackRow(
    int DiscNumber,
    int Position,
    string? Number,
    string Title,
    string? WorkTitle,
    string? Duration,
    Guid RecordingId,
    bool Held,
    IReadOnlyList<FileRow> Files,

    /// <summary>The recording's own billing line, where it differs from the album's.</summary>
    string? Artist,
    IReadOnlyList<Guid> On);

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

    /// <summary>Folders whose files all landed on one album.</summary>
    int FoldersAgreeing,

    IReadOnlyList<FolderDisagreement> FoldersSplit,
    IReadOnlyList<AlbumDisagreement> AlbumsSpanningFolders,
    IReadOnlyList<IncompleteRelease> Incomplete);

public sealed record OutcomeCount(string Outcome, int Files);

/// <summary>One folder whose files were filed under more than one album.</summary>
public sealed record FolderDisagreement(string Folder, IReadOnlyList<AttributionShare> Albums);

/// <summary>One album whose files came from more than one folder.</summary>
public sealed record AlbumDisagreement(Guid AlbumId, string Title, IReadOnlyList<AttributionShare> Folders);

/// <summary>How many files one side of a disagreement accounts for.</summary>
public sealed record AttributionShare(Guid AlbumId, string Title, int Files);

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

/// <summary>A page of open questions, with what the whole worklist holds.</summary>
/// <param name="Total">Open questions of every kind, before paging.</param>
/// <param name="Kinds">
/// The same total split by kind, and by files as well as by questions — one
/// release question can be ten files, so the two numbers answer different
/// questions about how much work is left.
/// </param>
/// <param name="Reasons">
/// The same total split by refusal, largest first, and counted over the whole
/// worklist rather than over the page. This is what makes a long queue readable:
/// four hundred files that AcoustID knows and MusicBrainz does not link are one
/// fact, and the client can say so once instead of four hundred times.
/// </param>
public sealed record MatchingQueueResponse(
    int Total,
    IReadOnlyList<OpenQuestionCount> Kinds,
    IReadOnlyList<OpenQuestionCount> Reasons,
    IReadOnlyList<OpenQuestion> Items);

/// <param name="Name">A kind or a reason, depending on which list this is in.</param>
public sealed record OpenQuestionCount(string Name, int Questions, int Files);

/// <summary>
/// One thing the passes would not decide.
/// </summary>
/// <param name="Id">
/// <c>release:{ticks}</c> for a component, <c>recording:{mediaFileId}</c> for a
/// file. Stable across runs only for as long as the refusal is: re-running a
/// pass re-stamps the component and mints a new id, which is correct — it is a
/// different decision, reached against a different candidate set.
/// </param>
/// <param name="Kind">
/// <c>release</c> — a set of files, to one release. <c>recording</c> — one file,
/// to one recording.
/// </param>
/// <param name="Reason">
/// The outcome enum's own name. The seven that can appear here are distinct
/// across all three enums, so a client can hold one flat table of wordings; the
/// names that <i>do</i> collide between the enums — <c>LookupFailed</c> and
/// <c>NotAttempted</c> — are exactly the ones this endpoint never returns.
/// </param>
/// <param name="Folders">
/// Where the files sit on disk. The filesystem's claim, carried because it is
/// how a person finds the music, and never the basis of the question.
/// </param>
/// <param name="Length">
/// What the audio measures, for a single file. Null for a component.
/// </param>
/// <param name="Size">Bytes on disk, for a single file. Null for a component.</param>
/// <param name="Format">The container, upper-cased. Null for a component.</param>
/// <remarks>
/// <b>The three file facts are catalogue reads and nothing more.</b> They come
/// from the row and the path, so a worklist of seven hundred rows still opens no
/// files — which is what keeps this endpoint a query rather than a pass. Bitrate,
/// sample rate and the file's own tags need the bytes and live on
/// <c>matching/files/{id}</c>, one file at a time.
///
/// <b><paramref name="Length"/> has two sources and prefers the older one.</b>
/// <c>FingerprintDuration</c> is what <c>fpcalc</c> measured when identification
/// reached the file, and it is what AcoustID scored against — so it is the
/// number a candidate's printed length should be compared with. Where it is
/// absent, <c>Quality.Duration</c> stands in, put there by
/// <c>matching/files/{id}</c> on some earlier visit. That is not a rare case: a
/// file whose AcoustID was adopted from a tag it already carried was never
/// fingerprinted, and on the target library that is a third of this list.
///
/// Null on a component rather than summed. A set of files has a total length and
/// a total size, and neither is a fact about music: an album's runtime is the
/// release's, not the rip's, and "412 MiB" says nothing a person deciding which
/// pressing this is can use.
/// </remarks>
public sealed record OpenQuestion(
    string Id,
    string Kind,
    string Reason,
    string Subject,
    IReadOnlyList<string> Folders,
    int Files,
    string? Length,
    string? Size,
    string? Format);

/// <summary>
/// One file, as fully as it can be described without asking a provider anything.
/// </summary>
/// <param name="Format">The container from the path — <c>FLAC</c>, <c>MP3</c>.</param>
/// <param name="Measured">
/// What <c>fpcalc</c> measured when it fingerprinted the file, formatted.
/// </param>
/// <param name="Duration">
/// What the container itself claims, read now.
/// </param>
/// <param name="Audio">
/// Codec, bitrate and the rest, read from the bytes. Null when nothing could
/// parse the file — see <paramref name="Note"/>, which then says why.
/// </param>
/// <param name="Tagged">
/// Whether the AcoustID was last seen in the file's own tags. Not the same fact
/// as holding one: with <c>Fonoteca:AllowFileMutation</c> off the catalogue
/// knows the cluster and the bytes do not carry it.
/// </param>
/// <param name="Tags">
/// Whatever the file claims about itself, in reading order.
/// </param>
/// <remarks>
/// <b><c>MUSICBRAINZ_TRACKID</c> is worth more than every score on the screen,
/// and nothing was reading it.</b> Identification asks AcoustID what the audio
/// is; it never asks the file what it says it is, and the two are different
/// questions with different failure modes. Measured against the library this was
/// built on, every sampled file that all three passes refused was carrying a full
/// Picard tag set — recording, release and release-group MBIDs included. That
/// contradicts the note this project has carried since the attribution work
/// ("sampling 60 files found <c>ACOUSTID_ID</c> and nothing else at all"); it is
/// not true of this library, and a person deciding by hand should have been
/// shown it long before now.
/// </remarks>
/// <param name="Note">
/// Why something is missing, when something is — and what the decoder said about
/// the stream, when it said anything.
/// </param>
/// <remarks>
/// <b>Two lengths, deliberately, and they are two different moments rather than
/// two different methods.</b> <paramref name="Measured"/> is what <c>fpcalc</c>
/// measured when the identification pass reached this file, and it is the number
/// AcoustID scored against — so it is the one a candidate's printed length
/// should be compared with. <paramref name="Duration"/> is read now. A gap
/// between them means the bytes changed under the catalogue.
///
/// <paramref name="Duration"/> also stands alone, and that matters more than the
/// comparison: a third of the target library's file-level questions were never
/// fingerprinted at all — their AcoustID was adopted from a tag the file already
/// carried — so for those it is the only length anything knows.
/// </remarks>
public sealed record SubjectFileResponse(
    Guid MediaFileId,
    string Path,
    string Folder,
    string Name,
    string Format,
    long SizeBytes,
    string Size,
    string? Measured,
    string? Duration,
    AudioQualityRow? Audio,
    string Identification,
    string Enrichment,
    string Attribution,
    Guid? AcoustId,
    bool Tagged,
    bool Fingerprinted,
    string Integrity,
    DateTimeOffset? LastScannedUtc,
    string? Recording,
    string? Release,
    IReadOnlyList<FileTagRow> Tags,
    string? Note);

/// <summary>What the decoder found in the stream.</summary>
/// <param name="BitrateKbps">Average, as the container reports it.</param>
/// <param name="BitDepth">Null on lossy formats, where it does not exist.</param>
/// <param name="Tier">
/// <c>QualityTier</c>'s own name — the ranking dedupe and upgrade monitoring
/// use, said in one word rather than left for a reader to derive from four
/// numbers.
/// </param>
public sealed record AudioQualityRow(
    string Codec,
    int BitrateKbps,
    int SampleRateHz,
    int? BitDepth,
    int Channels,
    bool Lossless,
    string Tier);

/// <summary>One tag the file carries, as it would be printed.</summary>
public sealed record FileTagRow(string Name, string Value);

/// <summary>
/// What one file's audio could be, recovered from its stored fingerprint.
/// </summary>
/// <param name="Measured">
/// What the file actually plays for, as `fpcalc` measured it.
/// </param>
/// <remarks>
/// The whole file's length, not the two minutes the fingerprint covers — that
/// is what `fpcalc` reports and what AcoustID matches on, because duration is
/// what separates a track from a twelve-minute extended mix that opens
/// identically. Carried for the same reason attribution leans on it: against a
/// candidate's printed length it is the edition discriminator, so a row three
/// seconds away from this is a different master rather than a different song.
/// Formatted here, like every other duration crossing this boundary, so one
/// rounding rule serves every screen.
/// </remarks>
/// <param name="Clusters">
/// The raw AcoustID answer, before it is collapsed onto recordings. The shape
/// of an ambiguous refusal lives here rather than in the ranking: two clusters
/// at 0.957 and 0.939 look like a disagreement until you see they name the same
/// recording, and after the collapse that fact is gone.
/// </param>
/// <param name="Total">
/// Recordings the clusters name, before <c>MaxCandidates</c> — so a set cut for
/// cost says so rather than looking like the whole answer.
/// </param>
/// <param name="AsOfUtc">
/// When this document was assembled from the two providers. Not when it was
/// requested — a week-old answer says so, which is the difference between a
/// cache a caller can reason about and one that quietly lies about its age.
/// </param>
/// <param name="FromCache">
/// Whether answering touched AcoustID or MusicBrainz at all. Worth returning
/// rather than inferring from <paramref name="AsOfUtc"/>: an answer built
/// moments ago and one read back from moments ago are the same timestamp and
/// very different requests.
/// </param>
public sealed record RecordingCandidatesResponse(
    Guid MediaFileId,
    string Measured,
    IReadOnlyList<AcoustIdClusterRow> Clusters,
    int Total,
    IReadOnlyList<RecordingCandidateRow> Candidates,
    DateTimeOffset AsOfUtc,
    bool FromCache);

/// <summary>One AcoustID cluster that matched, counted rather than listed.</summary>
public sealed record AcoustIdClusterRow(Guid AcoustId, double Score, int Recordings);

/// <summary>
/// One recording this file might hold, with the evidence for it.
/// </summary>
/// <param name="Title">
/// Null when MusicBrainz did not answer for this MBID. The row is still real —
/// AcoustID named it, and the score and counts beside it are what the rule
/// weighed.
/// </param>
/// <param name="Clusters">
/// How many AcoustID clusters point at this recording. Two clusters naming one
/// recording are one answer arriving twice, which is the commonest reason a
/// near-tie is not the disagreement it looks like.
/// </param>
/// <param name="Drift">
/// Signed seconds between what the file measures and what this recording prints.
/// The edition discriminator, and the number attribution already decides
/// pressings on.
/// </param>
/// <param name="FirstReleased">
/// The earliest date any release naming this recording carries. What separates
/// an original from the compilation that reprinted it, when the titles are
/// identical.
/// </param>
/// <param name="Work">The composition, where MusicBrainz links one.</param>
/// <param name="Isrcs">
/// The recording industry's own identifier for this performance. Decisive when a
/// file happens to carry one in its tags, which some do.
/// </param>
/// <param name="Appearances">
/// How many releases MusicBrainz named — <b>capped at 25 by WS/2</b>, which does
/// not say so. A recording reporting 25 here has probably appeared on more.
/// </param>
/// <param name="Releases">
/// Where it appears, earliest first, cut to <see cref="CatalogueEndpoints"/>'s
/// own limit. All of it comes back inside the <i>same</i> recording lookup that
/// was already being made for the title, so none of it costs a request — the
/// previous version of this row asked for artists, credits, releases, groups,
/// media, ISRCs and two relationship kinds and then printed four fields.
/// </param>
public sealed record RecordingCandidateRow(
    Guid Mbid,
    string? Title,
    string? Artist,
    string? Disambiguation,
    string? Length,
    double Score,
    int Sources,
    int Clusters,
    string? Release,
    string? Drift,
    string? FirstReleased,
    string? Work,
    IReadOnlyList<string> Isrcs,
    IReadOnlyList<CandidatePerformer> Performers,
    int Appearances,
    IReadOnlyList<CandidateAppearance> Releases);

/// <summary>One release this recording appears on, and where on it.</summary>
/// <param name="Released">
/// The date as MusicBrainz states it, partial parts and all — <c>1979</c>,
/// <c>1979-08</c>, <c>1979-08-10</c>.
/// </param>
/// <param name="TrackCount">Tracks on that disc, which is what "8 of 10" needs.</param>
public sealed record CandidateAppearance(
    Guid ReleaseId,
    string Title,
    string? Released,
    string? Country,
    string? Status,
    string? PrimaryType,
    int? DiscNumber,
    int? TrackPosition,
    string? TrackNumber,
    int? TrackCount);

/// <summary>
/// Somebody MusicBrainz links to the performance but does not bill on it.
/// </summary>
/// <remarks>
/// The reason a credit line is not enough, and it is the same reason
/// <c>PrimaryCredits</c> exists: MusicBrainz bills a Karajan reading of
/// Beethoven's Fifth to <i>Beethoven</i> and leaves the conductor and the
/// orchestra in relationships. Two candidate recordings of one symphony are
/// otherwise the same row twice — same title, same billed artist, lengths a few
/// seconds apart — and the conductor is the only thing on the screen that tells
/// them apart.
/// </remarks>
public sealed record CandidatePerformer(string Role, string Name, string? Type);

/// <summary>
/// A person's answer to one file's identification question.
/// </summary>
/// <param name="Answer">
/// <c>recording</c> — the file holds <paramref name="Recording"/>.
/// <c>none</c> — the audio is none of the recordings AcoustID named.
/// </param>
/// <param name="Recording">
/// The MusicBrainz recording MBID chosen. Required with <c>recording</c>,
/// forbidden with <c>none</c>.
/// </param>
/// <remarks>
/// <b>The answer is named rather than inferred.</b> A shape where "no recording
/// supplied" meant "none of them" would turn a client that forgot a field, or an
/// empty body, into a recorded human rejection — a decision that then outranks
/// every pass and can only be undone by hand. Two fields and one redundant word
/// is the price of that never happening.
///
/// <b>No cluster crosses this boundary.</b> The choice is a claim about music —
/// which recording this is — and the AcoustID that gets written into the file's
/// bytes is resolved server-side against a live lookup. See
/// <c>CatalogueEndpoints.DecideRecording</c>.
/// </remarks>
public sealed record RecordingDecisionRequest(string Answer, Guid? Recording)
{
    /// <summary>The person named a recording.</summary>
    public const string ChoseRecording = "recording";

    /// <summary>The person rejected every candidate.</summary>
    public const string ChoseNone = "none";
}

/// <summary>
/// What the decision did.
/// </summary>
/// <param name="Outcome">
/// The identification outcome now on the file — <c>IdentifiedByPerson</c> or
/// <c>RejectedByPerson</c>. Distinct from the values a pass writes, on purpose:
/// an answer a rule could not reach is not the same fact as one it did.
/// </param>
/// <param name="Enrichment">
/// The enrichment outcome now on the file. <c>Linked</c> after a recording was
/// chosen; unchanged after a rejection, which links nothing.
/// </param>
/// <param name="AcoustId">
/// The cluster resolved for the chosen recording, and written into the file.
/// Null when AcoustID named none — the link still stands, and
/// <paramref name="Tag"/> says so.
/// </param>
/// <param name="Tag">
/// A <c>TagWriteStatus</c> name, or <c>NotAttempted</c> when there was nothing
/// to write, or <c>Unreadable</c> when the file could not be parsed safely.
/// <c>Refused</c> is the ordinary answer while <c>Fonoteca:AllowFileMutation</c>
/// is off, and is not an error: everything except the write happened.
/// </param>
/// <param name="Detail">
/// Why, in one line, whenever <paramref name="Tag"/> is anything other than a
/// plain success. Shown to the person who pressed the button.
/// </param>
public sealed record RecordingDecisionResponse(
    Guid MediaFileId,
    string Outcome,
    string Enrichment,
    Guid? Recording,
    string? Title,
    Guid? AcoustId,
    string Tag,
    string? Detail);

/// <summary>
/// The candidate albums for one refused component, gathered again on demand.
/// </summary>
/// <remarks>
/// The counts are all here because a cut list and a complete one look identical
/// otherwise, and this list is cut in two independent places:
/// <paramref name="Browsed"/> against <paramref name="Recordings"/> says whether
/// every recording was asked about, and <paramref name="Total"/> against the
/// length of <paramref name="Candidates"/> says how much of the tail was never
/// looked up. That is the same lesson <c>MusicBrainzRecording.Appearances</c>
/// records: a silent cap is worse than a small answer.
/// </remarks>
/// <param name="Stamp">
/// The component's identity — the <c>UtcTicks</c> its files share, as a string.
/// A number would arrive in a browser rounded: ticks run to 6.4 × 10^17 and
/// JavaScript is exact to 9 × 10^15.
/// </param>
/// <param name="AsOfUtc">
/// When MusicBrainz was asked, which is usually not now: the attribution pass
/// writes one of these for every component it refuses, out of the gather it had
/// already paid for. Evidence with an unstated age is what makes a person
/// distrust a score that does not match the website today.
/// </param>
/// <param name="FromCache">
/// Whether this answer was read back rather than gathered. Describes the
/// response, not the document — <c>?refresh=true</c> skips the stored copy and
/// asks MusicBrainz again.
/// </param>
/// <param name="Files">Files in the component still waiting on an answer.</param>
/// <param name="Recordings">Distinct recordings among them.</param>
/// <param name="Browsed">How many of those recordings were actually put to MusicBrainz.</param>
/// <param name="Total">Distinct releases the browses named, before the list was cut.</param>
/// <param name="MinimumCoverage">
/// The share of a release the pass requires before believing it. Returned so the
/// screen colours a fit by the application's own gate rather than by a number of
/// its own.
/// </param>
/// <param name="MaximumDriftMs">The mean drift the pass will tolerate, same reason.</param>
public sealed record ComponentCandidatesResponse(
    string Stamp,
    DateTimeOffset AsOfUtc,
    bool FromCache,
    int Files,
    int Recordings,
    int Browsed,
    int Total,
    double MinimumCoverage,
    int MaximumDriftMs,
    IReadOnlyList<ComponentCandidateRow> Candidates,
    IReadOnlyList<ComponentFileRow> FileList);

/// <summary>One candidate release, scored against the component.</summary>
/// <param name="Artist">The release's own billing line, which its tracks do not share.</param>
/// <param name="Formats">CD, Digital Media, 12" Vinyl. Why a rip may be legitimately partial.</param>
/// <param name="FilesExplained">
/// Files this release accounts for. Never printed without
/// <paramref name="Coverage"/> beside it: a box set explaining twenty files
/// looks like the best answer until you see that twenty is a quarter of it.
/// </param>
/// <param name="Coverage"><paramref name="FilesExplained"/> over <paramref name="TrackCount"/>, 0..1.</param>
/// <param name="MeanDriftMs">
/// Mean distance between a file's measured length and the track's printed one.
/// <b>Null is unmeasurable, not zero</b> — a release MusicBrainz prints no
/// lengths for has produced no evidence, which is not the same as perfect
/// evidence, and it is the number that tells one mastering from another.
/// </param>
public sealed record ComponentCandidateRow(
    string Mbid,
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
    int FilesExplained,
    double Coverage,
    int? MeanDriftMs,
    bool Official,
    IReadOnlyList<ComponentSlotRow> Slots);

/// <summary>
/// One position on a candidate release, filled or not.
/// </summary>
/// <remarks>
/// Empty slots are rows. "You are missing track 7" is the question that separates
/// the album from the compilation reprinting half of it, and a list of only the
/// matched tracks cannot answer it.
/// </remarks>
/// <param name="Number">The printed number, which is not always the position: "A1", "12a".</param>
/// <param name="Duration">What the release prints, pre-formatted.</param>
/// <param name="Measured">What the file measures, same formatting. Null on an empty slot.</param>
/// <param name="DriftMs">
/// |measured − printed| in milliseconds. Null with a <paramref name="Path"/>
/// means one of the two lengths is unknown; null without one means no file
/// landed here at all.
/// </param>
public sealed record ComponentSlotRow(
    int DiscNumber,
    int Position,
    string? Number,
    string Title,
    string? Duration,
    string? Measured,
    int? DriftMs,
    string? Path,
    long? SizeBytes);

/// <summary>One file of the component, as it appears on the screen deciding it.</summary>
public sealed record ComponentFileRow(Guid Id, string Path, string? Title, string? Duration);

/// <summary>A person's answer to one component's album question.</summary>
/// <remarks>
/// <paramref name="Answer"/> is named rather than inferred from whether a
/// release arrived, for the reason the recording decision gives: an empty body
/// would otherwise deserialise into somebody rejecting every album.
/// </remarks>
public sealed record ComponentDecisionRequest(string Answer, Guid? Release)
{
    /// <summary>These files came from this release.</summary>
    public const string ChoseRelease = "release";

    /// <summary>None of the candidates is the album these files came from.</summary>
    public const string ChoseNone = "none";
}

/// <summary>What one component decision did.</summary>
/// <param name="Decided">Files filed under the chosen release, or closed by a rejection.</param>
/// <param name="StillOpen">
/// Files in the component the chosen release does not list. They keep their
/// refusal and their place in the queue — a set that was refused as one is not
/// necessarily one album.
/// </param>
public sealed record ComponentDecisionResponse(
    string Stamp,
    string Outcome,
    Guid? Release,
    string? Title,
    int Decided,
    int StillOpen,
    string Detail);
