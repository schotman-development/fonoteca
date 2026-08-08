namespace Fonoteca.Providers.MusicBrainz;

/// <summary>How to talk to MusicBrainz.</summary>
public sealed class MusicBrainzOptions
{
    /// <summary>Name of the configured <c>HttpClient</c>; the gate and the retries hang off it.</summary>
    public const string HttpClientName = "musicbrainz";

    /// <summary>The public instance. Named because the rate-limit rules only apply to it.</summary>
    public const string OfficialHost = "musicbrainz.org";

    /// <summary>The official rate limit: one request per second, averaged.</summary>
    public static readonly TimeSpan OfficialRequestInterval = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Which server to ask.
    /// </summary>
    /// <remarks>
    /// Configurable because a self-hosted mirror is the only real answer to the
    /// arithmetic: at the official instance's one request per second, looking
    /// up 100,000 recordings takes over a day of wall-clock time, and that is
    /// before releases. A mirror has no such limit and is the supported way to
    /// go faster — unlike simply lowering the interval, which is the
    /// unsupported way and ends in a blocked address.
    /// </remarks>
    public Uri Server { get; set; } = new($"https://{OfficialHost}");

    /// <summary>
    /// Least time between requests.
    /// </summary>
    /// <remarks>
    /// One second, which is what MusicBrainz asks for. Lowering it against the
    /// official host is refused at startup rather than obeyed; against a mirror
    /// you run yourself, zero is reasonable.
    /// </remarks>
    public TimeSpan MinimumRequestInterval { get; set; } = OfficialRequestInterval;

    /// <summary>Application name in the User-Agent.</summary>
    public string ApplicationName { get; set; } = "Fonoteca";

    /// <summary>Application version in the User-Agent.</summary>
    public string ApplicationVersion { get; set; } = "0.1";

    /// <summary>
    /// A URL or email address MusicBrainz can use to reach whoever is running this.
    /// </summary>
    /// <remarks>
    /// Not decoration. MusicBrainz requires an application-specific User-Agent
    /// carrying contact details and blocks generic ones outright — a client
    /// that identifies itself as a library name is indistinguishable from every
    /// other user of that library, which is exactly what their rule exists to
    /// prevent. Empty means lookups are refused locally, because the
    /// alternative is getting an address blocked to find out.
    /// </remarks>
    public string Contact { get; set; } = string.Empty;

    /// <summary>Whether <see cref="Server"/> is the public MusicBrainz instance.</summary>
    public bool IsOfficialServer =>
        string.Equals(Server.Host, OfficialHost, StringComparison.OrdinalIgnoreCase);
}
