using Fonoteca.Api.Endpoints;
using Fonoteca.Api.Library;
using Fonoteca.Api.Logging;
using Fonoteca.Data;
using Fonoteca.Domain.Abstractions;
using Fonoteca.Domain.Catalogue;
using Fonoteca.Providers.Qobuz;
using Microsoft.EntityFrameworkCore;

namespace Fonoteca.Api.Acquisition;

/// <summary>
/// What a download does after its last byte lands: files it, then writes its
/// tags, under the gate the acquire endpoint holds (ADR 0011).
/// </summary>
/// <remarks>
/// The tags are written here though no pass is otherwise automated to write
/// them — the owner's choice: a download is one person's click on one album,
/// and its files arrive with no tags at all. Only that album folder is
/// written, through the tag write itself, so the files are named by the same
/// rule as every other album's.
/// </remarks>
public sealed class Acquiring(
    LibraryWorkGate gate,
    DownloadFiling filing,
    TagWriteService tagWrites,
    MusicBrainzCatchUp catchUp,
    ICallerContext caller,
    FonotecaDbContext db,
    ILogger<Acquiring> logger)
{
    /// <summary>The kind a download takes the gate as.</summary>
    public const string Kind = "acquire.download";

    public LibraryWorkGate Gate => gate;

    public DownloadFiling Filing => filing;

    /// <summary>Files a download's landed tracks and writes their album folder.</summary>
    /// <param name="folder">The album folder the tracks are in now.</param>
    /// <param name="moved">Whether a replacement moved them there from where the download put them.</param>
    /// <param name="replacedKnown">
    /// Whether they replaced an album MusicBrainz knows. Such a download is
    /// filed only under a MusicBrainz release its barcode names; otherwise the
    /// passes, which found the old album, are left to find the new one — the
    /// owner's choice.
    /// </param>
    public async Task<AlbumFiling> FileAsync(
        QobuzAlbum album,
        AlbumDownload download,
        string folder,
        bool moved,
        bool replacedKnown,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(album);
        ArgumentNullException.ThrowIfNull(download);

        // An interrupted download is resumed by asking again, which finds what
        // landed by the names the download gave it. Filed and renamed now, it
        // would be taken for an album already held, and never finished.
        var failed = download.Tracks.Count(track => track.Outcome == TrackOutcome.Failed);

        if (failed > 0)
        {
            return AlbumFiling.Left(
                folder,
                $"{failed} track{(failed == 1 ? " did" : "s did")} not arrive. Download the album again to fetch "
                + $"{(failed == 1 ? "it" : "them")}; it is filed in the catalogue once it is whole.");
        }

        var tracks = album.Tracks.ToDictionary(track => track.Id);

        List<(QobuzTrack Track, string Path)> landed =
        [
            .. download.Tracks
                .Where(track => track.Path is not null && tracks.ContainsKey(track.TrackId))
                .Select(track => (tracks[track.TrackId], moved ? $"{folder}/{Path.GetFileName(track.Path)}" : track.Path!)),
        ];

        // MusicBrainz's release, where the shop's barcode and track list name
        // exactly one. Not answering is no reason to leave a download unfiled.
        MusicBrainzRelease? known = null;

        try
        {
            known = await catchUp
                .FindAsync(album.Upc, [.. album.Tracks.Select(track => (track.DiscNumber, track.TrackNumber))], cancellationToken)
                .ConfigureAwait(false);
        }
        catch (ProviderException cause)
        {
            Log.DownloadNotLookedUp(logger, album.Id, cause.Message);
        }

        if (known is null && replacedKnown)
        {
            return AlbumFiling.Left(
                folder,
                "MusicBrainz knows the album this replaced but no release of it with this barcode, so the passes will file it.");
        }

        var filed = await filing.FileAsync(album, landed, caller.ActorId, known, cancellationToken).ConfigureAwait(false);

        if (filed.Files.Count == 0) return new AlbumFiling(filed.AlbumId, folder, 0, filed.Kept, 0, 0, filed.Why);

        if (!tagWrites.MutationAllowed)
        {
            return new AlbumFiling(
                filed.AlbumId, folder, filed.Files.Count, filed.Kept, 0, 0,
                "File writing is off, so the tags were not written.");
        }

        var summary = await tagWrites.WriteFolderAsync(folder, caller.ActorId, null, cancellationToken).ConfigureAwait(false);

        var first = new MediaFileId(filed.Files[0]);
        var here = await db.MediaFiles
            .AsNoTracking()
            .Where(file => file.Id == first)
            .Select(file => file.Path)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        return new AlbumFiling(
            filed.AlbumId,
            here is null ? folder : AlbumFolder.Of(here),
            filed.Files.Count,
            filed.Kept,
            summary.Written,
            summary.Refused + summary.Failed + summary.Unsupported,
            null);
    }
}
