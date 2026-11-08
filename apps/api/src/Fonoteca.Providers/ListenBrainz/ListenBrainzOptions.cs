namespace Fonoteca.Providers.ListenBrainz;

/// <summary>How to talk to ListenBrainz.</summary>
public sealed class ListenBrainzOptions
{
    /// <summary>Name of the configured <c>HttpClient</c>; the gate and the retries hang off it.</summary>
    public const string HttpClientName = "listenbrainz";

    public static readonly Uri Server = new("https://api.listenbrainz.org/");

    /// <summary>Sent in the User-Agent, which ListenBrainz requires; the MusicBrainz contact, as for the other MetaBrainz service.</summary>
    public string Contact { get; set; } = string.Empty;

    /// <summary>
    /// Least time between requests: one a second, ListenBrainz's own guidance
    /// for a client that does not read its rate-limit headers.
    /// </summary>
    public TimeSpan MinimumRequestInterval { get; set; } = TimeSpan.FromSeconds(1);
}
