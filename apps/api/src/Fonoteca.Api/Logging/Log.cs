using Fonoteca.Domain.Catalogue;

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
///   1200-1219  library scanning
///   1220-1249  identification — fingerprinting, AcoustID, tag writing
///   1250-1279  enrichment — recordings, works and artists from MusicBrainz
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

    [LoggerMessage(
        EventId = 1220,
        Level = LogLevel.Information,
        Message = "Identification started ({JobId}): {Pending} files have no AcoustID yet. "
            + "Writing tags is {WriteMode}.")]
    public static partial void IdentificationStarted(
        ILogger logger,
        string jobId,
        int pending,
        string writeMode);

    [LoggerMessage(
        EventId = 1221,
        Level = LogLevel.Information,
        Message = "Identification finished ({JobId}): {Identified} identified, {Adopted} adopted "
            + "from existing tags, {Unknown} unknown to AcoustID, {Ambiguous} ambiguous, "
            + "{Tagged} tagged, {WriteRefused} would be tagged, {Failed} failed, in {ElapsedMs}ms")]
    public static partial void IdentificationCompleted(
        ILogger logger,
        string jobId,
        int identified,
        int adopted,
        int unknown,
        int ambiguous,
        int tagged,
        int writeRefused,
        int failed,
        long elapsedMs);

    [LoggerMessage(
        EventId = 1222,
        Level = LogLevel.Information,
        Message = "Identification requested while {ActiveKind} is running; the request was rejected.")]
    public static partial void IdentificationBusy(ILogger logger, string activeKind);

    /// <summary>
    /// Logged once per run, not once per file.
    /// </summary>
    /// <remarks>
    /// 7,735 identical warnings is not a log, it is a denial of service against
    /// whoever has to read it. The count goes in the summary above instead.
    /// </remarks>
    [LoggerMessage(
        EventId = 1223,
        Level = LogLevel.Information,
        Message = "Identification is not writing tags: Fonoteca:AllowFileMutation is false. "
            + "Fingerprints and AcoustIDs are still being stored, so enabling it and running "
            + "again costs no further lookups.")]
    public static partial void IdentificationWillNotWrite(ILogger logger);

    [LoggerMessage(
        EventId = 1224,
        Level = LogLevel.Warning,
        Message = "Identification stopped early: {Reason}")]
    public static partial void IdentificationAborted(ILogger logger, string reason);

    [LoggerMessage(
        EventId = 1225,
        Level = LogLevel.Warning,
        Message = "Could not write the AcoustID into {Path}: {Detail}")]
    public static partial void TagWriteFailed(ILogger logger, string path, string? detail);

    [LoggerMessage(
        EventId = 1226,
        Level = LogLevel.Information,
        Message = "Removed {Count} staging files left behind by an interrupted run.")]
    public static partial void StagingFilesSwept(ILogger logger, int count);

    [LoggerMessage(
        EventId = 1227,
        Level = LogLevel.Debug,
        Message = "{Path}: {Outcome} ({Detail})")]
    public static partial void FileIdentified(
        ILogger logger,
        string path,
        AcoustIdOutcome outcome,
        string? detail);

    /// <summary>
    /// The same, with the score unformatted.
    /// </summary>
    /// <remarks>
    /// A separate overload rather than a ToString at the call site: formatting
    /// eagerly costs an allocation per file whether or not debug logging is on,
    /// which over 100,000 files is the difference the source generator exists to
    /// avoid.
    /// </remarks>
    [LoggerMessage(
        EventId = 1228,
        Level = LogLevel.Debug,
        Message = "{Path}: {Outcome} (best score {Score})")]
    public static partial void FileNotIdentified(
        ILogger logger,
        string path,
        AcoustIdOutcome outcome,
        double? score);

    /// <summary>
    /// A file identified but deliberately left alone.
    /// </summary>
    /// <remarks>
    /// Information rather than Warning: nothing failed, and the pass did the
    /// right thing. It is still one line per file rather than a summary count,
    /// because the list is the actionable part — the user needs to know which
    /// files to repair, and there are few enough of them to name.
    /// </remarks>
    [LoggerMessage(
        EventId = 1229,
        Level = LogLevel.Information,
        Message = "{Path} was identified but not tagged: {Library} could not read its tags "
            + "({Cause}). The file is left untouched; repair it and run again, at no further "
            + "lookup cost.")]
    public static partial void FileTagUnreadable(
        ILogger logger,
        string path,
        string library,
        string? cause);

    /// <summary>
    /// The backstop, with the exception attached on purpose.
    /// </summary>
    /// <remarks>
    /// Anything reaching this is by definition unforeseen, so the stack trace is
    /// the only thing that will identify it. The pass carries on; this line is
    /// how the file it skipped stops being invisible.
    /// </remarks>
    [LoggerMessage(
        EventId = 1230,
        Level = LogLevel.Warning,
        Message = "{Path} was skipped: the pass hit an unexpected error on this file and "
            + "continued with the next.")]
    public static partial void FileFailed(ILogger logger, string path, Exception cause);

    [LoggerMessage(
        EventId = 1250,
        Level = LogLevel.Information,
        Message = "Enrichment started ({JobId}): {Pending} identified files have no recording yet.")]
    public static partial void EnrichmentStarted(ILogger logger, string jobId, int pending);

    [LoggerMessage(
        EventId = 1251,
        Level = LogLevel.Information,
        Message = "Enrichment finished ({JobId}): {Linked} linked, {NoRecording} clusters with no "
            + "MusicBrainz recording, {NotFound} recordings merged away, {Failed} failed; "
            + "{Recordings} recordings and {Artists} artists looked up, in {ElapsedMs}ms")]
    public static partial void EnrichmentCompleted(
        ILogger logger,
        string jobId,
        int linked,
        int noRecording,
        int notFound,
        int failed,
        int recordings,
        int artists,
        long elapsedMs);

    [LoggerMessage(
        EventId = 1252,
        Level = LogLevel.Information,
        Message = "Enrichment requested while {ActiveKind} is running; the request was rejected.")]
    public static partial void EnrichmentBusy(ILogger logger, string activeKind);

    [LoggerMessage(
        EventId = 1253,
        Level = LogLevel.Warning,
        Message = "Enrichment stopped early: {Reason}")]
    public static partial void EnrichmentAborted(ILogger logger, string reason);

    /// <summary>
    /// One file that did not resolve.
    /// </summary>
    /// <remarks>
    /// Debug, for the same reason <see cref="FileNotIdentified"/> is: on a first
    /// pass this fires for every file AcoustID has not linked to MusicBrainz, and
    /// the proportions belong in the summary rather than in thousands of lines.
    /// The outcome is on the row either way, so the question is answerable in SQL
    /// afterwards — which is the lesson the identification pass paid for.
    /// </remarks>
    [LoggerMessage(
        EventId = 1254,
        Level = LogLevel.Debug,
        Message = "{Path}: {Outcome} ({Detail})")]
    public static partial void FileNotEnriched(
        ILogger logger,
        string path,
        EnrichmentOutcome outcome,
        string? detail);
}
