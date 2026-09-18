using System.ComponentModel.DataAnnotations;
using Fonoteca.Providers.MusicBrainz;

namespace Fonoteca.Api.Configuration;

/// <summary>
/// Application configuration, validated at startup.
/// </summary>
/// <remarks>
/// Every property is validated before the host finishes starting, so a bad
/// library path or a missing connection string fails immediately and loudly
/// rather than surfacing three hours into a scan.
/// </remarks>
public sealed class FonotecaOptions : IValidatableObject
{
    public const string SectionName = "Fonoteca";

    /// <summary>Absolute path to the music library root, as seen by this process.</summary>
    [Required(AllowEmptyStrings = false)]
    public string LibraryPath { get; init; } = string.Empty;

    /// <summary>
    /// Whether the app may modify files in the library.
    /// </summary>
    /// <remarks>
    /// Defaults to FALSE. Nothing in this scaffold writes to audio files, and
    /// the switch stays off until the verified write path is implemented and
    /// tested. Treat flipping it as a deliberate act.
    /// </remarks>
    public bool AllowFileMutation { get; init; }

    /// <summary>Path to <c>fpcalc</c>. Resolved from PATH when empty.</summary>
    public string FpcalcPath { get; init; } = "fpcalc";

    /// <summary>Path to <c>ffprobe</c>. Resolved from PATH when empty.</summary>
    public string FfprobePath { get; init; } = "ffprobe";

    /// <summary>
    /// Whether an upgrade may take the album it replaced out of the library.
    /// </summary>
    /// <remarks>
    /// <b>Deliberately not <see cref="AllowFileMutation"/>.</b> That flag means
    /// "may rewrite a tag in place, having verified the write with a second
    /// library and journalled the previous value" — a considered, reversible
    /// edit. This one means "may take an album away". Somebody who turned the
    /// first on to get their files tagged has not agreed to the second, and one
    /// flag covering both would make that agreement implicit.
    ///
    /// Off, the upgrade still downloads and still says whether it would have
    /// replaced anything — which makes a run with it off a complete dry run, the
    /// same property <c>AcoustIdTaggedUtc</c> exists to give identification.
    /// </remarks>
    public bool AllowFileReplacement { get; init; }

    /// <summary>
    /// Where a replaced album is moved to. Defaults to the library root with
    /// <c>-replaced</c> on the end.
    /// </summary>
    /// <remarks>
    /// Outside the library root, always, or the next scan catalogues the archive
    /// and the album a person just replaced appears to still be there. A sibling
    /// by default because it is then on the same filesystem, which makes the
    /// move a rename that cannot half-finish; pointing this at another volume
    /// turns every replacement into a full copy of the album.
    /// </remarks>
    public string ReplacedPath { get; init; } = string.Empty;

    /// <summary>
    /// Where a file the file manager trashed is moved to. Defaults to the
    /// library root with <c>-trash</c> on the end.
    /// </summary>
    /// <remarks>
    /// <b>Not <see cref="ReplacedPath"/>, though the mechanism is identical.</b>
    /// That directory holds albums an upgrade decided were worse, which is a
    /// judgement this application made and can explain. This one holds what a
    /// person threw away. Mixed, the only way to tell a mistaken click from a
    /// mistaken upgrade is the timestamp on the folder — and they are undone
    /// differently, because a replacement has a better copy sitting in the
    /// library and a trashed folder has nothing.
    ///
    /// Outside the library root for the reason the archive is: the next scan
    /// would otherwise catalogue it and the folder somebody just deleted would
    /// appear to still be there. A sibling by default, so the move is a rename
    /// on one filesystem rather than a copy of an album.
    /// </remarks>
    public string TrashPath { get; init; } = string.Empty;

    /// <summary>Path to <c>ffmpeg</c>. Resolved from PATH when empty.</summary>
    public string FfmpegPath { get; init; } = "ffmpeg";

    /// <summary>How many files to hash and fingerprint concurrently.</summary>
    [Range(1, 64)]
    public int ScanConcurrency { get; init; } = 4;

    /// <summary>
    /// Contact string sent in the MusicBrainz User-Agent.
    /// </summary>
    /// <remarks>
    /// MusicBrainz requires an identifying User-Agent and enforces roughly one
    /// request per second. Sending a generic agent gets an IP blocked, which is
    /// why this is configuration rather than a constant.
    ///
    /// Empty is allowed and the application starts fine without it — but every
    /// MusicBrainz lookup is then refused locally rather than sent unidentified.
    /// </remarks>
    public string MusicBrainzContact { get; init; } = string.Empty;

