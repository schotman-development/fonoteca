# 0014 — Album motion artwork, from Apple Music's public pages

**Status:** accepted, 2026-10-07

## Context

Apple Music shows a looping video in place of many albums' sleeves — "album
motion" in Apple's partner documents. Labels deliver two per record through
their distributors: a square one (3840×3840, for Mac, iPad and TV) and a tall
3:4 one (2048×2732, for phones), 8 to 35 seconds, silent, made to loop, the
first frame matching the cover. There is no per-track video; the album's is
the one a song shows.

The owner wants these kept for a dedicated client, and written into the
library the way covers are. Apple offers no supported way to get them: the
documented Apple Music API omits them, MusicKit has no property for them, and
an Apple engineer said in 2022 that they are not made available to third-party
apps. Every tool that fetches them reads something Apple serves its own
clients.

## Decision

**Three public requests and a download, each something a browser fetches
without signing in** (`Providers/AppleMusic/AppleMusicMotions.cs`):

1. The documented search API (`itunes.apple.com/lookup?upc=…`, Apple's stated
   limit about 20 calls a minute) turns a barcode into Apple's album id — the
   display edition's barcode first, then the album's other editions' in one
   request, counted only under the same title. Where no barcode identifies a
   record, a title search counts only where a billed name, the title and the
   year agree: `QobuzCovers`' rule, reused rather than restated.
2. The album's public page (`music.apple.com/{store}/album/{id}`) embeds the
   JSON its header is drawn from; the header item carries
   `videoArtwork…motionDetailSquare.video` and
   `tallVideoArtwork…motionDetailTall.video`, each an HLS master playlist.
3. Each master lists an H.264 and an HEVC ladder. The best **H.264** rung —
   1080 square, 1080×1440 tall — is a single fragmented MP4 addressed by byte
   ranges, so the file it names is downloaded whole: a video every player
   opens, about 25 MB, no remuxing.

The US shop is asked first; `Fonoteca:AppleMusicFallbackStorefront` names a
second, asked only where the first identified no record at all, since a video
is the same in every shop that sells the record.

**Kept like a cover, written like one.** An eighth enrichment stage stores
both videos in `AlbumMotions`, one row per album (release group), with the
record they came from and how it was recognised. A row with no video is a
stamp and expires after a week; a row with one never does; an outage writes
nothing. The tag write then puts them beside the album as
`square_animated_artwork.mp4` and `tall_animated_artwork.mp4` — the names the
most-used Apple Music downloader writes, never `cover.*`, which a player globbing
for the sleeve would pick up — under `AllowFileMutation`, displacing rather than
overwriting, journalled as `tagging.motion` so Undo takes them back with the
sleeve.

## Why not the alternatives

**The web player's private API** (`amp-api.music.apple.com`, `extend=editorialVideo`)
answers in one request and is what most tools use. It needs the bearer token
inlined in the web player's JavaScript bundle, sent with a forged
`Origin: https://music.apple.com`. That is using a credential Apple issued to
its own player, not to this application; it is also the part that breaks —
about once a year, most recently in June 2026, when the token moved bundles
and changed its header. The public route has broken once in the same years (a
field on the page was renamed).

**A third-party service** (artwork.m8tec.top, artwork.boidu.dev) does the
scraping itself and hands back URLs. It moves the dependency onto a hobby
server that has already fallen over under load once, for a gain of fewer
lines here.

## Consequences

**Apple's terms forbid scraping its services**, and nothing here changes that.
The requests are paced well under what a person browsing would make, identify
themselves in the User-Agent, and nothing is fetched for an album nobody holds.

**A page that changes shape stops the stage rather than answering "none".**
No embedded JSON, or JSON without sections, throws `ProviderRejectedException`
and the stage ends unstamped, so a redesign cannot mark a whole library as
having no videos for a week. A page with sections but no header is that one
page being odd and reads as none, with a warning, so a single album cannot
block the stage forever.

**What is wrong with one record's video reads as none for it** — no H.264
rung, a rung split across files, a host other than `mvod.itunes.apple.com`,
anything not an MP4 or over 100 MB. An HTTP failure is an outage and throws.

**The catalogue grows by about 50 MB per album with motion artwork**, held in
PostgreSQL until the tag write copies it out, as a cover's bytes are.

**A video already in the folder is recognised by its length**, not compared
byte for byte, so a second run over a library does not read every video back
from the database and the disk.

**Not built:** serving the videos over the API (the client will), artist-page
motion, and any way for a person to refuse a wrong title match other than SQL.

## When to revisit

**When the page reader starts throwing** — the warning and the stopped stage
are the signal; the fix is in `PageAsync`.

**When the client exists**, for the endpoint that serves these.

**If title matches prove wrong**, for a refusal a person can make, as the
cover dialog has.
