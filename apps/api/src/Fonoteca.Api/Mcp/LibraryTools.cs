using System.ComponentModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Fonoteca.Api.Configuration;
using Fonoteca.Api.Endpoints;
using Fonoteca.Api.Library;
using Fonoteca.Data;
using Fonoteca.Domain.Abstractions;
using Fonoteca.Domain.Events;
using Fonoteca.Providers.MusicBrainz;
using Fonoteca.Tagging;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.Options;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace Fonoteca.Api.Mcp;

/// <summary>
/// The library as tools an agent calls over <c>/mcp</c>.
/// </summary>
/// <remarks>
/// <b>Every tool is a handler the web client already reaches, called directly.</b>
/// Not a copy of its rules and not a loopback request: the validation, the gate,
/// the 404s and the wording of every refusal are the endpoint's own, a problem
/// document comes back as the tool's error, and the answer is serialised with the
/// HTTP JSON options — so an agent reads exactly what the browser reads.
///
/// <b>Decisions go under <see cref="AgentCallerContext"/>, never the owner's.</b>
/// The owner approves a tool call; nobody listens to the file. The decision
/// endpoints write the agent twin of each by-a-person outcome for that caller and
/// the event log records the actor, so the catalogue can tell the two apart.
///
/// <b>What is deliberately missing.</b> The tag write, at any scope, which is the
/// one step nobody may automate and stays three buttons with no fourth route.
/// Trash, move and upload, which act on the disk. Qobuz. <c>decide_recording</c>
/// does write one AcoustID into one file, subject to
/// <c>Fonoteca:AllowFileMutation</c>, exactly as the button on the same question
/// does.
///
/// <b>Instance methods, so the SDK builds one per call from that request's
/// services.</b> A decision's database context and the event log it journals into
/// have to be the same scoped pair, or the journal entry is never saved.
///
/// Lists default to 50 rather than the endpoints' own defaults, which are sized
/// for a browser: a response here lands in a model's context.
/// </remarks>
[McpServerToolType]
public sealed class LibraryTools(IServiceProvider services, IOptions<JsonOptions> json)
{
    internal const string Route = "/mcp";

    private static readonly AgentCallerContext Agent = new();

    private static readonly string[] Startable = ["scan", "identify", "enrich", "probe", "attribute"];

    private static readonly string[] Cancellable = ["identify", "enrich", "probe", "attribute"];

    /// <summary>
    /// The token check, as middleware in front of <see cref="Route"/>.
    /// </summary>
    /// <remarks>
    /// Middleware, so it runs before anything the SDK maps under the route
    /// whatever shape those endpoints take. Read from options on every request:
    /// without a token the endpoint does not exist, and a client sending an empty
    /// bearer is not let in by an empty setting. Constant-time over the whole
    /// header, which leaks its length and nothing else.
    /// </remarks>
    internal static Task Guard(HttpContext context, RequestDelegate next)
    {
        if (!context.Request.Path.StartsWithSegments(Route)) return next(context);

        var token = context.RequestServices.GetRequiredService<IOptions<FonotecaOptions>>().Value.McpToken;

        if (string.IsNullOrWhiteSpace(token))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return Task.CompletedTask;
        }

        var sent = Encoding.UTF8.GetBytes(context.Request.Headers.Authorization.ToString());
        var expected = Encoding.UTF8.GetBytes($"Bearer {token}");

