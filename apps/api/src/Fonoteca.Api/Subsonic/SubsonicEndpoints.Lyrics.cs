using Fonoteca.Data;
using Fonoteca.Domain.Abstractions;
using Fonoteca.Domain.Catalogue;
using Fonoteca.Tagging;
using Microsoft.EntityFrameworkCore;

namespace Fonoteca.Api.Subsonic;

/// <summary>
/// A song's words: OpenSubsonic's <c>getLyricsBySongId</c>, timed line by line
/// where they are timed, and the original API's <c>getLyrics</c>, plain.
/// </summary>
/// <remarks>
/// <b>The catalogue's words, else the file's own</b> — what LRCLIB answered
/// before the tag write has put it in the file, and what the file carries
/// where LRCLIB had nothing. Read through <see cref="LyricsText"/>, the one
/// place LRC becomes lines.
/// </remarks>
public static partial class SubsonicEndpoints
{
    private static async Task<IResult> GetLyricsBySongId(
        HttpContext http,
        FonotecaDbContext db,
        TagReader reader,
        CancellationToken cancellationToken)
    {
        if (SubsonicIds.AsSong(Text(http, "id")) is not { } fileId)
        {
            return SubsonicResult.Error(70, "No such song.");
        }

        var songs = await SongsAsync(db, album: null, cancellationToken, fileId).ConfigureAwait(false);

        if (songs.Count == 0) return SubsonicResult.Error(70, "No such song.");

        var song = songs[0];
        var words = await WordsAsync(db, reader, fileId, song.Path, cancellationToken).ConfigureAwait(false);

        LyricsLines[] found = words is null ? [] : [LyricsText.Lines(words)];

        return SubsonicResult.Ok("lyricsList", list => list.Children("structuredLyrics", found, (writer, lyrics) =>
        {
            writer.Attr("displayArtist", song.Artist);
            writer.Attr("displayTitle", song.Title);

            // Neither LRCLIB nor a tag says which language the words are in.
            writer.Attr("lang", "xxx");
            writer.Attr("synced", lyrics.Synced);
            if (lyrics.Synced && lyrics.Offset != 0) writer.Attr("offset", lyrics.Offset);

            writer.Children("line", lyrics.Lines, (line, entry) =>
            {
                line.Attr("start", entry.Start);
                line.Value(entry.Text);
            });
        }));
    }

    /// <summary>
    /// The original API's lyrics, by artist and title.
    /// </summary>
    /// <remarks>
    /// A held song of that title, its billing line containing the artist; an
    /// empty answer where none has words, which is what clients expect rather
    /// than an error. Plain text: the method has no way to carry a time.
    /// </remarks>
    private static async Task<IResult> GetLyrics(
        HttpContext http,
        FonotecaDbContext db,
        TagReader reader,
        CancellationToken cancellationToken)
    {
        var artist = Text(http, "artist")?.Trim();
        var title = Text(http, "title")?.Trim();

        if (string.IsNullOrEmpty(title)) return SubsonicResult.Ok("lyrics", lyrics => lyrics.Value(string.Empty));

        var pattern = Escape(title);

        var songs = await SongsAsync(
                db,
                db.MediaFiles.AsNoTracking()
                    .Where(row => row.Recording != null && EF.Functions.ILike(row.Recording.Title, pattern, "\\"))
                    .OrderBy(row => row.Id)
                    .Take(MaxListSize),
                cancellationToken)
            .ConfigureAwait(false);

        var billed = songs
            .Where(song => string.IsNullOrEmpty(artist) || song.Artist?.Contains(artist, StringComparison.OrdinalIgnoreCase) == true)
            .Select(song => (Song: song, File: SubsonicIds.AsSong(song.Id)))
            .Where(candidate => candidate.File is not null)
            .ToList();

        // The catalogue's words for all of them in one query; a file's own
        // tag is read for the first only, since that is a file opened.
        var ids = billed.Select(candidate => candidate.File!.Value).ToList();

        var catalogued = await db.MediaFiles
            .AsNoTracking()
            .Where(row => ids.Contains(row.Id) && row.Recording!.Lyrics != null)
            .Select(row => new { row.Id, Words = row.Recording!.Lyrics!.Synced ?? row.Recording.Lyrics.Plain })
            .Where(row => row.Words != null)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var found = billed
            .Select(candidate => (candidate.Song, Words: catalogued.FirstOrDefault(row => row.Id == candidate.File)?.Words))
            .FirstOrDefault(candidate => candidate.Words is not null);

        if (found.Words is null && billed.Count > 0)
        {
            found = (billed[0].Song, await WordsAsync(db, reader, billed[0].File!.Value, billed[0].Song.Path, cancellationToken).ConfigureAwait(false));
        }

        return found.Words is { } words
            ? SubsonicResult.Ok("lyrics", lyrics =>
            {
                lyrics.Attr("artist", found.Song.Artist);
                lyrics.Attr("title", found.Song.Title);
                lyrics.Value(LyricsText.Plain(words));
            })
            : SubsonicResult.Ok("lyrics", lyrics => lyrics.Value(string.Empty));
    }

    /// <summary>The words LRCLIB gave the file's recording, else the file's own tag's, else null.</summary>
    private static async Task<string?> WordsAsync(
        FonotecaDbContext db,
        TagReader reader,
        MediaFileId file,
        string path,
        CancellationToken cancellationToken)
    {
        var catalogued = await db.MediaFiles
            .AsNoTracking()
            .Where(row => row.Id == file)
            .Select(row => row.Recording!.Lyrics!.Synced ?? row.Recording.Lyrics.Plain)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        if (catalogued is not null) return catalogued;

        try
        {
            return (await reader.ReadAsync(new LibraryPath(path), cancellationToken: cancellationToken).ConfigureAwait(false))
                .Find(CatalogueTags.Lyrics);
        }
        catch (Exception cause) when (cause is TagReadFailedException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            // A file that cannot be read, or a library not mounted, has no words to give.
            return null;
        }
    }
}
