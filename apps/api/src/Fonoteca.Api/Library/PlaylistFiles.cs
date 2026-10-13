using System.Security.Cryptography;
using System.Text;
using Fonoteca.Api.Endpoints;
using Fonoteca.Api.Logging;
using Fonoteca.Data;
using Fonoteca.Domain.Abstractions;
using Fonoteca.Domain.Catalogue;
using Fonoteca.Ingest;
using Microsoft.EntityFrameworkCore;

namespace Fonoteca.Api.Library;

/// <summary>
/// The m3u files under the library's Playlists folder: reading them, and every
/// write anything here makes to them.
/// </summary>
/// <remarks>
/// <b>A static playlist is its file and nothing else</b> (the owner's choice):
/// whatever another program writes there is believed, and Fonoteca changes a
/// line only where it moved the file that line names — the tag write's
/// renames and their undo, and a move in the file manager, each calling
/// <see cref="RepointAsync"/> straight after its own move commits. A file that
/// leaves the library leaves its line naming where it was: a copy of the same
/// song on another release is not a replacement. The one exception is an
/// upgrade, which <see cref="FollowFormatsAsync"/> carries from the old rip to
/// the new file of the same name in another format.
///
/// <b>A smart playlist's file is Fonoteca's</b> and is written whole from its
/// rules by <see cref="RefreshAsync"/>, after every release of
/// <see cref="LibraryWorkGate"/> — every pass, undo, tag edit, download and
/// answer on Identify — and every scan, which never takes the gate.
///
/// <b>One write at a time</b>, every kind through one semaphore, so a rename's
/// repoint cannot interleave with an edit's read and write. Each write is a
/// staged sibling renamed over the file, so a player never reads half of one.
/// A file that is not UTF-8 is read as Latin-1 and never written: a byte
/// written back through the wrong decoding is a path some other player can no
/// longer find.
///
/// <b>Nothing is written while the library looks unmounted.</b> A root holding
/// nothing but the playlists folder is a mount point with nothing behind it,
/// and writing there would put the folder on the host's own disk (rule 3).
/// </remarks>
public sealed class PlaylistFiles(
    IServiceScopeFactory scopeFactory,
    FileSystemAudioFileStore store,
    LibraryWorkGate gate,
    IClock clock,
    ILogger<PlaylistFiles> logger) : IHostedService, IDisposable
{
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private static readonly byte[] Bom = [0xEF, 0xBB, 0xBF];

    private readonly SemaphoreSlim _one = new(1, 1);

    /// <summary>1 while a refresh is asked for and has not yet started.</summary>
    private int _wanted;

    public void Dispose() => _one.Dispose();

    Task IHostedService.StartAsync(CancellationToken cancellationToken)
    {
        gate.Released += Refresh;
        return Task.CompletedTask;
    }

    Task IHostedService.StopAsync(CancellationToken cancellationToken)
    {
        gate.Released -= Refresh;
        return Task.CompletedTask;
    }

    /// <summary>
    /// Asks for every smart playlist to be worked out again, in the background;
    /// asks made while one is waiting to start are that one.
    /// </summary>
    public void Refresh()
    {
        if (Interlocked.Exchange(ref _wanted, 1) == 1) return;

        _ = Task.Run(async () =>
        {
            try
            {
                await _one.WaitAsync().ConfigureAwait(false);
            }
            catch (ObjectDisposedException)
            {
                return;
            }

            try
            {
                Interlocked.Exchange(ref _wanted, 0);
                await RefreshLockedAsync(null, CancellationToken.None).ConfigureAwait(false);
            }
#pragma warning disable CA1031 // Nobody awaits this; a failure is logged and the next release tries again.
            catch (Exception cause)
#pragma warning restore CA1031
            {
                Log.PlaylistSyncFailed(logger, cause);
            }
            finally
            {
                _one.Release();
            }
        });
    }

    /// <summary>Works out one smart playlist, or all of them, and rewrites each file whose text changed.</summary>
    /// <returns>How many files were written.</returns>
    public async Task<int> RefreshAsync(PlaylistId? only, CancellationToken cancellationToken = default)
    {
        await _one.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            return await RefreshLockedAsync(only, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _one.Release();
        }
    }

    private async Task<int> RefreshLockedAsync(PlaylistId? only, CancellationToken cancellationToken)
    {
        var scope = scopeFactory.CreateAsyncScope();

        await using (scope.ConfigureAwait(false))
        {
            var db = scope.ServiceProvider.GetRequiredService<FonotecaDbContext>();

            var playlists = await db.SmartPlaylists
                .AsNoTracking()
                .Where(playlist => only == null || playlist.Id == only)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            if (playlists.Count == 0 || !Mounted()) return 0;

            var written = 0;
            var now = clock.UtcNow;

            foreach (var playlist in playlists)
            {
                if (SmartRules.Parse(playlist.CriteriaJson) is not { } criteria)
                {
                    Log.SmartPlaylistUnreadable(logger, playlist.Path);
                    continue;
                }

                var songs = await SmartQuery
                    .SongsAsync(db, criteria, Seed(playlist), now, cancellationToken)
                    .ConfigureAwait(false);

                var lines = await LinesAsync(db, songs, cancellationToken).ConfigureAwait(false);

                if (await WriteLockedAsync(playlist.Path, PlaylistFile.Render(playlist.Name, lines), bom: false, cancellationToken)
                        .ConfigureAwait(false))
                {
                    written++;
                }
            }

            return written;
        }
    }

    /// <summary>What deals a smart playlist's random order: its own, and dealt again when its rules are saved.</summary>
    internal static string Seed(SmartPlaylist playlist) =>
        $"{playlist.Id.Value:N}{playlist.ChangedUtc.UtcTicks}";

    /// <summary>The m3u lines for these songs, each named by its billing as the catalogue has it.</summary>
    internal static async Task<List<PlaylistLine>> LinesAsync(
        FonotecaDbContext db,
        IReadOnlyList<SmartSong> songs,
        CancellationToken cancellationToken)
    {
        var billing = await BillingAsync(db, [.. songs.Select(song => song.RecordingId)], cancellationToken).ConfigureAwait(false);

        return [.. songs.Select(song => new PlaylistLine(
            song.Copy.Path,
            song.Duration,
            billing.GetValueOrDefault(song.RecordingId),
            song.Title))];
    }

    /// <summary>Each recording's credit line, as an m3u's <c>#EXTINF</c> names the artist.</summary>
    internal static async Task<Dictionary<RecordingId, string?>> BillingAsync(
        FonotecaDbContext db,
        List<RecordingId> recordings,
        CancellationToken cancellationToken)
    {
        var credits = await db.Recordings
            .AsNoTracking()
            .Where(recording => recordings.Contains(recording.Id))
            .Select(recording => new
            {
                recording.Id,
                Credits = recording.Credits
                    .OrderBy(credit => credit.Position)
                    .Select(credit => new
                    {
                        Name = credit.CreditedAs ?? credit.Artist!.LatinName ?? credit.Artist!.Name,
                        credit.JoinPhrase,
                    })
                    .ToList(),
            })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return credits.ToDictionary(
            recording => recording.Id,
            recording => CatalogueEndpoints.CreditLine(recording.Credits.Select(credit => (credit.Name, credit.JoinPhrase))));
    }

    /// <summary>
    /// Every entry naming a moved path, in every playlist, pointing where it
    /// went. Called after a move has committed; a playlist that will not
    /// change is logged and left, never the move's failure.
    /// </summary>
    public async Task RepointAsync(IReadOnlyList<PlaylistMove> moves, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(moves);

        if (moves.Count == 0) return;

        await _one.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            foreach (var path in AllPaths())
            {
                if (await ReadLockedAsync(path, cancellationToken).ConfigureAwait(false) is not { Editable: true } file) continue;

                var text = PlaylistFile.Repoint(file.Text, path, store.Root, moves);

                if (!ReferenceEquals(text, file.Text)) await WriteLockedAsync(path, text, file.Bom, cancellationToken).ConfigureAwait(false);
            }
        }
#pragma warning disable CA1031 // The move this follows has happened; a playlist left behind is logged, not the move's failure.
        catch (Exception cause)
#pragma warning restore CA1031
        {
            Log.PlaylistSyncFailed(logger, cause);
        }
        finally
        {
            _one.Release();
        }
    }

    /// <summary>
    /// After an upgrade: each entry naming a file in this album folder that is
    /// no longer there, pointing at the one audio file beside it of the same
    /// name in another format — the MP3 a FLAC replaced.
    /// </summary>
    /// <remarks>
    /// The owner's choice, and only here: everywhere else a line follows a
    /// file only where Fonoteca moved it. Exactly one candidate, or the line
    /// stays as it was.
    /// </remarks>
    public async Task FollowFormatsAsync(string folder, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(folder);

        await _one.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            var prefix = folder + "/";

            foreach (var path in AllPaths())
            {
                if (await ReadLockedAsync(path, cancellationToken).ConfigureAwait(false) is not { Editable: true } file) continue;

                var moves = PlaylistFile.Items(file.Text)
                    .Select(item => PlaylistFile.Resolve(item.Location, path, store.Root))
                    .OfType<string>()
                    .Where(line => line.StartsWith(prefix, StringComparison.Ordinal) && !File.Exists(AbsolutePathFor(line)))
                    .Distinct(StringComparer.Ordinal)
                    .Select(line => (Line: line, Now: Reformatted(line)))
                    .Where(pair => pair.Now is not null)
                    .Select(pair => new PlaylistMove(pair.Line, pair.Now!))
                    .ToList();

                if (moves.Count == 0) continue;

                var text = PlaylistFile.Repoint(file.Text, path, store.Root, moves);

                if (!ReferenceEquals(text, file.Text)) await WriteLockedAsync(path, text, file.Bom, cancellationToken).ConfigureAwait(false);
            }
        }
