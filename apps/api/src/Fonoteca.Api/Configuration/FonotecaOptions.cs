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
