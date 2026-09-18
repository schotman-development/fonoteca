using Fonoteca.Domain.Acquisition;
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
///   1200-1219  library scanning and measuring
///   1220-1249  identification — fingerprinting, AcoustID, tag writing
///   1250-1279  enrichment — recordings, works and artists from MusicBrainz
///   1280-1289  candidate warming — filling the worklist's answers ahead of a click
///   1290-1299  acquisition — manual Qobuz downloads into staging
///   1400-1409  the file manager — trashing, moving and uploading by hand
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
            + "{Recordings} recordings and {Artists} artists looked up, {Described} artists "
            + "described, in {ElapsedMs}ms")]
    public static partial void EnrichmentCompleted(
        ILogger logger,
        string jobId,
        int linked,
        int noRecording,
        int notFound,
        int failed,
        int recordings,
        int artists,
        int described,
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

    [LoggerMessage(
        EventId = 1206,
        Level = LogLevel.Information,
        Message = "Probe started ({JobId}): {Pending} files have never been measured.")]
    public static partial void ProbeStarted(ILogger logger, string jobId, int pending);

    [LoggerMessage(
        EventId = 1207,
        Level = LogLevel.Information,
        Message = "Probe finished ({JobId}): {Measured} measured, {Complained} the decoder "
            + "objected to, {Unreadable} with no audio, {Failed} failed, in {ElapsedMs}ms.")]
    public static partial void ProbeCompleted(
        ILogger logger,
        string jobId,
        int measured,
        int complained,
        int unreadable,
        int failed,
        long elapsedMs);

    [LoggerMessage(
        EventId = 1208,
        Level = LogLevel.Warning,
        Message = "Probe requested while {ActiveKind} is running; the request was rejected.")]
    public static partial void ProbeBusy(ILogger logger, string activeKind);

    [LoggerMessage(
        EventId = 1209,
        Level = LogLevel.Warning,
        Message = "Probe stopped early: {Reason}")]
    public static partial void ProbeAborted(ILogger logger, string reason);

    /// <remarks>
    /// Warning rather than Debug, unlike its identification counterpart: this is
    /// the decoder saying these particular bytes are wrong, and on the target
    /// library it fires on roughly one file in sixty rather than on hundreds.
    /// </remarks>
    [LoggerMessage(
        EventId = 1210,
        Level = LogLevel.Warning,
        Message = "The decoder objected while reading {Path}: {Complaint}")]
    public static partial void FileDecodeComplaint(ILogger logger, string path, string complaint);

    [LoggerMessage(
        EventId = 1295,
        Level = LogLevel.Information,
        Message = "Replacement refused for '{Folder}': {Verdict}. Nothing was moved.")]
    public static partial void ReplacementRefused(
        ILogger logger, string folder, ReplacementVerdict verdict);

    [LoggerMessage(
        EventId = 1294,
        Level = LogLevel.Warning,
        Message = "Retiring '{Folder}': moving {Files} files to '{Destination}'.")]
    public static partial void ReplacementStarting(
        ILogger logger, string folder, string destination, int files);

    [LoggerMessage(
        EventId = 1296,
        Level = LogLevel.Warning,
        Message = "Replaced '{Folder}' with '{DownloadedTo}': {Moved} files moved to the archive.")]
    public static partial void ReplacementDone(
        ILogger logger, string folder, string downloadedTo, int moved);

    [LoggerMessage(
        EventId = 1297,
        Level = LogLevel.Warning,
        Message = "A just-downloaded file could not be measured, so it does not count towards the "
            + "replacement: {Path}")]
    public static partial void ReplacementFileNotMeasured(ILogger logger, string path);

    [LoggerMessage(
        EventId = 1211,
        Level = LogLevel.Debug,
        Message = "{Path} could not be measured: {Reason}")]
    public static partial void FileNotProbed(ILogger logger, string path, string reason);

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

    /// <remarks>
    /// Debug, like <see cref="FileNotEnriched"/> above it and for the same
    /// reason: on a first run over a library this fires for every artist
    /// MusicBrainz cannot describe, and a warning per row would bury the
    /// summary line that says how the run actually went.
    /// </remarks>
    [LoggerMessage(
        EventId = 1255,
        Level = LogLevel.Debug,
        Message = "{Artist} was not described: {Reason}")]
    public static partial void ArtistNotDescribed(ILogger logger, string artist, string reason);

    /// <remarks>
    /// Warning rather than Debug, unlike its neighbours, because its subject is
    /// a batch of up to two hundred and fifty artists rather than one — and
    /// because it ends the stage. One line for the whole of it is not noise.
    /// </remarks>
    [LoggerMessage(
        EventId = 1256,
        Level = LogLevel.Warning,
        Message = "No pictures were found for a batch of {Artists} artists: {Reason}")]
    public static partial void PicturesNotFound(ILogger logger, int artists, string reason);

    /// <remarks>
    /// Debug, like <see cref="ArtistNotDescribed"/>, and for a narrower version
    /// of the same reason: the worklist here is the followed set rather than the
    /// catalogue, so this can never be thousands of lines — but a followed
    /// artist MusicBrainz credits with nothing is an ordinary answer, not a
    /// fault, and logging it louder would make an empty discography read as a
    /// broken one.
    /// </remarks>
    [LoggerMessage(
        EventId = 1257,
        Level = LogLevel.Debug,
        Message = "{Artist}'s discography was not fetched: {Reason}")]
    public static partial void DiscographyNotFetched(ILogger logger, string artist, string reason);

    /// <remarks>
    /// A sibling of <see cref="DiscographyNotFetched"/> rather than the same
    /// event with the provider glued into the reason, and the analyzer is right
    /// to insist: an interpolated argument is built whether or not Debug logging
    /// is on, and this one sits in a loop over every followed artist and every
    /// source. The provider is its own parameter, so nothing is composed unless
    /// the line is actually written.
    ///
    /// Debug, like its neighbour, because a shop being briefly unreachable is an
    /// ordinary event that costs nothing here — the MusicBrainz answer stands,
    /// the artist is still stamped, and the next run asks again.
    /// </remarks>
    [LoggerMessage(
        EventId = 1258,
        Level = LogLevel.Debug,
        Message = "{Provider} was not asked what {Artist} released: {Reason}")]
    public static partial void ReleasesNotDiscovered(
        ILogger logger,
        string provider,
        string artist,
        string reason);

    [LoggerMessage(
        EventId = 1260,
        Level = LogLevel.Information,
        Message = "Release attribution started ({JobId}): {Pending} files have no album yet.")]
    public static partial void AttributionStarted(ILogger logger, string jobId, int pending);

    [LoggerMessage(
        EventId = 1261,
        Level = LogLevel.Information,
        Message = "Release attribution finished ({JobId}): {Attributed} attributed, "
            + "{Ambiguous} from tied editions, {GroupOnly} to an album but no pressing, "
            + "{NoFit} with no confident fit, {NoCandidate} on no release at all, {Failed} failed; "
            + "{Releases} releases written, in {ElapsedMs}ms")]
    public static partial void AttributionCompleted(
        ILogger logger,
        string jobId,
        int attributed,
        int ambiguous,
        int groupOnly,
        int noFit,
        int noCandidate,
        int failed,
        int releases,
        long elapsedMs);

    [LoggerMessage(
        EventId = 1262,
        Level = LogLevel.Information,
        Message = "Release attribution requested while {ActiveKind} is running; the request was rejected.")]
    public static partial void AttributionBusy(ILogger logger, string activeKind);

    [LoggerMessage(
        EventId = 1263,
        Level = LogLevel.Warning,
        Message = "Release attribution stopped early: {Reason}")]
    public static partial void AttributionAborted(ILogger logger, string reason);

    /// <summary>
    /// A folder with more candidate releases than are worth a track list.
    /// </summary>
    /// <remarks>
    /// Warning rather than debug, and deliberately so: a capped component was
    /// decided on partial evidence, and from the outside that is indistinguishable
    /// from one that used all of it. Jazz standards are where this fires — a
    /// hundred-and-fifty-file box of standards has hundreds of releases holding
    /// two or more of them, and only the first sixty get a lookup.
    ///
    /// What is cut is always the tail: candidates are ordered by how many of the
    /// folder's recordings each holds, and nothing can hold more of a folder than
    /// the record the folder is. <paramref name="worthFetching"/> against the cap
    /// is what says how much tail went unread.
    /// </remarks>
    [LoggerMessage(
        EventId = 1264,
        Level = LogLevel.Warning,
        Message = "Component from {Path} ({Files} files) had {WorthFetching} candidate releases worth "
            + "a track list and fetched the best-supported few; the rest were not looked up.")]
    public static partial void AttributionComponentCapped(
        ILogger logger,
        string path,
        int files,
        int worthFetching);

    [LoggerMessage(
        EventId = 1280,
        Level = LogLevel.Information,
        Message = "Candidate warming swept the worklist: {Warmed} answers built, {Failures} failed.")]
    public static partial void CandidatesWarmed(ILogger logger, int warmed, int failures);

    /// <summary>One item a sweep could not build.</summary>
    /// <remarks>
    /// Debug, because nobody is waiting for it: the click that opens the same
    /// question still gets the live answer and still reports its own failure.
    /// </remarks>
    [LoggerMessage(
        EventId = 1281,
        Level = LogLevel.Debug,
        Message = "Candidate warming skipped an item.")]
    public static partial void CandidateWarmFailed(ILogger logger, Exception cause);

    /// <summary>A sweep that ended on something other than a provider saying no.</summary>
    /// <remarks>
    /// Warning rather than error, and it must never be a throw: an unhandled
    /// exception out of a <c>BackgroundService</c> stops the host, and a cache
    /// nobody is waiting for is not worth the API.
    /// </remarks>
    [LoggerMessage(
        EventId = 1282,
        Level = LogLevel.Warning,
        Message = "Candidate warming failed; the next sweep tries again.")]
    public static partial void CandidateWarmSweepFailed(ILogger logger, Exception cause);

    /// <summary>An album a person asked Qobuz for, once every track has landed.</summary>
    /// <remarks>
    /// Information, and one line per album rather than per track: acquisition is
    /// a manual act, so there are tens of these a day rather than 100,000.
    /// </remarks>
    [LoggerMessage(
        EventId = 1290,
        Level = LogLevel.Information,
        Message = "Qobuz album {AlbumId} downloaded: {Downloaded} of {Total} tracks into {Folder}")]
    public static partial void QobuzAlbumDownloaded(
        ILogger logger, string albumId, int downloaded, int total, string folder);

    /// <summary>One track of an album that could not be fetched.</summary>
    /// <remarks>
    /// Warning rather than an abort: a compilation with one unlicensed track is
    /// still eleven tracks worth having, and the response says which failed.
    /// </remarks>
    [LoggerMessage(
        EventId = 1291,
        Level = LogLevel.Warning,
        Message = "Qobuz track {TrackId} of album {AlbumId} was not downloaded")]
    public static partial void QobuzTrackFailed(
        ILogger logger, long trackId, string albumId, Exception cause);

    /// <summary>An album given up on because the refusal was not about one track.</summary>
    /// <remarks>
    /// Error rather than Warning: unlike a track Qobuz will not serve, this one
    /// means nothing will download until a person changes a setting.
    /// </remarks>
    [LoggerMessage(
        EventId = 1292,
        Level = LogLevel.Error,
        Message = "Qobuz album {AlbumId} abandoned after {Attempted} tracks; the refusal was not "
            + "about a track")]
    public static partial void QobuzAlbumAbandoned(
        ILogger logger, string albumId, int attempted, Exception cause);

    /// <summary>An album a person moved out of staging and into the library.</summary>
    /// <remarks>
    /// Information, and the only record that it happened: the move touches no
    /// catalogue row and writes no domain event, because until the next scan
    /// runs the library has files the catalogue has never heard of. This line is
    /// what connects the two halves in a log.
    /// </remarks>
    [LoggerMessage(
        EventId = 1293,
        Level = LogLevel.Information,
        Message = "Imported {Folder} from staging into the library: {Tracks} tracks, {Bytes} bytes")]
    public static partial void StagedAlbumImported(
        ILogger logger, string folder, int tracks, long bytes);

    [LoggerMessage(
        EventId = 1300,
        Level = LogLevel.Information,
        Message = "Tag write started ({JobId}) over {Scope}: {Pending} files hold catalogue data")]
    public static partial void TagWriteStarted(ILogger logger, string jobId, string scope, int pending);

    [LoggerMessage(
        EventId = 1301,
        Level = LogLevel.Information,
        Message = "Tag write finished ({JobId}): {Written} written, {Unchanged} already correct, "
            + "{Refused} refused, {Failed} failed, in {ElapsedMs}ms.")]
    public static partial void TagWriteCompleted(
        ILogger logger,
        string jobId,
        int written,
        int unchanged,
        int refused,
        int failed,
        long elapsedMs);

    [LoggerMessage(
        EventId = 1302,
        Level = LogLevel.Information,
        Message = "Tag write refused: {ActiveKind} already holds the library")]
    public static partial void TagWriteBusy(ILogger logger, string activeKind);

    [LoggerMessage(
        EventId = 1303,
        Level = LogLevel.Warning,
        Message = "Tag write stopped early: {Reason}")]
    public static partial void TagWriteAborted(ILogger logger, string reason);

    /// <remarks>
    /// Warning, not Debug: this is a verification failure on a file this
    /// application was about to rewrite, and the staged copy has been discarded.
    /// One of these means one file was left alone; a page of them means
    /// something about the write path is wrong.
    /// </remarks>
    [LoggerMessage(
        EventId = 1304,
        Level = LogLevel.Warning,
        Message = "Tags not written to {Path}: {Reason}")]
    public static partial void TagsNotWritten(ILogger logger, string path, string reason);

    [LoggerMessage(
        EventId = 1400,
        Level = LogLevel.Warning,
        Message = "Trashing '{Path}' to '{Destination}'.")]
    public static partial void FilesTrashing(ILogger logger, string path, string destination);

    [LoggerMessage(
        EventId = 1401,
        Level = LogLevel.Warning,
        Message = "Trashed {Entries} entries to '{Destination}'; {Rows} catalogue rows removed.")]
    public static partial void FilesTrashed(
        ILogger logger, int entries, string destination, int rows);

    [LoggerMessage(
        EventId = 1402,
        Level = LogLevel.Warning,
        Message = "Moved '{From}' to '{To}'; {Rows} catalogue rows repointed.")]
    public static partial void FilesMoved(ILogger logger, string from, string to, int rows);

    [LoggerMessage(
        EventId = 1403,
        Level = LogLevel.Information,
        Message = "Uploaded '{Path}' into the library.")]
    public static partial void FileUploaded(ILogger logger, string path);

    [LoggerMessage(
        EventId = 1404,
        Level = LogLevel.Warning,
        Message = "'{Path}' would not move: {Reason}")]
    public static partial void FilesTrashRefused(ILogger logger, string path, string reason);
}
