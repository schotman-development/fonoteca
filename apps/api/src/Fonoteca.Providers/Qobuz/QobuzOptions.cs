namespace Fonoteca.Providers.Qobuz;

/// <summary>How to talk to Qobuz.</summary>
/// <remarks>
/// Separate from the application's own <c>FonotecaOptions</c> for
/// <see cref="AcoustId.AcoustIdOptions"/>'s reason.
///
/// <b>This is a reverse-engineered API with no published contract.</b> Qobuz do
/// not document it and are free to change it without notice, which is why the
/// application id and the secret are configuration rather than constants: when
/// they rotate, the fix is an environment variable and not a release.
/// </remarks>
public sealed class QobuzOptions
{
    /// <summary>Name of the configured <c>HttpClient</c>; the gate and the retries hang off it.</summary>
    public const string HttpClientName = "qobuz";

    /// <summary>The web player's application id, sent as <c>X-App-Id</c>.</summary>
    public string AppId { get; set; } = string.Empty;

    /// <summary>
    /// The application secret, which never leaves this process.
    /// </summary>
    /// <remarks>
    /// Not a credential for the account — it is the key <c>track/getFileUrl</c>
    /// requests are signed with, and without it that one call is refused while
    /// every other call works. Search and album lookups therefore keep working
    /// with it unset, which is exactly the shape of failure worth naming in a
    /// message rather than discovering as "invalid request signature".
    /// </remarks>
    public string AppSecret { get; set; } = string.Empty;

    /// <summary>The subscriber's token, sent as <c>X-User-Auth-Token</c>.</summary>
    /// <remarks>
    /// Obtained once by hand and pasted into configuration. There is no login
    /// call here on purpose: a password in this process is a password this
    /// process can leak, and the token is what the API actually wants.
    /// </remarks>
    public string UserAuthToken { get; set; } = string.Empty;

    /// <summary>
    /// Whether Qobuz can be asked anything at all.
    /// </summary>
    /// <remarks>
    /// The two credentials every call needs. <see cref="AppSecret"/> is
    /// deliberately not among them: it is only needed to sign a download, so an
    /// installation without it can still search and browse — which is the
    /// distinction <c>QobuzStatusResponse</c> reports as two separate fields.
    /// </remarks>
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(AppId) && !string.IsNullOrWhiteSpace(UserAuthToken);

    /// <summary>The web service root. The trailing slash matters; requests are relative to it.</summary>
    public Uri BaseAddress { get; set; } = new("https://www.qobuz.com/api.json/0.2/");

    /// <summary>
    /// Which encoding to ask for: 5 MP3 320, 6 FLAC 16/44.1, 7 FLAC ≤96kHz, 27 FLAC ≤192kHz.
    /// </summary>
    /// <remarks>
    /// 27 by default and it costs nothing to ask high: Qobuz serves the best
    /// encoding the release <i>has</i> up to the one requested, so 27 against a
    /// CD-quality release returns CD quality rather than an error. The response
    /// states what actually came back, which is what gets recorded.
    /// </remarks>
    public int FormatId { get; set; } = 27;

    /// <summary>
    /// Least time between requests.
    /// </summary>
    /// <remarks>
    /// Qobuz publish no limit, so this is a bound chosen rather than discovered.
    /// One second, because the thing at risk here is not a retry — it is a paid
    /// subscription that a burst of traffic gets suspended.
    /// </remarks>
    public TimeSpan MinimumRequestInterval { get; set; } = TimeSpan.FromSeconds(1);
}
