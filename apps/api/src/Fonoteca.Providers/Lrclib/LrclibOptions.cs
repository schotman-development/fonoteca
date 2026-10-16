namespace Fonoteca.Providers.Lrclib;

/// <summary>How to talk to LRCLIB.</summary>
public sealed class LrclibOptions
{
    /// <summary>Name of the configured <c>HttpClient</c>; the gate and the retries hang off it.</summary>
    public const string HttpClientName = "lrclib";

    public static readonly Uri Server = new("https://lrclib.net/");

    /// <summary>Sent in the User-Agent; the MusicBrainz contact, as for the other public sources.</summary>
    public string Contact { get; set; } = string.Empty;

    /// <summary>
    /// Least time between requests.
    /// </summary>
    /// <remarks>
    /// One a second. LRCLIB publishes no limit and throttles nobody by name: a
    /// busy server answers anyone 503 with <c>Retry-After: 1</c>, and did for
    /// half of a first sample here even at this pace. The retries wait their
    /// turn here like any request.
    /// </remarks>
    public TimeSpan MinimumRequestInterval { get; set; } = TimeSpan.FromSeconds(1);
}
