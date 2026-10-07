namespace Fonoteca.Providers.AppleMusic;

/// <summary>How to talk to Apple Music's public pages.</summary>
/// <remarks>
/// Three hosts, three clients, because they are three different services with
/// three different costs: the documented search API, the album pages a browser
/// reads, and the video CDN. Nothing here needs a credential — see ADR 0014 for
/// why the web player's own key is not used.
/// </remarks>
public sealed class AppleMusicOptions
{
    /// <summary>The search and lookup API at itunes.apple.com; the gate hangs off it.</summary>
    public const string HttpClientName = "applemusic";

    /// <summary>The album pages at music.apple.com.</summary>
    public const string PageHttpClientName = "applemusic-pages";

    /// <summary>The video CDN, which gets neither gate nor retries.</summary>
    public const string VideoHttpClientName = "applemusic-video";

    public static readonly Uri SearchServer = new("https://itunes.apple.com/");

    public static readonly Uri PageServer = new("https://music.apple.com/");

    /// <summary>The shop asked first: the largest catalogue, and one video per record wherever it is sold.</summary>
    public const string Storefront = "us";

    /// <summary>
    /// A second shop, asked only where the first identified no record at all.
    /// </summary>
    /// <remarks>
    /// Empty asks the US shop alone. A pressing sold in one country is listed in
    /// that country's shop only, while a video is the same in every shop that
    /// sells the record — so a record the US shop already knows is never asked
    /// about again here.
    /// </remarks>
    public string FallbackStorefront { get; set; } = string.Empty;

    /// <summary>Sent in the User-Agent; the MusicBrainz contact, as for the other public sources.</summary>
    public string Contact { get; set; } = string.Empty;

    /// <summary>
    /// Least time between search API requests.
    /// </summary>
    /// <remarks>
    /// Three seconds: Apple documents the search API as limited to
    /// "approximately 20 calls per minute (subject to change)".
    /// </remarks>
    public TimeSpan MinimumRequestInterval { get; set; } = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Least time between album page requests.
    /// </summary>
    /// <remarks>
    /// Undocumented, so a browser's pace rather than a script's: a page is read
    /// only for a record already identified, at most a few per album.
    /// </remarks>
    public TimeSpan PageRequestInterval { get; set; } = TimeSpan.FromSeconds(1);
}
