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

    /// <summary>Origins allowed to call the API. The web app's dev server in development.</summary>
    public IReadOnlyList<string> CorsOrigins { get; init; } = [];

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
