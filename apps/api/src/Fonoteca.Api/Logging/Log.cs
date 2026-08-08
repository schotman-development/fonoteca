namespace Fonoteca.Api.Logging;

/// <summary>
/// Source-generated log messages.
/// </summary>
/// <remarks>
/// <c>[LoggerMessage]</c> generates a strongly-typed, allocation-free method per
/// entry and checks whether the level is enabled before evaluating arguments.
/// That is mostly a performance concern, but it also gives every message a
/// stable <c>EventId</c> — which is what makes logs from a six-hour scan
/// filterable rather than a wall of prose.
///
/// EventId ranges, so a message's origin is obvious from its id alone:
///   1000-1099  startup and host lifecycle
///   1100-1199  realtime / SignalR
///   1200-1299  library scanning
///   1300-1399  external providers — in Fonoteca.Providers.Logging.ProviderLog,
///              another assembly, but the same numbering
/// </remarks>
internal static partial class Log
{
    [LoggerMessage(
        EventId = 1000,
        Level = LogLevel.Information,
        Message = "Database migrations applied")]
    public static partial void MigrationsApplied(ILogger logger);

    [LoggerMessage(
        EventId = 1001,
        Level = LogLevel.Warning,
        Message = "Library path {LibraryPath} does not exist. Scanning will find nothing.")]
    public static partial void LibraryPathMissing(ILogger logger, string libraryPath);

    [LoggerMessage(
        EventId = 1002,
        Level = LogLevel.Information,
        Message = "File mutation is DISABLED. No audio file will be modified. "
            + "Set Fonoteca:AllowFileMutation to enable, once the verified write path exists.")]
    public static partial void FileMutationDisabled(ILogger logger);

    [LoggerMessage(
        EventId = 1003,
        Level = LogLevel.Information,
        Message = "Design-time host detected; skipping migrations and startup checks.")]
    public static partial void DesignTimeStartupSkipped(ILogger logger);

    [LoggerMessage(
        EventId = 1004,
        Level = LogLevel.Information,
        Message = "AcoustID has no API key (Fonoteca:AcoustIdApiKey is empty). Fingerprint "
            + "lookups will be refused. Keys are free for non-commercial use from "
            + "https://acoustid.org/new-application.")]
    public static partial void AcoustIdNotConfigured(ILogger logger);

    [LoggerMessage(
        EventId = 1005,
        Level = LogLevel.Information,
        Message = "MusicBrainz has no contact (Fonoteca:MusicBrainzContact is empty). Lookups "
            + "will be refused rather than sent unidentified, which MusicBrainz blocks addresses "
            + "for. Set it to a URL or an email address.")]
    public static partial void MusicBrainzNotConfigured(ILogger logger);

    [LoggerMessage(
        EventId = 1100,
        Level = LogLevel.Information,
        Message = "Heartbeat service started, interval {IntervalSeconds}s")]
    public static partial void HeartbeatStarted(ILogger logger, double intervalSeconds);

    [LoggerMessage(
        EventId = 1200,
        Level = LogLevel.Information,
        Message = "Library scan started at {LibraryRoot}")]
    public static partial void ScanStarted(ILogger logger, string libraryRoot);

    [LoggerMessage(
        EventId = 1201,
        Level = LogLevel.Information,
        Message = "Library scan finished: {FilesSeen} files seen, {Added} added, {Updated} updated, "
            + "{Unchanged} unchanged, {Removed} removed, in {ElapsedMs}ms")]
    public static partial void ScanCompleted(
        ILogger logger,
        int filesSeen,
        int added,
        int updated,
        int unchanged,
        int removed,
        long elapsedMs);

    [LoggerMessage(
        EventId = 1202,
        Level = LogLevel.Warning,
        Message = "Library scan refused: {LibraryRoot} does not exist. Nothing was changed.")]
    public static partial void ScanRootMissing(ILogger logger, string libraryRoot);

    [LoggerMessage(
        EventId = 1203,
        Level = LogLevel.Warning,
        Message = "Library scan found no files under {LibraryRoot} while the catalogue holds "
            + "{KnownFiles}. Treating this as an unmounted library and removing nothing.")]
    public static partial void ScanFoundNothing(ILogger logger, string libraryRoot, int knownFiles);

    [LoggerMessage(
        EventId = 1205,
        Level = LogLevel.Warning,
        Message = "Library scan could not read {UnreadableDirectories} directories, so it saw an "
            + "incomplete library. {MissingFiles} catalogued files were not found and none were "
            + "removed; fix the permissions and scan again.")]
    public static partial void ScanWalkIncomplete(
        ILogger logger,
        int unreadableDirectories,
        int missingFiles);

    [LoggerMessage(
        EventId = 1204,
        Level = LogLevel.Information,
        Message = "Library scan requested while one is already running; the request was rejected.")]
    public static partial void ScanAlreadyRunning(ILogger logger);
}
