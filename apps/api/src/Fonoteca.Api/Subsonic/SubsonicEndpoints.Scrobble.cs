using System.Globalization;
using Fonoteca.Api.Listening;
using Fonoteca.Data;
using Fonoteca.Domain.Abstractions;
using Fonoteca.Domain.Catalogue;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Primitives;
using Npgsql;

namespace Fonoteca.Api.Subsonic;

/// <summary>
/// <c>scrobble</c>: a listener's play, counted here and sent on to ListenBrainz
/// (client issue #67, the owner's choice, as Navidrome does).
/// </summary>
/// <remarks>
/// <b><c>submission=false</c> is what is playing now</b>: told to ListenBrainz,
/// counted nowhere. <b>Otherwise each <c>id</c> is a play</b> that began at
/// the <c>time</c> beside it, milliseconds since the epoch, else now; kept to
/// the second (<see cref="Play"/>), so a play reported twice — a client's
/// offline queue sent again — counts once. A song id for no file here, or a
/// folder entry no pass has catalogued, is acknowledged and counted nowhere,
/// as the protocol allows.
/// </remarks>
public static partial class SubsonicEndpoints
{
    private static async Task<IResult> Scrobble(
        HttpContext http,
        FonotecaDbContext db,
        ListenBrainzService listenBrainz,
        IClock clock,
        CancellationToken cancellationToken)
    {
        var ids = http.Request.Query["id"];

        if (ids.Count == 0) return SubsonicResult.Error(10, "Required parameter is missing: id.");

        var account = db.AccountId!.Value;
        var times = http.Request.Query["time"];
        var nowPlaying = string.Equals(Text(http, "submission"), "false", StringComparison.OrdinalIgnoreCase);

        var asked = ids.Select((id, index) => (File: SubsonicIds.AsSong(id), Index: index)).Where(song => song.File is not null).ToList();
        var wanted = asked.Select(song => song.File!.Value).Distinct().ToList();

        var files = await db.MediaFiles
            .AsNoTracking()
            .Where(file => wanted.Contains(file.Id))
            .Select(file => new { file.Id, file.RecordingId, file.ReleaseGroupId })
            .ToDictionaryAsync(file => file.Id, cancellationToken)
            .ConfigureAwait(false);

        if (nowPlaying)
        {
            var playing = asked.Select(song => song.File!.Value).Where(files.ContainsKey).ToList();

            if (playing.Count > 0) listenBrainz.Playing(account, playing[^1]);

            return SubsonicResult.Ok();
        }

        var plays = asked
            .Where(song => files.ContainsKey(song.File!.Value))
            .Select(song => (File: files[song.File!.Value], Began: Began(times, song.Index, clock)))
            .DistinctBy(play => play.Began)
            .ToList();

        var began = plays.Select(play => play.Began).ToList();

        var known = await db.Plays
            .Where(play => began.Contains(play.PlayedUtc))
            .Select(play => play.PlayedUtc)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        foreach (var (file, at) in plays.Where(play => !known.Contains(play.Began)))
        {
            db.Plays.Add(new Play
            {
                Id = Guid.CreateVersion7(),
                AccountId = account,
                PlayedUtc = at,
                MediaFileId = file.Id,
                RecordingId = file.RecordingId,
                ReleaseGroupId = file.ReleaseGroupId,
            });
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException cause) when (cause.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // The same play, reported twice at once: it is counted already.
        }

        listenBrainz.Wake(account);

        return SubsonicResult.Ok();
    }

    /// <summary>When the play at <paramref name="index"/> began, to the second: its <c>time</c>, else now.</summary>
    private static DateTimeOffset Began(StringValues times, int index, IClock clock)
    {
        var stated = index < times.Count
            && long.TryParse(times[index], NumberStyles.None, CultureInfo.InvariantCulture, out var milliseconds)
            && milliseconds <= DateTimeOffset.MaxValue.ToUnixTimeMilliseconds()
                ? DateTimeOffset.FromUnixTimeMilliseconds(milliseconds)
                : clock.UtcNow;

        return DateTimeOffset.FromUnixTimeSeconds(stated.ToUnixTimeSeconds());
    }
}
