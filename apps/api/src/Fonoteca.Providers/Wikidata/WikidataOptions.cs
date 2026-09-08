namespace Fonoteca.Providers.Wikidata;

/// <summary>How to talk to the Wikidata Query Service.</summary>
public sealed class WikidataOptions
{
    /// <summary>Name of the configured <c>HttpClient</c>; the gate and the retries hang off it.</summary>
    public const string HttpClientName = "wikidata";

    /// <summary>
    /// The SPARQL endpoint.
    /// </summary>
    /// <remarks>
    /// Fixed, unlike <c>MusicBrainzOptions.Server</c> beside it, and the
    /// difference is that a mirror is the answer to a rate limit nobody here
    /// has: the whole of a library is a dozen requests. A settable property
    /// nothing sets and nothing validates is a constant wearing configuration's
    /// clothes.
    /// </remarks>
    public static readonly Uri Server = new("https://query.wikidata.org/");

    /// <summary>
    /// Least time between requests.
    /// </summary>
    /// <remarks>
    /// A second, which is the same interval MusicBrainz asks for and more than
    /// Wikimedia publish for this endpoint. It costs nothing to honour: the
    /// whole library is a dozen requests, so the entire pass spends twelve
    /// seconds at this gate.
    /// </remarks>
    public TimeSpan MinimumRequestInterval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Artists asked about in one query.
    /// </summary>
    /// <remarks>
    /// <b>The bound that matters is this client's attempt timeout, not the
    /// endpoint's query budget.</b> Wikidata allows a query 60 seconds;
    /// <c>AddResilienceThenGate</c> gives an attempt 30 and the whole request 90,
    /// so a batch the server would have finished in 40 is cancelled here and
    /// <i>retried</i> — spending another full query on the far end for nothing,
    /// which is the traffic pattern this file's other remarks warn about.
    ///
    /// Measured, 307 ids came back in 13 seconds. Two hundred and fifty leaves
    /// better than a factor of two under the attempt timeout and makes the whole
    /// of a 2,900-artist library twelve requests. Raising it trades that margin
    /// for requests nobody is waiting on.
    ///
    /// <b>The request is a POST for this reason.</b> Three hundred ids is a
    /// 12 KB query string, which the endpoint answers with an empty body and no
    /// error — measured, and it looks exactly like "no artist has a picture".
    /// </remarks>
    public int BatchSize { get; set; } = 250;

    /// <summary>
    /// A URL or email address Wikimedia can use to reach whoever is running this.
    /// </summary>
    /// <remarks>
    /// Their user-agent policy is MusicBrainz's, for the same reason and with
    /// the same consequence. Empty means the lookup is refused locally rather
    /// than sent anonymously to find out.
    /// </remarks>
    public string Contact { get; set; } = string.Empty;
}
