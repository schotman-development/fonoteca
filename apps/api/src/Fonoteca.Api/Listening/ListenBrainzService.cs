using System.Threading.Channels;
using Fonoteca.Api.Endpoints;
using Fonoteca.Api.Logging;
using Fonoteca.Api.Subsonic;
using Fonoteca.Data;
using Fonoteca.Domain.Abstractions;
using Fonoteca.Domain.Catalogue;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace Fonoteca.Api.Listening;

/// <summary>
/// Each account's plays, to and from ListenBrainz (client issue #67): its
/// listens counted once when it connects, and every play here sent on.
/// </summary>
/// <remarks>
/// <b>The client tells the server, the server tells ListenBrainz</b>, the
/// owner's choice, as Navidrome does: a play is stored first
/// (<see cref="Play"/>) and sent after, oldest first, with the time it began,
/// so one heard offline and reported late is a listen at its own time. What is
/// playing now is sent as it is heard of and kept nowhere.
///
/// <b>Woken, never swept</b>: by a scrobble, by connecting, and once for every
/// connected account when the server starts, which is when a send that failed
/// is tried again — otherwise at that account's next play. One account at a
/// time, so two wakes cannot send one play twice; ListenBrainz's gate is one
/// request a second anyway.
///
/// <b>The seed counts only what the library holds</b> (<see cref="ListenMatch"/>),
/// marks each counted listen as sent, and marks a play here at the same second
/// as sent too, since ListenBrainz has it. A listen of nothing held is not
/// kept. A seed cut short starts over on the next wake and counts nothing
/// twice, because a play is one per account per second.
/// </remarks>
public sealed class ListenBrainzService(
    IServiceScopeFactory scopes,
    IListenBrainz listenBrainz,
    IClock clock,
    IHostApplicationLifetime lifetime,
    ILogger<ListenBrainzService> logger) : BackgroundService
{
    /// <summary>Plays sent in one submission; well inside ListenBrainz's thousand and its request size.</summary>
    private const int SendBatch = 100;

    private readonly Channel<WakeUp> _wakes = Channel.CreateUnbounded<WakeUp>(new UnboundedChannelOptions { SingleReader = true });

    /// <summary>Counts the account's listens if they have not been, and sends its plays not yet sent.</summary>
    public void Wake(Guid account) => _wakes.Writer.TryWrite(new WakeUp(account, NowPlaying: null));

    /// <summary>Tells ListenBrainz what the account is playing now.</summary>
    public void Playing(Guid account, MediaFileId file) => _wakes.Writer.TryWrite(new WakeUp(account, file));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            // After StartupTasks has migrated the database, which a hosted
            // service registered before it does not wait for.
            await StartedAsync(stoppingToken).ConfigureAwait(false);

            try
            {
                await WakeConnectedAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (Exception error) when (error is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
            {
                Log.ListenBrainzFailed(logger, Guid.Empty, error);
            }

            await foreach (var wake in _wakes.Reader.ReadAllAsync(stoppingToken).ConfigureAwait(false))
            {
                try
                {
                    await HandleAsync(wake, stoppingToken).ConfigureAwait(false);
                }
                catch (Exception error) when (error is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
                {
                    // An exception out of a BackgroundService stops the host;
                    // the plays stay unsent and go at the next wake.
                    Log.ListenBrainzFailed(logger, wake.Account, error);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private async Task StartedAsync(CancellationToken cancellationToken)
    {
        var started = new TaskCompletionSource();

        using (lifetime.ApplicationStarted.Register(() => started.TrySetResult()))
        {
            await started.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task WakeConnectedAsync(CancellationToken cancellationToken)
    {
        var scope = scopes.CreateAsyncScope();

        await using (scope.ConfigureAwait(false))
        {
            var db = scope.ServiceProvider.GetRequiredService<FonotecaDbContext>();

            foreach (var account in await db.Users
                         .Where(account => account.ListenBrainzToken != null)
                         .Select(account => account.Id)
                         .ToListAsync(cancellationToken)
                         .ConfigureAwait(false))
            {
                Wake(account);
            }
        }
    }

    private async Task HandleAsync(WakeUp wake, CancellationToken cancellationToken)
    {
        var scope = scopes.CreateAsyncScope();

        await using (scope.ConfigureAwait(false))
        {
            var db = scope.ServiceProvider.GetRequiredService<FonotecaDbContext>();
            var protection = scope.ServiceProvider.GetRequiredService<IDataProtectionProvider>();

            if (await db.Users.FindAsync([wake.Account], cancellationToken).ConfigureAwait(false) is not { } account
                || AccountEndpoints.ListenBrainzToken(protection, account) is not { } token)
            {
                return;
            }

            db.AccountId = account.Id;

            if (wake.NowPlaying is { } file)
            {
                await SendPlayingAsync(db, token, file, cancellationToken).ConfigureAwait(false);
                return;
            }

            if (account.ListenBrainzSeededUtc is null && account.ListenBrainzUser is { } user)
            {
                await SeedAsync(db, account, user, cancellationToken).ConfigureAwait(false);
            }

            await SendPlaysAsync(db, token, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task SendPlayingAsync(FonotecaDbContext db, string token, MediaFileId file, CancellationToken cancellationToken)
    {
        var songs = await SubsonicEndpoints.SongsOfAsync(db, [file], cancellationToken).ConfigureAwait(false);

        if (songs.TryGetValue(file, out var song) && Listen(song, at: null) is { } listen)
        {
            await listenBrainz.SubmitAsync(token, [listen], cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task SeedAsync(FonotecaDbContext db, Account account, string user, CancellationToken cancellationToken)
    {
        var match = new ListenMatch(await HeldSongsAsync(db, cancellationToken).ConfigureAwait(false));
        var counted = 0;
        DateTimeOffset? before = null;

        while (true)
        {
            var page = await listenBrainz.ListensAsync(user, before, cancellationToken).ConfigureAwait(false);

            if (page.Count == 0) break;

            counted += await CountAsync(db, account.Id, page, match, cancellationToken).ConfigureAwait(false);

            var oldest = page.Min(listen => listen.ListenedAt);

            if (before is { } previous && oldest >= previous) break;

            before = oldest;
        }

        account.ListenBrainzSeededUtc = StoreTime.ToStorePrecision(clock.UtcNow);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        Log.ListenBrainzSeeded(logger, account.Id, user, counted);
    }

    /// <summary>Counts one page of listens as plays; the number new here.</summary>
    private async Task<int> CountAsync(
        FonotecaDbContext db,
        Guid account,
        IReadOnlyList<HeardListen> page,
        ListenMatch match,
        CancellationToken cancellationToken)
    {
        var now = StoreTime.ToStorePrecision(clock.UtcNow);
        var times = page.Select(listen => listen.ListenedAt).Distinct().ToList();

        var held = await db.Plays
            .Where(play => times.Contains(play.PlayedUtc))
            .ToDictionaryAsync(play => play.PlayedUtc, cancellationToken)
            .ConfigureAwait(false);

        var counted = 0;

        foreach (var listen in page)
        {
            if (held.TryGetValue(listen.ListenedAt, out var known))
            {
                known.ForwardedUtc ??= now;
                continue;
            }

            if (match.Of(listen) is not { } song) continue;

            var play = new Play
            {
                Id = Guid.CreateVersion7(),
                AccountId = account,
                PlayedUtc = listen.ListenedAt,
                MediaFileId = song.File,
                RecordingId = song.Recording,
                ReleaseGroupId = song.AlbumId,
                ForwardedUtc = now,
            };

            db.Plays.Add(play);
            held[play.PlayedUtc] = play;
            counted++;
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return counted;
    }

    /// <summary>Every held file with a recording, as a listen is matched against it.</summary>
    /// <remarks>
    /// ponytail: the whole library in memory, once per connection; page it by
    /// title if a seed at 100k files shows.
    /// </remarks>
    private static async Task<List<HeldSong>> HeldSongsAsync(FonotecaDbContext db, CancellationToken cancellationToken)
    {
        var rows = await db.MediaFiles
            .AsNoTracking()
            .Where(file => file.RecordingId != null)
            .Select(file => new
            {
                file.Id,
                Recording = file.RecordingId!.Value,
                file.ReleaseGroupId,
                file.Recording!.Mbid,
                Title = file.Track != null && file.Track.Title != null ? file.Track.Title : file.Recording.Title,
                Album = file.ReleaseGroup != null ? file.ReleaseGroup.Title : null,
                Credits = file.Recording.Credits
                    .OrderBy(credit => credit.Position)
                    .Select(credit => new { Name = credit.CreditedAs ?? credit.Artist!.Name, credit.JoinPhrase })
                    .ToList(),
            })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return
        [
            .. rows
                .Select(row => (row, Artist: CatalogueEndpoints.CreditLine(row.Credits.Select(credit => (credit.Name, credit.JoinPhrase)))))
                .Where(song => song.Artist is not null)
                .Select(song => new HeldSong(song.row.Id, song.row.Recording, song.row.ReleaseGroupId, song.row.Mbid, song.row.Title, song.Artist!, song.row.Album)),
        ];
    }

    private async Task SendPlaysAsync(FonotecaDbContext db, string token, CancellationToken cancellationToken)
    {
        while (true)
        {
            var batch = await db.Plays
                .Where(play => play.ForwardedUtc == null)
                .OrderBy(play => play.PlayedUtc)
                .Take(SendBatch)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            if (batch.Count == 0) return;

            var files = batch.Where(play => play.MediaFileId is not null).Select(play => play.MediaFileId!.Value).Distinct().ToList();
            var songs = await SubsonicEndpoints.SongsOfAsync(db, files, cancellationToken).ConfigureAwait(false);

            var listens = new List<(Play Play, ListenToSubmit Listen)>();

            foreach (var play in batch)
            {
                if (play.MediaFileId is { } file && songs.TryGetValue(file, out var song) && Listen(song, play.PlayedUtc) is { } listen)
                {
                    listens.Add((play, listen));
                }
                else
                {
                    // Nothing left to say what it was: the file is gone, or names no artist.
                    Log.PlayNotSent(logger, play.Id);
                }
            }

            if (listens.Count > 0 && !await SubmitAsync(token, [.. listens.Select(sent => sent.Listen)], cancellationToken).ConfigureAwait(false))
            {
                return;
            }

            var now = StoreTime.ToStorePrecision(clock.UtcNow);
            foreach (var play in batch) play.ForwardedUtc = now;

            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Sends the listens; false where the token was refused, so they wait for
    /// a token that works. Listens ListenBrainz refuses are marked as sent,
    /// since sending them again would be refused again.
    /// </summary>
    private async Task<bool> SubmitAsync(string token, IReadOnlyList<ListenToSubmit> listens, CancellationToken cancellationToken)
    {
        try
        {
            await listenBrainz.SubmitAsync(token, listens, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (ProviderRejectedException refusal)
        {
            if (await listenBrainz.UserOfAsync(token, cancellationToken).ConfigureAwait(false) is null)
            {
                Log.ListenBrainzTokenRefused(logger);
                return false;
            }

            Log.ListensRefused(logger, listens.Count, refusal.Message);
            return true;
        }
    }

    /// <summary>A song as ListenBrainz is told of it: as a client is shown it, at whole seconds of when it began.</summary>
    private static ListenToSubmit? Listen(Song song, DateTimeOffset? at) =>
        song.Artist is null
            ? null
            : new ListenToSubmit(
                at is { } began ? DateTimeOffset.FromUnixTimeSeconds(began.ToUnixTimeSeconds()) : null,
                song.Artist,
                song.Title,
                song.Album,
                song.Mbid,
                song.Track,
                song.DurationSeconds);

    private sealed record WakeUp(Guid Account, MediaFileId? NowPlaying);
}
