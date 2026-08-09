using System.Net;
using Fonoteca.Domain.Abstractions;
using Fonoteca.Domain.Catalogue;
using Fonoteca.Providers.Logging;
using MetaBrainz.Common;
using MetaBrainz.MusicBrainz;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Polly;

namespace Fonoteca.Providers.MusicBrainz;

/// <summary>
/// <see cref="IMusicBrainzCatalogue"/> over the MusicBrainz WS/2 API.
/// </summary>
/// <remarks>
/// Wraps <c>MetaBrainz.MusicBrainz</c>, whose author also maintains the
/// MusicBrainz server, rather than hand-rolling WS/2 parsing — the schema is
/// forty entity types deep and every one of them is somebody's edge case.
///
/// What is <i>not</i> delegated to it is rate limiting. See the constructor.
/// </remarks>
public sealed class MusicBrainzCatalogue : IMusicBrainzCatalogue, IDisposable
{
    /// <summary>Name used in <see cref="ProviderException.Provider"/> and in log messages.</summary>
    public const string ProviderName = "MusicBrainz";

    /// <summary>
    /// Everything one round trip can answer about a recording.
    /// </summary>
    /// <remarks>
    /// Deliberately greedy. Each of these could be a separate request, and at
    /// one request per second that would turn identifying a track into four
    /// seconds instead of one. The response is larger; the wall clock is what
    /// is actually scarce.
    /// </remarks>
    private const Include RecordingIncludes =
        Include.Artists
        | Include.ArtistCredits
        | Include.Releases
        | Include.ReleaseGroups
        | Include.Media
        | Include.Isrcs
        // The people the credit line does not name. A classical recording is
        // billed to its composer, and the conductor, the orchestra and the
        // soloists are relationships — so without these two the recording is
        // unfindable under anyone who actually played it. The work link is a
        // stub; its composer needs the separate lookup below.
        | Include.ArtistRelationships
        | Include.WorkRelationships;

    /// <summary>
    /// A work is asked only for who wrote it.
    /// </summary>
    /// <remarks>
    /// No <c>Include.Recordings</c>: a popular work has thousands, they arrive
    /// paginated, and the question here is the composer.
    /// </remarks>
    private const Include WorkIncludes = Include.ArtistRelationships;

    private const Include ReleaseIncludes =
        Include.Artists
        | Include.ArtistCredits
        | Include.Recordings
        | Include.ReleaseGroups
        | Include.Media
        | Include.Labels;

    private readonly Query _query;
    private readonly MusicBrainzOptions _config;
    private readonly ILogger<MusicBrainzCatalogue> _logger;

    public MusicBrainzCatalogue(
        IHttpClientFactory clients,
        IOptions<MusicBrainzOptions> options,
        ILogger<MusicBrainzCatalogue> logger)
    {
        ArgumentNullException.ThrowIfNull(clients);
        ArgumentNullException.ThrowIfNull(options);

        _config = options.Value;
        _logger = logger;

        // MetaBrainz throttles through a STATIC, process-wide delay. Turned off
        // here and replaced by the RequestGate on the named client below, for
        // three reasons: a static cannot be configured per server, so a mirror
        // and the official instance could not have different limits; a static
        // cannot be substituted in a test, so every test that touched this
        // would wait a real second; and it only covers requests this library
        // makes, while the gate sits in the message pipeline and covers
        // retries the resilience handler makes on its own.
        //
        // Removing this line without the gate in place is how an address gets
        // blocked by MusicBrainz.
        Query.DelayBetweenRequests = 0;

        _query = new Query(
            clients.CreateClient(MusicBrainzOptions.HttpClientName),
            takeOwnership: false)
        {
            UrlScheme = _config.Server.Scheme,
            Server = _config.Server.Host,
            Port = _config.Server.IsDefaultPort ? -1 : _config.Server.Port,
        };

        var userAgent = UserAgentFor(_config);

        ProviderLog.MusicBrainzConfigured(
            logger,
            _config.Server.Host,
            _config.MinimumRequestInterval.TotalMilliseconds,
            userAgent);
    }

