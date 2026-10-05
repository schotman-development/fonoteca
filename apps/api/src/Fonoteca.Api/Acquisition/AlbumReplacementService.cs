using System.Globalization;
using System.Text.Json.Serialization;
using Fonoteca.Api.Configuration;
using Fonoteca.Api.Logging;
using Fonoteca.Data;
using Fonoteca.Domain.Abstractions;
using Fonoteca.Domain.Acquisition;
using Fonoteca.Domain.Catalogue;
using Fonoteca.Ingest;
using Fonoteca.Providers.Qobuz;
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
    /// <summary>
    /// The album folders already holding the album a Qobuz download is of.
    /// </summary>
    /// <remarks>
    /// <b>So a plain download is an upgrade when it is one.</b> A folder counts
    /// when either of two things finds it: the catalogue's own albums, matched
    /// by <see cref="QobuzCovers.Match"/> — the rule that already decides which
    /// shop album is which catalogue record for a cover, called rather than
    /// copied — or the <c>Artist/Album</c> folder the download would land in,
    /// which catches a folder nothing has filed yet and is exactly the one a
    /// download would otherwise collide with.
    ///
    /// Only album folders. A file loose under an artist has <c>Artist</c> as
    /// its folder, and that prefix is the artist's whole discography.
    /// </remarks>
    public async Task<IReadOnlyList<HeldFolder>> HeldAsync(
        QobuzAlbum album,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(album);

        var albums = await AlbumsAsync(album, cancellationToken).ConfigureAwait(false);
        var landing = $"{StagedFileName.Segment(album.Artist)}/{StagedFileName.Segment(album.Title)}";

        var found = await FoldersAsync(albums.Matched, landing, cancellationToken).ConfigureAwait(false);
        if (found.Count > 0) return found;

        // Nothing by barcode or billing: one held album of the same title,
        // whatever its billing, is asked about rather than downloaded beside —
        // the owner's choice. "Porgy & Bess" is "Porgy and Bess"; a band billed
        // by its leader's name, an album with no credits, miss the billing.
        // More than one is a common title, and no question.
        var titled = await FoldersAsync(albums.Titled, null, cancellationToken).ConfigureAwait(false);
        return titled.Count == 1 ? titled : [];
    }

    /// <summary>The album folders mostly filed under these albums, and the one a download would land in if it holds anything.</summary>
    private async Task<List<HeldFolder>> FoldersAsync(
        HashSet<ReleaseGroupId> matched,
        string? landing,
        CancellationToken cancellationToken)
    {
        var folders = (await db.MediaFiles
                .AsNoTracking()
                .Where(file => file.ReleaseGroupId != null && matched.Contains(file.ReleaseGroupId.Value))
                .Select(file => file.Path)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false))
            .Select(AlbumFolder.Of)
            .Concat(landing is null ? Array.Empty<string>() : [landing])
            .Where(folder => folder.Contains('/', StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var found = new List<HeldFolder>();

        foreach (var folder in folders)
        {
            var prefix = folder + "/";
            var filed = await db.MediaFiles
                .AsNoTracking()
                .Where(file => file.Path.StartsWith(prefix))
                .Select(file => file.ReleaseGroupId)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            var mine = filed.Count(group => group is { } id && matched.Contains(id));

            // The album needs more than half the folder — attribution's own
            // rule, so one track of it on a compilation does not make the
            // compilation this album. The folder the download would land in
            // counts whatever its files are: it is the collision, and whether
            // the download may replace what is there is the offer's question,
            // and a person's where the files cannot show they are this album.
            if (mine * 2 > filed.Count || (folder == landing && filed.Count > 0))
            {
                found.Add(new HeldFolder(folder, filed.Count));
            }
        }

        return found;
    }

    /// <summary>The catalogue's albums — release groups with files — that a Qobuz album is.</summary>
    /// <remarks>
    /// A barcode of any of the album's editions says so outright. Otherwise the
    /// cover rule's title and billing, <see cref="QobuzCovers.Match"/>, asked
    /// with the shop's title and with it less an edition note: "Rumours
    /// (Deluxe Edition)" is Rumours, "Rumours (Live)" is another record.
    /// </remarks>
    /// <returns>
    /// The albums it is, and — for a question, never an answer — those that
    /// only share its title.
    /// </returns>
    private async Task<(HashSet<ReleaseGroupId> Matched, HashSet<ReleaseGroupId> Titled)> AlbumsAsync(
        QobuzAlbum album,
        CancellationToken cancellationToken)
    {
        var held = db.MediaFiles
            .Where(file => file.ReleaseGroupId != null)
            .Select(file => file.ReleaseGroupId!.Value);

        var groups = await db.ReleaseGroups
            .AsNoTracking()
            .Where(group => held.Contains(group.Id))
            .Select(group => new
            {
                group.Id,
                group.Title,
                group.FirstReleaseYear,
                Barcodes = group.Releases
                    .Where(release => release.Barcode != null)
                    .Select(release => release.Barcode)
                    .ToList(),
                Credits = group.Credits
                    .OrderBy(credit => credit.Position)
                    .Select(credit => new
                    {
                        credit.CreditedAs,
                        credit.JoinPhrase,
                        credit.Artist!.Name,
                        credit.Artist.LatinName,
                    })
                    .ToList(),
            })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var upc = Barcodes.Normalise(album.Upc);
        var bare = UpgradeReplacement.WithoutEditionNote(album.Title);
        IReadOnlyList<QobuzAlbum> asked = bare.Length > 0 && bare != album.Title ? [album, album with { Title = bare }] : [album];

        var matched = groups
            .Where(group => (upc is not null && group.Barcodes.Any(barcode => Barcodes.Normalise(barcode) == upc))
                || QobuzCovers.Match(
                    asked,
                    group.Title,
                    string.Concat(group.Credits.Select(credit => (credit.CreditedAs ?? credit.Name) + credit.JoinPhrase)),
                    [.. group.Credits
                        .SelectMany(credit => new[] { credit.CreditedAs, credit.LatinName, credit.Name })
                        .OfType<string>()],
                    // Not asked: a shop dates a record by its reissue as often as
                    // not.
                    null,
                    barcode: null,
                    group.Barcodes,
                    out _) is not null)
            .Select(group => group.Id)
            .ToHashSet();

        var title = TitleKey(album.Title);
        var titled = groups
            .Where(group => title.Length > 0 && TitleKey(group.Title) == title)
            .Select(group => group.Id)
            .ToHashSet();

        return (matched, titled);
    }

    private static string TitleKey(string title) =>
        ArtistNameMatch.Normalise(UpgradeReplacement.WithoutEditionNote(title).Replace("&", " and ", StringComparison.Ordinal));

    /// <summary>Whether a folder's files are mostly filed under an album other than these.</summary>
    /// <remarks>Unfiled files do not vote, and a folder of none is nobody's to claim.</remarks>
    private static bool AnotherAlbum(IEnumerable<ReleaseGroupId?> filed, HashSet<ReleaseGroupId> matched)
    {
        var known = filed.OfType<ReleaseGroupId>().ToList();
        return known.Count > 0 && known.Count(matched.Contains) * 2 <= known.Count;
    }

    /// <summary>
    /// Whether an album is worth downloading to replace the one in <paramref name="folder"/>.
    /// </summary>
    /// <remarks>
    /// Asked before a byte is fetched, on what Qobuz says it will serve —
    /// <see cref="UpgradeReplacement.Offer"/>. Null means go ahead;
    /// <see cref="ReplaceAsync"/> still decides on the files that arrive.
    /// With <c>Fonoteca:AllowFileReplacement</c> off it refuses here too, since
    /// a download fetched to replace an album is only ever kept in its place.
    /// </remarks>
    /// <param name="formatId">The encoding downloads ask for, <c>QobuzOptions.FormatId</c>.</param>
    /// <param name="confirmed">
    /// A person has said to replace the folder though its files cannot be
    /// shown to be this album: filed under another, or under none.
    /// </param>
    public async Task<AlbumReplacement?> RefuseOfferAsync(
        string folder,
        int expectedFiles,
        QobuzAlbum album,
        int formatId,
        bool confirmed,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        ArgumentNullException.ThrowIfNull(album);

        var trimmed = folder.Trim('/');

        var (old, refusal) = await OldAsync(trimmed, expectedFiles, cancellationToken).ConfigureAwait(false);
        if (refusal is not null) return refusal;

        // Before the fetch as well as after it, so a folder that would be
        // refused later costs no download now.
        if (Stranger(LibraryRoot(options.Value), trimmed, old) is { } stranger)
        {
            Log.ReplacementRefused(logger, trimmed, ReplacementVerdict.NotHeld);

            return AlbumReplacement.Refused(
                trimmed,
                ReplacementVerdict.NotHeld,
                $"'{stranger}' is on disk but not in the catalogue, and would stay beside the new album. "
                + "Run a library scan and try again. Nothing was downloaded.");
        }

        // The album is the release group, and whether this is that album comes
        // before whether it is better. Where the folder's files cannot show it
        // — filed under another album, or under none — a person says so, the
        // owner's choice: the check misses a shop's "(Live)", a band billed by
        // its leader's name, an album with no credits.
        var filed = old.Select(file => file.ReleaseGroupId).OfType<ReleaseGroupId>().ToList();
        var albums = await AlbumsAsync(album, cancellationToken).ConfigureAwait(false);

        if (!confirmed && (filed.Count == 0 || AnotherAlbum(filed.Select(id => (ReleaseGroupId?)id), albums.Matched)))
        {
            // Of the same title only: as likely another album of that name.
            var titled = filed.Any(id => albums.Titled.Contains(id) && !albums.Matched.Contains(id));
            var question = titled ? ReplacementVerdict.SameTitle : ReplacementVerdict.Unconfirmed;

            Log.ReplacementRefused(logger, trimmed, question);

            return AlbumReplacement.Refused(
                trimmed,
                question,
                titled
                    ? $"'{trimmed}' holds an album of the same title as Qobuz's '{album.Title}' that neither barcode "
                        + "nor billing shows to be it. Replace it, or download this as a separate album. Nothing was "
                        + "downloaded."
                    : (filed.Count == 0
                        ? $"Nothing in '{trimmed}' is filed under an album, so it cannot be shown to be Qobuz's '{album.Title}'."
                        : $"The files in '{trimmed}' are filed under an album that neither barcode nor billing shows to be Qobuz's '{album.Title}'.")
                    + " Confirm to replace it anyway. Nothing was downloaded.");
        }

        var heldTracks = HeldTracks(old);
        var streamable = album.Tracks.Count(track => track.Streamable);

        var verdict = UpgradeReplacement.Offer(
            [.. old.Select(file => file.Quality)], heldTracks, Offered(album, formatId), streamable);

        if (verdict != ReplacementVerdict.Replace)
        {
            Log.ReplacementRefused(logger, trimmed, verdict);
            return AlbumReplacement.Refused(
                trimmed, verdict, ExplainOffer(verdict, heldTracks, streamable));
        }

        return options.Value.AllowFileReplacement
            ? null
            : AlbumReplacement.Refused(
                trimmed,
                ReplacementVerdict.Replace,
                "Qobuz offers an upgrade of this album, but Fonoteca:AllowFileReplacement is off, so "
                + "nothing was downloaded.");
    }

    public async Task<AlbumReplacement> ReplaceAsync(
        string folder,
        int expectedFiles,
        AlbumDownload download,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        ArgumentNullException.ThrowIfNull(download);

        var result = await RetireAsync(folder.Trim('/'), expectedFiles, download, cancellationToken)
            .ConfigureAwait(false);

        // A download fetched to replace an album is wanted only in its place:
        // moved there on a replacement, removed on a refusal. Never on an
        // exception, which can land between archiving the old album and moving
        // the new one in — the hidden copy is then the only one.
        //
        // Kept where a track failed rather than was refused: that is an
        // interrupted download, and asking again resumes it for a stat a track.
        if (FetchedToReplace(download))
        {
            var fetched = Path.Combine(LibraryRoot(options.Value), download.Folder);
            var replaced = result.ArchivedTo is not null;

            if (!replaced && download.Tracks.Any(track => track.Outcome == TrackOutcome.Failed))
            {
                return result with { Detail = $"{result.Detail} Downloading again resumes where this stopped." };
            }

            if (Directory.Exists(fetched)) Directory.Delete(fetched, recursive: true);

            store.PruneEmptyDirectories(new LibraryPath(QobuzDownloadService.ReplacementArea));

            if (!replaced)
            {
                result = result with { Detail = $"{result.Detail} What was downloaded has been removed." };
            }
        }

        return result;
    }

    private async Task<AlbumReplacement> RetireAsync(
        string trimmed,
        int expectedFiles,
        AlbumDownload download,
        CancellationToken cancellationToken)
    {
        var config = options.Value;

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

        var (old, refusal) = await OldAsync(trimmed, expectedFiles, cancellationToken).ConfigureAwait(false);
        if (refusal is not null) return refusal;

        var heldTracks = HeldTracks(old);

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

        var verdict = UpgradeReplacement.Check([.. old.Select(file => file.Quality)], heldTracks, arrived);

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

        var root = LibraryRoot(config);
        var fetched = FetchedToReplace(download);

        if (fetched && Stranger(root, trimmed, old) is { } stranger)
        {
            Log.ReplacementRefused(logger, trimmed, ReplacementVerdict.NotHeld);

            return AlbumReplacement.Refused(
                trimmed,
                ReplacementVerdict.NotHeld,
                $"'{stranger}' is on disk but not in the catalogue, and would stay beside the new album. "
                + "Run a library scan and try again. Nothing was moved.");
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

        // The archived files' rows go with them, the owner's choice, rather
        // than wait for a scan: the new album is about to be filed at the same
        // paths, and an old row there would be found and kept.
        await db.MediaFiles
            .Where(file => moved.Contains(file.Path))
            .ExecuteDeleteAsync(cancellationToken)
            .ConfigureAwait(false);

        if (fetched) MoveInto(root, download, trimmed);

        // The folders the move emptied, disc folders first. One copy of this
        // lives on the store, for the reason its own remarks give.
        foreach (var directory in old
                     .Select(file => Path.GetDirectoryName(file.Path) ?? trimmed)
                     .Distinct(StringComparer.Ordinal)
                     .OrderByDescending(directory => directory.Length))
        {
            store.PruneEmptyDirectories(new LibraryPath(directory));
        }

        var landed = fetched ? trimmed : download.Folder;

        Log.ReplacementDone(logger, trimmed, landed, moved.Count);

        return new AlbumReplacement(
            trimmed,
            landed,
            ReplacementVerdict.Replace,
            Archived: moved.Count,
            ArchivedTo: destination,
            Detail: null);
    }

    /// <summary>The catalogue's files under an album folder, or why they cannot be replaced.</summary>
    private async Task<(List<HeldFile> Old, AlbumReplacement? Refusal)> OldAsync(
        string trimmed,
        int expectedFiles,
        CancellationToken cancellationToken)
    {
        // The trailing slash matters: 'Artist/Album' also prefixes
        // 'Artist/Album Live', and archiving that would be silent.
        var prefix = trimmed + "/";

        var old = await db.MediaFiles
            .AsNoTracking()
            .Where(file => file.Path.StartsWith(prefix))
            .Select(file => new HeldFile(file.Path, file.Quality, file.TrackId, file.ReleaseGroupId))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        if (old.Count == 0)
        {
            Log.ReplacementRefused(logger, trimmed, ReplacementVerdict.NotHeld);

            return (old, AlbumReplacement.Refused(
                trimmed,
                ReplacementVerdict.NotHeld,
                $"The catalogue holds no files under '{trimmed}'. Nothing was moved."));
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

            return (old, AlbumReplacement.Refused(
                trimmed,
                ReplacementVerdict.NotHeld,
                $"'{trimmed}' holds {old.Count} files, but the row said {expectedFiles}. Either "
                + "the library changed since the list was drawn, or that folder covers more than "
                + "the album. Nothing was moved."));
        }

        return (old, null);
    }

    private static int HeldTracks(List<HeldFile> old)
    {
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

        return attributed + old.Count(file => file.TrackId is null);

    }

    /// <summary>
    /// The encoding a download of this album comes back in: its own ceiling,
    /// capped by the format asked for.
    /// </summary>
    /// <remarks>
    /// Format 5 is the only MP3 tier, 6 is CD, 7 stops at 96 kHz and 27 at
    /// 192 — the table on <c>QobuzOptions.FormatId</c>. A lossless album that
    /// states no ceiling is taken to be a CD, the least Qobuz sells as FLAC.
    /// </remarks>
    internal static AudioQuality Offered(QobuzAlbum album, int formatId)
    {
        if (formatId == 5)
        {
            return new AudioQuality
            {
                Codec = "mp3",
                SampleRateHz = 44_100,
                Channels = 2,
                BitrateBps = 320_000,
                IsLossless = false,
            };
        }

        var (depth, rate) = formatId switch
        {
            6 => (16, 44_100),
            7 => (24, 96_000),
            _ => (24, 192_000),
        };

        return new AudioQuality
        {
            Codec = "flac",
            SampleRateHz = Math.Min((int)Math.Round((album.MaximumSamplingRate ?? 44.1) * 1000), rate),
            Channels = 2,
            BitDepth = Math.Min(album.MaximumBitDepth ?? 16, depth),
            BitrateBps = 0,
            IsLossless = true,
        };
    }

    /// <summary>Whether a download was fetched into the hidden area to replace an album.</summary>
    private static bool FetchedToReplace(AlbumDownload download) =>
        download.Folder.StartsWith(QobuzDownloadService.ReplacementArea + "/", StringComparison.Ordinal);

    /// <summary>
    /// Moves a download fetched to replace an album into that album's folder.
    /// </summary>
    /// <remarks>
    /// Into the old folder rather than one named by Qobuz, so whatever else the
    /// folder holds — a sleeve, a booklet — stays with the album, and the scan
    /// finds the new files where the old ones were.
    /// </remarks>
    private static void MoveInto(string root, AlbumDownload download, string folder)
    {
        foreach (var track in download.Tracks)
        {
            if (track.Path is null) continue;

            var target = Path.Combine(root, folder, Path.GetFileName(track.Path));

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Move(Path.Combine(root, track.Path), target);
        }
    }

    /// <summary>Audio in the folder the catalogue does not hold, which archiving would leave behind.</summary>
    /// <remarks>
    /// Asked before the old album is archived. Left there, it would sit beside
    /// the new album as a stray from the old — or take a downloaded track's
    /// name, and fail the move in with neither album in the library.
    /// </remarks>
    private static string? Stranger(string root, string folder, List<HeldFile> old)
    {
        var directory = Path.Combine(root, folder);

        if (!Directory.Exists(directory)) return null;

        var archived = old.Select(file => file.Path).ToHashSet(StringComparer.Ordinal);

        return Directory
            .EnumerateFiles(directory, "*", SearchOption.AllDirectories)
            .Where(AudioFormats.IsAudioFile)
            .Select(path => Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/'))
            .FirstOrDefault(path => !archived.Contains(path));
    }

    private static string LibraryRoot(FonotecaOptions config) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(config.LibraryPath));

    private sealed record HeldFile(string Path, AudioQuality? Quality, TrackId? TrackId, ReleaseGroupId? ReleaseGroupId);

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

        // The rip's own sidecars describe the files just archived — a cue
        // sheet or a playlist names them — and go with them. A sleeve or a
        // booklet describes the album, and stays for the new one.
        var directory = Path.Combine(root, folder.Replace('/', Path.DirectorySeparatorChar));

        if (Directory.Exists(directory))
        {
            foreach (var sidecar in Directory
                         .EnumerateFiles(directory, "*", SearchOption.AllDirectories)
                         .Where(file => RipSidecars.Contains(Path.GetExtension(file)))
                         .ToList())
            {
                var destination = Path.Combine(archive, Path.GetRelativePath(directory, sidecar));
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Move(sidecar, destination, overwrite: true);
            }
        }

        return moved;
    }

    private static readonly HashSet<string> RipSidecars =
        new([".cue", ".m3u", ".m3u8", ".log", ".accurip", ".sfv", ".md5", ".ffp"], StringComparer.OrdinalIgnoreCase);

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

    private static string ExplainOffer(ReplacementVerdict verdict, int heldTracks, int offered) => verdict switch
    {
        ReplacementVerdict.NothingArrived =>
            "Qobuz offers none of this album's tracks. Nothing was downloaded.",

        ReplacementVerdict.Incomplete =>
            $"Qobuz offers {offered} tracks and the album here has {heldTracks} — usually a "
            + "licensing gap. Replacing would lose the rest, so nothing was downloaded.",

        ReplacementVerdict.NotBetter when offered > heldTracks =>
            $"Qobuz's album has {offered} tracks to the {heldTracks} here, but at a lower quality than the "
            + "best file already held. Nothing was downloaded.",

        ReplacementVerdict.NotBetter =>
            "Qobuz offers nothing better than the best file already held, and no tracks it is "
            + "missing. Nothing was downloaded.",

        ReplacementVerdict.NotMeasured =>
            "Nothing has measured the album here, so it cannot be shown to be worse. Run the "
            + "measure pass and try again. Nothing was downloaded.",

        _ => "Nothing was downloaded.",
    };

    private static string Explain(ReplacementVerdict verdict, int heldTracks, int arrived) => verdict switch
    {
        ReplacementVerdict.NothingArrived =>
            "No track downloaded, so there is nothing to replace the album with.",

        ReplacementVerdict.Incomplete =>
            $"Only {arrived} of the album's {heldTracks} tracks downloaded — usually a licensing "
            + "gap. Replacing would lose the rest, so nothing was moved.",

        ReplacementVerdict.NotBetter =>
            "What arrived is no better than the best file already held, so nothing was moved.",

        ReplacementVerdict.NotMeasured =>
            "Nothing has measured the album being replaced, so it cannot be shown to be worse. "
            + "Run the measure pass and try again.",

        _ => "Nothing was moved.",
    };
}

/// <summary>An album folder the library already holds, and how many files are under it.</summary>
/// <param name="Folder">Library-relative album folder.</param>
/// <param name="Files">Catalogued files under it, which a replacement must agree with.</param>
public sealed record HeldFolder(string Folder, int Files);

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
