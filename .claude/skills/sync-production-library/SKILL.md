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
   tree or `/mnt/vault/fonoteca-test/music-trash`. If one does not, it is the
   only copy: stop and ask.

## Apply

- Hardlink the new files: `ln "${T:?}/${p:?}" "${P:?}/${p:?}"`.
- Move the stale and changed production files to
  `/mnt/vault/media/music-trash/<date> sync`, keeping their layout, rather than
  deleting them. That makes the sync reversible, and the user decides when to
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
