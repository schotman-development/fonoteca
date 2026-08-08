# Qobuzarr

A Lidarr-style release monitor for [Qobuz](https://www.qobuz.com/). You follow artists;
a deliberately slow background indexer re-checks each one for new releases, records them,
and queues the ones you want; a sequential worker downloads them straight from Qobuz,
tags them and files them into your library.

No torrents. No usenet. No indexers to configure. No transcoding — Qobuz already serves
ready-to-use FLAC and MP3, so nothing is re-encoded and `ffmpeg` is not required.

```
React single-page app  ─┐
JSON API (/api/*)      ─┼─►  one global rate limiter  ─►  Qobuz API  ─►  SQLite + your library
CLI (cli.py)           ─┘
```

The web UI is a React app that talks to the same `/api/*` JSON the CLI and any script of
yours would use — there is no private back channel, and nothing the interface can do is
missing from the API. It is built once with Node and then served by the same FastAPI
process; see [Install](#install).

---

## How it differs from Lidarr

| | Lidarr | Qobuzarr |
|---|---|---|
| Source of files | Torrent/usenet indexers + download clients | Qobuz's own API, using **your** paid subscription |
| Metadata | MusicBrainz + Lidarr's metadata proxy | Qobuz catalogue only (one source of truth) |
| Release matching | Quality profiles, scene naming, release scoring, import lists | Qobuz `format_id` + per-artist quality profile; one edition per release |
| Concurrency | Parallel downloads, many indexer queries | **One** artist indexed at a time, **one** album downloaded at a time |
| Politeness | Best effort | A hard global limiter: min interval + rolling hourly cap + circuit breaker |
| Transcoding | Optional, external | Never — the files arrive playable |
| Deployment | Mono/.NET, Docker | Plain Python 3.12, one process, SQLite; one Node build for the UI, then never again at runtime |
| Legality | Depends entirely on where the files come from | Streams you are already entitled to, for personal use |

The design bias throughout is **be slow and be boring**. A single unattended Qobuzarr should
look, from Qobuz's side, like a person browsing the catalogue occasionally.

---

## Downloading is opt-in

**Qobuzarr never downloads anything on its own unless you tell it to.** This is the one place it
deliberately departs from Lidarr's hands-off default, because "follow an artist" can otherwise mean
"start fetching a 50-album discography in lossless" — hundreds of gigabytes — from one click.

With the default `AUTO_DOWNLOAD=false`:

* the indexer still runs, discovers releases and marks matching ones **wanted**;
* nothing is ever queued automatically — the queue stays empty;
* you start downloads explicitly, whenever you like:
  * **Download** on any album row,
  * **Download wanted (N)** on an artist page to start that artist's whole backlog,
  * **Download all (N)** on [Library ▸ Releases](#running) to start the whole library's backlog,
  * `POST /api/albums/{album_id}/queue`, `POST /api/artists/{artist_id}/download-wanted`
    or `POST /api/wanted/download`.

The Releases screen is the single place to see what is outstanding across every artist, and
why: waiting on you, previously failed, or ignored. Bulk actions are capped at 500
albums per press, so one mis-click cannot commit a whole library to disk.

**Download all (N)** deliberately queues the whole monitored backlog rather than the rows
a filter happens to be showing. The count on the button and its confirmation message both
describe what it will really do, and the message says so explicitly when a filter is
narrowing the view.

### Ignoring a release you do not want

Every table that lists albums — the Releases backlog, the queue, the artist page, a release
group — carries a monitor toggle on each row: **●** monitored, **○** ignored. Press
it and that release is dropped; nothing will fetch it, and it leaves the backlog. Press it
again to want it back.

Ignoring a *wanted* release also marks it `skipped`, which is what makes it disappear from
the backlog instead of lingering there as a row nothing will ever act on. The toast that
follows the press is the only place that is explained, so it says so. Ignoring an album that
is already queued or downloading does **not** cancel it — that is what **Cancel** in the
Activity queue is for — it only stops the release coming back afterwards.
Nothing about the toggle touches the artist: they stay monitored, and the indexer keeps
checking them for everything else.

The same operation is `POST /api/albums/{album_id}/monitor` with `{"monitored": false}`,
which unlike the button takes an explicit value rather than toggling; an empty body toggles.

### When a release is not really that artist's

Two different ways Qobuz will offer you somebody else's record under an artist you follow,
and each has its own answer.

**Guest appearances.** Qobuz files a release under everybody credited on it, including the
guests. So following Joe Bonamassa offers six Black Country Communion albums, three Scary
Pockets covers and a Dion single as though he had made them — across 41 monitored artists in
one real library that is **122 releases, 8.2% of everything they offer**. Qobuz's own payload
already says which is which, so nothing has to be guessed: a release where the artist is
credited `featured-artist` and never `main-artist` is not theirs, and is not wanted. A genuine
collaboration, where both names carry `main-artist`, still counts as theirs. Turn it back on
per artist with **Include guest appearances** — worth doing for a session player, whose guest
work is most of their catalogue.

The rule is deliberately *"featured and never main"* rather than *"not main"*. That array
lists performers, so a composer is routinely absent from their own release — Samuel Barber's
id appears on 13 of his 169 — and the looser reading would throw away 92% of a composer's
catalogue.

**One artist id, several artists.** Qobuz sometimes gives several different people the same
artist entry. Id `322476` "Boaz" holds releases by at least eleven: a Dutch country singer, a
Pittsburgh rapper, a French rapper, a Tanzanian gospel act. Every one of them is stamped
`main-artist: 322476` in Qobuz's own data, so no rule about the release can separate them,
and no upstream helps — Deezer merges exactly the same people into one artist, and
MusicBrainz has only 12% of the ISRCs.

What does separate them is the credit line on the tracks: `Boaz Roelevink` and
`Boaz Ndarivoi` are different people in Qobuz's own `performers` string. So the artist page
offers **Credits**, which reads them (one call per release, so it runs in batches and you can
watch it), and lists each credited person with how many releases they account for and a few
titles. Tick the ones that are the artist you follow. From then on only their releases are
wanted, and the ones already in your backlog move to match — 37 releases left one real
backlog on the press, and the count comes back in the toast because otherwise the largest
thing it did would be invisible.

Three things worth knowing. Releases whose every credit is the bare artist name cannot be
attributed either way; while a filter is on they are not wanted, and the per-release monitor
toggle is how you claim one. Clearing the tick list removes the filter entirely rather than
rejecting everything. And nothing you already have on disk is ever demoted by either of
these — `downloaded` is a fact about the disk, and only `wanted` rows move.

### Upgrading a release you already have

Once a release is on disk, the artist page stops offering to download it and starts
comparing instead. The **Quality** column shows what is actually in your library rather
than what the catalogue advertises, and the action column shows one of two things:

* **Upgrade** — Qobuz can deliver this release in a better format than your worst file for
  it, on this account, under this artist's quality profile. Pressing it queues the album
  again; the download loop re-fetches only the tracks that are genuinely below what it can
  now get, and reuses the rest.
* **nothing at all** — the copy you have is already the best obtainable. There is no
  button because there is no honest one: re-downloading would land the same files.

Three separate ceilings can each rule an upgrade out, and the lowest wins: your
subscription (`getFileUrl` silently downgrades what your plan does not cover), the artist's
`quality_profile`, and the release itself — a CD master is 16/44.1 however politely you ask
for hi-res. `app/core/quality.py` applies all three, and both the button and the download
loop consult it, so *Upgrade* can never queue a download that then skips every track.

Anything unknown counts as "no upgrade". A release whose files Qobuzarr cannot measure —
adopted from disk with unreadable tags, say — shows no button rather than a guess. Queue it
through `POST /api/albums/{album_id}/queue` if you want to force a re-fetch.

`GET /api/albums/{album_id}` exposes the same comparison as `owned_format_id` and
`upgrade_format_id` (Qobuz format ids: 5 = MP3 320, 6 = FLAC 16/44.1, 7 = FLAC 24/≤96,
27 = FLAC 24/≤192), plus `queue_state` — a downloaded album keeps its `downloaded` status
while an upgrade is queued, so `status` alone will not tell you.

With the default naming template the quality tag is part of the album folder name, so an
upgrade lands in a *new* folder. Once the whole release has landed intact, the copy it
replaced is moved to the [trash](#library-management) — recoverable, not destroyed. An
upgrade that finished with a failed or unstreamable track never cleans up, because the old
copy is then the more complete one. Set `UPGRADE_CLEANUP=false` to always keep both.

### Library management

Everything above only ever *adds* to your library. These are the operations that change what
is already there, and they live on **Health ▸ Tidy** (`/system/tidy`, or scoped to one
artist from their page). The four panels there are ordered by increasing cost, which is the
safety argument rather than layout taste: preview-then-apply, then the additive one, then
the one that edits files in place, then the trash.

**Nothing is ever unlinked from `LIBRARY_PATH`.** Deleting moves files to `TRASH_PATH`
(`data/trash` by default) under a timestamped folder with a manifest saying where they came
from, and there they stay until you empty the trash. Emptying it is the one action in
Qobuzarr that cannot be undone, and it only ever touches the trash directory.

| Action | Where | What it does |
|---|---|---|
| **Delete** | Any release on the artist page | Folder to the trash; the release goes back to `wanted` (or `skipped` if it is not monitored). Nothing is re-queued — deleting is not a request to fetch it again. |
| **Re-file** | Health ▸ Tidy | Moves album folders, and the files inside them, to wherever `NAMING_TEMPLATE` renders today. Previews first, always. |
| **Re-tag** | Health ▸ Tidy | Rewrites tags and embedded cover art in place from the catalogue metadata already in the database. No Qobuz calls, no re-downloading, audio untouched. Releases adopted from disk are covered too: the files supply the track-level values Qobuzarr never recorded, and the database supplies the release-level ones. |
| **Restore** | Health ▸ Tidy → Trash | Puts a batch back where it came from. Run a disk scan afterwards so Qobuzarr adopts it again. |
| **Empty trash** | Health ▸ Tidy → Trash | Permanent. |

Four rules hold across all of them, and `app/core/librarian.py` is the only module allowed
to break the library's read-only-ness:

* **Every path is proved to be inside the library first.** Symlinks are resolved *before*
  the containment check, so neither `../..` nor a link planted in a folder name can walk
  out, and the library root itself is always refused.
* **A folder holding more than one release is refused.** If your library is laid out flat —
  audio sitting directly in the artist folder rather than in a folder per album — then several
  releases share one directory, and deleting or re-filing one of them would take the rest with
  it. Both say so and stop.
* **The download worker owns what it is working on.** A release that is downloading, or has
  an active queue item, is refused by delete, re-file and re-tag alike — moving files out
  from under the download loop is how you get a half-album that looks complete.
* **Re-file names a folder after what is in it.** The `{quality}` tag comes from the files on
  disk, not from what the catalogue advertises, so re-filing is idempotent rather than
  oscillating. When the files themselves cannot say — MP3s carry no bit depth — the release is
  reported as blocked instead of being given a quality tag nobody verified.

Re-file is worth a preview every time: it reports what *would* move, and lists anything it
cannot do (a busy download, an occupied destination) with the reason rather than skipping it
silently. Re-tag has no preview — it writes the metadata already in the database, so a dry
run would only ever list every album back at you. There is no undo for the old tags.

The equivalents are `DELETE /api/albums/{id}/files`, `POST /api/albums/{id}/refile`,
`POST /api/albums/{id}/retag`, `POST /api/library/refile?apply=true`,
`POST /api/library/retag`, `GET /api/library/trash`,
`POST /api/library/trash/{entry_id}/restore` and `DELETE /api/library/trash`.

Set `AUTO_DOWNLOAD=true` for classic *arr behaviour: every newly discovered release by a monitored
artist queues itself and downloads unattended, still one album at a time at the configured pace.

Two related safety notes:

* Following an artist triggers an immediate catalogue **index** (`AUTO_INDEX_ON_FOLLOW=true`). That
  is read-only and rate-limited — it downloads nothing — but it is many API calls, so you can turn
  it off and let the artist wait for the next scheduled tick.
* `cli.py` never starts the scheduler or the queue worker, so **no CLI command can begin a
  download**. Only the running server downloads.

---

## Install

Python 3.12 to run it, and **Node ≥ 20 once** to build the web UI. No system packages beyond
those two.

```bash
git clone <this repo> qobuz-downloader
cd qobuz-downloader

python3 -m venv .venv                # see the note below if this fails
./.venv/bin/pip install -r requirements.txt

npm install --prefix web             # front-end dependencies
npm run build --prefix web           # builds the UI into static/app

cp .env.example .env
$EDITOR .env                         # add QOBUZ_APP_ID and QOBUZ_USER_AUTH_TOKEN
./.venv/bin/python cli.py verify-credentials
```

**Why a build step at all.** The interface is a React app, and `static/app` — where Vite
puts it — is generated output, so it is not in the repository: committing a build is how a
checkout ends up serving something that no longer matches its own source. That means a fresh
clone has no UI until `npm run build --prefix web` has run once. Qobuzarr does not pretend
otherwise: with the build missing, every page answers with a short document naming that
exact command, and the JSON API, `/api/docs` and the CLI all work normally meanwhile. The
server picks the build up without a restart, so you can run the two in either order.

Once built, nothing about running Qobuzarr needs Node. The output is plain files served by
the same FastAPI process, and it fetches nothing from a CDN — no fonts, no scripts, no
stylesheets. Only cover art comes from outside, and it degrades to an initial when absent.

> **If `python3 -m venv .venv` fails** with an `ensurepip` error (Ubuntu ships 3.12 without
> `python3-venv`), bootstrap it the two-step way:
>
> ```bash
> python3 -m venv --without-pip .venv
> curl -sS https://bootstrap.pypa.io/get-pip.py | ./.venv/bin/python
> ./.venv/bin/pip install -r requirements.txt
> ```

`verify-credentials` is the acid test: it logs in, prints your subscription entitlements
and the `format_id`s your account may stream, derives and validates the app secret, and
performs one real signed `track/getFileUrl` call. If that command is green, everything
else works.

### Getting the credentials

`QOBUZ_APP_ID` and `QOBUZ_USER_AUTH_TOKEN` come from a logged-in Qobuz web-player session
(browser dev tools → Network → any `api.json` request → the `X-App-Id` and
`X-User-Auth-Token` request headers). They are your own account's credentials; keep them
out of version control (`.env` is gitignored, and the logger redacts both).

---

## Running

```bash
./.venv/bin/python -m uvicorn main:app --host 127.0.0.1 --port 8000
# or
./.venv/bin/python main.py
# or
./.venv/bin/python cli.py serve --port 8000
```

Then open <http://127.0.0.1:8000>.

There are four sections — **Library**, **Activity**, **Health**, **Settings** — and Library
is home. The old dashboard is gone: a screen whose job was to link to other screens is a
screen you pass through, so each of its blocks moved to where the thing it described lives.

| Section | Screen | What it is for |
| --- | --- | --- |
| Library | `/library` | Followed artists, as a grid or a dense table, with bulk monitoring edits and the counters the dashboard used to carry |
| Library | `/library/releases` | The backlog — everything discovered but not on disk, across every artist, with a per-release monitor toggle. Opens filtered to *wanted and monitored* |
| Library | `/library/artists/{id}` | One artist: their releases, their activity, and a **Settings tab** with that artist's own download behaviour |
| Library | `/library/releases/{id}` | One release in full: identity, what each metadata source voted for, other editions of the same record, the tracklist |
| Library | `/library/release-groups/{key}` | Every edition of one record, best first |
| Library | `/library/add` | Search the Qobuz catalogue and start monitoring someone |
| Activity | `/activity` | Queue and history as one stream: the sequential download queue live at the top, the append-only event log beneath it |
| Health | `/system` | Credentials, the indexer countdown, the API budget, albums by status, and a card per sub-screen |
| Health | `/system/scan` | Disk scan and bulk import — what is already in the library folder, and what Qobuzarr made of it |
| Health | `/system/enrichment` | Metadata coverage, the review list of what refused to guess, and the identify picker |
| Health | `/system/integrity` | What is actually on disk: the five-state histogram, baseline and verify passes, replaced and corrupt files |
| Health | `/system/tidy` | Re-file, NFO, re-tag and the trash — ordered by increasing cost |
| Settings | `/settings` | The effective configuration, read-only |

The Health section is addressed `/system` rather than `/health` for one small reason worth
knowing: `/health` is the server's JSON liveness probe and it wins that path, so a hard
reload on a `/health` screen would hand your browser a JSON blob instead of the application
— and a hard reload is exactly what people do when a health screen looks wrong.

**Per-artist download settings live on the artist.** Monitoring, monitor mode, quality
profile and accepted release types are a tab on that artist's own screen, next to the
releases they apply to, rather than a row in a global settings page they have nothing to do
with. `/settings` is global configuration only, it is read-only, and changing any of it means
editing `.env` and restarting.

Everything the UI does is also available as JSON under `/api/*` (interactive docs at
`/api/docs`), and `/health` is a plain liveness probe. The previous server-rendered UI is
still mounted at `/legacy` during the transition and will be removed.

### The screens update themselves

Nothing here needs reloading. Every counter, progress meter and status readout re-fetches
itself at a rate matched to how fast the thing behind it actually moves:

| Region | Refresh |
| --- | --- |
| The download queue | 5s |
| Status footer, Health overview | 10s |
| Backlog rows, artist header and releases, activity history | 15s |
| Sidebar badges | 20s |
| Library stat strip, the artist page's activity strip | 30s |
| Import progress, an integrity pass | 4s / 10s — **only while one is running** |
| A single release, a release group, the tidy screen, Settings | never — they are static until you act |

Three things make that cheap rather than chatty. Polling stops entirely while the tab is in
the background and does one catch-up refresh when you come back, never a queued burst. Every
action you take — queueing, ignoring, deleting, retrying — refreshes the live data
immediately, so the intervals above are the worst case rather than the normal one. And a
screen with nothing happening on it asks for nothing at all: the import and integrity
pollers exist only while a run is in progress.

Filters live in the address bar and travel with every refresh, so a tick can never reset a
search you have typed or a dropdown you have set — and a filtered screen is a link you can
send someone. The one list that does not refresh itself is the artist grid: it has tick boxes
for bulk edits, and losing a half-made selection to a background poll is worse than a
slightly stale count.

### Bulk editing what to monitor

Tick artists on the Library page (either view) and the bar at the top applies one
change to all of them:

| Control | Options |
| --- | --- |
| Monitoring | no change · monitor · stop monitoring |
| Monitor mode | no change · all · future · none |
| Release types | no change · **also accept** · **stop accepting** · **accept only** |

Every dropdown defaults to **no change**, and only the ones you touch are written —
changing the monitor mode for fifty artists leaves their release types and quality
profiles exactly as they were. *Also accept* and *stop accepting* are set operations
against whatever each artist already has, so "start taking singles for these twenty"
does not require knowing what each one currently accepts.

Two things worth knowing:

* Release types govern what **future** scans mark wanted. Releases already recorded
  keep the status they have — widening the types does not retroactively un-skip
  anything.
* Removing every type from an artist means they will never want anything again.
  That is allowed, but it is called out by name in the confirmation message and
  logged as a warning rather than applied quietly.

The same operation is available as `POST /api/artists/bulk`, and bulk editing only
ever changes monitoring — it never queues a download.

### Scanning music you already have

Point `LIBRARY_PATH` at a folder that already contains music and press **Scan now**
on Health ▸ Disk scan (`/system/scan`). Qobuzarr reads the tags off every audio file, groups them into
albums, matches each one against the artists you follow, and marks the complete ones
`downloaded` — so a release you already own stops sitting in the backlog.

Three things it will not do:

* **It never writes to the library.** No renaming, no moving, no tag repair, no
  deleting. Whatever laid out that folder — Beets, Picard, Lidarr, you — keeps
  owning it. Qobuzarr only writes to its own database.
* **It never marks anything wanted.** Adoption is one-directional: `wanted` may
  become `downloaded`, never the reverse. A scan can only ever *reduce* the amount
  of downloading Qobuzarr would do, which is why it is safe on the nightly job.
* **It makes no Qobuz API call.** The scan is entirely local, so it costs nothing
  against the rate limit and takes about a second per thousand files.

What it finds, it reports in three groups:

| Group | Meaning | What to do |
| --- | --- | --- |
| Incomplete on disk | Matched a followed release, but fewer files than Qobuz lists | Left `wanted` on purpose — download it, or lower `LIBRARY_SCAN_COMPLETE_RATIO` |
| Not in the catalogue | The artist is followed; this particular release is not in the database | Usually a different edition, or the indexer has not reached it yet |
| Artists you do not follow | Music by someone Qobuzarr has never heard of | **Find on Qobuz** searches for them — one live API call, only when you press it |

Matching is forgiving about layout on purpose, because the point is to adopt a folder
Qobuzarr did not create: `CD 01`/`Disc 2` sub-folders fold into their parent, a trailing
`(2018)` or `[FLAC 24-96]` is stripped off the folder name, artist names are compared with
accents and articles folded (`Thorbjørn` matches `Thorbjorn`, `The Beatles` matches
`Beatles`), and album titles go through the same edition-collapsing normaliser the indexer
uses, so `Rumours [2013 Remastered]` on disk finds `Rumours` in the database.

Use **Preview** first if you want to see the report without writing anything. The same
scan is `cli.py scan-library [--dry-run]` and `POST /api/library/scan`, and it runs
nightly with housekeeping unless you set `LIBRARY_SCAN_NIGHTLY=false`.

### Noticing when a file has changed underneath you

Everything Qobuzarr records about a release — which record it is, the identifiers written
into the tags, whether a file plays at all — is a claim the database makes about a file on
disk. A tag editor, a re-rip, a media server with write access or a copy that stopped
half-way makes that claim false, quietly, and nothing else in the program would notice.

So each file gets a hash, and there are **three** answers rather than two. A file nothing
has ever hashed is *unknown*, which is not the same as *changed* — that distinction is the
whole reason the feature is usable on an existing library, because collapsing it would
report every file you own as tampered with on the first night and bury the one edit that
mattered. Once a file has a baseline it can be *verified*, *retagged* (different bytes,
identical audio — the metadata moved, the music did not, so the identification still holds)
or *replaced* (different audio wearing the same filename, so the release is worked out again
from scratch). Alongside the hash Qobuzarr stores the number of audio samples, which is what
separates the last two without needing to know where a FLAC's tags stop and its audio starts.

Hashing costs about 200 ms a file, so the nightly pass does not do it to everything: it
checks size and modification time first and only opens the files where that pair moved. That
is a tripwire rather than evidence — a program that rewrites a file and puts the timestamp
back walks straight past it — so a fixed slice of the library is re-hashed every night
regardless, a thirtieth by default, which works through everything monthly at a cost decided
in advance. `INTEGRITY_ENABLED=false` turns the whole thing off.

Give an existing library its baseline with `cli.py baseline-library`, and check one on demand
with `cli.py verify-library`. Both are entirely local — no credentials, no network — and
neither downloads anything: a file found to hold different audio goes back on the *metadata*
work list to be identified again, not into the download queue.

### Importing those artists in bulk

Nobody is clicking *Find on Qobuz* five hundred times. **Import artists** looks up every
unfollowed artist in the library folder in one background run — one `catalog/search` call
each, through the same global limiter — and follows the ones that match:

```
500 artists x 1 search x QOBUZ_MIN_REQUEST_INTERVAL(2.0s)  ~=  17 minutes
```

The page shows live progress and a **Stop after this artist** button; the run is resumable,
because a second import only looks up whoever is still unfollowed.

**Only exact names are followed.** After the same normalisation the matcher uses everywhere
(accents, case, punctuation and a leading article folded, so `Thorbjørn` finds `Thorbjorn`
and `The Robert Cray Band` finds `Robert Cray Band`), a search hit either *is* the folder
name or it is not. Searching for `Joanne Shaw Taylor` returns both her and a
`Joanna Shaw Taylor` with one release; guessing between those is how you end up monitoring
a stranger's discography. Anything without an exact hit goes to a **Needs a decision** list
with its top candidates, one click each.

**It follows but does not index.** Firing 500 back-catalogue imports at once would be tens
of thousands of API calls in a few minutes. New artists are followed and left for the
scheduled indexer, which picks them up one per tick at its normal pace — so releases appear
over the following hours, and the disk scan will honestly report their albums as *not in the
catalogue* until it has caught up. Tick **index each catalogue immediately** only if you
understand that cost. Nothing is downloaded either way; `AUTO_DOWNLOAD` still governs that.

A run stops itself after five consecutive Qobuz failures rather than grinding through
hundreds of them, so a tripped circuit breaker or an expired token fails fast and says so.

From the terminal:

```bash
./.venv/bin/python cli.py import-library --dry-run     # who would be looked up
./.venv/bin/python cli.py import-library --yes         # actually do it
```

and over JSON: `POST /api/library/import`, `GET /api/library/import` for progress,
`POST /api/library/import/cancel`, `GET /api/library/import/preview` for the free estimate.

### About the interface

The look comes from a [Claude Design](https://claude.ai/design) document — a light,
spacious, hairline-ruled layout in the spirit of a hi-fi component manual: an app shell with
a grouped sidebar, IBM Plex type, and monospace for anything numeric.

Under it is React 19 and TypeScript with a design system of thirty-seven components in
`web/src/design`, all of them CSS Modules over one file of custom properties
(`web/src/styles/tokens.css`). No Tailwind, no component library, no CSS-in-JS. Everything is
bundled into `static/app` at build time and nothing is fetched from a CDN at runtime — the
font stacks name IBM Plex first and fall back to system faces, so the app renders identically
on a machine with no internet at all. Cover art is the only thing fetched from outside, and
it degrades to an initial when absent.

Two rules run through the whole interface and are worth knowing as a user, because they
explain what you are looking at:

**Colour marks an exception.** A screen where nothing is wrong shows no status colour at
all — `downloaded` is plain ink, not green, because a healthy state is not news. When
something is amber or red on one of these screens, it is because it wants you.

**An unknown is never drawn as a zero.** A release the disk scan adopted has no per-track
record, so its completeness meter shows an em dash and says "not counted yet" rather than
0%, which would report a complete album as empty. The same three-valued treatment runs
through every counter, meter and badge: nothing has counted this, versus this really is
none.

`web/README.md` documents the front end for anyone changing it, and
`web/src/design/README.md` is the design system's own reference.

---

## Configuration

Every setting is an environment variable, read from `.env` (or the real environment, which
wins). `.env.example` documents all of them with the same defaults. Nothing is required
except the first two.

**Seven of them can also be changed from the Settings screen**, and those writes go to an
`app_setting` table in the database rather than back into `.env`. The seven are marked
`[OVERRIDABLE]` in `.env.example` where each one appears — and on their own rows in the
tables further down — so a person reading either at the line they are about to edit finds
out there that editing it may change nothing:

| Overridable | What it changes |
|---|---|
| `NAMING_TEMPLATE` | Where a track is filed. Refused if two tracks of one release would land on the same path — that template overwrites an album one file at a time. |
| `ENRICHMENT_SOURCES` | The enrichment ladder, in order. The only one that may not take effect instantly: if a tick is running, the rebuild happens at the start of the next one and the screen says so. |
| `UPGRADE_CLEANUP` | Whether a complete upgrade trashes the copy it superseded. |
| `LIBRARY_SCAN_NIGHTLY` | Whether the nightly job scans the library folder. |
| `INTEGRITY_ENABLED` | Whether files are checked against the hash recorded for them. |
| `ENRICHMENT_WRITE_BACK` | Whether an identification is written into the files on disk. |
| `NFO_ENABLED` | Whether `artist.nfo` / `album.nfo` are written next to the music. |

The database wins over `.env` — you pressed the switch more recently than you edited the
file — and the screen shows which of the two each value is coming from, with a reset that
deletes the stored row and hands the key back to `.env`. Everything else, including every
credential, every path and every rate-limit figure, is environment-only: asking to change
one through the API is an error rather than a silent no-op.

The `cli.py` commands read the same seven values, so `enrich-now` runs the ladder you
arranged on the Settings screen and `verify-library` reports the switch you actually pressed.
Nothing else changes: `.env` is still where the other settings live, and a database that
predates this table simply means the CLI runs on `.env` alone.

### Credentials and transport

| Variable | Default | Meaning |
|---|---|---|
| `QOBUZ_APP_ID` | *(empty, required)* | App id sent as `X-App-Id`. |
| `QOBUZ_USER_AUTH_TOKEN` | *(empty, required)* | Your account token, sent as `X-User-Auth-Token`. Never logged. |
| `QOBUZ_APP_SECRET` | *(unset)* | Signing secret. Derived and cached automatically when unset — see below. |
| `QOBUZ_API_BASE` | `https://www.qobuz.com/api.json/0.2/` | API root (a trailing `/` is enforced). |
| `QOBUZ_USER_AGENT` | a desktop Chrome UA | Sent on every request. |
| `QOBUZ_FAVORITE_SYNC` | `false` | Mirror follow/unfollow to your Qobuz favourites. |
| `QOBUZ_REQUEST_TIMEOUT` | `30.0` | Per-API-call timeout, seconds. |
| `DOWNLOAD_TIMEOUT` | `300.0` | Per-file streaming timeout, seconds. |

### Rate limiting (the headline requirement)

| Variable | Default | Meaning |
|---|---|---|
| `QOBUZ_MIN_REQUEST_INTERVAL` | `2.0` | Minimum seconds between **any** two Qobuz calls, globally. |
| `QOBUZ_MAX_REQUESTS_PER_HOUR` | `1200` | Rolling one-hour ceiling on API calls. |
| `INDEXER_ARTIST_INTERVAL` | `300` | Seconds between indexer ticks (one artist per tick). |
| `INDEXER_FULL_SWEEP_HOURS` | `6` | Target period in which every monitored artist is visited once. |
| `DOWNLOAD_TRACK_DELAY` | `3.0` | Pause between consecutive track downloads. |
| `DOWNLOAD_CONCURRENCY` | `1` | Parallel downloads. Forced to ≥ 1; leave it at 1. |
| `INDEXER_JITTER_PCT` | `0.25` | ± jitter applied to every interval so the pattern is not periodic. |
| `BACKOFF_INITIAL` | `5.0` | First retry delay after a 429/5xx. |
| `BACKOFF_MAX` | `600.0` | Ceiling for retry delays. |
| `BACKOFF_MULTIPLIER` | `2.0` | Exponential factor between retries. |
| `BACKOFF_JITTER` | `0.3` | ± jitter on each backoff delay. |
| `REQUEST_MAX_RETRIES` | `5` | Attempts per API call before giving up. |
| `CIRCUIT_BREAKER_THRESHOLD` | `3` | 429s within the window before the breaker trips. |
| `CIRCUIT_BREAKER_WINDOW` | `300` | Seconds counted for the threshold. |
| `CIRCUIT_BREAKER_COOLDOWN` | `1800` | Seconds all outbound calls are paused once tripped (grows on repeats). |

`Retry-After` is honoured when Qobuz sends it. The breaker's state and the hourly budget
are visible on Health ▸ Overview, in the status footer, and at `GET /api/status`.

#### The three profiles

Copy one block into `.env`. They are all present (commented) in `.env.example`.

| | Moderate (default) | Very slow | Glacial |
|---|---|---|---|
| `QOBUZ_MIN_REQUEST_INTERVAL` | `2.0` | `6.0` | `20.0` |
| `QOBUZ_MAX_REQUESTS_PER_HOUR` | `1200` | `400` | `150` |
| `INDEXER_ARTIST_INTERVAL` | `300` | `900` | `3600` |
| `INDEXER_FULL_SWEEP_HOURS` | `6` | `24` | `72` |
| `DOWNLOAD_TRACK_DELAY` | `3.0` | `8.0` | `20.0` |
| `DOWNLOAD_CONCURRENCY` | `1` | `1` | `1` |

### Indexer and downloader

| Variable | Default | Meaning |
|---|---|---|
| `INDEXER_ENABLED` | `true` | Master switch for the background indexer. |
| `INDEXER_PAGE_SIZE` | `50` | Releases per catalogue page. |
| `INDEXER_MAX_PAGES` | `20` | Page cap per release type, per artist. |
| `AUTO_INDEX_ON_FOLLOW` | `true` | Import an artist's back catalogue as soon as you follow them. Read-only and rate-limited; downloads nothing. |
| **`AUTO_DOWNLOAD`** | **`false`** | **Opt-in switch for automatic downloading — see below.** |
| `DOWNLOAD_MAX_ATTEMPTS` | `3` | Attempts per album (and per track) before it is marked failed. |
| `DOWNLOAD_RETRY_DELAY` | `60.0` | Base delay before a failed queue item is retried. |
| `DOWNLOAD_CHUNK_SIZE` | `1048576` | Streaming chunk size in bytes. |
| `DOWNLOAD_EMBED_COVER` | `true` | Embed cover art in each file's tags. |
| `DOWNLOAD_WRITE_COVER_FILE` | `true` | Also write `cover.jpg` in the album folder. |

### Library and naming

| Variable | Default | Meaning |
|---|---|---|
| `LIBRARY_PATH` | `<repo>/music` | Where music is filed. |
| `DATA_PATH` | `<repo>/data` | SQLite database, log, artwork cache, `secret.cache`. |
| `DEFAULT_FORMAT_ID` | `27` | Preferred quality: `5` MP3 320, `6` FLAC 16/44.1, `7` FLAC 24/≤96, `27` FLAC 24/≤192. |
| `NAMING_TEMPLATE` | see below | Path template, relative to `LIBRARY_PATH`. **[OVERRIDABLE]** — also editable on the Settings screen, and a stored value there wins over this file. |
| `MAX_PATH_COMPONENT_LENGTH` | `180` | Character cap per path component (also capped at 255 UTF-8 bytes). |
| `PATH_REPLACEMENT_CHAR` | `_` | Replaces `/` and NUL inside a value. |
| `DEFAULT_MONITOR_MODE` | `all` | New artists: `all`, `future`, or `none`. |
| `DEFAULT_QUALITY_PROFILE` | `default` | New artists' profile (`default`, `lossless`, `hires`, `max`, or a bare format id). |
| `DEFAULT_ACCEPTED_RELEASE_TYPES` | `album,ep` | CSV of `album,ep,single,live,compilation,download,other`. |
| `LIBRARY_SCAN_NIGHTLY` | `true` | Run the disk scan as part of nightly housekeeping. Local-only and one-directional, so it cannot cause a download. **[OVERRIDABLE]** — a switch on the Settings screen, and a stored value there wins over this file. |
| `LIBRARY_FOLLOW_NIGHTLY` | `false` | Also **follow** the artists the nightly scan found on disk but nobody follows yet — the one nightly step that adds work rather than reducing it. One Qobuz search per unfollowed artist, followed only on an exact name match or an exact barcode match through AcoustID → MusicBrainz → Qobuz. Downloads nothing (`AUTO_DOWNLOAD` still governs that), but it can add hundreds of wanted releases overnight. |
| `LIBRARY_SCAN_COMPLETE_RATIO` | `1.0` | Fraction of an album's Qobuz track count that must be on disk before it counts as complete. Clamped to (0, 1]. |
| `LIBRARY_SCAN_FOLLOW_SYMLINKS` | `false` | Descend into symlinked directories while walking. Off so a self-referential link cannot loop. |
| `TRASH_PATH` | `<DATA_PATH>/trash` | Where deleted and superseded files go. Nothing is ever unlinked from `LIBRARY_PATH`. Same filesystem as the library makes a delete a rename rather than a copy. |
| `UPGRADE_CLEANUP` | `true` | Let a *completed* upgrade move the copy it replaced to the trash. An incomplete one never does. **[OVERRIDABLE]** — a switch on the Settings screen, and a stored value there wins over this file. |
| `INTEGRITY_ENABLED` | `true` | Check a file against its recorded hash before trusting what the database says about it. A tag editor, a re-rip or a half-finished copy leaves the two disagreeing, and nothing else notices. A file nothing has baselined is *unknown*, which is not *changed* — so switching this on does not report an untouched library as tampered with. **[OVERRIDABLE]** — a switch on the Settings screen, and a stored value there wins over this file. `POST /api/integrity/verify` reads the same effective value, so the switch also governs the manual pass. |
| `INTEGRITY_REVERIFY_FRACTION` | `0.0333` (1/30) | Share of the library re-hashed each nightly run regardless of whether it looks changed. The routine check is size + mtime, because hashing everything costs ~200 ms a file; but that pair is a tripwire, not evidence — a tool that restores an mtime after rewriting a file walks past it. This bounds how long that can hide, at a cost fixed in advance. The default verifies everything monthly. Clamped to (0, 1]; to stop entirely use `INTEGRITY_ENABLED=false`, not `0`. |

### Server and logging

| Variable | Default | Meaning |
|---|---|---|
| `HOST` | `127.0.0.1` | Bind address. |
| `PORT` | `8000` | Bind port. |
| `RELOAD` | `false` | uvicorn auto-reload (development only). |
| `LOG_LEVEL` | `INFO` | `DEBUG`/`INFO`/`WARNING`/`ERROR`/`CRITICAL`. |
| `LOG_FILE_NAME` | `qobuzarr.log` | Rotating log inside `DATA_PATH`. |
| `LOG_MAX_BYTES` | `5242880` | Rotation size. |
| `LOG_BACKUP_COUNT` | `5` | Rotated files kept. |
| `LOG_REDACT_SECRETS` | `true` | Mask tokens, secrets, signatures and signed URLs in every log line. |

### Metadata enrichment

Qobuz knows what it sells. Everything else a library wants to know — who an artist is,
what a release actually *is*, the identifiers other tools expect to find in the files —
comes from open catalogues: **AcoustID, Deezer, MusicBrainz, the Cover Art Archive and
Wikidata**.

**The audio is the anchor, so AcoustID runs first.** Every other input to an
identification is something a person typed: the folder name, the tags some earlier program
wrote, a barcode hand-entered into a catalogue. A library that needs identifying is exactly
the one where some of that is wrong — that is what makes it a problem worth solving. The one
thing in it that cannot be mistagged is the waveform. So identification starts from the
audio, and every rung below it corroborates what the audio said rather than proposing an
answer of its own.

**Identification solves the album, never the track.** A single file's fingerprint matches
several recordings, and each of those sits on any number of releases, so asking "what is this
file?" produces a list, not an answer. Asking "what is this *folder*?" produces one: a release
is admissible only when every file maps to a distinct position in it, its track count agrees,
and it accounts for essentially all the files. On one Mark Knopfler folder, *Down the Road
Wherever* explained all 15 files and *On the Road to Milano* explained 1. That is a coverage
requirement rather than a ranking — nothing is scored and nothing wins by a nose. No
admissible release means unidentified and it goes to the review list; several means they are
editions of the same record. There is no setting for the threshold, deliberately: a knob would
be turned down until something matched, and a requirement that can be lowered is a score.

Deezer still runs ahead of MusicBrainz, for the reason it always did: MusicBrainz can only be
matched exactly, exact matching needs a barcode, Qobuz fills in `upc` for a small minority of
releases, and Deezer supplies most of the rest. What changed is that Deezer is no longer the
only way to get one — the audio yields barcodes too, by way of the releases AcoustID's
recordings sit on, and so do the tags already in your files. (Qobuz album ids that *look* like
barcodes are not treated as barcodes. Some are; nothing distinguishes those from the ones that
merely resemble one, and using a value as something it is not is how a library ends up
confidently wrong.)

**Only what is on disk is enriched.** Following an artist puts their whole discography in the
database, but enrichment exists to improve *your library*: it writes tags into files, renders
`artist.nfo` and `album.nfo`, and fills in the artist and album pages. A release you have not
downloaded has no files, no NFO and nothing to correct. So an album becomes eligible when its
status is `downloaded` — a fresh download and a folder the disk scan adopted count equally,
and both become eligible the moment they land — and an artist becomes eligible when they own
at least one such album. Wanted and ignored releases are not enriched, and nothing about this
is configurable.

That matters more than it sounds: one real library held 3313 albums with 32 of them
downloaded, and 11418 pending enrichment jobs — the thirty-two releases anybody could
actually play were queued behind three thousand nobody had. Rows for releases that are not in
the library are deleted by nightly housekeeping, or on demand with `enrich-purge`. Only the
work list goes; what the sources already taught Qobuzarr stays, so a release you download
later is re-queued rather than re-fetched from scratch. The coverage figures on
**Health ▸ Enrichment** count against the library, not the catalogue, and show both numbers ("32 of
3313 albums are on disk") so a small denominator reads as scope rather than as damage.

Two further rules hold everywhere:

- **Exact or nothing.** No fuzzy scoring, no closest-hit-wins, no accepting a search
  engine's relevance ranking as evidence. Anything unmatched or ambiguous is stashed for a
  human rather than guessed at — the same doctrine the bulk importer follows, for the same
  reason.
- **A majority to overwrite.** Qobuz counts as a source, so with one rung enabled a
  disagreement is 1–1, a tie, and nothing changes. Only `release_type` is ever written back,
  and only on a real majority; everything else lives beside the Qobuz values and is merged
  when a page is rendered.

Whatever the matchers refuse to guess at lands on **Health ▸ Enrichment**, where **Find it…** opens a
picker: it searches MusicBrainz or Deezer for you and shows what came back — artwork, the
title, who it is by, the year, the track count, the barcode, and whatever note the source
keeps for telling two same-named records apart. Press *This one* and it is recorded. You can
edit the search, because what your library calls a release is often exactly why nothing
matched; and if you would rather paste, a page URL works as well as the bare id at the end of
it.

That search is the only one in the whole feature, and it is safe for one reason: nothing
picks from it but you. The automatic rungs never call those endpoints, and a candidate list
is never resolved by taking the top row — that is precisely the fuzzy scoring the matchers
refuse to do. What you supply is marked `manual` and no later automatic pass may overwrite
it; only you can correct your own answer. The other three rungs are keyed by ids their
predecessors establish, so there is nothing to search for and no picker is offered.

What leaves the machine: artist names, release barcodes, and — if you enable fingerprinting
— an acoustic hash of files already on disk. Never the files, nothing about the account. All
of it read-only: enrichment never downloads audio and never queues anything. The single
exception is `ACOUSTID_SUBMIT`, which contributes unmatched fingerprints back to AcoustID; it
is off by default and stays off unless you decide otherwise, because it writes to a public
database under your own key and nothing you send there can be taken back.

| Variable | Default | Meaning |
|---|---|---|
| `ENRICHMENT_ENABLED` | `true` | Master switch. Off means no request to a third party is ever made. |
| `ENRICHMENT_SOURCES` | `acoustid,musicbrainz,deezer,coverartarchive,wikidata` | The ladder, in the order it runs. Drop any of them. AcoustID leads because the audio is the anchor — everything else is a string somebody typed. MusicBrainz now runs **before** Deezer: AcoustID writes a MusicBrainz release id straight onto the album, which is a stronger key than a barcode and one MusicBrainz owns, so the rung the old order was protecting is the one arriving with a key already in hand. For a release the audio could not pin, Deezer-first is still better and one order cannot serve both — what settles it is that a rung refusing for want of a barcode is re-opened as soon as any later rung learns one. **[OVERRIDABLE]** — also re-orderable on the Settings screen, and a stored value there wins over this file. The one override that may not take effect on the spot: the ladder owns a rate limiter per source and there must never be two ladders alive, so a rebuild that cannot happen while a tick is draining waits for the next one, and the screen says so. |
| `ENRICHMENT_CONTACT` | *(unset)* | Email or URL for the MusicBrainz User-Agent. Their policy requires one and blocks generic browser strings, so until it is set that rung reports itself *gated* and is skipped — everything else keeps working. Deliberately not defaulted. |
| `ACOUSTID_API_KEY` | *(unset)* | Free key from [acoustid.org](https://acoustid.org/new-application). The only source here that needs registering; without it fingerprinting is skipped. |
| `ACOUSTID_SUBMIT` | `false` | Contribute fingerprints that matched nothing back to AcoustID. **Off, and it must be.** Everything else here is a read that leaves no trace upstream; this is a write to a public database under your own key, and it is not retractable — a release Qobuzarr got wrong becomes everybody's wrong answer. Does nothing without `ACOUSTID_API_KEY`. |
| `FPCALC_PATH` | *(unset)* | Chromaprint's `fpcalc` binary. Empty means "look on `PATH`" — set it explicitly if Qobuzarr runs as a long-lived service, since the process keeps whatever `PATH` it was started with and will not notice a later install. |
| `ENRICHMENT_CONSENSUS_THRESHOLD` | `0.51` | Fraction of the sources with an opinion that must agree before a Qobuz value is overwritten. Clamped up from anything at or below `0.5` — that is a tie, not a majority. |
| `ENRICHMENT_APPLY_RELEASE_TYPE` | `true` | Let a majority correct `release_type`. Qobuz genuinely guesses it (under four tracks becomes a "single"). Changes the type and never the status, so it can neither create a wanted release nor cancel one, and the original is kept so it reverses. |
| `ENRICHMENT_INTERVAL` | `300` | Seconds between enrichment ticks. |
| `ENRICHMENT_BATCH_SIZE` | `25` | Entities per tick. |
| `ENRICHMENT_TICK_BUDGET` | `240` | Wall-clock seconds a tick may spend before leaving the rest for the next one. |
| `ENRICHMENT_REFRESH_DAYS` | `90` | How long a successful match stays fresh. |
| `ENRICHMENT_FAILURE_CUTOFF` | `5` | Consecutive all-failed ticks before the job pauses itself. Stops an offline machine writing one activity row per album. |
| `ENRICHMENT_PREFER_EXTERNAL_COVER` | `false` | Prefer external artwork over Qobuz's. Off by default — Qobuz's image matches the exact release it sells, so the others fill gaps. Behind Qobuz the order is Deezer (the label's own 1000×1000) then the Cover Art Archive (whatever a contributor uploaded). |
| `ENRICHMENT_WRITE_BACK` | `true` | Write the ids into the files once a release is identified. Enrichment is scoped to the library, so it always runs *after* the download loop tagged the files — without this, MusicBrainz ids stay in the database where no player reads them. Only releases Qobuzarr downloaded itself are re-tagged; an adopted album keeps its own tags and gets only its (merged) NFO. **[OVERRIDABLE]** — a switch on the Settings screen, and a stored value there wins over this file. |
| `DEEZER_MIN_REQUEST_INTERVAL` | `0.25` | Deezer publishes roughly 50 requests per 5 seconds. |
| `DEEZER_MAX_REQUESTS_PER_HOUR` | `5000` | |
| `MUSICBRAINZ_MIN_REQUEST_INTERVAL` | `1.1` | MusicBrainz asks for about one per second and enforces it with HTTP 503. |
| `MUSICBRAINZ_MAX_REQUESTS_PER_HOUR` | `3000` | |
| `ACOUSTID_MIN_REQUEST_INTERVAL` | `0.34` | |
| `ACOUSTID_MAX_REQUESTS_PER_HOUR` | `3000` | |
| `COVERART_MIN_REQUEST_INTERVAL` | `0.5` | |
| `COVERART_MAX_REQUESTS_PER_HOUR` | `3000` | |
| `WIKIDATA_MIN_REQUEST_INTERVAL` | `0.5` | |
| `WIKIDATA_MAX_REQUESTS_PER_HOUR` | `3000` | |
| `NFO_ENABLED` | `true` | Write Kodi/Jellyfin-style `artist.nfo` and `album.nfo` next to the music. Existing files are **merged into, never replaced** — see below. **[OVERRIDABLE]** — a switch on the Settings screen, and a stored value there wins over this file. |

#### NFO files and an existing media server

If Jellyfin, Emby or Kodi already manages this library, it has already written
`artist.nfo` and `album.nfo` files, and they contain things Qobuzarr does not know: a
biography, artwork paths, that server's own ids, a `dateadded`. Qobuzarr updates only the
elements it generated — MusicBrainz ids, ISNI, barcode, catalogue number, release country —
and leaves everything else exactly where it was. Where the two spell the same fact
differently, the spelling already in the file wins, so the document stays internally
consistent.

A file containing `<lockdata>true</lockdata>` is not touched at all. That flag is how you
tell your media server to stop editing a file, and Qobuzarr treats it the same way. An
unchanged result is not rewritten either, so a stable mtime does not send your server off to
re-scan for nothing.

Set `NFO_ENABLED=false` to leave `.nfo` files alone entirely.

Each source gets its own rate limiter and its own circuit breaker. That is still the
one-limiter-per-upstream rule — MusicBrainz's allowance has nothing to do with what a paid
Qobuz account can safely spend.

Licensing, since this puts other people's data in your library: MusicBrainz is CC0, Cover
Art Archive images carry per-item licences from whoever uploaded them, and Wikipedia text is
CC BY-SA — which is why a biography is always stored, and displayed, with its source link.

---

## CLI

```bash
./.venv/bin/python cli.py <command> [options]
```

| Command | What it does |
|---|---|
| `verify-credentials` | Log in, print account + entitlements + allowed `format_id`s, resolve and validate the app secret, and make one real signed `getFileUrl` call. Use `--refresh-secret` to ignore the cache, `--track-id` to pick the probe track. |
| `add-artist <query\|id>` | Follow an artist. A numeric argument is treated as a Qobuz artist id; anything else is searched. `--dry-run` lists matches, `--index N` picks one, `--scan` indexes immediately, plus `--monitor-mode`, `--quality-profile`, `--release-types`, `--unmonitored`. |
| `list-artists` | Table of followed artists with album/wanted/downloaded counts and last-checked time. |
| `scan-now` | Run a single indexer pass now. Without arguments it takes the least-recently-checked monitored artist; `--artist <id>` targets one. |
| `import-library` | Look every artist in `LIBRARY_PATH` up on Qobuz and follow the exact matches — one search each, through the shared limiter. `--dry-run` lists who would be looked up, `--yes` is required to actually spend the calls, plus `--limit N`, `--index-now`, `--unmonitored`, `-v`. |
| `scan-library` | Walk `LIBRARY_PATH` and mark albums already on disk as downloaded. No credentials needed and no API call made. `--dry-run` reports without writing, `-v` lists the folders needing attention, `--artist <id>` restricts changes to one artist, `--path` overrides the root. |
| `baseline-library` | Record a hash for every file that has never had one. Until a file is baselined it makes no claim, so nothing can tell that it changed — this is what gives an existing library something to be checked against. Only touches rows with no recorded hash, so it never overwrites a measurement and every run shrinks the work; `--limit N` spreads the first pass over several runs. Local only. |
| `verify-library` | Open every file a track row points at, hash it, and classify it against the hash recorded for it: verified, retagged (different bytes, identical audio), replaced (different audio), missing, or unknown. The expensive check on purpose — ~200 ms a file — because the nightly pass trusts size and mtime, and a tool that restores an mtime after rewriting a file walks straight past that. `--limit N` bounds a run in the same rotation the nightly job uses, so it measures whatever has gone longest unchecked; `--dry-run` reports without writing. Local only, and nothing is queued: files found to hold different audio go back on the *enrichment* list to be identified again. |
| `queue-status` | Queue counts plus the head of the queue (`--all` includes finished/failed, `--limit N`). |
| `enrich-now` | Run one metadata batch against the open sources, or report what is due. Touches no Qobuz endpoint and needs no credentials. Without `--run` it is a dry report; `--artist <id>` / `--album <id>` push one to the front, `--limit N` caps the batch. |
| `enrich-purge` | Delete the enrichment work list for albums that are not downloaded and artists who own none — enrichment is scoped to what is on disk. Local only; nightly housekeeping does the same thing. `--dry-run` reports the scope and the row count without deleting. Metadata already fetched is never touched. |
| `folder` | Record the two decisions the audio cannot make about one library folder. `folder <path> --album <id>` binds it to a Qobuz release by hand (run `scan-library` afterwards to adopt it); `folder <path>` with no `--album` marks it as holding **no** catalogue release at all — covers never released anywhere, a game soundtrack, a radio bootleg. Only a person may say that second thing: the automatic chain failing to find a release is not evidence that none exists, so a machine allowed to conclude it would quietly retire folders it merely could not identify. A marked folder leaves the unmatched count (it is reported separately as `albums_excluded`, never hidden) and `bind-folders` stops spending requests on it. `--note` records why, `--clear` undoes either and returns the folder to the unmatched list, `--list` shows every decision. Local only. |
| `bind-folders` | Identify the folders the disk scan could not name, using their audio, and bind them to the Qobuz release they hold. The scan matches a folder by normalising its album title, so a folder whose name is a different string for the same record never matches — `Play: The Guitar Album` against a catalogue that calls it `Play` — and that release then sits in the wanted list waiting to be downloaded again. This runs AcoustID over the files, takes every barcode in the release group MusicBrainz agrees on, and lets one of those barcodes pick the Qobuz album out of a search; a name only ever narrows the search and never chooses, so two candidates is reported as ambiguous rather than guessed at. Writes only the binding — no status moves, no file is touched, nothing is queued — so run `scan-library` afterwards to adopt them. `--limit N` caps the pass (default 200), `--dry-run` identifies and writes nothing, `-v` lists what is still unresolved. Needs `ACOUSTID_API_KEY`, `ENRICHMENT_CONTACT` and `fpcalc`; without them it reports itself gated and does nothing. |
| `backfill-upc` | Fetch the barcode Qobuz holds for releases already on disk that have none. The indexer builds album rows from `artist/getReleasesList`, which does not publish a `upc`; only `album/get` does, and only the download loop calls it — so a release Qobuzarr fetched has a barcode and a release the disk scan adopted does not. That field is the key exact matching is built on, so without it Deezer and MusicBrainz can only match by browsing an artist's discography. One `album/get` per release, scoped to what is on disk and capped by `--limit` (default 1000); `--dry-run` makes the requests and reports without writing. Fills only — a release that already has one is skipped — and writes no other column, queues nothing and touches no file. The enrichment rows it unblocks are re-opened by the enricher's own next tick. |
| `serve` | Run the web server (`--host`, `--port`, `--reload`). |

The CLI deliberately does **not** start the queue worker or the scheduler, so it never
begins a download behind your back — only the server does that. It shares the same rate
limiter settings, so it is exactly as polite.

---

## Library layout

Default template:

```
{artist}/{album} ({year})[ [{quality}]]/{disc_prefix}{track:02d} - {title}.{ext}
```

```
music/
└── Nils Frahm/
    └── Night (2025) [FLAC 24-96]/
        ├── cover.jpg
        ├── 01 - Wesen.flac
        ├── 02 - Monuments Again.flac
        └── ...
    └── Some Live Thing (2019) [FLAC 24-96]/
        ├── cover.jpg
        ├── Disc 1/01 - Opening.flac      # "Disc N/" only when media_count > 1
        └── Disc 2/01 - Encore.flac
```

* Placeholders: `{artist}` `{albumartist}` `{album}` `{album_title}` `{version}` `{year}`
  `{quality}` `{format}` `{disc_prefix}` `{disc}` `{discs}` `{track}` `{tracks}` `{title}`
  `{track_title}` `{ext}` `{label}` `{genre}` `{isrc}` `{upc}`.
  Note the **underscore** in `{disc_prefix}` — a hyphen cannot be used with `str.format`.
* `[ ... ]` marks an optional segment: it disappears entirely when the placeholder inside
  is empty, which is how `[ [{quality}]]` renders as ` [FLAC 24-96]` or as nothing at all.
  Only the outermost bracket pair is a marker; brackets inside are literal.
* `{album}` and `{title}` include the Qobuz `version` suffix (`All Melody (Deluxe Edition)`)
  so two editions cannot collide; `{album_title}`/`{track_title}` are the bare forms.
* Components are NFC-normalised, stripped of `/` and control characters, and capped at
  `MAX_PATH_COMPONENT_LENGTH` characters and 255 UTF-8 bytes.
* Audio is streamed to `<name>.part`, tagged there, then atomically renamed — a file that
  exists is always complete and tagged, which is what makes downloads resumable.
* Tags written (FLAC Vorbis comments / ID3v2.4): title, artist, albumartist, album, date,
  tracknumber/totaltracks, discnumber/totaldiscs, genre, isrc, label, composer, plus
  embedded cover art.
* Naming and tagging always use the `format_id`/`bit_depth`/`sampling_rate` that
  `getFileUrl` actually **returned** — Qobuz silently downgrades a request that exceeds
  your entitlement.
* Because `{quality}` is part of the album folder by default, an
  [upgrade](#upgrading-a-release-you-already-have) lands in a new folder rather than
  overwriting the old one; the copy it replaced is moved to the trash once the new one is
  complete. See [Library management](#library-management) for re-filing an existing library
  after changing this template.

---

## The app secret

Qobuz signs `track/getFileUrl` with an `app_secret` that the official web player embeds in
its JavaScript bundle. Qobuzarr reproduces exactly what the player does, so that your own
paid account can use the official API:

1. If `QOBUZ_APP_SECRET` is set, it is used verbatim.
2. Otherwise, if `data/secret.cache` holds a previously validated secret, that is used.
3. Otherwise it fetches `https://play.qobuz.com/login`, extracts the bundle path, and pulls
   out every candidate secret (the plaintext `production:{api:{...}}` pair plus each
   `initialSeed` + `info` + `extras` triple, base64-decoded after dropping the trailing 44
   characters).
4. Each candidate is **validated** with a real signed request. The winner is cached to
   `data/secret.cache` and logged as a hint to paste into `.env`.

Two field notes worth keeping: the plaintext secret currently in the bundle is stale and is
rejected by the API — the winner is one of the seed-derived candidates, which is why every
candidate is validated rather than trusted. And the bundle path contains letters
(`.../resources/8.2.0-b034/bundle.js`), so a digits-only version regex will not match.

If nothing validates, Qobuzarr raises an actionable error asking you to set
`QOBUZ_APP_SECRET` manually. If Qobuz ever rejects a signature at runtime, the client
re-derives once and retries automatically.

The signature itself is
`md5("trackgetFileUrl" + "format_id" + <id> + "intent" + <intent> + "track_id" + <id> + <ts> + <secret>)`
— endpoint without slashes, then parameters in alphabetical order, then the timestamp, then
the secret. Secrets, tokens and signed URLs are redacted from every log line.

---

## How the slow indexer paces itself

One APScheduler job wakes on an interval and processes **exactly one artist**, never more,
never concurrently.

```
tick interval        = jitter(INDEXER_ARTIST_INTERVAL)
artist_period(n)     = max(INDEXER_ARTIST_INTERVAL, INDEXER_FULL_SWEEP_HOURS * 3600 / n)
full_cycle(n)        = artist_period(n) * n
jitter(x)            = x * (1 ± INDEXER_JITTER_PCT)      # uniform, clamped at 0
```

where `n` is the number of monitored artists. On each tick the indexer picks the single
artist whose `last_checked_at` is oldest **and** older than `artist_period(n)`; if none is
due it does nothing and goes back to sleep. With the defaults:

| Monitored artists | `artist_period` | Full sweep |
|---|---|---|
| 10 | 2160 s (36 min) | 6 h |
| 72 | 300 s (the floor) | 6 h |
| 200 | 300 s (the floor) | ~16.7 h |
| 1000 | 300 s (the floor) | ~83 h |

Once the library is big enough that the 5-minute floor binds, the sweep simply takes longer
— the request rate never rises. Every one of those requests still has to pass the global
limiter (≥ 2 s apart, ≤ 1200/hour, shared with UI searches and downloads), and a tripped
circuit breaker pauses the indexer entirely for the cooldown.

Per artist, the indexer fetches only the release types that artist accepts, pages through
them (`INDEXER_PAGE_SIZE` × `INDEXER_MAX_PAGES` at most), upserts what it finds, de-duplicates
editions of the same release (preferring hi-res / more tracks, never demoting something
already downloaded), marks the matching new releases `wanted`, and enqueues them. The
download worker then takes one album at a time.

---

## Running as a service (systemd)

`/etc/systemd/system/qobuzarr.service` — replace `YOUR_USER` and the two paths with
your own:

```ini
[Unit]
Description=Qobuzarr — Qobuz release monitor
After=network-online.target
Wants=network-online.target

[Service]
Type=simple
User=YOUR_USER
Group=YOUR_USER
WorkingDirectory=/path/to/qobuzarr
Environment=PYTHONUNBUFFERED=1
ExecStart=/path/to/qobuzarr/.venv/bin/python -m uvicorn main:app --host 127.0.0.1 --port 8000
Restart=on-failure
RestartSec=10s
TimeoutStopSec=60

# Hardening (relax if your library lives elsewhere)
NoNewPrivileges=true
PrivateTmp=true
ProtectSystem=full
ProtectHome=read-only
ReadWritePaths=/path/to/qobuzarr/data /path/to/music/library

[Install]
WantedBy=multi-user.target
```

```bash
sudo systemctl daemon-reload
sudo systemctl enable --now qobuzarr
journalctl -u qobuzarr -f
```

`TimeoutStopSec=60` matters: on shutdown the worker finishes the file it is streaming and
leaves the album queued, so a restart resumes cleanly instead of leaving debris.

The service unit runs Python only — Node is a build-time tool and has no business in a
long-lived process. Run `npm run build --prefix web` when you deploy a new version; the
server notices the new build without a restart, so the order does not matter. One thing that
*is* worth setting explicitly for a service: `FPCALC_PATH`. A process keeps the `PATH` it was
started with, so installing Chromaprint after the unit is running leaves the fingerprinting
rung reporting itself unavailable while `which fpcalc` at your prompt says everything is
fine.

---

## Development

```bash
./.venv/bin/python -m pytest tests -q      # 1651 tests, no network
./.venv/bin/python -c "import main"        # import smoke test
./.venv/bin/python -m uvicorn main:app --port 8899

npm test --prefix web                      # 1050 component tests, jsdom, no network
npm run typecheck --prefix web             # tsc -b, strict
npm run lint --prefix web
```

### Working on the front end

Building after every edit is not how to do it. Run the API and the Vite dev server side by
side instead:

```bash
./.venv/bin/python -m uvicorn main:app --port 8000     # terminal one
npm run dev --prefix web                               # terminal two, then open :5173
```

Vite proxies `/api`, `/health` and `/static` through to uvicorn on port 8000, so the dev
server is talking to your real database, your real library and your real Qobuz credentials
— there is no mock layer to keep honest and no second copy of the API to drift. Hot module
replacement means an edit is on screen before you have looked up. When you are done, one
`npm run build --prefix web` puts it back under `http://127.0.0.1:8000` for good.

`web/src/api/types.ts` mirrors `app/schemas.py` by hand rather than by code generation,
because a generator has nowhere to record *why* a field is nullable. The cost of that choice
is drift, so `tests/test_wire_contract.py` walks the running server's own OpenAPI document
and fails when the two halves disagree — add a field on one side and the Python suite tells
you about the other. It skips entirely when `web/` is absent, so a back-end-only checkout
still runs green.

There is no Alembic: `init_db()` only runs `create_all`, which adds new *tables* to an
existing database and silently will not add a new *column*. A new table is therefore free —
`app_setting`, which stores the overridable settings, needed nothing — while a release that
adds a column ships a one-off script beside it, each `PRAGMA`-guarded per column, idempotent
and safe to run twice, with a `--dry-run` that writes nothing. Stop the server, then:

```bash
./.venv/bin/python scripts/migrate_integrity.py     # content_hash, qid and friends
./.venv/bin/python scripts/migrate_album_flags.py   # pin_tags, freeze_path, mute_integrity
./.venv/bin/python scripts/migrate_artist_tags.py   # sort_name, aliases_json
./.venv/bin/python scripts/migrate_artist_attribution.py  # guest appearances, credit filter
./.venv/bin/python scripts/migrate_folder_bindings.py     # folder_bindings.state, .note
```

A database created fresh needs none of them. Skipping one on a database that does need it
shows up as `no such column: albums.pin_tags` the first time a page reads it, not at startup.

The last one is the exception that proves the rule about new tables being free.
`folder_bindings` *was* free — `create_all` made it. Two columns were added to it a few hours
later, by which point the table already existed on the one database the intermediate build had
been run against, so `create_all` saw the name, decided there was nothing to do, and the
columns never appeared. **A new table stops being free the moment anybody has run the build
that created it.** That script rebuilds the table rather than adding columns, because
`album_id` had to become nullable and SQLite cannot drop a `NOT NULL` constraint with
`ALTER TABLE`; rows are carried over inside one transaction.

Architecture notes for future work live in `CLAUDE.md`; the front end has its own in
`web/README.md`.

---

## Legal / terms of service

Qobuzarr is a personal-use tool that drives the **official Qobuz API with your own
credentials**. It does not circumvent DRM, does not share files, contains no accounts or
keys of its own, and gives you nothing your subscription does not already entitle you to.

* You need your **own paid Qobuz subscription**. Qobuzarr is useless without one.
* Downloading is limited to what your subscription lets you stream. The API silently
  downgrades anything above your entitlement, and Qobuzarr records what it actually got.
* Redistributing the downloaded files, or using them commercially, is on you and is almost
  certainly a breach of Qobuz's terms and of copyright law.
* Automated access may not be something Qobuz's terms of service contemplate. That is why
  everything is rate limited by default and why the "very slow" and "glacial" profiles
  exist. Do not raise the limits to hammer their API; you risk your own account.
* No warranty. Use at your own risk.