    /// <summary>The User-Agent MusicBrainz requires: application, version, and a way to be reached.</summary>
    public static string UserAgentFor(MusicBrainzOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return $"{options.ApplicationName}/{options.ApplicationVersion} ( {options.Contact} )";
    }

    public Task<MusicBrainzRecording?> GetRecordingAsync(
        Mbid id,
        CancellationToken cancellationToken = default) =>
        LookupAsync(
            "recording",
            id,
            async token => MusicBrainzMapper.ToRecording(
                await _query.LookupRecordingAsync(id.Value, RecordingIncludes, cancellationToken: token)
                    .ConfigureAwait(false)),
            cancellationToken);

    public Task<MusicBrainzRelease?> GetReleaseAsync(
        Mbid id,
        CancellationToken cancellationToken = default) =>
        LookupAsync(
            "release",
            id,
            async token => MusicBrainzMapper.ToRelease(
                await _query.LookupReleaseAsync(id.Value, ReleaseIncludes, token)
                    .ConfigureAwait(false)),
            cancellationToken);

    public Task<MusicBrainzWork?> GetWorkAsync(
        Mbid id,
        CancellationToken cancellationToken = default) =>
        LookupAsync(
            "work",
            id,
            async token => MusicBrainzMapper.ToWork(
                await _query.LookupWorkAsync(id.Value, WorkIncludes, token)
                    .ConfigureAwait(false)),
            cancellationToken);

    public void Dispose() => _query.Dispose();

    private async Task<T?> LookupAsync<T>(
        string entityType,
        Mbid id,
        Func<CancellationToken, Task<T>> lookup,
        CancellationToken cancellationToken)
        where T : class
    {
        if (string.IsNullOrWhiteSpace(_config.Contact))
        {
            // Refused locally. Sending an unidentified request works exactly
            // once per address, and the punishment lands on whoever is running
            // this rather than on the code that caused it.
            throw new ProviderRejectedException(
                ProviderName,
                "No MusicBrainz contact is configured. Set Fonoteca:MusicBrainzContact to a URL or "
                + "email address — MusicBrainz requires every client to identify itself and blocks "
                + "those that do not.");
        }

        try
        {
            var result = await lookup(cancellationToken).ConfigureAwait(false);
            ProviderLog.MusicBrainzLookedUp(_logger, entityType, id.Value);
            return result;
        }
        catch (HttpError error) when (error.Status == HttpStatusCode.NotFound)
        {
            // Not a failure. An MBID AcoustID still points at can have been
            // merged away hours ago, and "MusicBrainz no longer has this" is an
            // answer the caller can act on.
            ProviderLog.MusicBrainzNotFound(_logger, entityType, id.Value);
            return null;
        }
        catch (HttpError error)
        {
            throw Translate(error);
        }
        catch (HttpRequestException cause)
        {
            throw Unavailable(cause.Message, cause);
        }
        catch (TaskCanceledException cause) when (!cancellationToken.IsCancellationRequested)
        {
            throw Unavailable("the request timed out", cause);
        }
        catch (ExecutionRejectedException cause)
        {
            // An open circuit, or the resilience pipeline's total timeout.
            throw Unavailable(cause.Message, cause);
        }
    }

    private static ProviderException Translate(HttpError error)
    {
        var status = (int)error.Status;

        // 503 is what MusicBrainz returns when a client has outrun the rate
        // limit, which makes it the single most likely error here and firmly a
        // "come back later" rather than anything the caller did wrong.
        var transient = status >= 500
            || error.Status is HttpStatusCode.TooManyRequests or HttpStatusCode.RequestTimeout;

        var reason = $"MusicBrainz returned {status} {error.Status}: {error.Reason ?? error.Message}";

        return transient
            ? new ProviderUnavailableException(ProviderName, reason, error)
            : new ProviderRejectedException(ProviderName, reason, error);
    }

    private static ProviderUnavailableException Unavailable(string reason, Exception cause) =>
        new(ProviderName, $"MusicBrainz lookup failed: {reason}.", cause);
}
