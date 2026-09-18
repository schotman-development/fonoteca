using Microsoft.Extensions.Logging;

namespace Fonoteca.Providers.Logging;

/// <summary>
/// Source-generated log messages for the external-service adapters.
/// </summary>
/// <remarks>
/// Same reasoning as <c>Fonoteca.Api.Logging.Log</c>, and the same EventId map,
/// continued here because these live in another assembly:
///   1300-1349  AcoustID
///   1350-1399  MusicBrainz
///   1400-1449  Qobuz
///   1450-1499  Wikidata
///
/// Deliberately sparse. An identification pass makes one of these calls per
/// file, so anything logged per lookup is logged 100,000 times — Debug is where
/// per-call detail belongs. Failures are not logged here at all: they are
/// thrown, carrying the same text, and logging them on the way out would put
/// every one in the log twice with no more information the second time.
/// </remarks>
internal static partial class ProviderLog
{
    [LoggerMessage(
        EventId = 1300,
        Level = LogLevel.Debug,
        Message = "AcoustID matched {Matches} clusters for a {DurationSeconds}s fingerprint")]
    public static partial void AcoustIdMatched(ILogger logger, int matches, int durationSeconds);

    [LoggerMessage(
        EventId = 1310,
        Level = LogLevel.Information,
        Message = "AcoustID accepted {Submissions} fingerprint submissions")]
    public static partial void AcoustIdSubmitted(ILogger logger, int submissions);

    [LoggerMessage(
        EventId = 1350,
        Level = LogLevel.Debug,
        Message = "MusicBrainz returned {EntityType} {Mbid}")]
    public static partial void MusicBrainzLookedUp(ILogger logger, string entityType, Guid mbid);

    [LoggerMessage(
        EventId = 1351,
        Level = LogLevel.Debug,
        Message = "MusicBrainz has no {EntityType} {Mbid}")]
    public static partial void MusicBrainzNotFound(ILogger logger, string entityType, Guid mbid);

    [LoggerMessage(
        EventId = 1353,
        Level = LogLevel.Debug,
        Message = "MusicBrainz search for {Query} returned {Matches} releases")]
    public static partial void MusicBrainzSearched(ILogger logger, string query, int matches);

    [LoggerMessage(
        EventId = 1352,
        Level = LogLevel.Information,
        Message = "MusicBrainz client configured for {Server}, one request every {IntervalMs}ms, "
            + "identifying as {UserAgent}")]
    public static partial void MusicBrainzConfigured(
        ILogger logger,
        string server,
        double intervalMs,
        string userAgent);

    [LoggerMessage(
        EventId = 1400,
        Level = LogLevel.Debug,
        Message = "Qobuz search for {Query} returned {Matches} albums")]
    public static partial void QobuzSearched(ILogger logger, string query, int matches);

    /// <remarks>
    /// Warning, and rare by construction: it means Qobuz named the right artist
    /// and gave a picture on a host this application will not serve. Either
    /// their CDN moved or something is wrong, and both are worth one line.
    /// </remarks>
    [LoggerMessage(
        EventId = 1401,
        Level = LogLevel.Warning,
        Message = "Qobuz's picture for {Artist} was not served from the expected host: {Url}")]
    public static partial void QobuzPortraitRejected(ILogger logger, string artist, string url);

    /// <remarks>
    /// Information rather than Warning: two people sharing a name is the world
    /// being as it is, not something going wrong. It is worth a line because the
    /// consequence — this artist keeps the lesser picture — is otherwise
    /// invisible.
    /// </remarks>
    [LoggerMessage(
        EventId = 1402,
        Level = LogLevel.Information,
        Message = "Qobuz has {Matches} artists called {Artist}; none of them can be assumed to "
            + "be this one")]
    public static partial void QobuzArtistAmbiguous(ILogger logger, string artist, int matches);

    /// <remarks>
    /// Debug, because it is the ordinary outcome rather than a problem: a shop
    /// carries records by the artists it sells, and a followed artist it has
    /// never heard of is an answer. Counted at the caller, where a run's worth of
    /// them becomes one summary line instead of one line per artist.
    /// </remarks>
    [LoggerMessage(
        EventId = 1403,
        Level = LogLevel.Debug,
        Message = "Qobuz carries no artist called {Artist}, so nothing was asked about releases")]
    public static partial void QobuzArtistNotFound(ILogger logger, string artist);

    /// <remarks>
    /// <b>The cut is worth a line because it is invisible otherwise.</b> Qobuz
    /// report their own total, and one real artist measured at 166 albums against
    /// a 100-row page — a caller that read only the rows would treat part of a
    /// discography as all of it and report nothing missing.
    /// </remarks>
    [LoggerMessage(
        EventId = 1404,
        Level = LogLevel.Debug,
        Message = "Qobuz returned {Returned} of {Total} albums for {Artist}")]
    public static partial void QobuzArtistAlbums(
        ILogger logger,
        string artist,
        int returned,
        int total);

    /// <remarks>
    /// Debug and not a warning: an album the shop does not carry, or carries
    /// under a name that does not fold to ours, is the ordinary answer. What
    /// makes it worth a line at all is that the alternative — a cover appearing
    /// from nowhere — leaves no trace of <i>which</i> record it was taken from,
    /// and that is the one thing a wrong sleeve needs explaining by.
    /// </remarks>
    [LoggerMessage(
        EventId = 1405,
        Level = LogLevel.Debug,
        Message = "Qobuz offered no cover for {Artist} - {Title}: {Searched} albums searched, "
            + "none matched by barcode or by title")]
    public static partial void QobuzCoverUnmatched(
        ILogger logger,
        string? artist,
        string title,
        int searched);

    [LoggerMessage(
        EventId = 1406,
        Level = LogLevel.Debug,
        Message = "Qobuz album {Album} supplies the cover for {Artist} - {Title}, matched by {How}")]
    public static partial void QobuzCoverMatched(
        ILogger logger,
        string album,
        string? artist,
        string title,
        string how);

    /// <remarks>
    /// A warning, unlike its portrait twin: a picture served from somewhere that
    /// is not their CDN, or under a type this application will not serve, is the
    /// shape of a redirect landing somewhere unexpected — and the bytes were
    /// about to be stored and handed to a browser.
    /// </remarks>
    [LoggerMessage(
        EventId = 1407,
        Level = LogLevel.Warning,
        Message = "Qobuz offered {Url} as a cover, which is not a picture this application "
            + "will serve")]
    public static partial void QobuzCoverRejected(ILogger logger, string url);

    [LoggerMessage(
        EventId = 1430,
        Level = LogLevel.Warning,
        Message = "TheAudioDB offered {Url} for {Artist}, which is not a picture this application "
            + "will serve")]
    public static partial void AudioDbPortraitRejected(ILogger logger, string artist, string url);

    [LoggerMessage(
        EventId = 1450,
        Level = LogLevel.Information,
        Message = "Wikidata has a picture for {Found} of {Asked} artists")]
    public static partial void WikidataPortraitsFound(ILogger logger, int found, int asked);
}
