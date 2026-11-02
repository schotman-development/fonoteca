using System.Globalization;
using Fonoteca.Data;
using Fonoteca.Domain.Abstractions;
using Fonoteca.Domain.Catalogue;
using Microsoft.EntityFrameworkCore;

namespace Fonoteca.Api.Subsonic;

/// <summary>
/// Where a client left off: <c>savePlayQueue</c> and <c>getPlayQueue</c>,
/// kept in <see cref="PlayQueue"/>.
/// </summary>
/// <remarks>
/// <b>One queue per account, the protocol's model</b>: the last save wins,
/// from whichever client, and <c>changedBy</c> names it. A save with no song
/// clears it; one with songs names the song playing in <c>current</c>, which
/// must be one of them, and <c>position</c> is milliseconds into it.
///
/// <b>A song id is a file, as in a playlist</b>, and the queue keeps every id
/// sent whether the library holds it or not. What the library no longer
/// holds is left out when the queue is read, as <c>getPlaylist</c> leaves it
/// out; where that is the song playing, the queue resumes at the start of the
/// next song it holds, else of the nearest one before.
///
/// <b>Nothing saved is answered as Navidrome answers it</b>: the owner, a
/// <c>changed</c> in the year 1 and no <c>changedBy</c>, which clients read as
/// no queue. The spec requires <c>playQueue</c> and those three, and says
/// nothing of this case.
/// </remarks>
public static partial class SubsonicEndpoints
{
    private static async Task<IResult> SavePlayQueue(
        HttpContext http,
        FonotecaDbContext db,
        IClock clock,
        CancellationToken cancellationToken)
    {
        var (songs, problem) = FilesOf(http.Request.Query["id"]);

        if (problem is not null) return SubsonicResult.Error(70, problem);

        if (songs.Count == 0)
        {
            await db.PlayQueues.ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
            return SubsonicResult.Ok();
        }

        if (Text(http, "current") is not { } playing) return SubsonicResult.Error(10, "Required parameter is missing: current.");

        var current = SubsonicIds.AsSong(playing) is { } song ? songs.IndexOf(song.Value) : -1;

        if (current < 0) return SubsonicResult.Error(10, $"The current song is not one of the songs in the queue: {playing}.");

        var position = long.TryParse(Text(http, "position"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var milliseconds) ? Math.Max(milliseconds, 0) : 0;
        // PostgreSQL's text refuses a NUL, so it is kept as U+2400, as the tag journal keeps one.
        var changedBy = (Text(http, "c") ?? string.Empty).Replace('\0', '\u2400');
        var changed = StoreTime.ToStorePrecision(clock.UtcNow);

        // One statement, so two saves from one client at once cannot both
        // find no queue and collide on the key; the later one wins.
        await db.Database
            .ExecuteSqlAsync(
                $"""
                INSERT INTO "PlayQueues" ("AccountId", "Songs", "Current", "PositionMs", "ChangedUtc", "ChangedBy")
                VALUES ({db.AccountId}, {songs}, {current}, {position}, {changed}, {changedBy})
                ON CONFLICT ("AccountId") DO UPDATE SET
                    "Songs" = excluded."Songs",
                    "Current" = excluded."Current",
                    "PositionMs" = excluded."PositionMs",
                    "ChangedUtc" = excluded."ChangedUtc",
                    "ChangedBy" = excluded."ChangedBy"
                """,
                cancellationToken)
            .ConfigureAwait(false);

        return SubsonicResult.Ok();
    }

    private static async Task<IResult> GetPlayQueue(
        HttpContext http,
        FonotecaDbContext db,
        CancellationToken cancellationToken)
    {
        var owner = Owner(http);
        var queue = await db.PlayQueues.AsNoTracking().FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);

        if (queue is null)
        {
            return SubsonicResult.Ok("playQueue", root =>
            {
                root.Attr("username", owner);
                root.Attr("changed", DateTimeOffset.MinValue);
                root.Attr("changedBy", string.Empty);
            });
        }

        List<MediaFileId> copies = [.. queue.Songs.Distinct().Select(file => new MediaFileId(file))];

        var held = (await SongsAsync(db, db.MediaFiles.AsNoTracking().Where(file => copies.Contains(file.Id)), cancellationToken)
                .ConfigureAwait(false))
            .ToDictionary(song => song.Id);

        var entries = queue.Songs.Select(file => held.GetValueOrDefault(SubsonicIds.Song(new MediaFileId(file)))).ToList();
        var resumed = Resumed(entries, queue.Current);

        return SubsonicResult.Ok("playQueue", root =>
        {
            root.Attr("current", resumed is { } at ? entries[at]!.Id : null);
            root.Attr("position", resumed == queue.Current ? queue.PositionMs : 0);
            root.Attr("username", owner);
            root.Attr("changed", queue.ChangedUtc);
            root.Attr("changedBy", queue.ChangedBy);
            root.Children("entry", entries.OfType<Song>(), WriteSong);
        });
    }

    /// <summary>The place to resume at: the song playing, else the next one held, else the nearest before; null where none is.</summary>
    private static int? Resumed(List<Song?> entries, int current)
    {
        for (var at = current; at < entries.Count; at++)
        {
            if (entries[at] is not null) return at;
        }

        for (var at = Math.Min(current, entries.Count) - 1; at >= 0; at--)
        {
            if (entries[at] is not null) return at;
        }

        return null;
    }
}