        if (!CryptographicOperations.FixedTimeEquals(sent, expected))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return Task.CompletedTask;
        }

        return next(context);
    }

    [McpServerTool(Name = "library_status", ReadOnly = true)]
    [Description(
        "Whether each pass is running, how far it has got, what is pending and how its last run went: "
        + "scan, identify, enrich, probe, attribute, and the tag write — which is reported here but "
        + "has no tool that starts it.")]
    public async Task<string> LibraryStatus(CancellationToken cancellationToken = default) =>
        Json(new
        {
            Scan = LibraryEndpoints.GetLibraryScanStatus(Get<LibraryScanService>()).Value,
            Identify = (await LibraryEndpoints
                .GetIdentificationStatus(Get<IdentificationService>(), cancellationToken)
                .ConfigureAwait(false)).Value,
            Enrich = (await LibraryEndpoints
                .GetEnrichmentStatus(Get<EnrichmentService>(), cancellationToken)
                .ConfigureAwait(false)).Value,
            Probe = (await LibraryEndpoints
                .GetProbeStatus(Get<ProbeService>(), cancellationToken)
                .ConfigureAwait(false)).Value,
            Attribute = (await LibraryEndpoints
                .GetAttributionStatus(Get<ReleaseAttributionService>(), cancellationToken)
                .ConfigureAwait(false)).Value,
            Tags = (await LibraryEndpoints
                .GetTagWriteStatus(Get<TagWriteService>(), cancellationToken)
                .ConfigureAwait(false)).Value,
        });

    [McpServerTool(Name = "musicbrainz_health", ReadOnly = true)]
    [Description("Which MusicBrainz server is configured and whether it is answering. Cached for 30 seconds.")]
    public async Task<string> MusicBrainzHealth(CancellationToken cancellationToken = default) =>
        Json(await SystemEndpoints
            .GetMusicBrainzHealth(Get<MusicBrainzHealthProbe>(), cancellationToken)
            .ConfigureAwait(false));

    [McpServerTool(Name = "open_questions", ReadOnly = true)]
    [Description(
        "What the passes refused to decide, as questions for a person, counted by reason. An item id "
        + "`recording:{guid}` is one media file: pass the guid to recording_candidates, get_file and "
        + "decide_recording. `release:{stamp}` is an album component: pass the stamp, as a string, to "
        + "component_candidates and decide_component. Questions nothing can recover candidates for "
        + "are answered by folder: folder_contents, search_releases, release_slots, file_under_release "
        + "or mark_folder_unreleased.")]
    public async Task<string> OpenQuestions(
        int skip = 0,
        int take = 50,
        CancellationToken cancellationToken = default) =>
        Answer(await CatalogueEndpoints
            .GetOpenQuestions(Get<FonotecaDbContext>(), cancellationToken, skip, take)
            .ConfigureAwait(false));

    [McpServerTool(Name = "list_artists", ReadOnly = true)]
    [Description(
        "Artists in the library, alphabetically. `query` matches anywhere in the name, "
        + "case-insensitively. `sort`: omit for alphabetical, `tracks` for most-held first. `scope`: "
        + "omit for the artists albums are billed to, `all` for everyone credited — composers, "
        + "conductors, guests.")]
    public async Task<string> ListArtists(
        string? query = null,
        string? sort = null,
        string? scope = null,
        int skip = 0,
        int take = 50,
        CancellationToken cancellationToken = default) =>
        Answer(await CatalogueEndpoints
            .GetArtists(Get<FonotecaDbContext>(), cancellationToken, query, sort, scope, skip, take)
            .ConfigureAwait(false));

    [McpServerTool(Name = "get_artist", ReadOnly = true)]
    [Description("One artist, by catalogue id, and every track of theirs in the library.")]
    public async Task<string> GetArtist(Guid id, CancellationToken cancellationToken = default) =>
        Answer(await CatalogueEndpoints
            .GetArtist(id, Get<FonotecaDbContext>(), cancellationToken)
            .ConfigureAwait(false));

    [McpServerTool(Name = "list_releases", ReadOnly = true)]
    [Description(
        "Albums the library holds at least one track of, with held against track count and the "
        + "weakest certainty any of their files carries. `query` matches the title. `sort`: omit for "
        + "title, `year` (newest first), `artist` or `added`.")]
    public async Task<string> ListReleases(
        string? query = null,
        string? sort = null,
        int skip = 0,
        int take = 50,
        CancellationToken cancellationToken = default) =>
        Answer(await CatalogueEndpoints
            .GetReleases(Get<FonotecaDbContext>(), cancellationToken, query, sort, skip, take)
            .ConfigureAwait(false));

    [McpServerTool(Name = "get_release", ReadOnly = true)]
    [Description("One album, by catalogue id, and its whole track list with each track flagged held or not.")]
    public async Task<string> GetRelease(Guid id, CancellationToken cancellationToken = default) =>
        Answer(await CatalogueEndpoints
            .GetRelease(id, Get<FonotecaDbContext>(), cancellationToken)
            .ConfigureAwait(false));

    [McpServerTool(Name = "get_file", ReadOnly = true)]
    [Description(
        "One media file: path, size, measured length and each pass's outcome, plus — read from the "
        + "bytes now — codec, bitrate, sample rate, bit depth, channels and the file's own tags, "
        + "including any MusicBrainz ids a tagger left in it.")]
    public async Task<string> GetFile(Guid file, CancellationToken cancellationToken = default) =>
        Answer(await CatalogueEndpoints
            .GetSubjectFile(file, Get<FonotecaDbContext>(), Get<AudioFileDescriber>(), cancellationToken)
            .ConfigureAwait(false));

    [McpServerTool(Name = "list_folder", ReadOnly = true)]
    [Description(
        "One directory of the library as it is on disk, non-audio files included, with the "
        + "catalogue's counts on each entry. `path` is library-relative; omit it for the root.")]
    public async Task<string> ListFolder(string? path = null, CancellationToken cancellationToken = default) =>
        Answer(await FileEndpoints
            .ListFolder(Get<FileManagerService>(), cancellationToken, path)
            .ConfigureAwait(false));

    [McpServerTool(Name = "folder_contents", ReadOnly = true)]
    [Description(
        "Every catalogued file under one library-relative folder, in path order, with what each is "
        + "filed under and whether it is still an open question. The media file ids for "
        + "file_under_release come from here.")]
    public async Task<string> FolderContents(string folder, CancellationToken cancellationToken = default) =>
        Answer(await CatalogueEndpoints
            .GetFolderContents(folder, Get<FonotecaDbContext>(), cancellationToken)
            .ConfigureAwait(false));

    [McpServerTool(Name = "recording_candidates", ReadOnly = true)]
    [Description(
        "The recordings one file could be, re-asked from its stored fingerprint and answered from the "
        + "catalogue's cache when that is under a week old; a cold answer takes tens of seconds. Each "
        + "candidate carries its drift against the file, performers, releases and ISRCs. For "
        + "questions whose reason is Ambiguous or BelowThreshold.")]
    public async Task<string> RecordingCandidates(Guid file, CancellationToken cancellationToken = default) =>
        Answer(await CatalogueEndpoints
            .GetRecordingCandidates(
                file,
                Get<FonotecaDbContext>(),
                Get<IAcoustIdLookup>(),
                Get<IMusicBrainzCatalogue>(),
                Get<IClock>(),
                cancellationToken)
            .ConfigureAwait(false));

    [McpServerTool(Name = "component_candidates", ReadOnly = true)]
    [Description(
        "The albums one refused component could be, with how many of its files each explains and "
        + "how closely the lengths agree. `stamp` is the number in the question's `release:{stamp}` "
        + "id, as a string. For questions whose reason is NoConfidentFit.")]
    public async Task<string> ComponentCandidates(string stamp, CancellationToken cancellationToken = default) =>
        Answer(await CatalogueEndpoints
            .GetComponentCandidates(
                stamp,
                Get<FonotecaDbContext>(),
                Get<IMusicBrainzCatalogue>(),
                Get<IOptions<FonotecaOptions>>(),
                Get<IClock>(),
                cancellationToken)
            .ConfigureAwait(false));

    [McpServerTool(Name = "search_releases", ReadOnly = true)]
    [Description(
        "Free-text MusicBrainz release search, in MusicBrainz query syntax (`artist:`, `date:`). A "
        + "release MBID or URL is looked up directly instead. Search fails against a self-hosted "
        + "mirror, which has no search index; a pasted MBID still works there.")]
    public async Task<string> SearchReleases(
        string q,
        int take = 10,
        CancellationToken cancellationToken = default) =>
        Answer(await CatalogueEndpoints
            .SearchReleases(Get<IMusicBrainzCatalogue>(), cancellationToken, q, take)
            .ConfigureAwait(false));

    [McpServerTool(Name = "release_slots", ReadOnly = true)]
    [Description(
        "Every position on one MusicBrainz release, by release MBID, with lengths in milliseconds. "
        + "Given a library-relative `folder`, positions already held by that folder's files filed "
        + "under this same release name them in `heldBy` — seat new files onto the gaps.")]
    public async Task<string> ReleaseSlots(
        Guid release,
        string? folder = null,
        CancellationToken cancellationToken = default) =>
        Answer(await CatalogueEndpoints
            .GetReleaseSlots(release, folder, Get<FonotecaDbContext>(), Get<IMusicBrainzCatalogue>(), cancellationToken)
            .ConfigureAwait(false));

    [McpServerTool(Name = "start_pass")]
    [Description(
        "Start a pass. `scan` walks the library and reconciles the catalogue, answers when done, and "
        + "starts identify after it when that is configured. `identify` fingerprints files with no "
        + "AcoustID and looks them up; it writes tags only with Fonoteca:AllowFileMutation on. `enrich` "
        + "fetches recordings, works and artists. `probe` decodes every file for quality and "
        + "integrity. `attribute` decides each album folder's release. One runs at a time and the "
        + "rest answer busy; poll library_status.")]
    public async Task<string> StartPass(
        [Description("scan, identify, enrich, probe or attribute")] string kind,
        CancellationToken cancellationToken = default) =>
        Answer(kind switch
        {
            "scan" => (IResult)await LibraryEndpoints
                .ScanLibrary(
                    Get<LibraryScanService>(),
                    Get<IdentificationService>(),
                    Get<IOptions<FonotecaOptions>>(),
                    cancellationToken)
                .ConfigureAwait(false),
            "identify" => await LibraryEndpoints
                .StartIdentification(Get<IdentificationService>(), cancellationToken)
                .ConfigureAwait(false),
            "enrich" => await LibraryEndpoints
                .StartEnrichment(Get<EnrichmentService>(), cancellationToken)
                .ConfigureAwait(false),
            "probe" => await LibraryEndpoints
                .StartProbe(Get<ProbeService>(), cancellationToken)
                .ConfigureAwait(false),
            "attribute" => await LibraryEndpoints
                .StartAttribution(Get<ReleaseAttributionService>(), cancellationToken)
                .ConfigureAwait(false),
            _ => throw Unknown(kind, Startable),
        });

    [McpServerTool(Name = "cancel_pass")]
    [Description("Ask a running pass to stop after the item it is on. A scan cannot be cancelled.")]
    public string CancelPass([Description("identify, enrich, probe or attribute")] string kind) =>
        Answer(kind switch
        {
            "identify" => (IResult)LibraryEndpoints.CancelIdentification(Get<IdentificationService>()),
            "enrich" => LibraryEndpoints.CancelEnrichment(Get<EnrichmentService>()),
            "probe" => LibraryEndpoints.CancelProbe(Get<ProbeService>()),
            "attribute" => LibraryEndpoints.CancelAttribution(Get<ReleaseAttributionService>()),
            _ => throw Unknown(kind, Cancellable),
        });

    [McpServerTool(Name = "decide_recording")]
    [Description(
        "Answer one file's identification question. `answer` is `recording`, with the chosen MBID "
        + "from recording_candidates, or `none` — the audio is none of the candidates. Recorded as an "
        + "agent's decision, never the owner's, and it takes the file off the identification and "
        + "enrichment worklists for good. Choosing a recording writes its AcoustID into the file's "
        + "tags when Fonoteca:AllowFileMutation is on. Put the candidates and your reasoning in front "
        + "of the owner before calling this.")]
    public async Task<string> DecideRecording(
        [Description("The media file id: the guid in a `recording:{guid}` question.")] Guid file,
        [Description("`recording` or `none`.")] string answer,
        [Description("The MusicBrainz recording MBID. Required with `recording`, refused with `none`.")] Guid? recording = null,
        CancellationToken cancellationToken = default) =>
        Answer(await CatalogueEndpoints
            .DecideRecording(
                file,
                new RecordingDecisionRequest(answer, recording),
                Get<FonotecaDbContext>(),
                Get<IAcoustIdLookup>(),
                Get<IMusicBrainzCatalogue>(),
                Get<AcoustIdTagWriter>(),
                Get<IEventLog>(),
                Get<LibraryWorkGate>(),
                Agent,
                Get<IClock>(),
                cancellationToken)
            .ConfigureAwait(false));

    [McpServerTool(Name = "decide_component")]
    [Description(
        "Answer one album component's question. `answer` is `release`, with the MBID from "
        + "component_candidates, or `none` — these files came from none of the albums offered. Files "
        + "the chosen release does not list stay open. Catalogue only: no file is touched. Recorded "
        + "as an agent's decision. Put the candidates and your reasoning in front of the owner first.")]
    public async Task<string> DecideComponent(
        [Description("The number in a `release:{stamp}` question, as a string.")] string stamp,
        [Description("`release` or `none`.")] string answer,
        [Description("The MusicBrainz release MBID. Required with `release`, refused with `none`.")] Guid? release = null,
        CancellationToken cancellationToken = default) =>
        Answer(await CatalogueEndpoints
            .DecideComponent(
                stamp,
                new ComponentDecisionRequest(answer, release),
                Get<FonotecaDbContext>(),
                Get<IMusicBrainzCatalogue>(),
                Get<IEventLog>(),
                Get<LibraryWorkGate>(),
                Agent,
                Get<IClock>(),
                cancellationToken)
            .ConfigureAwait(false));

    [McpServerTool(Name = "file_under_release")]
    [Description(
        "File chosen files onto chosen positions of one MusicBrainz release, for files nothing could "
        + "identify. The seating is committed exactly as sent and nothing checks it, so compare each "
        + "file's length with release_slots first. Files that are no longer open questions are "
        + "skipped. Catalogue only; recorded as an agent's decision. Show the owner the pairing "
        + "before calling this.")]
    public async Task<string> FileUnderRelease(
        [Description("The MusicBrainz release MBID.")] Guid release,
        [Description("One entry per file: the media file id, and the disc and position it sits on.")] IReadOnlyList<AlbumFilingPair> pairs,
        CancellationToken cancellationToken = default) =>
        Answer(await CatalogueEndpoints
            .FileFilesUnderRelease(
                new AlbumFilingRequest(release, pairs),
                Get<FonotecaDbContext>(),
                Get<IMusicBrainzCatalogue>(),
                Get<IEventLog>(),
                Get<LibraryWorkGate>(),
                Agent,
                Get<IClock>(),
                cancellationToken)
            .ConfigureAwait(false));

    [McpServerTool(Name = "mark_folder_unreleased")]
    [Description(
        "Say a library-relative folder, and everything beneath it, is nobody's release — a mixtape, "
        + "a private recording — so its open questions close and no pass asks about them again. Only "
        + "the refused legs change. Undoing it is a hand-written UPDATE. Recorded as an agent's "
        + "decision.")]
    public async Task<string> MarkFolderUnreleased(string folder, CancellationToken cancellationToken = default) =>
        Answer(await CatalogueEndpoints
            .MarkFolderUnreleased(
                new FolderUnreleasedRequest(folder),
                Get<FonotecaDbContext>(),
                Get<IEventLog>(),
                Get<LibraryWorkGate>(),
                Agent,
                Get<IClock>(),
                cancellationToken)
            .ConfigureAwait(false));

    [McpServerTool(Name = "reopen_folder")]
    [Description(
        "Say the passes matched a library-relative folder wrongly: every placed file under it gives "
        + "up its recording, track and release and becomes one open question that no pass will "
        + "answer again. Recorded as an agent's decision.")]
    public async Task<string> ReopenFolder(string folder, CancellationToken cancellationToken = default) =>
        Answer(await CatalogueEndpoints
            .ReopenFolder(
                new FolderReopenRequest(folder),
                Get<FonotecaDbContext>(),
                Get<IEventLog>(),
                Get<LibraryWorkGate>(),
                Agent,
                Get<IClock>(),
                cancellationToken)
            .ConfigureAwait(false));

    /// <summary>A handler's result as the tool's answer, or its problem document as the tool's error.</summary>
    private string Answer(IResult result) => result switch
    {
        INestedHttpResult nested => Answer(nested.Result),
        ProblemHttpResult problem => throw new McpException(
            $"{problem.StatusCode}: {problem.ProblemDetails.Title}. {problem.ProblemDetails.Detail}"),
        IValueHttpResult { Value: var value } => Json(value),
        IStatusCodeHttpResult { StatusCode: var status } => Json(new { Status = status }),
        _ => throw new InvalidOperationException($"No answer for a {result.GetType().Name}."),
    };

    private string Json(object? value) => JsonSerializer.Serialize(value, json.Value.SerializerOptions);

    private T Get<T>()
        where T : notnull =>
        services.GetRequiredService<T>();

    private static McpException Unknown(string kind, IEnumerable<string> allowed) =>
        new($"`kind` must be one of {string.Join(", ", allowed)}; got `{kind}`.");
}
