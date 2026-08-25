namespace Fonoteca.Providers.AcoustId;

/// <summary>How to talk to AcoustID.</summary>
/// <remarks>
/// Separate from the application's own <c>FonotecaOptions</c> on purpose. This
/// project knows what AcoustID needs; it does not know what a Fonoteca
/// configuration file looks like, and an adapter that reads the host's settings
/// section directly is one that cannot be lifted out of it.
/// </remarks>
public sealed class AcoustIdOptions
{
    /// <summary>Name of the configured <c>HttpClient</c>; the gate and the retries hang off it.</summary>
    public const string HttpClientName = "acoustid";

    /// <summary>
    /// The application API key.
    /// </summary>
    /// <remarks>
    /// Free, per-application, from <c>https://acoustid.org/new-application</c>.
    /// Empty is a supported state — the rest of Fonoteca works without it — and
    /// a lookup attempted without one is refused locally rather than sent, so
    /// the failure names the setting instead of returning "invalid API key".
    /// </remarks>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>
    /// The operator's own user API key, which submissions need and lookups do not.
    /// </summary>
    /// <remarks>
    /// A second key, from the account page at <c>https://acoustid.org/</c> once
    /// signed in, and not interchangeable with <see cref="ApiKey"/>: that one
    /// says which application is calling, this one says who is making the claim.
    /// AcoustID's documentation asks that it not be baked into an application —
    /// "each user should provide their own" — which for a self-hosted library
    /// manager is satisfied exactly by it being configuration the operator sets,
    /// since the operator is the user.
    ///
    /// Empty is a supported state, as with <see cref="ApiKey"/>: everything else
    /// works, and a submission attempted without one is refused here rather than
    /// sent, so the message names the setting.
    /// </remarks>
    public string UserKey { get; set; } = string.Empty;

    /// <summary>The web service root. The trailing slash matters; requests are relative to it.</summary>
    public Uri BaseAddress { get; set; } = new("https://api.acoustid.org/v2/");

    /// <summary>
    /// Least time between requests. Their guideline is three per second.
    /// </summary>
    /// <remarks>
    /// 340ms rather than 333ms, because the limit is enforced on their clock,
    /// not ours, and the cost of being 2% slow is nothing next to the cost of
    /// being 1% fast.
    /// </remarks>
    public TimeSpan MinimumRequestInterval { get; set; } = TimeSpan.FromMilliseconds(340);

    /// <summary>
    /// Gzip the request body.
    /// </summary>
    /// <remarks>
    /// On by default because AcoustID asks for it: a full-length fingerprint is
    /// several kilobytes of base64 and their documentation states compressed
    /// POSTs are preferred. A switch rather than a constant so a proxy that
    /// mangles <c>Content-Encoding</c> on requests can be worked around without
    /// a rebuild.
    /// </remarks>
    public bool CompressRequests { get; set; } = true;
}
