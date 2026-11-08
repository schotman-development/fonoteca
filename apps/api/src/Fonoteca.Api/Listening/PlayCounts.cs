using Fonoteca.Data;
using Fonoteca.Domain.Catalogue;
using Microsoft.EntityFrameworkCore;

namespace Fonoteca.Api.Listening;

/// <summary>How often, and when last, the signed-in listener played each song and album.</summary>
/// <remarks>
/// <b>A song's plays are its recording's</b>, so every copy of it agrees, as
/// its heart does; a file with no recording counts its own. <b>An album's are
/// the plays of the files filed under it</b>, so a song on the album and on a
/// best-of counts on the one it was heard from. Both read through the file to
/// what it is now, and fall back on what it was when played where the file
/// is gone (<see cref="Play"/>). The context's account filter says whose.
/// </remarks>
internal static class PlayCounts
{
    internal readonly record struct Count(int Plays, DateTimeOffset Last);

    internal static async Task<(Dictionary<RecordingId, Count> Recordings, Dictionary<MediaFileId, Count> Files)> OfSongsAsync(
        FonotecaDbContext db,
        IReadOnlyCollection<RecordingId> recordings,
        IReadOnlyCollection<MediaFileId> files,
        CancellationToken cancellationToken)
    {
        var plays = await db.Plays
            .AsNoTracking()
            .Select(play => new
            {
                Recording = play.MediaFile != null ? play.MediaFile.RecordingId : play.RecordingId,
                play.MediaFileId,
                play.PlayedUtc,
            })
            .Where(play => play.Recording != null
                ? recordings.Contains(play.Recording.Value)
                : play.MediaFileId != null && files.Contains(play.MediaFileId.Value))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return (
            plays
                .Where(play => play.Recording is not null)
                .GroupBy(play => play.Recording!.Value)
                .ToDictionary(song => song.Key, song => new Count(song.Count(), song.Max(play => play.PlayedUtc))),
            plays
                .Where(play => play.Recording is null)
                .GroupBy(play => play.MediaFileId!.Value)
                .ToDictionary(file => file.Key, file => new Count(file.Count(), file.Max(play => play.PlayedUtc))));
    }

    internal static async Task<Dictionary<ReleaseGroupId, Count>> OfAlbumsAsync(
        FonotecaDbContext db,
        CancellationToken cancellationToken) =>
        (await db.Plays
            .AsNoTracking()
            .Select(play => new
            {
                Album = play.MediaFile != null ? play.MediaFile.ReleaseGroupId : play.ReleaseGroupId,
                play.PlayedUtc,
            })
            .Where(play => play.Album != null)
            .GroupBy(play => play.Album)
            .Select(album => new { album.Key, Plays = album.Count(), Last = album.Max(play => play.PlayedUtc) })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false))
        .ToDictionary(album => album.Key!.Value, album => new Count(album.Plays, album.Last));

    /// <summary>Every recording the listener has played, with how often: what a piece's plays are summed from.</summary>
    internal static async Task<Dictionary<RecordingId, int>> OfRecordingsAsync(
        FonotecaDbContext db,
        CancellationToken cancellationToken) =>
        (await db.Plays
            .AsNoTracking()
            .Select(play => play.MediaFile != null ? play.MediaFile.RecordingId : play.RecordingId)
            .Where(recording => recording != null)
            .GroupBy(recording => recording)
            .Select(song => new { song.Key, Plays = song.Count() })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false))
        .ToDictionary(song => song.Key!.Value, song => song.Plays);
}