    /// <summary>
    /// The MusicBrainz server to query.
    /// </summary>
    /// <remarks>
    /// Point this at a mirror you host and the rate limit stops being the thing
    /// that decides how long identifying a library takes. Against the public
    /// instance, 100,000 recordings is more than a day of requests.
    /// </remarks>
    [Required(AllowEmptyStrings = false)]
    public string MusicBrainzServer { get; init; } = $"https://{MusicBrainzOptions.OfficialHost}";

    /// <summary>
    /// Milliseconds between MusicBrainz requests.
    /// </summary>
    /// <remarks>
    /// Below 1000 against the public instance is refused — see
    /// <see cref="Validate"/>. Against your own mirror, 0 is reasonable.
    /// </remarks>
    [Range(0, 60_000)]
    public int MusicBrainzRequestIntervalMs { get; init; } = 1_000;

    /// <summary>
    /// AcoustID application API key.
    /// </summary>
    /// <remarks>
    /// Free for non-commercial use from <c>https://acoustid.org/new-application</c>.
    /// Empty is allowed; fingerprint lookups are then refused with a message
    /// naming this setting, rather than the service's "invalid API key".
    /// </remarks>
    public string AcoustIdApiKey { get; init; } = string.Empty;

    /// <summary>
    /// AcoustID <i>user</i> API key — the operator's own, needed only to submit.
    /// </summary>
    /// <remarks>
    /// From the account page at <c>https://acoustid.org/</c> after signing in,
    /// and a different thing from <see cref="AcoustIdApiKey"/>: that one says
    /// which application is calling, this one says who is making the claim. It
    /// is what makes a contribution attributable, which is why AcoustID asks
    /// that applications not ship one of their own.
    ///
    /// Empty is allowed and is the ordinary state — nothing in Fonoteca submits
    /// anything unless a person presses the button, so a library that only ever
    /// reads never needs this set.
    /// </remarks>
    public string AcoustIdUserKey { get; init; } = string.Empty;

    /// <summary>
    /// How well a fingerprint must match before its AcoustID is written to a file.
    /// </summary>
    /// <remarks>
    /// Configurable because a library of CD rips and a library of live bootlegs
    /// deserve different answers. The default of 0.90 is measured rather than
    /// picked: a confident match against a real file scored 0.9667.
    ///
    /// Lowering it does not just tag more files, it tags more files
    /// <i>wrongly</i>, and a wrong identifier is believed by every later pass and
    /// by every other tool that reads the file. A margin over the runner-up
    /// applies as well; see <c>AcoustIdSelection</c>.
    /// </remarks>
    [Range(0.0, 1.0)]
    public double AcoustIdMinimumScore { get; init; } = 0.90;

    /// <summary>
    /// How far clear of the runner-up the winning cluster must be.
    /// </summary>
    /// <remarks>
    /// Guards against the failure a score threshold cannot see: two clusters that
    /// both match well, which is AcoustID reporting candidates rather than an
    /// answer. Taking the higher one there is a coin flip dressed as a decision.
    ///
    /// <b>0.05 is deliberately conservative, and measured evidence says it is on
    /// the strict side.</b> Against a real 7,962-file library it left about one
    /// file in eight unidentified, and the ones it caught looked like this:
    /// 0.9894 against 0.9506, where the winner linked to three MusicBrainz
    /// recordings and the runner-up to one — an established cluster beside a
    /// poorly-merged duplicate of itself, not a track beside its remaster.
    /// Lowering this to about 0.02 identifies those; it also narrows the gap that
    /// protects an album version from being tagged as its single. Which risk
    /// matters more depends on the library, which is why it is a setting and not
    /// a constant.
    /// </remarks>
    [Range(0.0, 1.0)]
    public double AcoustIdMinimumMargin { get; init; } = 0.05;

