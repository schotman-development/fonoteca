namespace Fonoteca.Providers.AudioDb;

/// <summary>How to talk to TheAudioDB.</summary>
public sealed class AudioDbOptions
{
    /// <summary>Name of the configured <c>HttpClient</c>; the gate and the retries hang off it.</summary>
    public const string HttpClientName = "audiodb";

    public static readonly Uri Server = new("https://www.theaudiodb.com/");

    /// <summary>
    /// Their API key.
    /// </summary>
    /// <remarks>
    /// <b>The default is TheAudioDB's own published test key</b>, which is what
    /// makes this source work with nothing configured. It is rate-limited and
    /// shared with everyone else using it, so a run against it will occasionally
    /// be refused — which the pass treats as "this source is down", falls
    /// through, and retries next time. Their Patreon supporters get a private
    /// key; setting <c>Fonoteca:Providers:AudioDb:ApiKey</c> to one is the
    /// difference between a source that usually answers and one that always
    /// does.
    ///
    /// It is a key rather than a credential — it identifies the caller, carries
    /// no account and buys nothing that costs money — so unlike
    /// <c>AcoustIdApiKey</c> a missing one is not worth refusing the lookup
    /// over. There is a working default; there is no way to be anonymous here.
    /// </remarks>
    public string ApiKey { get; set; } = "2";

    /// <summary>
    /// Least time between requests.
    /// </summary>
    /// <remarks>
    /// Half a second. Their published free-tier limit for the test key is two
    /// requests a second, and the gate is the only thing standing between a
    /// 326-artist shelf and finding out what happens above it.
    /// </remarks>
    public TimeSpan MinimumRequestInterval { get; set; } = TimeSpan.FromMilliseconds(500);
}
