using System.Globalization;
using Fonoteca.Api.Endpoints;
using Fonoteca.Api.Library;
using Fonoteca.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Fonoteca.Api.Subsonic;

/// <summary>
/// Each account's own tree of playlists: OpenSubsonic's
/// <c>playlistFolders</c>, version 1 as its pull request 255 proposes it.
/// </summary>
/// <remarks>
/// <b>The folders and the places are the account's, the playlists
/// everyone's</b> (ADR 0018). Placing a playlist changes nothing in it or its
/// file, so any playlist may be placed, a smart one too, and two people keep
/// one in different folders. Both are read through the context's account
/// filter; an admin reads another account's tree by <c>username</c>, as the
/// extension allows.
///
/// <b>A place whose playlist is gone is no place.</b> A static playlist is its
/// file and can go without Fonoteca seeing it; its row stays, is shown
/// nowhere and keeps no folder from being deleted. A rename or delete here
/// carries or drops it (<c>CatalogueEndpoints.UpdatePlaylist</c>,
/// <c>DeletePlaylist</c>, <c>DeleteSmartPlaylist</c>).
///
/// <b>Sort orders are the client's</b>, kept as sent, duplicates and all; the
/// server picks one only where none came, after the highest of the siblings,
/// folders and playlists alike. A playlist never placed is at the root with no
/// order, so its <c>playlistFolderSortOrder</c> is left out: a number would
/// claim a place nobody gave it.
/// </remarks>
public static partial class SubsonicEndpoints
{
    private static async Task<IResult> GetPlaylistFolders(
        HttpContext http,
        FonotecaDbContext db,
        UserManager<Account> users,
        CancellationToken cancellationToken)
    {
        var (account, refused) = await SelectedAsync(http, db, users).ConfigureAwait(false);
        if (refused is not null) return refused;

        var folders = await db.PlaylistFolders
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(folder => folder.AccountId == account)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return SubsonicResult.Ok("playlistFolders", list => list.Children(
            "playlistFolder",
            folders.OrderBy(folder => folder.SortOrder ?? int.MaxValue).ThenBy(folder => folder.Name, StringComparer.OrdinalIgnoreCase),
            WriteFolder));
    }

    private static async Task<IResult> CreatePlaylistFolder(HttpContext http, FonotecaDbContext db, CancellationToken cancellationToken)
    {
        if (NameSent(http) is not { } name) return SubsonicResult.Error(10, "Required parameter is missing: name.");
        if (name.Length > PlaylistFolder.NameLength) return FolderNameTooLong();

        Guid? parent = null;

        if (Text(http, "parentId") is { } parentId)
        {
            if (await FolderAsync(db, parentId, cancellationToken).ConfigureAwait(false) is not { } found) return NoSuchFolder();

            parent = found.Id;
        }

        if (!SortOrder(Text(http, "sortOrder"), out var order)) return NotASortOrder();

        var folder = new PlaylistFolder
        {
            Id = Guid.CreateVersion7(),
            AccountId = db.AccountId!.Value,
            Name = name,
            ParentId = parent,
            SortOrder = order ?? await AfterAsync(db, parent, [], cancellationToken).ConfigureAwait(false),
        };

        db.PlaylistFolders.Add(folder);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return SubsonicResult.Ok("playlistFolder", writer => WriteFolder(writer, folder));
    }

    /// <summary>A rename, a move under another folder or to the root (an empty <c>parentId</c>), a new place.</summary>
    private static async Task<IResult> UpdatePlaylistFolder(HttpContext http, FonotecaDbContext db, CancellationToken cancellationToken)
    {
        if (Missing(http, "playlistFolderId") is { } missing) return missing;

        var folders = await db.PlaylistFolders.ToListAsync(cancellationToken).ConfigureAwait(false);

        if (SubsonicIds.AsPlaylistFolder(Text(http, "playlistFolderId")) is not { } id
            || folders.FirstOrDefault(row => row.Id == id) is not { } folder)
        {
            return NoSuchFolder();
        }

        var name = NameSent(http);
        var moving = http.Request.Query.ContainsKey("parentId");

        if (!SortOrder(Text(http, "sortOrder"), out var order)) return NotASortOrder();

        if (name is null && !moving && order is null)
        {
            return SubsonicResult.Error(10, "Nothing to change: send name, parentId or sortOrder.");
        }

        if (name is { Length: > PlaylistFolder.NameLength }) return FolderNameTooLong();

        if (moving)
        {
            Guid? parent = null;

            if (Text(http, "parentId") is { } parentId)
            {
                if (SubsonicIds.AsPlaylistFolder(parentId) is not { } wanted || folders.All(row => row.Id != wanted)) return NoSuchFolder();
                if (Below(folders, row => row.Id, row => row.ParentId, folder.Id).Contains(wanted))
                {
                    return SubsonicResult.Error(0, "A folder cannot go inside itself.");
                }

                parent = wanted;
            }

            folder.ParentId = parent;
        }

        folder.Name = name ?? folder.Name;
        folder.SortOrder = order ?? folder.SortOrder;

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return SubsonicResult.Ok();
    }