    /// <summary>
    /// How much of a release's track list must be present before a file may be
    /// filed under it.
    /// </summary>
    /// <remarks>
    /// The loosest rung of <c>ReleaseAttribution</c>'s ladder; the strict rungs
    /// above it are fixed, because loosening those is what lets a compilation
    /// outbid the album it drew from. This is also the prune the attribution pass
    /// uses to decide whether a candidate release is worth a request at all, so
    /// lowering it costs wall-clock as well as precision.
    ///
    /// 0.25 against a real library leaves anthologies of licensed catalogue
    /// unattributed, which is the intended answer: they genuinely cannot be told
    /// from the twenty other anthologies carrying the same recordings. Raise it
    /// for a library of complete album rips; lower it if you would rather have a
    /// probable album than none.
    /// </remarks>
    [Range(0.0, 1.0)]
    public double ReleaseMinimumCoverage { get; init; } = 0.25;

    /// <summary>
    /// How far a file's measured length may sit from a release's printed one.
    /// </summary>
    /// <remarks>
    /// The other half of the same rung. Drift is what separates two editions with
    /// identical track lists — a correctly attributed album in the author's
    /// library sits under 100ms, and the compilations that cause trouble sit near
    /// two seconds — so this is the setting that decides how much mastering
    /// difference counts as the same release.
    ///
    /// Milliseconds rather than a <c>TimeSpan</c>, matching how
    /// <c>MusicBrainzRequestIntervalMs</c> is configured, because environment
    /// variables carry strings and a number is unambiguous.
    /// </remarks>
    [Range(0, 60_000)]
    public int ReleaseMaximumDriftMs { get; init; } = 3_000;

    /// <summary>
    /// Whether finishing a scan starts an identification pass.
    /// </summary>
    /// <remarks>
    /// On by default, which is what makes identification happen "during
    /// scanning" without welding a forty-minute file-writing job onto a
    /// six-second read-only one. The pass converges: once the library is
    /// identified, later scans find nothing to do and it finishes immediately.
    /// </remarks>
    public bool IdentifyAfterScan { get; init; } = true;

    /// <summary>
    /// Whether the candidate caches are filled in the background.
    /// </summary>
    /// <remarks>
    /// On by default: without it the first person to open any question on the
    /// worklist waits for the providers to answer — 24 seconds for a recording
    /// and over two minutes for a large component, measured — and every question
    /// is somebody's first. Off is for tests, which stub the providers and count
    /// the calls, and for a run where the rate limit is wanted for a pass
    /// instead. See <c>CandidateWarmService</c>.
    /// </remarks>
    public bool WarmCandidates { get; init; } = true;

    /// <summary>
    /// How stale a followed artist's discography may get before the enrichment
    /// pass asks MusicBrainz again.
    /// </summary>
    /// <remarks>
    /// <b>Without a re-ask the monitoring feature cannot work at all.</b> The
    /// discography worklist was <c>DiscographyLookupUtc IS NULL</c>, so a
    /// followed artist was browsed exactly once and never again — and
    /// <c>ReleaseGroup.Monitored</c> is defined as "turned up <i>after</i> you
    /// followed them", which is a comparison against a second browse that never
    /// happened. The column would have been permanently false for everybody.
    ///
    /// <b>It is the one place this application re-asks a provider on a clock,
    /// and that is affordable only because of what it is asking about.</b> The
    /// worklist is the followed set rather than the catalogue — a few dozen
    /// artists, one gated browse each — where the same idea applied to
    /// <c>AcoustIdCheckedUtc</c> or <c>RecordingLookupUtc</c> would re-ask about
    /// a hundred thousand files. It also spends nothing at Qobuz and buys
    /// nothing automatically: acquisition is still a person pressing a button.
    ///
    /// Nothing auto-starts enrichment, so this is a ceiling on what a pass
    /// <i>may</i> re-ask rather than a timer. Seven days against a service whose
    /// data moves in weeks; raise it if the followed set grows large enough for
    /// the browses to be felt at the rate limit.
    /// </remarks>
    public int DiscographyRecheckDays { get; init; } = 7;

    /// <summary>Settings for the acquisition providers, which are nested rather than flat.</summary>
    /// <remarks>
    /// The odd one out in this file, and deliberately so. Every other setting
    /// here is flat because <c>FonotecaOptions</c> is — that flatness is what
    /// the AcoustID key spent months getting wrong, shipped as
    /// <c>Fonoteca__Providers__AcoustId__ApiKey</c> and binding to nothing.
    ///
    /// These bind under <c>Fonoteca:Providers:Qobuz</c> because that is the
    /// shape <c>.env.example</c> has reserved since before any of this existed,
    /// and because a name people have already put in a <c>.env</c> is worth more
    /// than consistency with the file it lands in. The lesson from last time
    /// stands either way: the binding has to match the name, and there is a test
    /// asserting this one does.
    /// </remarks>
    public ProviderSettings Providers { get; init; } = new();

