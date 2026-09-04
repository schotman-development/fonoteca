using System.Text.Json.Serialization;
using Fonoteca.Api.Configuration;
using Fonoteca.Api.Logging;
using Fonoteca.Domain.Abstractions;
using Fonoteca.Domain.Acquisition;
using Fonoteca.Domain.Catalogue;
using Fonoteca.Providers.Qobuz;
using Microsoft.Extensions.Options;

namespace Fonoteca.Api.Acquisition;

/// <summary>
/// Downloads an album a person has chosen straight into the library.
/// </summary>
/// <remarks>
/// <b>Manual, and it writes into the library.</b> There is no queue and no
/// schedule; a download is a person who searched, read a track list and pressed
/// a button. What it is not any more is quarantined.
///
/// This used to land in a staging directory that a person then imported by
/// hand, on the reasoning that an acquisition writing into the library could put
/// a file there that the catalogue has never seen. That reasoning did not
/// survive contact with the write path: a file is written to a
/// <c>.part</c> sibling, whose extension <c>AudioFormats</c> does not recognise,
/// so a scan cannot see it — and the final name only exists after the body has
/// been checked against <c>Content-Length</c> and atomically renamed. A scan
/// therefore only ever sees whole files, which is exactly what staging was
/// there to guarantee. The import step was a second manual act buying a
/// property the first one already had.
///
/// <b>What staging did buy, and is kept below, is the collision check.</b> An
/// album folder the library already holds must not be quietly merged with a
/// different edition of itself.
///
/// It runs in the foreground of the request, which is the library scan's
/// reasoning applied to a different cost: a person pressed a button and is
/// waiting for the answer, and the alternative is a job store this application
/// does not have (ADR 0007). The ceiling is real — a hi-res box set is tens of
/// minutes and a reverse proxy in front of this will give up first.
/// ponytail: foreground request, one album at a time. Move to Fonoteca.Jobs if
/// albums start being big enough to time out.
/// </remarks>
public sealed class QobuzDownloadService(
    QobuzClient qobuz,
    IOptions<FonotecaOptions> options,
    IClock clock,
    ILogger<QobuzDownloadService> logger) : IDisposable
{
    /// <summary>Suffix on a file still being written.</summary>
    /// <remarks>
    /// So an interrupted download cannot be mistaken for a finished one. The
    /// rename at the end is what makes the final name mean "complete", which is
    /// the same promise <c>OpenForReplaceAsync</c> makes in the library.
    /// </remarks>
    private const string PartialSuffix = ".part";

    private const int CopyBufferBytes = 128 * 1024;

    /// <summary>Every container a download can land in, for the resume check.</summary>
    /// <remarks>
    /// Two, because <see cref="ExtensionFor"/> produces two. It has to stay in
    /// step with that method: a container missing here is a track re-downloaded
    /// on every run, which is wasteful rather than wrong — but silently so.
    /// </remarks>
    private static readonly string[] Containers = ["flac", "mp3"];

    /// <summary>
    /// One album at a time.
    /// </summary>
    /// <remarks>
    /// Not about throughput — it is about a double-click. Two runs of the same
    /// album write the same paths, and the loser corrupts the winner's files
    /// rather than failing. Refusing the second outright is the smaller
    /// surprise, and the caller gets a 409 rather than a folder of half-tracks.
    /// </remarks>
    private readonly SemaphoreSlim _oneAtATime = new(1, 1);

    /// <summary>Whether a download is running right now.</summary>
    public bool IsBusy => _oneAtATime.CurrentCount == 0;

    /// <summary>Fetches every streamable track of an album into the library.</summary>
    /// <exception cref="ProviderRejectedException">Nothing is configured, or Qobuz refused.</exception>
    /// <exception cref="DownloadInProgressException">Another download is already running.</exception>
    public async Task<AlbumDownload> DownloadAlbumAsync(
        string albumId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(albumId);

        var config = options.Value;
        var root = LibraryRoot(config);

        if (!_oneAtATime.Wait(0, CancellationToken.None))
        {
            throw new DownloadInProgressException();
        }

        try
        {
            var album = await qobuz.GetAlbumAsync(albumId, cancellationToken).ConfigureAwait(false)
                ?? throw new ProviderRejectedException(
                    QobuzClient.ProviderName, $"Qobuz has no album {albumId}.");

            var folder = $"{StagedFileName.Segment(album.Artist)}/{StagedFileName.Segment(album.Title)}";

            RefuseToMergeEditions(root, folder, album);

            var delay = TimeSpan.FromMilliseconds(config.Download.TrackDelayMs);

            var results = new List<TrackDownload>(album.Tracks.Count);
            var downloaded = 0;
            var first = true;

            foreach (var track in album.Tracks)
            {
                if (!track.Streamable)
                {
                    // A licensing gap on one track of an otherwise available
                    // album, which is common on compilations. Not an error, and
                    // not worth a request to confirm.
                    results.Add(TrackDownload.Skipped(track, "Qobuz does not offer this track."));
                    continue;
                }

                // Before the request, not after: a track already on disk should
                // not cost a turn at the gate to discover.
                if (!first) await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                first = false;

                try
                {
                    var result = await DownloadTrackAsync(root, folder, album, track, cancellationToken)
                        .ConfigureAwait(false);

                    results.Add(result);
                    if (result.Outcome == TrackOutcome.Downloaded) downloaded++;
                }
                catch (ProviderUnavailableException cause)
                {
                    // One track failing does not fail the album — eleven of
                    // twelve is worth having, and the response says which.
                    //
                    // Only the transient kind. A track the subscription does not
                    // cover never reaches here at all: it comes back as an
                    // answer rather than an exception, and is recorded as
                    // Skipped below.
                    Log.QobuzTrackFailed(logger, track.Id, album.Id, cause);
                    results.Add(TrackDownload.Failed(track, cause.Message));
                }
                catch (ProviderRejectedException cause)
                {
                    /*
                      Stops the album on the first one, and this is the whole
                      lesson of this method.

                      A rejection that is not track-specific means a human has to
                      change something — a wrong app secret, an expired token, a
                      rotated app id — and the codebase's own rule for that
                      exception is that retrying is pure waste. Caught alongside
                      the track-specific ones it was not waste, it was three
                      minutes: a real 178-track box set sent 178 doomed signed
                      requests at one per second, reported HTTP 200, and produced
                      a result page saying 178 tracks were each unavailable. The
                      cause was one setting, and it was invisible in the noise.

                      Identification already paid for exactly this — "a truncated
                      FLAC marks one row; a missing binary must stop the pass on
                      the first file. Merged, a PATH problem marks 100,000 files
                      unreadable."
                    */
                    Log.QobuzAlbumAbandoned(logger, album.Id, results.Count, cause);
                    throw;
                }
            }

            Log.QobuzAlbumDownloaded(logger, album.Id, downloaded, album.Tracks.Count, folder);

            return new AlbumDownload(
                album.Id,
                album.Title,
                album.Artist,
                folder,
                downloaded,
                // Qobuz's own count, so a truncated track page is visible rather
                // than reported as a complete album that happened to be short.
                album.TrackCount,
                clock.UtcNow,
                results);
        }
        finally
        {
            _oneAtATime.Release();
        }
    }

    private async Task<TrackDownload> DownloadTrackAsync(
        string root,
        string folder,
        QobuzAlbum album,
        QobuzTrack track,
        CancellationToken cancellationToken)
    {
        // The container is the only part of the name that needs the response, so
        // the check for an existing file is made against every container this
        // can produce rather than against one. That ordering is the whole resume
        // story: an album whose fourth track failed is re-requested, and the
        // first three cost a stat each instead of three signed URL requests at
        // one per second. Asking first and comparing after would spend the rate
        // limit re-deriving a filename already on disk.
        foreach (var container in Containers)
        {
            var candidate = Named(album, track, container);

            if (File.Exists(Contained(root, candidate)))
            {
                return TrackDownload.Skipped(track, "Already in staging.") with { Path = candidate };
            }
        }

        var audio = await qobuz.GetTrackFileUrlAsync(track.Id, cancellationToken)
            .ConfigureAwait(false);

        if (audio.Url is not { } url)
        {
            // Skipped, not Failed. Qobuz answered; the answer is that this track
            // is not on offer at this format, which is the same category as the
            // `!track.Streamable` case above and not a fault to be retried.
            return TrackDownload.Skipped(track, audio.Refusal ?? "Qobuz offers no audio.");
        }

        var relative = Named(album, track, ExtensionFor(url));
        var target = Contained(root, relative);

        Directory.CreateDirectory(Path.GetDirectoryName(target)!);

        var partial = target + PartialSuffix;
        long written;
        long? declared;

        using (var response = await qobuz.OpenAudioAsync(url.Url, cancellationToken).ConfigureAwait(false))
        {
            declared = response.Content.Headers.ContentLength;

            var source = await response.Content.ReadAsStreamAsync(cancellationToken)
                .ConfigureAwait(false);

            await using (source.ConfigureAwait(false))
            {
                // Create, so a .part left by a crash is overwritten rather than
                // appended to. A .part belonging to a download running right now
                // cannot exist: the semaphore above allows one at a time.
                var file = new FileStream(
                    partial,
                    FileMode.Create,
                    FileAccess.Write,
                    FileShare.None,
                    CopyBufferBytes,
                    FileOptions.Asynchronous);

                await using (file.ConfigureAwait(false))
                {
                    await source.CopyToAsync(file, CopyBufferBytes, cancellationToken)
                        .ConfigureAwait(false);

                    written = file.Length;
                }
            }
        }

        // What came off the wire has to be what the server said it was sending.
        //
        // HttpClient already throws on a body cut short of a stated
        // Content-Length or a chunked terminator — but a response that states
        // neither and simply closes reads as a clean end of stream, and a short
        // FLAC would then be renamed to a name this class documents as meaning
        // "complete". This library has already paid for that once: a FLAC
        // truncated to a quarter of its bytes reads back at its original
        // duration and 3 kbps, typed lossless. Refusing here costs one
        // re-request; not refusing puts a broken file where a scan will adopt it.
        if (declared is { } expected && written != expected)
        {
            File.Delete(partial);

            throw new ProviderUnavailableException(
                QobuzClient.ProviderName,
                $"Qobuz sent {written} bytes of a {expected}-byte track ({track.Title}). "
                + "The transfer was cut short; nothing was kept.");
        }

        // The rename is what makes the final name mean "complete". Anything that
        // throws above leaves a .part, which is visibly unfinished.
        File.Move(partial, target);

        return new TrackDownload(
            track.Id,
            track.DiscNumber,
            track.TrackNumber,
            track.Title,
            TrackOutcome.Downloaded,
            relative,
            written,
            url.FormatId,
            url.BitDepth,
            url.SamplingRate,
            Detail: null);
    }

    private static string Named(QobuzAlbum album, QobuzTrack track, string extension) =>
        StagedFileName.For(
            album.Artist,
            album.Title,
            track.DiscNumber,
            track.TrackNumber,
            track.Title,
            extension,
            album.DiscCount);

    /// <summary>
    /// The container Qobuz actually served, from what it said rather than what
    /// was asked for.
    /// </summary>
    /// <remarks>
    /// Format 5 is the only MP3 tier; everything above it is FLAC. The mime type
    /// is preferred where it is present because it is the response describing
    /// itself, and the format id is the fallback for the same reason
    /// <c>AudioQuality</c> takes losslessness off the codec and never off the
    /// extension.
    /// </remarks>
    private static string ExtensionFor(QobuzFileUrl url) => url.MimeType switch
    {
        "audio/mpeg" => "mp3",
        "audio/flac" or "audio/x-flac" => "flac",
        _ => url.FormatId == 5 ? "mp3" : "flac",
    };

    /// <summary>Absolute path for a library-relative one, refusing anything outside the root.</summary>
    /// <remarks>
    /// <see cref="StagedFileName"/> has already stripped separators out of every
    /// segment, so this cannot fire on names it built. It is here because the
    /// names come from a remote service: a check that only holds while the
    /// sanitiser is correct is a check that stops holding the day it is edited.
    /// It matters more now than it did — the root it is guarding is the library.
    /// </remarks>
    private static string Contained(string root, string relative)
    {
        var absolute = Path.GetFullPath(Path.Combine(root, relative));

        return absolute.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            ? absolute
            : throw new UnauthorizedAccessException(
                $"'{relative}' resolves outside the library root.");
    }

    /// <summary>
    /// Refuses a download that would merge two editions into one folder.
    /// </summary>
    /// <remarks>
    /// The one thing the old staging-then-import step was genuinely buying.
    /// <c>Artist/Album</c> is not unique across editions — "Rumours" and
    /// "Rumours (Deluxe Edition)" differ, but a remaster and its original often
    /// do not — so downloading over an album already held silently produces one
    /// directory containing two rips, which every later pass then reads as a
    /// single very strange album.
    ///
    /// Re-downloading the SAME album is not a collision and must keep working:
    /// it is how an interrupted download is resumed, and every file already
    /// there is skipped for the cost of a stat. So the test is not "is the
    /// folder occupied" but "does it hold audio this download would not
    /// produce" — a name-by-name comparison against the track list in hand.
    /// </remarks>
    private static void RefuseToMergeEditions(string root, string folder, QobuzAlbum album)
    {
        var directory = Contained(root, folder);

        if (!Directory.Exists(directory)) return;

        var expected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var track in album.Tracks)
        {
            // Both containers, because which one arrives is not known until
            // Qobuz answers — the same reason the resume check tries both.
            foreach (var container in Containers)
            {
                expected.Add(Path.GetFileName(Named(album, track, container)));
            }
        }

        var strangers = Directory
            .EnumerateFiles(directory, "*", SearchOption.AllDirectories)
            .Where(AudioFormats.IsAudioFile)
            .Select(Path.GetFileName)
            .Where(name => name is not null && !expected.Contains(name))
            .Take(3)
            .ToArray();

        if (strangers.Length == 0) return;

        throw new ProviderRejectedException(
            QobuzClient.ProviderName,
            $"'{folder}' already holds audio this album does not list (for example "
            + $"{string.Join(", ", strangers)}). Downloading would merge two editions into one "
            + "folder, which nothing downstream could tell apart afterwards. Move or rename what "
            + "is there first.");
    }

    private static string LibraryRoot(FonotecaOptions config)
    {
        if (string.IsNullOrWhiteSpace(config.LibraryPath))
        {
            throw new ProviderRejectedException(
                QobuzClient.ProviderName,
                "No library path is configured. Set Fonoteca:LibraryPath — downloads are written "
                + "into it directly.");
        }

        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(config.LibraryPath));
    }

    public void Dispose() => _oneAtATime.Dispose();
}

