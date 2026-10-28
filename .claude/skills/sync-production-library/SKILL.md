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

## Only after the tags are written

Linking a file before the tag write freezes its untagged bytes in production
(the write gives the test path a new inode). The user's condition: sync only
once tags have been written on the new files. With `library_status`, check
that a Write tags run finished after the new files' identify, enrich, probe and
analyse, and that no pass is running. A download writes its own folder's tags
at once, and neither that nor the AcoustID tag counts. If a write is still
running, wait for its own job id in the API log
(`journalctl --user -u fonoteca-api.service -o cat`, `Tag write finished
(<jobId>)`), since each download logs a finish line of its own.

## Before touching anything

1. `python3 -I plan.py <out>.json` (beside this file) walks both trees by
   path and inode, skipping the test tree's `.fonoteca-*`, and writes the plan:
   new, changed, stale, only-copy, links and folders. It is read-only.
2. Classify each path: same inode (nothing to do), different inode (changed),
   only in test (new), or only in production (stale). The share is
   case-sensitive, so `CASIOPEA` → `Casiopea` is a new folder and a stale one.
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

Auto mode may deny even a read-only query of this db as [Production Reads].
Put one AskUserQuestion up front that names the reads, the stop and the
writes; the user's answer there is the go-ahead.

1. After the file sync, `POST /api/scan` (synchronous) creates the new
   tracks. Aria also rescans by itself within minutes, so check `tracks`
   first. Plays outlive their track row (no foreign key), so a re-link still
   works after a rescan. Save `GET /api/tracks?limit=100000` *before* the
   sync, because it is the only place the old rows' `addedAt` survives.
2. Compare each album's `albumId` before and after. It is the folder plus the
   ALBUM tag, case-folded, so a tag write can change it. Move `enrich_cache`
   (`album`, `albumInfo`, `sources`), `match_decisions` and album
   `tag_items`/`edits` 1:1 with insert-or-ignore, then delete the old rows.
   If the new id already holds a match, compare the release, and if they
   agree, just drop the old rows.
3. `podman stop aria`, then back up `aria.db` into `~/aria-relink-<date>/`
   with sqlite's backup API (`src.backup(dst)`).
4. Re-key the rows that name a track id:
   - `plays.trackId`
   - `playlist_tracks.trackId`
   - `tag_items` and `edits` where the kind is `track`
   - `enrich_cache`, every kind keyed on the old track id (insert or ignore,
     then delete the old row)
   - `tracks.addedAt`, restored from the old row
   - `tracks.favourite`

   `track_audio` and `track_fp` cascade with the track and are recomputed
   from the bytes, so leave them. Before writing the script, scan every
   table's every column for the old ids (read-only), so a table this list
   misses shows up. Pair old and new tracks by folder and track number, and
   assert that the titles agree. Dry-run the script, then apply it, and have
   it assert that no rows are left on the old ids.
5. `podman unshare chown 65532:65532` the db files, then `podman start aria`,
   and check `GET /api/plays/counts`.

## Apply

`python3 -I apply.py <plan>.json "/mnt/vault/media/music-trash/<date> sync"`
does all of the following in this order, so a path is never missing: new
folders, files and links, then each changed file through a hidden sibling and
`os.replace`, then stale files into the trash, then the emptied folders. It
refuses a trash folder that already exists, and a link that changed.

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