    /// <summary>How acquisition paces itself.</summary>
    public DownloadSettings Download { get; init; } = new();

    /// <summary>Origins allowed to call the API. The web app's dev server in development.</summary>
    public IReadOnlyList<string> CorsOrigins { get; init; } = [];

    /// <summary>
    /// The bearer token <c>/mcp</c> requires. Empty turns the endpoint off.
    /// </summary>
    /// <remarks>
    /// Off by default, because <c>/mcp</c> lets an agent start passes and answer
    /// the worklist, and a fresh install should not offer that to its network.
    /// The token locks this one endpoint and nothing else: every <c>/api</c>
    /// route is as open as it was, so who can reach the port is still the real
    /// boundary. Read per request rather than at startup, so it is one rule in
    /// one place — see <c>LibraryTools.Guard</c>.
    /// </remarks>
    public string McpToken { get; init; } = string.Empty;

    /// <summary>
    /// The rules that involve more than one setting.
    /// </summary>
    /// <remarks>
    /// Only one so far, and it exists because the consequence of getting it
    /// wrong is not a stack trace. Undercutting MusicBrainz's published rate
    /// limit does not fail — it works, for a while, and then the address is
    /// blocked and every lookup fails for reasons nothing in the logs explains.
    /// Refusing to start is a far better outcome than discovering that.
    ///
    /// <c>ValidateDataAnnotations</c> runs this, so it fails at host start along
    /// with everything else here.
    /// </remarks>
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        // The archive must be outside the library, and both the doc comment and
        // .env.example said so with nothing enforcing it. Inside, the next scan
        // catalogues the archive and the album somebody just replaced looks like
        // it is still there — which is the failure the comment names, arriving
        // as a puzzle rather than as an error.
        if (!string.IsNullOrWhiteSpace(ReplacedPath) && !string.IsNullOrWhiteSpace(LibraryPath))
        {
            var library = Path.TrimEndingDirectorySeparator(Path.GetFullPath(LibraryPath));
            var archive = Path.TrimEndingDirectorySeparator(Path.GetFullPath(ReplacedPath));

            if (archive.Equals(library, StringComparison.Ordinal)
                || archive.StartsWith(library + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            {
                yield return new ValidationResult(
                    $"Fonoteca:ReplacedPath ('{archive}') is inside Fonoteca:LibraryPath "
                    + $"('{library}'). A replaced album kept there is catalogued by the next scan, "
                    + "so it looks like it was never replaced. Put it beside the library, not in it.",
                    [nameof(ReplacedPath)]);
            }
        }

        // And the trash, for a worse version of the same reason. A replaced album
        // kept inside the library merely looks unreplaced; a *trashed* folder
        // kept inside it is re-catalogued as brand-new files after its rows have
        // already been deleted — every AcoustID, recording link, album decision
        // and human answer under it gone, which is the exact catastrophe the
        // feature exists to prevent. One rule per option rather than a shared
        // loop, because the message is the whole value.
        if (!string.IsNullOrWhiteSpace(TrashPath) && !string.IsNullOrWhiteSpace(LibraryPath))
        {
            var library = Path.TrimEndingDirectorySeparator(Path.GetFullPath(LibraryPath));
            var trash = Path.TrimEndingDirectorySeparator(Path.GetFullPath(TrashPath));

            if (trash.Equals(library, StringComparison.Ordinal)
                || trash.StartsWith(library + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            {
                yield return new ValidationResult(
                    $"Fonoteca:TrashPath ('{trash}') is inside Fonoteca:LibraryPath "
                    + $"('{library}'). Trashing deletes the catalogue rows and moves the files, so "
                    + "a bin inside the library is re-scanned as new files with every identity, "
                    + "album and decision on them lost. Put it beside the library, not in it.",
                    [nameof(TrashPath)]);
            }
        }

        if (!Uri.TryCreate(MusicBrainzServer, UriKind.Absolute, out var server)
            || (server.Scheme != Uri.UriSchemeHttp && server.Scheme != Uri.UriSchemeHttps))
        {
            yield return new ValidationResult(
                $"Fonoteca:MusicBrainzServer must be an absolute http or https URL; got "
                + $"'{MusicBrainzServer}'.",
                [nameof(MusicBrainzServer)]);

            yield break;
        }

        var official = string.Equals(
            server.Host, MusicBrainzOptions.OfficialHost, StringComparison.OrdinalIgnoreCase);

        if (official && MusicBrainzRequestIntervalMs < 1_000)
        {
            yield return new ValidationResult(
                $"Fonoteca:MusicBrainzRequestIntervalMs is {MusicBrainzRequestIntervalMs}ms, which "
                + $"exceeds the one-request-per-second limit {MusicBrainzOptions.OfficialHost} "
                + "enforces by blocking the client's address. Either leave it at 1000 or point "
                + "Fonoteca:MusicBrainzServer at a mirror you host yourself.",
                [nameof(MusicBrainzRequestIntervalMs)]);
        }
    }
}

/// <summary>The nested <c>Fonoteca:Providers</c> tree.</summary>
public sealed class ProviderSettings
{
    public QobuzSettings Qobuz { get; init; } = new();

    public AudioDbSettings AudioDb { get; init; } = new();
}

/// <summary>
/// TheAudioDB, one of the four artist-picture sources.
/// </summary>
/// <remarks>
/// One setting, and it is optional: they publish a test key and the options
/// class defaults to it, so leaving this blank gives a working — if shared and
/// rate-limited — source rather than a disabled one. A Patreon key goes here.
///
/// Deezer has no counterpart because its search API needs no credential at all.
/// <c>Fonoteca:Providers:Deezer:Arl</c> in the environment is reserved for a
/// download feature that does not exist and is not read by the picture source.
/// </remarks>
public sealed class AudioDbSettings
{
    public string ApiKey { get; init; } = string.Empty;
}

/// <summary>
/// The Qobuz subscription this instance downloads with.
/// </summary>
/// <remarks>
/// All three come from a signed-in web player session and belong to a person,
/// not to the application. Every one may be empty: the app starts, and the
/// acquisition endpoints refuse locally with a message naming the setting
/// rather than sending a request that comes back "invalid request signature".
/// </remarks>
public sealed class QobuzSettings
{
    public string AppId { get; init; } = string.Empty;

    /// <summary>Signs track URL requests. Not the app id, and not the auth token.</summary>
    public string AppSecret { get; init; } = string.Empty;

    public string UserAuthToken { get; init; } = string.Empty;

    /// <summary>5 MP3 320, 6 FLAC 16/44.1, 7 FLAC to 96kHz, 27 FLAC to 192kHz.</summary>
    public int FormatId { get; init; } = 27;

    /// <summary>
    /// Least time between Qobuz requests. Their limit is unpublished; be slow.
    /// </summary>
    /// <remarks>
    /// No <c>[Range]</c>, unlike its flat siblings above, and that is not an
    /// oversight — it is that the attribute would not run.
    /// <c>ValidateDataAnnotations</c> is
    /// <c>Validator.TryValidateObject(validateAllProperties: true)</c>, which
    /// does not recurse into complex nested properties, so every annotation on
    /// this class and on <see cref="DownloadSettings"/> is decorative. A guard
    /// that does nothing is worse than none: it reads as a promise. The clamp
    /// that actually holds is in <c>Program.cs</c>, where these are mapped.
    /// </remarks>
    public int MinRequestIntervalMs { get; init; } = 1_000;
}

/// <summary>How acquisition paces itself.</summary>
public sealed class DownloadSettings
{
    /// <summary>
    /// Pause between tracks of one album.
    /// </summary>
    /// <remarks>
    /// On top of the provider's own request gate, and for a different reason:
    /// the gate protects the JSON API, this spaces out the CDN transfers. Twelve
    /// hi-res tracks pulled back to back with no gap is the traffic shape that
    /// gets a personal subscription looked at.
    ///
    /// No <c>[Range]</c>, for the reason given on
    /// <see cref="QobuzSettings.MinRequestIntervalMs"/>: nested annotations are
    /// not validated. Clamped in <c>Program.cs</c>.
    /// </remarks>
    public int TrackDelayMs { get; init; } = 500;
}
