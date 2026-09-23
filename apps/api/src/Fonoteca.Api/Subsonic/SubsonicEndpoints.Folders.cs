using Fonoteca.Api.Library;
using Fonoteca.Data;
using Microsoft.EntityFrameworkCore;

namespace Fonoteca.Api.Subsonic;

/// <summary>
/// Browsing what is on the disk.
/// </summary>
/// <remarks>
/// The protocol's older browsing family, and here it is not the legacy one. The
/// catalogue answers what the library <i>means</i>; this answers what is
/// <i>there</i>, which is the same file manager's distinction, and it is why
/// nothing in the other half needs a fallback: a file no pass has filed is
/// unreachable by artist and reachable by folder, which is the truth about it.
///
/// <b>The folder tree is an output, not a compromise.</b> A repaired library is
/// one whose folders say what its catalogue says — <c>AlbumFolder</c> already
/// argues that the grouping is the one thing about a library that is nearly
/// always right — and a client browsing here before the repair is finished sees
/// the work in progress.
///
/// <b>A song keeps its catalogue id here too.</b> Where a file has a row, it
/// goes out as the same <c>tr-</c> id the album browse gives it, so the same
/// file is the same song to a client whichever way it arrived. Only a file
/// nothing has scanned falls back to its path.
///
/// <b>Non-audio entries are not listed.</b> A rip log and a cue sheet are things
/// the file screen exists for; a music client has nothing to do with them and
/// every one of them is a row somebody has to scroll past.
/// </remarks>
public static partial class SubsonicEndpoints
{
    /// <summary>The only music folder there is.</summary>
    private static readonly int[] TheRoot = [0];

    /// <summary>
    /// The one music folder, which is <c>Fonoteca:LibraryPath</c>.
    /// </summary>
    /// <remarks>
    /// Id zero and one entry. There is exactly one root here and the protocol
    /// has no way to say so, so every client asks and every answer is this.
    /// </remarks>
    private static IResult GetMusicFolders() =>
        SubsonicResult.Ok("musicFolders", folders => folders.Children(
            "musicFolder",
            TheRoot,
            (folder, id) =>
            {
                folder.Attr("id", id);
                folder.Attr("name", "Music");
            }));

    private static async Task<IResult> GetIndexes(
        FileManagerService files,
        FonotecaDbContext db,
        CancellationToken cancellationToken)
    {
        var listing = await files.ListAsync(null, cancellationToken).ConfigureAwait(false);

        // The newest thing at the root, which is what a client's conditional
        // request is asking about. `UtcNow` would be an answer that is never the
        // same twice and therefore never a cache hit.
        var modified = listing.Entries.Count == 0
            ? DateTimeOffset.UnixEpoch
            : listing.Entries.Max(entry => entry.ModifiedUtc);

        var directories = listing.Entries
            .Where(entry => entry.IsDirectory)
            .OrderBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
            .GroupBy(entry => Initial(entry.Name))
            .OrderBy(bucket => bucket.Key, StringComparer.Ordinal)
            .ToList();

        var loose = await ChildrenAsync(
                db,
                listing.Entries.Where(entry => entry.IsAudio).ToList(),
                cancellationToken)
            .ConfigureAwait(false);

        return SubsonicResult.Ok("indexes", indexes =>
        {
            indexes.Attr("ignoredArticles", IgnoredArticles);
            indexes.Attr("lastModified", modified.ToUnixTimeMilliseconds());

            indexes.Children("index", directories, (index, bucket) =>
            {
                index.Attr("name", bucket.Key);

                index.Children("artist", bucket, (artist, entry) =>
                {
                    artist.Attr("id", SubsonicIds.Path(entry.Path));
                    artist.Attr("name", entry.Name);
                });
            });

            indexes.Children("child", loose, WriteChild);
        });
    }