/// <summary>A second download was asked for while one was running.</summary>
/// <remarks>
/// Its own type rather than a bare <see cref="InvalidOperationException"/>,
/// because the endpoint turns it into a 409 by catching it — and catching the
/// base type would turn any <c>InvalidOperationException</c> from anywhere below
/// into a conflict, reporting an internal message as "already running".
/// </remarks>
public sealed class DownloadInProgressException()
    : InvalidOperationException(
        "A Qobuz download is already running. This instance runs one at a time.");

/// <summary>What one album's download did.</summary>
/// <param name="Folder">Library-relative, so the answer does not print the server's paths.</param>
/// <param name="TrackCount">Qobuz's count of the album. More than <c>Tracks.Count</c> means the list was cut.</param>
public sealed record AlbumDownload(
    string AlbumId,
    string Title,
    string? Artist,
    string Folder,
    int Downloaded,
    int TrackCount,
    DateTimeOffset FinishedUtc,
    IReadOnlyList<TrackDownload> Tracks);

public enum TrackOutcome
{
    /// <summary>Its bytes are on disk under <c>Path</c>.</summary>
    Downloaded,

    /// <summary>Already staged, or Qobuz does not offer it. <c>Detail</c> says which.</summary>
    Skipped,

    /// <summary>Qobuz refused or the transfer broke. The album kept going.</summary>
    Failed,
}

public sealed record TrackDownload(
    long TrackId,
    int DiscNumber,
    int TrackNumber,
    string Title,
    [property: JsonConverter(typeof(JsonStringEnumConverter<TrackOutcome>))]
    TrackOutcome Outcome,
    string? Path,
    long? SizeBytes,
    int? FormatId,
    int? BitDepth,
    double? SamplingRate,
    string? Detail)
{
    internal static TrackDownload Skipped(QobuzTrack track, string why) =>
        new(track.Id, track.DiscNumber, track.TrackNumber, track.Title,
            TrackOutcome.Skipped, null, null, null, null, null, why);

    internal static TrackDownload Failed(QobuzTrack track, string why) =>
        new(track.Id, track.DiscNumber, track.TrackNumber, track.Title,
            TrackOutcome.Failed, null, null, null, null, null, why);
}
