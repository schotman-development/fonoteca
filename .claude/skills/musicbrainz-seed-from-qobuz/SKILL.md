---
name: musicbrainz-seed-from-qobuz
description: Seeding a new MusicBrainz release (or a new edition in an existing release group) from a Qobuz album, as an HTML form that opens MB's release editor prefilled. Use when asked to "seed it on MusicBrainz", add a Qobuz album to MB, or add an edition with extra tracks to an existing release group.
---

# Seed a MusicBrainz release from Qobuz

1. **Qobuz data, via the API, not the page.** The web page renders only the first 50 tracks.
   `GET https://www.qobuz.com/api.json/0.2/album/get?album_id=<id>&limit=500` with headers
   `X-App-Id` and `X-User-Auth-Token` (in fonoteca's `.env`, `Fonoteca__Providers__Qobuz__*`).
   Gives upc, label, `release_date_original`, tracks with `title`, `version`, `duration` (s), `isrc`, `performers`.
   No auth token = 401. Album ids that aren't UPCs (e.g. `ndtjd687vdsha`) work too.
   A UPC isn't always the album id (`0710347409441` gave 404); find the id with `album/search?query=<title artist>`.
2. **Check it isn't there first**: `/ws/2/release?query=barcode:<upc>`, `/ws/2/url?resource=<qobuz url>&inc=release-rels`,
   `/ws/2/isrc/<isrc>?inc=releases` on a sample, `tracks:<n>` with the artist. 3s spacing on musicbrainz.org, back off on 503.
   A Fonoteca "NoRecording" folder may need no seed at all: if the open files' ISRC tags resolve to recordings on the
   album the folder's other files are filed under, only AcoustID lacks the link, so it gets filed on Identify instead.
   A same-artist recording with no ISRC (e.g. on a later compilation) and a length within ~1-2 s: seed its MBID, say so
   in the edit note, and tell the user to clear it in the Recordings tab if they disagree.
   Labels: read the disambiguation. "X GmbH: not for release label use" / "not an imprint" point to the imprint to seed
   (Ruf Records releases: "Ruf" `16eb1ea3-5178-4934-b942-dee1542d1bd1`).
3. **Build a hidden-input form** POSTing to `https://musicbrainz.org/release/add` (target `_blank`). `seed.py` here is the
   plain case (`python3 seed.py album.json <qobuz url> out.html`; hardcodes Nat King Cole's MBID, edit that). Fields
   (wiki: Development/Release_Editor_Seeding): `name`, `type` (repeatable: Album, Compilation), `status=official`,
   `packaging=None`, `language`, `script`, `barcode`, `events.0.date.year|month|day`, `events.0.country=XW`,
   `labels.0.mbid|name|catalog_number`, `artist_credit.names.N.mbid|name|join_phrase`, `mediums.0.format=Digital Media`,
   `mediums.0.track.N.name|number|length(ms)|recording|artist_credit.names.0.*`, `urls.N.url` + `urls.N.link_type`
   (74 purchase for download, 980 streaming, 85 free streaming — verified), `edit_note`, `comment`, `release_group`.
4. **Style calls**: store version suffixes become lowercase ETI — "Title (remastered)", but proper nouns stay
   ("Spanish version"). A store "(Bonus Track Version)" goes to `comment`, not the title. Enter store typos as printed
   and tell the user. A Jan-1 date is a placeholder: seed the year only.
5. **New edition in an existing group**: seed `release_group=<rg mbid>`, copy the sibling release's artist credit and
   track titles/credits, and seed `recording` MBIDs for tracks whose ISRC matches the sibling's recordings exactly.
   Lengths from the user's files (ffprobe) where the folder is this edition.
6. **Universal barcodes on Qobuz** drop the UPC check digit: `0002894758328` = 12-digit `028947583288`. Restore it
   (UPC-A: 3×odd positions + even, check = (10 − sum mod 10) mod 10) and say so in the edit note.
7. **Delivery**: SendUserFile has not reliably reached the user and serving on the LAN can be blocked. Give an
   `scp` command instead: `whoami` and `hostname` for the user part and host; if the host name won't resolve from
   the user's machine, add `tailscale status --self --json | jq -r '.Self.DNSName'` or the IP as a fallback. The
   scratchpad file is under `/tmp`, so say it won't survive a reboot.
8. **Same-artist name variants**: if Qobuz's artist name differs from MB's canonical one but is really the same
   act billed differently, check MB's alias list before creating a second artist — seed with the existing artist's
   mbid and the variant name as `artist_credit.names.N.name` (MB records it as a name-as-credited, no new artist).

Not seedable: ISRCs (add after saving, e.g. MagicISRC), performer relationships (put them in the edit note).

**No store page (release withdrawn):** seed from the purchased files instead — production copy tags (album, date, `copyright`, a Qobuz-style `comment` "Label : X - Distributor", track n/N, disc), the embedded sleeve (`ffmpeg -an -c:v copy -frames:v 1`), and the recordings of another release of the same broadcast. Missing tracks may be inferred from N and a sibling's running order; say so in the edit note and leave their lengths empty. Bootleg labels ("live bootlegs" disambiguation) → `status=bootleg`, types Album + Live. The user may save it into an existing release group rather than a new one (Tokyo 1988, 2026-09-28).
