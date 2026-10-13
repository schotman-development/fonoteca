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
/// Keeps every playlist's m3u naming what its songs are now.
/// </summary>
/// <remarks>
/// <b>Written after every tag write and its undo, and after every edit to a
/// playlist — nothing else</b> (the owner's choice, over a sweep a minute). The
/// tag write is what renames the library's folders, so it is what moves the
/// paths a playlist names. A move in the file manager, a scan, a replacement or
/// a better copy arriving reaches the files at the next of those; until then
/// the m3u names what was there. Each write resolves every entry and rewrites
/// only the files whose text changed, so none of those places has to know
/// playlists exist.
///
/// <b>Nothing is written while the library looks unmounted.</b> A root holding
/// nothing but the playlists folder is a mount point with nothing behind it,
/// and writing there would put the folder on the host's own disk (rule 3).
/// </remarks>
public sealed class PlaylistFiles(
    IServiceScopeFactory scopeFactory,
    FileSystemAudioFileStore store,
    ILogger<PlaylistFiles> logger) : IDisposable
{
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>One write at a time: an edit's waits for a tag write's.</summary>
    private readonly SemaphoreSlim _one = new(1, 1);

    public void Dispose() => _one.Dispose();

    /// <summary>
    /// Resolves every entry, stores where each song went, and rewrites each m3u
    /// whose text changed.
    /// </summary>
    /// <returns>How many files were written.</returns>
    public async Task<int> SyncAsync(CancellationToken cancellationToken = default)
    {
        await _one.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            var scope = scopeFactory.CreateAsyncScope();

            await using (scope.ConfigureAwait(false))
            {
                var db = scope.ServiceProvider.GetRequiredService<FonotecaDbContext>();

                var playlists = await db.Playlists
                    .Include(playlist => playlist.Entries)
                    .ToListAsync(cancellationToken)
                    .ConfigureAwait(false);

                if (playlists.Count == 0) return 0;

                var entries = playlists.SelectMany(playlist => playlist.Entries).ToList();
                var songs = await ResolveAsync(db, entries, cancellationToken).ConfigureAwait(false);

                foreach (var entry in entries)
                {
                    var song = songs[entry.Id];

                    entry.RecordingId = song.RecordingId;
                    if (song.Copy is { } copy) entry.MediaFileId = copy.Id;
                    entry.Title = Clamp(song.Title, PlaylistEntry.TitleLength);
                    entry.Artist = song.Artist is null ? null : Clamp(song.Artist, PlaylistEntry.ArtistLength);
                }

                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

                if (!Mounted()) return 0;

                var written = 0;

                foreach (var playlist in playlists)
                {
                    var lines = playlist.Entries
                        .OrderBy(entry => entry.Position)
                        .Select(entry => songs[entry.Id])
                        .Where(song => song.Copy is not null)
                        .Select(song => new PlaylistLine(song.Copy!.Path, song.Duration, song.Artist, song.Title));

                    if (await WriteAsync(playlist.Path, PlaylistFile.Render(playlist.Name, lines), cancellationToken).ConfigureAwait(false))
                    {
                        written++;
                    }
                }

                return written;
            }
        }
        finally
        {
            _one.Release();
        }
    }

    /// <summary>
    /// What each entry is now: its song, the copy that plays it, and how to name it.
    /// </summary>
    /// <remarks>
    /// The entry's own recording while it holds a copy; otherwise the recording
    /// its last copy now belongs to, which is where a merge, a fold or a re-filing
    /// took the song. Neither holding one, it keeps its recording, and plays its
    /// last copy if that file is now linked to nothing at all — an undone tag
    /// write reopens it, an outside tagger's bytes withdraw its AcoustID, and an
    /// answer withdrawn is not a different answer (rule 3). Otherwise it is not
    /// held.
    /// </remarks>
    internal static async Task<Dictionary<Guid, PlaylistSong>> ResolveAsync(
        FonotecaDbContext db,
        IReadOnlyCollection<PlaylistEntry> entries,
        CancellationToken cancellationToken)
    {
        var named = entries.Select(entry => entry.MediaFileId).OfType<MediaFileId>().Distinct().ToList();

        var lasts = await db.MediaFiles
            .AsNoTracking()
            .Where(file => named.Contains(file.Id))
            .Select(file => new { file.Id, file.RecordingId, file.Path, file.SizeBytes, file.Quality, file.Measurement, file.Integrity })
            .ToDictionaryAsync(file => file.Id, cancellationToken)
            .ConfigureAwait(false);

        var asked = entries
            .Select(entry => entry.RecordingId)
            .Concat(lasts.Values.Select(file => file.RecordingId))
            .OfType<RecordingId>()
            .Distinct()
            .ToList();

        // Nullable for the files' column: EF translates no member access on a typed id.
        List<RecordingId?> askedOfFiles = [.. asked.Select(id => (RecordingId?)id)];

        var copies = (await db.MediaFiles
                .AsNoTracking()
                .Where(file => askedOfFiles.Contains(file.RecordingId))
                .Select(file => new { file.RecordingId, file.Id, file.Path, file.SizeBytes, file.Quality, file.Measurement, file.Integrity })
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false))
            .GroupBy(file => file.RecordingId!.Value)
            .ToDictionary(
                group => group.Key,
                group => group
                    .Select(file => new PlaylistCopy(file.Id, file.Path, file.SizeBytes, file.Quality, file.Measurement, file.Integrity))
                    .ToList());

        var recordings = await db.Recordings
            .AsNoTracking()
            .Where(recording => asked.Contains(recording.Id))
            .Select(recording => new
            {
                recording.Id,
                recording.Title,
                recording.Duration,
                Credits = recording.Credits
                    .OrderBy(credit => credit.Position)
                    .Select(credit => new
                    {
                        Name = credit.CreditedAs ?? credit.Artist!.LatinName ?? credit.Artist!.Name,
                        credit.JoinPhrase,
                    })
                    .ToList(),
            })
            .ToDictionaryAsync(recording => recording.Id, cancellationToken)
            .ConfigureAwait(false);

        var songs = new Dictionary<Guid, PlaylistSong>(entries.Count);

        foreach (var entry in entries)
        {
            var last = entry.MediaFileId is { } lastId ? lasts.GetValueOrDefault(lastId) : null;
            var followed = last?.RecordingId;

            var recording = entry.RecordingId is { } own && copies.ContainsKey(own)
                ? own
                : followed is { } now && copies.ContainsKey(now)
                    ? now
                    : entry.RecordingId ?? followed;

            PlaylistCopy? copy = null;
            var linked = false;

            if (recording is { } held && copies.TryGetValue(held, out var files))
            {
                copy = PlaylistFile.Best(files);
                linked = true;
            }
            else if (last is { RecordingId: null })
            {
                copy = new PlaylistCopy(last.Id, last.Path, last.SizeBytes, last.Quality, last.Measurement, last.Integrity);
            }
            var facts = recording is { } known ? recordings.GetValueOrDefault(known) : null;

            songs[entry.Id] = new PlaylistSong(
                recording,
                copy,
                linked,
                facts?.Title ?? entry.Title,
                facts is null
                    ? entry.Artist
                    : CatalogueEndpoints.CreditLine(facts.Credits.Select(credit => (credit.Name, credit.JoinPhrase))),
                copy?.Quality?.Duration ?? facts?.Duration);
        }

        return songs;
    }

    /// <summary>
    /// Playlist entries moved from each recording to the one taking its place,
    /// in the caller's save.
    /// </summary>
    /// <remarks>
    /// For the places that delete a recording after moving its files onto
    /// another: a merge, a shop recording folded into MusicBrainz's, a download
    /// moved or re-filed onto a MusicBrainz release. The entry's last copy leads
    /// it there too, but only while that file exists, and the next playlist
    /// write may come after an album replacement has deleted it — leaving the
    /// entry with neither. Tracked rather than <c>ExecuteUpdate</c>, so it
    /// commits or rolls back with the move it follows.
    /// </remarks>
    internal static async Task FollowAsync(
        FonotecaDbContext db,
        IReadOnlyDictionary<RecordingId, RecordingId> moves,
        CancellationToken cancellationToken)
    {
        if (moves.Count == 0) return;

        // Nullable for the entries' column: EF translates no member access on a typed id.
        List<RecordingId?> from = [.. moves.Keys.Select(id => (RecordingId?)id)];

        var entries = await db.PlaylistEntries
            .Where(entry => from.Contains(entry.RecordingId))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        foreach (var entry in entries) entry.RecordingId = moves[entry.RecordingId!.Value];
    }

    /// <summary>A library-relative m3u path as an absolute one, through the store's containment check.</summary>
    internal string AbsolutePathFor(string path) => store.AbsolutePathFor(new LibraryPath(path));

    /// <summary>Whether anything but the playlists folder is under the root.</summary>
    internal bool Mounted() =>
        Directory.Exists(store.Root)
        && Directory.EnumerateFileSystemEntries(store.Root)
            .Any(entry => !string.Equals(Path.GetFileName(entry), PlaylistFile.Folder, StringComparison.Ordinal));

    /// <summary>Writes the text where it differs from what is there; a staged sibling renamed over it, so a reader never sees half a file.</summary>
    private async Task<bool> WriteAsync(string path, string text, CancellationToken cancellationToken)
    {
        try
        {
            var absolute = AbsolutePathFor(path);

            if (File.Exists(absolute)
                && string.Equals(await File.ReadAllTextAsync(absolute, cancellationToken).ConfigureAwait(false), text, StringComparison.Ordinal))
            {
                return false;
            }

            var folder = Path.GetDirectoryName(absolute)!;
            Directory.CreateDirectory(folder);

            var staged = Path.Combine(folder, $".{Path.GetFileName(absolute)}.tmp");
            await File.WriteAllTextAsync(staged, text, Utf8, cancellationToken).ConfigureAwait(false);
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

    private static string Clamp(string value, int length) => value.Length <= length ? value : value[..length];
}

/// <summary>One entry, resolved.</summary>
/// <param name="Copy">The file the m3u names; null where the library holds none.</param>
/// <param name="Linked">Whether the copy is a file of <paramref name="RecordingId"/>, rather than its last copy now linked to nothing.</param>
internal sealed record PlaylistSong(
    RecordingId? RecordingId,
    PlaylistCopy? Copy,
    bool Linked,
    string Title,
    string? Artist,
    TimeSpan? Duration);
