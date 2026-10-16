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
    /// One a second. LRCLIB publishes no limit, and slower buys nothing: its
    /// limiter, which is not in its published source, turned away a third to
    /// half of the asks at every pace measured from one a second to one in
    /// six. The retries wait their turn here like any request, and how they
    /// wait is in <c>AddLrclib</c>.
    /// </remarks>
    public TimeSpan MinimumRequestInterval { get; set; } = TimeSpan.FromSeconds(1);
}
