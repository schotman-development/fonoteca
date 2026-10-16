---
name: sync-production-library
description: Bringing the production music library (/mnt/vault/media/music, read by Aria) up to date with Fonoteca's test library (/mnt/vault/fonoteca-test/music) as hardlinks. Use when asked to "update production", "sync production", or "bring the production library up to date".
---

# Sync production from the test library

Production is a hardlink copy of the test library on the same NFS export
(10.0.0.8:/Vault). Fonoteca's tag write commits by rename, so a written file
gets a new inode in the test tree while production keeps the old one; Fonoteca's
renames, moves and trash never reach production. A sync makes production
identical by path and inode.

## Before touching anything

1. Inventory both trees with `find -type f -printf '%P\t%i\t%s\n' | sort`. Skip
   the test tree's hidden Fonoteca folders (`.fonoteca-*`). Compare symlinks
   separately with `find -type l -printf '%P\t%l\n'`.
2. Classify each path: same inode (nothing to do), different inode (changed),
   only in test (new), or only in production (stale).
3. **Check the audio.** Aria's track id is sha1 of the path relative to the
   music root. If any audio path appears or disappears, Aria loses favourites,
   playlists and plays on its next scan and needs the re-link described in the
   project memory `production-library-hardlinked`. Stop and ask the user before
   going on. A sync that only touches images, booklets, PDFs and motion videos
   is safe.
4. Check that every stale or changed production inode also exists in the test
   tree, `/mnt/vault/fonoteca-test/music-trash` or `music-replaced`. If one
   does not, it is the only copy: stop and ask. A tag write leaves exactly
   this, so compare the decoded audio
   (`ffmpeg -nostdin -v error -i f -map 0:a -f md5 -`; without `-nostdin`
   ffmpeg eats a `while read` loop's input, and ffprobe needs `</dev/null`)
   and tell the user whether only the tags differ. Also compare the ALBUM tag:
   if it is unchanged, Aria's album ids hold.
5. Before asking, count what Aria would lose, read-only. Old ids are
   `sha1(relative path)`. Count favourites from `GET /api/tracks?limit=100000`,
   plays from `GET /api/plays/counts`, and entries from each
   `GET /api/playlists/{id}/tracks`.

## Re-linking Aria (when the user asks for it)

Aria is `podman-compose@aria.service` on `admin@10.0.0.10`. Its database is
`~/.local/share/containers/storage/volumes/aria-data/_data/aria.db`. There is
no sqlite3 there, so use `podman unshare python3`, and open it with
`?mode=ro` while you are only reading.

1. After the file sync, `POST /api/scan` (synchronous) creates the new
   tracks.
2. Compare each album's `albumId` before and after. It is the folder plus the
   ALBUM tag, so a tag write can change it. If the new id already holds a
   better match, drop the old id's leftover rows rather than moving them.
3. `podman stop aria`, then copy `aria.db` into `~/aria-relink-<date>/`.
4. Re-key the rows that name a track id:
   - `plays.trackId`
   - `playlist_tracks.trackId`
   - `tag_items` and `edits` where the kind is `track`
   - `enrich_cache` where the kind is `track` or `lyrics` (insert or ignore,
     then delete the old row)
   - `tracks.addedAt`, restored from the old row
   - `tracks.favourite`

   Before writing it, scan every table's every column for the old ids
   (read-only), so a table this list misses shows up. Dry-run the script,
   then apply it.
5. `podman unshare chown 65532:65532` the db files, then `podman start aria`,
   and check `GET /api/plays/counts`.

## Apply

- Hardlink the new files: `ln "${T:?}/${p:?}" "${P:?}/${p:?}"`.
- Move the stale and changed production files to
  `/mnt/vault/media/music-trash/<date> sync`, keeping their layout, rather than
  deleting them. A second sync on the same day needs its own folder
  (`<date> sync 2`), since the same paths collide. That makes the sync reversible, and the user decides when to
  empty it.
- Hardlink the replacements for the changed files.
- If asked, remove empty folders bottom-up with
  `find <root> -mindepth 1 -depth -type d -empty -exec rmdir {} \;`.
- **Never replace the `music` directory itself.** Its inode (258) is what
  Aria's container bind-mounts.

## Verify

Re-run the inventory. Both trees must have zero differences by path and inode,
the same folder list and the same symlinks, with no broken links
(`find -xtype l`).
