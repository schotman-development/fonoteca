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
}
