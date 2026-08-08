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
        EventId = 1100,
        Level = LogLevel.Information,
        Message = "Heartbeat service started, interval {IntervalSeconds}s")]
    public static partial void HeartbeatStarted(ILogger logger, double intervalSeconds);
}
