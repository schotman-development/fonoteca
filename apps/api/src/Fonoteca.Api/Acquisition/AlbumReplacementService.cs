using System.Globalization;
using System.Text.Json.Serialization;
using Fonoteca.Api.Configuration;
using Fonoteca.Api.Logging;
using Fonoteca.Data;
using Fonoteca.Domain.Abstractions;
using Fonoteca.Domain.Acquisition;
using Fonoteca.Domain.Catalogue;
using Fonoteca.Ingest;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Fonoteca.Api.Acquisition;

/// <summary>
/// Retires the album a download was meant to replace.
/// </summary>
/// <remarks>
/// <b>The first operation in this project that takes a file away from somebody,
/// and every decision here is about that.</b>
///
/// <list type="bullet">
/// <item><b>It runs after the download, never before.</b> What Qobuz advertises
/// is a ceiling; what arrives is whatever the release has. The old album is
/// still whole and still the only copy until the new one is on disk and has been
/// measured.</item>
///
/// <item><b>It measures the new files rather than believing the response.</b>
/// <c>track/getFileUrl</c> states a depth and a rate, and this endpoint's whole
/// job is to delete music on the strength of that number — which makes it the
/// last place to take a provider's word for anything. <c>ffprobe</c> reads the
/// bytes that actually landed, which is the same standard
/// <c>GET /api/catalogue/matching/files/{id}</c> already holds and for a much
/// weaker reason.</item>
///
/// <item><b>Nothing is deleted. Files are moved.</b> To
/// <c>Fonoteca:ReplacedPath</c>, under a stamp, keeping their library-relative
/// layout — so undoing a bad replacement is a <c>mv</c> and not a restore from
/// backup. It is outside the library root, or the scan would catalogue the
/// archive and the album would appear to still be there. Disk is the cost and it
/// is the right cost: the alternative is a delete that cannot be inspected
/// afterwards.</item>
///
/// <item><b>A move, not a copy.</b> The archive sits beside the library root by
/// default, so the rename is instant and cannot half-finish. A configured path
/// on another volume turns every replacement into a full copy of the album; that
/// is a choice somebody makes, and it is why the default is a sibling.</item>
///
/// <item><b>The catalogue is not touched.</b> The rows for the old files now
/// point at paths that are gone, and removing rows whose files are missing is
/// exactly what the scan does — including the four sharp edges it already has
/// about not doing so on an unmounted volume. Writing them off here would be a
/// second, worse copy of that logic.</item>
///
/// <item><b>It refuses to archive anything it just wrote.</b> Qobuz's own naming
/// decides where a download lands, and there is nothing stopping that being
/// inside the folder being replaced. Without the check, an upgrade of
/// <c>Artist/Album</c> that landed in <c>Artist/Album</c> archives itself and
/// reports success.</item>
/// </list>
///
/// Behind <c>Fonoteca:AllowFileReplacement</c>, which is deliberately not
/// <c>AllowFileMutation</c>. That flag means "may rewrite a tag in place, having
/// verified the write with a second library" — a considered, reversible edit
/// with an undo journal behind it. This one means "may take an album away". A
/// person who turned the first on to get their files tagged has not agreed to
/// the second.
/// </remarks>
public sealed class AlbumReplacementService(
    FonotecaDbContext db,
    IAudioProbe probe,
    FileSystemAudioFileStore store,
    IOptions<FonotecaOptions> options,
    IClock clock,
    ILogger<AlbumReplacementService> logger)
{
    public async Task<AlbumReplacement> ReplaceAsync(
        string folder,
        int expectedFiles,
        AlbumDownload download,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        ArgumentNullException.ThrowIfNull(download);

        var config = options.Value;
        var trimmed = folder.Trim('/');

        // The download landed somewhere Qobuz's metadata chose. If that is
        // inside what is about to be archived, archiving it moves the new album
        // into the bin and reports an upgrade.
        if (download.Folder.Trim('/').Equals(trimmed, StringComparison.OrdinalIgnoreCase)
            || download.Folder.Trim('/').StartsWith(trimmed + "/", StringComparison.OrdinalIgnoreCase))
        {
            Log.ReplacementRefused(logger, trimmed, ReplacementVerdict.LandedInside);

            return AlbumReplacement.Refused(
                trimmed,
                ReplacementVerdict.LandedInside,
                $"The download landed in '{download.Folder}', which is inside the album being "
                + "replaced. Nothing was moved.");
        }

        // The trailing slash matters: 'Artist/Album' also prefixes
        // 'Artist/Album Live', and archiving that would be silent.
        var prefix = trimmed + "/";

        var old = await db.MediaFiles
            .AsNoTracking()
            .Where(file => file.Path.StartsWith(prefix))
            .Select(file => new { file.Path, file.Quality, file.TrackId })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        if (old.Count == 0)
        {
            Log.ReplacementRefused(logger, trimmed, ReplacementVerdict.NotHeld);

            return AlbumReplacement.Refused(
                trimmed,
                ReplacementVerdict.NotHeld,
                $"The catalogue holds no files under '{trimmed}'. Nothing was moved.");
        }

        // The caller says how many files the row it clicked covered, and the
        // prefix has to agree.
        //
        // <b>What it catches is a folder that is not the album the row was
        // about, and a library that moved under the screen.</b> `folder` is a
        // prefix and a row's folder is not always an album: a loose file at
        // `Artist/track.flac` produces `Artist`, whose prefix then matches every
        // album under that artist while the row counted one file. Same lesson
        // `ReleaseCandidateSet.Files` already paid for — a different count means
        // the numbers describe something that is no longer there, and it is a
        // miss rather than a failure.
        //
        // <b>What it does not catch, and what does.</b> On a
        // `Genre/Artist/Album` library `UpgradeScan.AlbumFolder` cuts every row
        // at `Genre/Artist`, so the row genuinely *is* the discography and the
        // counts agree — this guard passes and is right to. The backstop there
        // is `heldTracks` below: two hundred files held means two hundred tracks
        // have to arrive. That is the completeness gate doing the work, not this
        // check, and it is why that count under-counting was the critical bug.
        if (expectedFiles > 0 && old.Count != expectedFiles)
        {
            // Worth a line even though it refused: this is the "you nearly
            // archived a discography" event.
            Log.ReplacementRefused(logger, trimmed, ReplacementVerdict.NotHeld);

            return AlbumReplacement.Refused(
                trimmed,
                ReplacementVerdict.NotHeld,
                $"'{trimmed}' holds {old.Count} files, but the row said {expectedFiles}. Either "
                + "the library changed since the list was drawn, or that folder covers more than "
                + "the album. Nothing was moved.");
        }

        // Distinct tracks where they are known, plus one per file where they are
        // not.
        //
        // <b>Counting only the attributed files is how this gate fails open, and
        // it failed open on most of this library.</b> Partial attribution is the
        // normal state of a catalogue — CLAUDE.md's own example is Rumours, 11
        // files of which 10 are filed — and a folder with one attributed file
        // among twenty-one collapsed to `heldTracks = 1`, so a one-track
        // download satisfied "every track arrived" and archived all twenty-one.
        // Measured against the live database that was 156 of 519 rows and 535
        // files that nothing downloaded would have replaced.
        //
        // Five encodings of one song still count once, because they share a
        // TrackId. A file with no TrackId cannot be shown to duplicate anything,
        // so it counts as its own track — which over-counts a duplicate rip
        // nobody has filed, and over-counting only ever causes a refusal.
        var attributed = old
            .Where(file => file.TrackId is not null)
            .Select(file => file.TrackId)
            .Distinct()
            .Count();

        var heldTracks = attributed + old.Count(file => file.TrackId is null);

        // Every track whose bytes are on disk, which is not the same as every
        // track that was fetched *this* run. An interrupted upgrade re-run
        // reports its already-present tracks as Skipped — with a path — and
        // counting only Downloaded made a resumed upgrade permanently impossible:
        // nothing to measure, so "no track downloaded, there is nothing to
        // replace the album with" while the whole album sat on disk. A track
        // Qobuz does not offer is Skipped too and carries no path, which is
        // exactly the one that should not count.
        var expected = download.Tracks.Count(track => track.Path is not null);
        var arrived = await MeasureAsync(download, cancellationToken).ConfigureAwait(false);

        // A track that landed and would not measure is not a track that can be
        // ranked, and dropping it silently makes replacement *more* likely
        // rather than less: `Worst(arrived)` is computed over the survivors, so
        // losing the worst arrival raises the bar the download has to clear.
        if (arrived.Count != expected)
        {
            Log.ReplacementRefused(logger, trimmed, ReplacementVerdict.ArrivalNotMeasured);

            return AlbumReplacement.Refused(
                trimmed,
                ReplacementVerdict.ArrivalNotMeasured,
                $"{expected - arrived.Count} of the {expected} tracks that downloaded could not be "
                + "measured, so what arrived cannot be ranked against what is here. Nothing was "
                + "moved.");
        }

        var verdict = UpgradeReplacement.Check(
            [.. old.Select(file => file.Quality)], heldTracks, arrived);

        if (verdict != ReplacementVerdict.Replace)
        {
            Log.ReplacementRefused(logger, trimmed, verdict);
            return AlbumReplacement.Refused(trimmed, verdict, Explain(verdict, heldTracks, arrived.Count));
        }

        if (!config.AllowFileReplacement)
        {
            return AlbumReplacement.Refused(
                trimmed,
                ReplacementVerdict.Replace,
                "The download is a genuine upgrade, but Fonoteca:AllowFileReplacement is off, so "
                + $"nothing was moved. The new album is at '{download.Folder}' and the old one is "
                + "still where it was.");
        }

        // Stamped, and the log line comes first. A crash part-way through leaves
        // half an album under the archive and half in place; without a line
        // saying which folder was being emptied, the only evidence is the
        // filesystem itself.
        var destination = Path.Combine(
            ArchiveRoot(config),
            clock.UtcNow.ToString("yyyy-MM-dd HHmmss", CultureInfo.InvariantCulture),
            trimmed.Replace('/', Path.DirectorySeparatorChar));

        Log.ReplacementStarting(logger, trimmed, destination, old.Count);

        var moved = Archive(config, trimmed, destination, [.. old.Select(file => file.Path)]);

        // The folders the move emptied. One copy of this lives on the store, for
        // the reason its own remarks give.
        store.PruneEmptyDirectories(new LibraryPath(trimmed));

        Log.ReplacementDone(logger, trimmed, download.Folder, moved.Count);

        return new AlbumReplacement(
            trimmed,
            download.Folder,
            ReplacementVerdict.Replace,
            Archived: moved.Count,
            ArchivedTo: destination,
            Detail: null);
    }

    /// <summary>What actually landed, read from the bytes rather than the response.</summary>
    /// <remarks>
    /// A track whose file cannot be measured is left out rather than guessed at,
    /// which makes the download look shorter than it was — and that is the safe
    /// direction: it can only cause a refusal.
    /// </remarks>
    private async Task<List<AudioQuality>> MeasureAsync(
        AlbumDownload download,
        CancellationToken cancellationToken)
    {
        var measured = new List<AudioQuality>();

        foreach (var track in download.Tracks)
        {
            if (track.Path is null) continue;

            try
            {
                var reading = await probe
                    .ProbeAsync(new LibraryPath(track.Path), cancellationToken)
                    .ConfigureAwait(false);

                // A complaint is the decoder objecting to bytes that were
                // downloaded seconds ago, so it is not a quality reading — it is
                // a reason not to trade anything for them.
                if (reading is { DecodedCleanly: true }) measured.Add(reading.Quality);
                else Log.ReplacementFileNotMeasured(logger, track.Path);
            }
            catch (OperationCanceledException)
            {
                // Otherwise every remaining probe throws instantly, `arrived`
                // shrinks, and a cancelled request comes back 200 with a
                // refusal that blames the files.
                throw;
            }
#pragma warning disable CA1031 // A file that will not measure must not 500 the request.
            catch (Exception cause)
#pragma warning restore CA1031
            {
                Log.ReplacementFileNotMeasured(logger, $"{track.Path}: {cause.Message}");
            }
        }

        return measured;
    }

    /// <summary>Moves the old album out of the library, keeping its layout.</summary>
    private static List<string> Archive(
        FonotecaOptions config, string folder, string archive, IReadOnlyList<string> paths)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(config.LibraryPath));

        var moved = new List<string>(paths.Count);

        foreach (var path in paths)
        {
            var source = Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar));

            if (!File.Exists(source)) continue;

            // The path below the album folder, so a two-disc set keeps its CD1
            // and CD2 in the archive and can be moved back in one command.
            var below = path.Length > folder.Length + 1 ? path[(folder.Length + 1)..] : Path.GetFileName(path);
            var destination = Path.Combine(archive, below.Replace('/', Path.DirectorySeparatorChar));

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

            // Overwrite is safe only because the archive is stamped: unstamped,
            // replacing one album twice moved the second copy over the first and
            // destroyed it — the one path in this feature where bytes actually
            // went away.
            File.Move(source, destination, overwrite: true);
            moved.Add(path);
        }

        return moved;
    }

    /// <summary>
    /// Where the archive lives.
    /// </summary>
    /// <remarks>
    /// A sibling of the library root by default, so the move is a rename on the
    /// same filesystem and the scan never sees it. Naming it after the root
    /// rather than hiding it inside one keeps both of those true without a rule
    /// in the walk about which directories to ignore.
    /// </remarks>
    private static string ArchiveRoot(FonotecaOptions config)
    {
        if (!string.IsNullOrWhiteSpace(config.ReplacedPath))
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(config.ReplacedPath));
        }

        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(config.LibraryPath)) + "-replaced";
    }

    private static string Explain(ReplacementVerdict verdict, int heldTracks, int arrived) => verdict switch
    {
        ReplacementVerdict.NothingArrived =>
            "No track downloaded, so there is nothing to replace the album with.",

        ReplacementVerdict.Incomplete =>
            $"Only {arrived} of the album's {heldTracks} tracks downloaded — usually a licensing "
            + "gap. Replacing would lose the rest, so nothing was moved.",

        ReplacementVerdict.NotBetter =>
            "What arrived is no better than the best file already held, so nothing was moved. "
            + "Both copies are on disk.",

        ReplacementVerdict.NotMeasured =>
            "Nothing has measured the album being replaced, so it cannot be shown to be worse. "
            + "Run the measure pass and try again.",

        _ => "Nothing was moved.",
    };
}

/// <param name="Archived">Files moved out of the library. Zero on every refusal.</param>
/// <param name="ArchivedTo">Where they went, so a bad replacement can be undone by hand.</param>
public sealed record AlbumReplacement(
    string Folder,
    string DownloadedTo,
    // A name on the wire, not an ordinal — the same converter TrackOutcome
    // carries. A generated client typing this `number` makes every branch on it
    // a magic constant that silently changes meaning when a value is inserted.
    [property: JsonConverter(typeof(JsonStringEnumConverter<ReplacementVerdict>))]
    ReplacementVerdict Verdict,
    int Archived,
    string? ArchivedTo,
    string? Detail)
{
    internal static AlbumReplacement Refused(string folder, ReplacementVerdict verdict, string detail) =>
        new(folder, string.Empty, verdict, 0, null, detail);
}
