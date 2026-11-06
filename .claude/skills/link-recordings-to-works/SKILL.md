---
name: link-recordings-to-works
description: Linking catalogue recordings that have no work to MusicBrainz works in bulk (set_recording_work), and adding tracks to a static playlist by hand. Use when asked to "link them into a work", fill missing work links on classical albums, or add a piece to a playlist like Classical Favorites.
---

# Linking recordings to works

Scripts are in `scripts/` beside this file. Run them with plain `python3` from that
directory, not `python3 -I`: `-I` drops the script's folder from the path, so
`from mbhttp import get` fails.

## 1. Find what is unlinked

DB: `podman exec -i fonoteca-postgres-1 psql -U fonoteca -d fonoteca -A -F $'\t' -t < q.sql`.
`Recordings.WorkId IS NULL` is the gap. `Genres` columns are comma-separated text.
Count an album as classical when its genres or any of its recordings' genres have
`classical` or `… classical`. Leave out `classical crossover`/`pop` albums, and also
film, musical and pop albums that only one track tags classical (Snow White, Katherine
Jenkins, Bocelli, Nat King Cole). A `Recordings.Mbid` of null means a shop-minted
recording. Those can still be linked, because the endpoint asks for no MBID.

## 2. Find the work on MusicBrainz

`Fonoteca__MusicBrainzServer` is musicbrainz.org, with no mirror. Check
`library_status` first. If enrichment is running, it is already using the address's
one request a second, and extra requests get 503s. `mbhttp.py` waits 3 s between
requests and retries a 503.

- `mbpick.py 'work:"Symphony no. 4" AND arid:<composer mbid> AND type:Symphony' 3`:
  the top matches with their parts, and flags any that are `ARRANGEMENT of`.
  Composer MBIDs come from `"Artists"`. `artist:` matches the name as MusicBrainz
  stores it, so a Cyrillic Shostakovich needs `arid:`.
- `mbinfo.py <mbid> …`: disambiguation (`[orch. Schönberg]`, `[catch-all for
  arrangements]`, `[1898 revision, in 2 parts]`), how many recordings link to the work,
  and its relations. **Choose by this, not by search score.**
- `mbtree.py <mbid> 2`: the parts, for sections below a movement (Mahler 8, Das klagende Lied).

Picking rules, learned on the Mahler box (2026-11-04):
- MusicBrainz holds duplicates. The canonical work is the one with no arrangement or
  orchestration disambiguation and the most recordings. Its parts usually have v3 ids
  (`xxxxxxxx-xxxx-3xxx-…`). Mahler 6's search hit was Zemlinsky's piano duet. The real
  work was only reachable as the parent of the most-recorded first movement.
- Match the version the recording plays: Haitink's *Das klagende Lied* is the 1898
  two-part revision, not the 1880 three-part original.
- Match a movement by its title, never by its track position. Haitink plays Mahler 6
  with the Scherzo second.
- When a track spans several sections (no track starts at the next section), link the
  enclosing part instead (Mahler 8 Part I or II). When MusicBrainz has no deeper split,
  every sub-track goes to the movement (Mahler 2's 5a–5e all go to V).
- When the album's numbering and MusicBrainz's disagree (Naudot op. 17 "No. 1" against
  MusicBrainz's No. 3 with the same movements), or the work can't name its source
  (Castello's "Sonata seconda", which both books contain), leave it unlinked and report it.

## 3. Link

`mcpcall.py set_recording_work '{"recording":"<catalogue recording id>","work":"<work mbid>"}'`
posts to `/mcp` with the token from `.env`, so the tool works even when the session's
tool list lacks it. The link is recorded as the agent's. The call goes through the
app's own MusicBrainz gate and brings in the work's parent and composers. Keep a
`recording work label` file, loop over it, and compare each answer's `mbid` with what
you sent. Then check `Works.ParentId` in the DB.

## Adding tracks to a static playlist

There is no MCP tool for this, and `/api` needs a signed-in account. A static playlist
is just its file (`<library>/Playlists/<name>.m3u8`), so append to it by hand in
Fonoteca's own format: `#EXTINF:<round(Quality_Duration or Recording.Duration)>,<credit line> - <recording title>`,
then `../<library-relative path>`. The credit line is `ArtistCredits` ordered by
`Position`, each `CreditedAs ?? LatinName ?? Name` followed by its `JoinPhrase`. Write
through a temp file and `os.replace`. Keep the file's newline and final newline.
Copy the old file to the scratchpad first.
