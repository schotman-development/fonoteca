namespace Fonoteca.Providers.RadioBrowser;

/// <summary>How to talk to radio-browser.info.</summary>
public sealed class RadioBrowserOptions
{
    /// <summary>Name of the configured <c>HttpClient</c>; the gate and the retries hang off it.</summary>
    public const string HttpClientName = "radio-browser";

    /// <summary>
    /// Every server at once: radio-browser's own instruction is to resolve
    /// this name and use what it lists, never one server by name, since the
    /// names change. The resolver does that, and its certificate covers it.
    /// </summary>
    public static readonly Uri Server = new("https://all.api.radio-browser.info/");

    /// <summary>Sent in the User-Agent; the MusicBrainz contact, as for the other public sources.</summary>
    public string Contact { get; set; } = string.Empty;

    /// <summary>Least time between requests. radio-browser publishes no limit; one a second is the other sources' pace.</summary>
    public TimeSpan MinimumRequestInterval { get; set; } = TimeSpan.FromSeconds(1);
}