#pragma warning disable CA1031 // The upgrade has happened; a playlist left behind is logged, not the upgrade's failure.
        catch (Exception cause)
#pragma warning restore CA1031
        {
            Log.PlaylistSyncFailed(logger, cause);
        }
        finally
        {
            _one.Release();
        }
    }

    /// <summary>The one audio file beside a missing one with its name and another extension; null with none, or more than one.</summary>
    private string? Reformatted(string missing)
    {
        var directory = Path.GetDirectoryName(AbsolutePathFor(missing));

        if (directory is null || !Directory.Exists(directory)) return null;

        var stem = Path.GetFileNameWithoutExtension(missing);
        var folder = missing[..missing.LastIndexOf('/')];

        var candidates = Directory.EnumerateFiles(directory)
            .Where(candidate => Path.GetFileNameWithoutExtension(candidate) == stem && AudioFormats.IsAudioFile(candidate))
            .Take(2)
            .ToList();

        return candidates is [{ } only] ? $"{folder}/{Path.GetFileName(only)}" : null;
    }

    /// <summary>Runs an edit's read and write under the one lock.</summary>
    internal async Task<T> EditAsync<T>(Func<Task<T>> edit, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(edit);

        await _one.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            return await edit().ConfigureAwait(false);
        }
        finally
        {
            _one.Release();
        }
    }

    /// <summary>
    /// Every m3u at the top of the Playlists folder, by library-relative path.
    /// Not its subfolders, and not a hidden file — a staged write is one.
    /// </summary>
    internal IReadOnlyList<string> AllPaths()
    {
        var folder = Path.Combine(store.Root, PlaylistFile.Folder);

        if (!Directory.Exists(folder)) return [];

        return [.. Directory.EnumerateFiles(folder)
            .Select(Path.GetFileName)
            .OfType<string>()
            .Where(name => !name.StartsWith('.') && PlaylistFile.Extensions.Contains(Path.GetExtension(name)))
            .Order(StringComparer.Ordinal)
            .Select(name => $"{PlaylistFile.Folder}/{name}")];
    }

    /// <summary>A playlist's text as the file has it; null where there is no such file.</summary>
    /// <remarks>Read under the lock, so it is not caught halfway through a write of Fonoteca's own.</remarks>
    internal async Task<PlaylistText?> ReadAsync(string path, CancellationToken cancellationToken)
    {
        await _one.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            return await ReadLockedAsync(path, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _one.Release();
        }
    }

    /// <summary>For a caller already holding the lock, through <see cref="EditAsync"/>.</summary>
    internal async Task<PlaylistText?> ReadLockedAsync(string path, CancellationToken cancellationToken)
    {
        var absolute = AbsolutePathFor(path);

        if (!File.Exists(absolute)) return null;

        var bytes = await File.ReadAllBytesAsync(absolute, cancellationToken).ConfigureAwait(false);
        var bom = bytes.AsSpan().StartsWith(Bom);
        var body = bom ? bytes[Bom.Length..] : bytes;

        string text;
        var editable = true;

        try
        {
            text = Utf8.GetString(body);
        }
        catch (DecoderFallbackException)
        {
            text = Encoding.Latin1.GetString(body);
            editable = false;
        }

        return new PlaylistText(path, text, editable, bom, File.GetLastWriteTimeUtc(absolute), Version(text));
    }

    /// <summary>
    /// Writes the text where it differs from what is there, for a caller holding
    /// the lock; a staged sibling renamed over it, so a reader never sees half a
    /// file. Nothing while the library looks unmounted.
    /// </summary>
    internal async Task<bool> WriteLockedAsync(string path, string text, bool bom, CancellationToken cancellationToken)
    {
        if (!Mounted()) return false;

        try
        {
            var absolute = AbsolutePathFor(path);

            if (File.Exists(absolute)
                && await ReadLockedAsync(path, cancellationToken).ConfigureAwait(false) is { } current
                && string.Equals(current.Text, text, StringComparison.Ordinal))
            {
                return false;
            }

            var folder = Path.GetDirectoryName(absolute)!;
            Directory.CreateDirectory(folder);

            var staged = Path.Combine(folder, $".{Path.GetFileName(absolute)}.tmp");
            var bytes = Utf8.GetBytes(text);
            await File.WriteAllBytesAsync(staged, bom ? [.. Bom, .. bytes] : bytes, cancellationToken).ConfigureAwait(false);
            File.Move(staged, absolute, overwrite: true);

            Log.PlaylistWritten(logger, path);
            return true;
        }
        catch (Exception cause) when (cause is IOException or UnauthorizedAccessException)
        {
            Log.PlaylistNotWritten(logger, path, cause);
            return false;
        }
    }

    /// <summary>A library-relative m3u path as an absolute one, through the store's containment check.</summary>
    internal string AbsolutePathFor(string path) => store.AbsolutePathFor(new LibraryPath(path));

    internal string Root => store.Root;

    /// <summary>Whether anything but the playlists folder is under the root.</summary>
    internal bool Mounted() =>
        Directory.Exists(store.Root)
        && Directory.EnumerateFileSystemEntries(store.Root)
            .Any(entry => !string.Equals(Path.GetFileName(entry), PlaylistFile.Folder, StringComparison.Ordinal));

    /// <summary>What an edit names to say which text it was made against: a hash of it.</summary>
    internal static string Version(string text) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..16];
}

/// <summary>A playlist file as read.</summary>
/// <param name="Path">Library-relative.</param>
/// <param name="Editable">Whether it is UTF-8, and so may be written back.</param>
/// <param name="Bom">Whether it starts with a byte-order mark, which a write keeps.</param>
/// <param name="Version">A hash of <paramref name="Text"/>, which an edit names to say what it was made against.</param>
internal sealed record PlaylistText(string Path, string Text, bool Editable, bool Bom, DateTime ChangedUtc, string Version);
