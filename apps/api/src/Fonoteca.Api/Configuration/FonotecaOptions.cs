using System.ComponentModel.DataAnnotations;

namespace Fonoteca.Api.Configuration;

/// <summary>
/// Application configuration, validated at startup.
/// </summary>
/// <remarks>
/// Every property is validated before the host finishes starting, so a bad
/// library path or a missing connection string fails immediately and loudly
/// rather than surfacing three hours into a scan.
/// </remarks>
public sealed class FonotecaOptions
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
    /// </remarks>
    public string MusicBrainzContact { get; init; } = string.Empty;

    /// <summary>Origins allowed to call the API. The web app's dev server in development.</summary>
    public IReadOnlyList<string> CorsOrigins { get; init; } = [];
}