    /// <summary>An empty folder: no folder in it, and no playlist there is.</summary>
    private static async Task<IResult> DeletePlaylistFolder(
        HttpContext http,
        FonotecaDbContext db,
        PlaylistFiles files,
        CancellationToken cancellationToken)
    {
        if (Missing(http, "playlistFolderId") is { } missing) return missing;

        if (await FolderAsync(db, Text(http, "playlistFolderId"), cancellationToken).ConfigureAwait(false) is not { } folder)
        {
            return NoSuchFolder();
        }

        var placed = await db.PlaylistPlacements
            .Where(placement => placement.FolderId == folder.Id)
            .Select(placement => placement.Playlist)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var visible = await VisiblePlaylistsAsync(db, files, cancellationToken).ConfigureAwait(false);

        if (placed.Any(visible.Contains)
            || await db.PlaylistFolders.AnyAsync(row => row.ParentId == folder.Id, cancellationToken).ConfigureAwait(false))
        {
            return SubsonicResult.Error(0, "The folder is not empty.");
        }

        // A place left in it for a playlist that is gone goes with it, by the cascade.
        await db.PlaylistFolders.Where(row => row.Id == folder.Id).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);

        return SubsonicResult.Ok();
    }

    /// <summary>
    /// Playlists into a folder, or to the root (an empty
    /// <c>playlistFolderId</c>), or only to new places among their siblings.
    /// </summary>
    /// <remarks>
    /// Each <c>sortOrder</c> pairs with the <c>playlistId</c> in its place; with
    /// none, the playlists go after the destination's contents in the order
    /// sent. A playlist sent twice takes the later place.
    /// </remarks>
    private static async Task<IResult> MovePlaylist(
        HttpContext http,
        FonotecaDbContext db,
        PlaylistFiles files,
        CancellationToken cancellationToken)
    {
        var ids = http.Request.Query["playlistId"];

        if (ids.All(string.IsNullOrEmpty)) return SubsonicResult.Error(10, "Required parameter is missing: playlistId.");

        var moving = http.Request.Query.ContainsKey("playlistFolderId");
        var sent = http.Request.Query["sortOrder"];

        if (!moving && sent.Count == 0) return SubsonicResult.Error(10, "Nothing to change: send playlistFolderId or sortOrder.");
        if (sent.Count > 0 && sent.Count != ids.Count) return SubsonicResult.Error(10, "Send one sortOrder for each playlistId.");

        var orders = new List<int>();

        foreach (var raw in sent)
        {
            if (!SortOrder(raw, out var order) || order is null) return NotASortOrder();

            orders.Add(order.Value);
        }

        var visible = await VisiblePlaylistsAsync(db, files, cancellationToken).ConfigureAwait(false);
        var keys = new List<string>();

        foreach (var id in ids)
        {
            if (PlaylistKey(id) is not { } key || !visible.Contains(key)) return SubsonicResult.Error(70, "No such playlist.");

            keys.Add(key);
        }

        Guid? destination = null;

        if (moving && Text(http, "playlistFolderId") is { } folderId)
        {
            if (await FolderAsync(db, folderId, cancellationToken).ConfigureAwait(false) is not { } folder) return NoSuchFolder();

            destination = folder.Id;
        }

        // A double tap places the same playlist twice at once: the one that
        // loses is asked again once, and finds the place made.
        for (var attempt = 0; ; attempt++)
        {
            var placements = await db.PlaylistPlacements
                .Where(placement => keys.Contains(placement.Playlist))
                .ToDictionaryAsync(placement => placement.Playlist, cancellationToken)
                .ConfigureAwait(false);

            var next = orders.Count == 0 ? await AfterAsync(db, destination, keys, cancellationToken).ConfigureAwait(false) : 0;

            for (var at = 0; at < keys.Count; at++)
            {
                if (!placements.TryGetValue(keys[at], out var placement))
                {
                    placement = new PlaylistPlacement { Id = Guid.CreateVersion7(), AccountId = db.AccountId!.Value, Playlist = keys[at] };
                    placements[keys[at]] = placement;
                    db.PlaylistPlacements.Add(placement);
                }

                if (moving) placement.FolderId = destination;

                placement.SortOrder = orders.Count > 0 ? orders[at] : next;

                if (next < int.MaxValue) next++;
            }

            try
            {
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

                return SubsonicResult.Ok();
            }
            catch (DbUpdateException cause) when (attempt == 0 && cause.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
            {
                db.ChangeTracker.Clear();
            }
        }
    }

    /// <summary>
    /// Whose tree a read shows: the account signed in, or the one an admin
    /// names in <c>username</c>.
    /// </summary>
    private static async Task<(Guid Account, SubsonicResult? Refusal)> SelectedAsync(
        HttpContext http,
        FonotecaDbContext db,
        UserManager<Account> users)
    {
        var self = db.AccountId!.Value;

        if (Text(http, "username") is not { } name || Own(http, users, name)) return (self, null);
        if (!May(http, AccountRoles.Admin)) return (self, NotAuthorized());

        return await users.FindByNameAsync(name).ConfigureAwait(false) is { } other ? (other.Id, null) : (self, NoSuchUser());
    }

    /// <summary>Where one account keeps each playlist, by the catalogue's id for it.</summary>
    private static Task<Dictionary<string, PlaylistPlacement>> PlacementsAsync(
        FonotecaDbContext db,
        Guid account,
        CancellationToken cancellationToken) =>
        db.PlaylistPlacements
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(placement => placement.AccountId == account)
            .ToDictionaryAsync(placement => placement.Playlist, cancellationToken);

    /// <summary>The catalogue's id for every playlist there is, by the playlist list's own handler.</summary>
    private static async Task<HashSet<string>> VisiblePlaylistsAsync(
        FonotecaDbContext db,
        PlaylistFiles files,
        CancellationToken cancellationToken) =>
        [.. (await CatalogueEndpoints.GetPlaylists(db, files, cancellationToken).ConfigureAwait(false)).Value!.Items.Select(playlist => playlist.Id)];

    /// <summary>The catalogue's id for a playlist a Subsonic id names: a smart one's id, a static one's file name.</summary>
    private static string? PlaylistKey(string? id) =>
        SubsonicIds.AsPlaylist(id) is { } smart ? smart.Value.ToString() : SubsonicIds.AsPlaylistFile(id);

    /// <summary>A place after every sibling under <paramref name="parent"/> that has one, the playlists being placed apart.</summary>
    private static async Task<int> AfterAsync(
        FonotecaDbContext db,
        Guid? parent,
        List<string> placing,
        CancellationToken cancellationToken)
    {
        var folders = await db.PlaylistFolders
            .Where(folder => folder.ParentId == parent)
            .MaxAsync(folder => folder.SortOrder, cancellationToken)
            .ConfigureAwait(false);

        var playlists = await db.PlaylistPlacements
            .Where(placement => placement.FolderId == parent && !placing.Contains(placement.Playlist))
            .MaxAsync(placement => placement.SortOrder, cancellationToken)
            .ConfigureAwait(false);

        return new[] { folders, playlists }.Max() is { } highest ? (highest == int.MaxValue ? highest : highest + 1) : 1;
    }

    private static async Task<PlaylistFolder?> FolderAsync(FonotecaDbContext db, string? id, CancellationToken cancellationToken) =>
        SubsonicIds.AsPlaylistFolder(id) is { } wanted
            ? await db.PlaylistFolders.AsNoTracking().FirstOrDefaultAsync(folder => folder.Id == wanted, cancellationToken).ConfigureAwait(false)
            : null;

    /// <summary>A sort order as sent; false where one came that is not a whole number.</summary>
    private static bool SortOrder(string? raw, out int? order)
    {
        order = null;

        if (string.IsNullOrEmpty(raw)) return true;
        if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)) return false;

        order = value;
        return true;
    }

    private static void WriteFolder(ISubsonicWriter writer, PlaylistFolder folder)
    {
        writer.Attr("id", SubsonicIds.PlaylistFolder(folder.Id));
        writer.Attr("name", folder.Name);
        writer.Attr("parentId", folder.ParentId is { } parent ? SubsonicIds.PlaylistFolder(parent) : null);
        writer.Attr("sortOrder", folder.SortOrder);
    }

    private static SubsonicResult NoSuchFolder() => SubsonicResult.Error(70, "No such playlist folder.");

    private static SubsonicResult NotASortOrder() => SubsonicResult.Error(0, "A sortOrder is a whole number.");

    private static SubsonicResult FolderNameTooLong() =>
        SubsonicResult.Error(0, $"A folder's name is at most {PlaylistFolder.NameLength} characters.");
}
