using Fonoteca.Data;
using Fonoteca.Domain.Catalogue;
using Microsoft.EntityFrameworkCore;

namespace Fonoteca.Api.Library;

/// <summary>
/// The ReplayGain album values and the album DR of the album folders some files sit in.
/// </summary>
/// <remarks>
/// <b>An album folder, not a release group</b>: ReplayGain's album is the
/// programme a listener plays straight through, which is one rip — two rips of
/// one album are two programmes. A file directly under an artist has no album
/// folder and no album gain. Computed when asked rather than stored, because a
/// file added to the folder changes the answer for every file already in it.
/// One rule for the tag write and Subsonic, so a file and a client agree.
/// </remarks>
internal static class AlbumLoudness
{
    /// <summary>An album folder's ReplayGain and DR, each null unless every file in it has its reading.</summary>
    public sealed record Figures((double Gain, double Peak)? ReplayGain, int? DynamicRange);

    public static async Task<Dictionary<string, Figures>> ForAsync(
        FonotecaDbContext db,
        IEnumerable<string> paths,
        CancellationToken cancellationToken)
    {
        var folders = paths.Where(InAlbumFolder).Select(AlbumFolder.Of).Distinct(StringComparer.Ordinal).ToList();

        if (folders.Count == 0) return new Dictionary<string, Figures>(StringComparer.Ordinal);

        var prefixes = folders.Select(folder => folder + "/").ToList();

        var siblings = await db.MediaFiles
            .AsNoTracking()
            .Where(file => prefixes.Any(prefix => file.Path.StartsWith(prefix)))
            .Select(file => new { file.Path, file.Measurement })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return siblings
            .GroupBy(file => AlbumFolder.Of(file.Path), StringComparer.Ordinal)
            .Where(folder => folders.Contains(folder.Key))
            .ToDictionary(
                folder => folder.Key,
                folder => new Figures(
                    AudioAnalysis.Album([.. folder.Select(file => file.Measurement)]),
                    AudioAnalysis.AlbumDynamicRange(folder.Select(file => file.Measurement))),
                StringComparer.Ordinal);
    }

    /// <summary>Whether a path is deep enough to sit in an album folder rather than loose under an artist.</summary>
    public static bool InAlbumFolder(string path) =>
        path.Count(c => c is '/' or '\\') >= AlbumFolder.Depth;
}