    private static async Task<IResult> GetMusicDirectory(
        HttpContext http,
        FileManagerService files,
        FonotecaDbContext db,
        CancellationToken cancellationToken)
    {
        var id = Text(http, "id");
        var path = SubsonicIds.AsPath(id);

        if (path is null) return SubsonicResult.Error(70, "No such directory.");

        Library.FolderListing listing;

        try
        {
            listing = await files.ListAsync(path, cancellationToken).ConfigureAwait(false);
        }
        catch (UnauthorizedAccessException)
        {
            // The store's containment check: a path that resolves outside the
            // library root, or through a symlinked directory. Not found rather
            // than not authorised, because the protocol's 50 means "this user
            // may not" and there is no user here — the directory is simply not
            // one this server has.
            return SubsonicResult.Error(70, "No such directory.");
        }

        if (!listing.Exists) return SubsonicResult.Error(70, "No such directory.");

        var children = await ChildrenAsync(
                db,
                listing.Entries.Where(entry => entry.IsDirectory || entry.IsAudio).ToList(),
                cancellationToken)
            .ConfigureAwait(false);

        return SubsonicResult.Ok("directory", directory =>
        {
            directory.Attr("id", SubsonicIds.Path(listing.Path));
            directory.Attr(
                "parent",
                listing.Parent is null ? null : SubsonicIds.Path(listing.Parent));
            directory.Attr("name", NameOf(listing.Path));

            directory.Children("child", children, WriteChild);
        });
    }

    /// <summary>
    /// One listing's entries, with the catalogue's answer attached where it has
    /// one.
    /// </summary>
    /// <remarks>
    /// One query for the whole folder, not one per file. A file with a row keeps
    /// its <c>tr-</c> id and everything the probe pass measured; a file without
    /// one is still a song, still streamable and still named — it just carries
    /// nothing but what the filesystem knows, which is exactly the state this
    /// application exists to move it out of.
    /// </remarks>
    private static async Task<IReadOnlyList<Child>> ChildrenAsync(
        FonotecaDbContext db,
        IReadOnlyList<FolderEntry> entries,
        CancellationToken cancellationToken)
    {
        var audio = entries.Where(entry => !entry.IsDirectory).Select(entry => entry.Path).ToList();

        var known = audio.Count == 0
            ? []
            : await SongsAsync(
                    db.MediaFiles.AsNoTracking().Where(file => audio.Contains(file.Path)),
                    cancellationToken)
                .ConfigureAwait(false);

        var byPath = known.ToDictionary(song => song.Path, StringComparer.Ordinal);

        return entries
            .OrderByDescending(entry => entry.IsDirectory)
            .ThenBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
            .Select(entry => entry.IsDirectory
                ? new Child(SubsonicIds.Path(entry.Path), entry.Name, true, null, entry)
                : new Child(
                    byPath.TryGetValue(entry.Path, out var song) ? song.Id : SubsonicIds.Path(entry.Path),
                    System.IO.Path.GetFileNameWithoutExtension(entry.Name),
                    false,
                    song,
                    entry))
            .ToList();
    }

    private static void WriteChild(ISubsonicWriter writer, Child child)
    {
        var parent = SubsonicIds.Path(ParentOf(child.Entry.Path));

        // The folder being listed, not the album the file was filed under.
        // `parent` is how a client walks back up, and in this half of the
        // protocol up is a directory — a song whose parent is its album would
        // send it somewhere the listing it came from does not contain.
        if (child.Song is { } song)
        {
            WriteSong(writer, song, parent);
            return;
        }

        writer.Attr("id", child.Id);
        writer.Attr("parent", parent);
        writer.Attr("isDir", child.IsDirectory);
        writer.Attr("title", child.Title);
        writer.Attr("created", child.Entry.ModifiedUtc);

        if (child.IsDirectory)
        {
            writer.Attr("coverArt", child.Id);
            return;
        }

        // An audio file nothing has scanned. Everything a client needs to play
        // it, and nothing it has not been told: no duration, because nothing has
        // decoded it, and no album, because nothing has decided.
        writer.Attr("size", child.Entry.SizeBytes);
        writer.Attr("suffix", System.IO.Path.GetExtension(child.Entry.Name).TrimStart('.').ToLowerInvariant());
        writer.Attr("contentType", Domain.Catalogue.FilePreview.Of(child.Entry.Path).MediaType);
        writer.Attr("type", "music");
        writer.Attr("isVideo", false);
    }

    private static string NameOf(string path)
    {
        var cut = path.LastIndexOf('/');
        return cut < 0 ? (path.Length == 0 ? "Music" : path) : path[(cut + 1)..];
    }

    private static string ParentOf(string path)
    {
        var cut = path.LastIndexOf('/');
        return cut < 0 ? string.Empty : path[..cut];
    }

    /// <summary>
    /// One row of a directory listing.
    /// </summary>
    /// <param name="Song">
    /// What the catalogue holds about it, or null for a directory and for a file
    /// no scan has reached.
    /// </param>
    private sealed record Child(
        string Id,
        string Title,
        bool IsDirectory,
        Song? Song,
        FolderEntry Entry);
}
