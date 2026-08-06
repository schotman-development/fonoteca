# Fonoteca

A Lidarr-style release monitor for [Qobuz](https://www.qobuz.com/). You follow artists;
a deliberately slow background indexer re-checks each one for new releases, records them,
and queues the ones you want; a sequential worker downloads them straight from Qobuz,
tags them and files them into your library.

No torrents. No usenet. No indexers to configure. No transcoding — Qobuz already serves
ready-to-use FLAC and MP3, so nothing is re-encoded and `ffmpeg` is not required.

```
FastAPI + HTMX web UI  ─┐
JSON API (/api/*)      ─┼─►  one global rate limiter  ─►  Qobuz API  ─►  SQLite + your library
CLI (cli.py)           ─┘
```

---

## How it differs from Lidarr

| | Lidarr | Fonoteca |
|---|---|---|
| Source of files | Torrent/usenet indexers + download clients | Qobuz's own API, using **your** paid subscription |
| Metadata | MusicBrainz + Lidarr's metadata proxy | Qobuz catalogue only (one source of truth) |
| Release matching | Quality profiles, scene naming, release scoring, import lists | Qobuz `format_id` + per-artist quality profile; one edition per release |
| Concurrency | Parallel downloads, many indexer queries | **One** artist indexed at a time, **one** album downloaded at a time |
| Politeness | Best effort | A hard global limiter: min interval + rolling hourly cap + circuit breaker |
| Transcoding | Optional, external | Never — the files arrive playable |
| Deployment | Mono/.NET, Docker | Plain Python 3.12, one process, SQLite, no build step |
| Legality | Depends entirely on where the files come from | Streams you are already entitled to, for personal use |

The design bias throughout is **be slow and be boring**. A single unattended Fonoteca should
look, from Qobuz's side, like a person browsing the catalogue occasionally.

---

## Downloading is opt-in

**Fonoteca never downloads anything on its own unless you tell it to.** This is the one place it
deliberately departs from Lidarr's hands-off default, because "follow an artist" can otherwise mean
"start fetching a 50-album discography in lossless" — hundreds of gigabytes — from one click.

With the default `AUTO_DOWNLOAD=false`:

* the indexer still runs, discovers releases and marks matching ones **wanted**;
* nothing is ever queued automatically — the queue stays empty;
* you start downloads explicitly, whenever you like:
  * **Download** on any album row,
  * **Download wanted (N)** on an artist page to start that artist's whole backlog,
  * **Download all (N)** on the [Wanted](#running) page to start the whole library's backlog,
  * `POST /api/albums/{album_id}/queue`, `POST /api/artists/{artist_id}/download-wanted`
    or `POST /api/wanted/download`.

The Wanted page is the single place to see what is outstanding across every artist, and
why: waiting on you, previously failed, or ignored. Bulk actions are capped at 500
albums per press, so one mis-click cannot commit a whole library to disk.

**Download all (N)** deliberately queues the whole monitored backlog rather than the rows
a filter happens to be showing. The count on the button and its confirmation message both
describe what it will really do, and the message says so explicitly when a filter is
narrowing the view.

### Ignoring a release you do not want

Every table that lists albums — the Wanted page, the queue, the dashboard preview and the
artist page — carries a monitor toggle on each row: **●** monitored, **○** ignored. Press
it and that release is dropped; nothing will fetch it, and it leaves the backlog. Press it
again to want it back.

Ignoring a *wanted* release also marks it `skipped`, which is what makes it disappear from
the Wanted page instead of lingering there as a row nothing will ever act on. Ignoring an
album that is already queued or downloading does **not** cancel it — that is what
**Cancel** on the queue page is for — it only stops the release coming back afterwards.
Nothing about the toggle touches the artist: they stay monitored, and the indexer keeps
checking them for everything else.

The same operation is `POST /api/albums/{album_id}/monitor` with `{"monitored": false}`,
which unlike the button takes an explicit value rather than toggling; an empty body toggles.

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

Anything unknown counts as "no upgrade". A release whose files Fonoteca cannot measure —
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
is already there, and they live on the **Library tidy** page (`/library/tidy`, or scoped to
one artist from their page).

**Nothing is ever unlinked from `LIBRARY_PATH`.** Deleting moves files to `TRASH_PATH`
(`data/trash` by default) under a timestamped folder with a manifest saying where they came
from, and there they stay until you empty the trash. Emptying it is the one action in
Fonoteca that cannot be undone, and it only ever touches the trash directory.

| Action | Where | What it does |
|---|---|---|
| **Delete** | Any release on the artist page | Folder to the trash; the release goes back to `wanted` (or `skipped` if it is not monitored). Nothing is re-queued — deleting is not a request to fetch it again. |
| **Re-file** | Library tidy | Moves album folders, and the files inside them, to wherever `NAMING_TEMPLATE` renders today. Previews first, always. |
| **Re-tag** | Library tidy | Rewrites tags and embedded cover art in place from the catalogue metadata already in the database. No Qobuz calls, no re-downloading, audio untouched. |
| **Restore** | Library tidy → Trash | Puts a batch back where it came from. Run a disk scan afterwards so Fonoteca adopts it again. |
| **Empty trash** | Library tidy → Trash | Permanent. |

Three rules hold across all of them, and `app/core/librarian.py` is the only module allowed
to break the library's read-only-ness:

* **Every path is proved to be inside the library first.** Symlinks are resolved *before*
  the containment check, so neither `../..` nor a link planted in a folder name can walk
  out, and the library root itself is always refused.
* **The download worker owns what it is working on.** A release that is downloading, or has
  an active queue item, is refused by delete, re-file and re-tag alike — moving files out
  from under the download loop is how you get a half-album that looks complete.
* **Re-file names a folder after what is in it.** The `{quality}` tag comes from the files on
  disk, not from what the catalogue advertises, so re-filing is idempotent rather than
  oscillating.

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

Python 3.12, no system packages beyond Python itself.

```bash
git clone <this repo> fonoteca
cd fonoteca

python3 -m venv .venv                # see the note below if this fails
./.venv/bin/pip install -r requirements.txt

cp .env.example .env
$EDITOR .env                         # add QOBUZ_APP_ID and QOBUZ_USER_AUTH_TOKEN
./.venv/bin/python cli.py verify-credentials
```

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

| Section | Page | What it is for |
| --- | --- | --- |
| Collection | `/` | Dashboard — counts, the indexer countdown, the API budget, recent activity |
| Collection | `/artists` | Library — followed artists as an artwork grid (`?view=table` for the dense table), with bulk monitoring edits |
| Collection | `/artists/{id}` | One artist: releases (searchable by title, edition or label), per-artist monitoring, their activity |
| Collection | `/add` | Search the Qobuz catalogue and start monitoring someone |
| Automation | `/wanted` | The backlog — everything discovered but not on disk, across all artists, with a per-release monitor toggle |
| Automation | `/queue` | The sequential download queue, polled live |
| Automation | `/activity` | Append-only history of every index, queue and download event |
| System | `/library/scan` | Disk scan — what is already in the library folder, and what Fonoteca made of it |
| System | `/settings` | The effective configuration, read-only |

Everything the UI does is also available as JSON under `/api/*` (interactive docs at
`/api/docs`), and `/health` is a plain liveness probe.

### The pages update themselves

Nothing here needs reloading. Every counter, progress meter and status readout is a
region that re-fetches its own fragment, at a rate matched to how fast the thing behind
it actually moves:

| Region | Refresh |
| --- | --- |
| Queue table and the dashboard's queue preview | 5s |
| Dashboard counters, the indexer and rate-limiter cards, the status footer | 10s |
| Wanted backlog, artist releases and stats, activity log, dashboard backlog | 15s |
| Sidebar badges, dashboard activity | 20s |
| "Least recently checked" artist tiles | 60s |

Two things make that cheap rather than chatty. Polling stops entirely while the tab is
in the background and does one catch-up refresh when you come back, and every action you
take — queueing, ignoring, deleting, retrying — refreshes the live regions immediately,
so the intervals above are the worst case, not the normal one.

Tables that you can filter carry their filter form into every refresh, so a tick can
never reset a search you have typed or a dropdown you have set. The one screen that does
*not* refresh itself is the Library artist grid: it has tick boxes for bulk edits, and
losing a half-made selection to a background poll is worse than a stale count.

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
on the Disk scan page. Fonoteca reads the tags off every audio file, groups them into
albums, matches each one against the artists you follow, and marks the complete ones
`downloaded` — so a release you already own stops sitting on the Wanted page.

Three things it will not do:

* **It never writes to the library.** No renaming, no moving, no tag repair, no
  deleting. Whatever laid out that folder — Beets, Picard, Lidarr, you — keeps
  owning it. Fonoteca only writes to its own database.
* **It never marks anything wanted.** Adoption is one-directional: `wanted` may
  become `downloaded`, never the reverse. A scan can only ever *reduce* the amount
  of downloading Fonoteca would do, which is why it is safe on the nightly job.
* **It makes no Qobuz API call.** The scan is entirely local, so it costs nothing
  against the rate limit and takes about a second per thousand files.

What it finds, it reports in three groups:

| Group | Meaning | What to do |
| --- | --- | --- |
| Incomplete on disk | Matched a followed release, but fewer files than Qobuz lists | Left `wanted` on purpose — download it, or lower `LIBRARY_SCAN_COMPLETE_RATIO` |
| Not in the catalogue | The artist is followed; this particular release is not in the database | Usually a different edition, or the indexer has not reached it yet |
| Artists you do not follow | Music by someone Fonoteca has never heard of | **Find on Qobuz** searches for them — one live API call, only when you press it |

Matching is forgiving about layout on purpose, because the point is to adopt a folder
Fonoteca did not create: `CD 01`/`Disc 2` sub-folders fold into their parent, a trailing
`(2018)` or `[FLAC 24-96]` is stripped off the folder name, artist names are compared with
accents and articles folded (`Thorbjørn` matches `Thorbjorn`, `The Beatles` matches
`Beatles`), and album titles go through the same edition-collapsing normaliser the indexer
uses, so `Rumours [2013 Remastered]` on disk finds `Rumours` in the database.

Use **Preview** first if you want to see the report without writing anything. The same
scan is `cli.py scan-library [--dry-run]` and `POST /api/library/scan`, and it runs
nightly with housekeeping unless you set `LIBRARY_SCAN_NIGHTLY=false`.

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
dense, hairline-ruled layout in the spirit of a hi-fi component manual: an app shell with
a grouped sidebar, IBM Plex type, and monospace for anything numeric.

It has no build step and no network dependencies. The HTMX-compatible behaviour is a
hand-written shim inside `templates/base.html`, the styling is one plain `static/style.css`,
and the font stacks name IBM Plex first but fall back to system faces — so the app renders
identically on a machine with no internet at all. Cover art is the only thing fetched from
outside, and it degrades to an initial when absent.

The shim implements the attribute subset the templates use — `hx-get/post/delete/put/patch`,
`hx-target`, `hx-swap`, `hx-confirm`, `hx-include`, `hx-indicator`, and `hx-trigger` with
`every Ns`, `keyup changed delay:Nms` and `<event> from:body`. All the polling runs off one
ticker rather than a timer per element, which is what lets a region's polling stop when the
region is swapped away, pause with the tab, and skip a tick while its own request is still
in flight. If you would rather have the real thing, drop an `htmx.min.js` into `static/`
and delete the shim — the markup is stock htmx.

---

## Configuration

Every setting is an environment variable, read from `.env` (or the real environment, which
wins). `.env.example` documents all of them with the same defaults. Nothing is required
except the first two.

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
are visible on the dashboard and at `GET /api/status`.

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
| `NAMING_TEMPLATE` | see below | Path template, relative to `LIBRARY_PATH`. |
| `MAX_PATH_COMPONENT_LENGTH` | `180` | Character cap per path component (also capped at 255 UTF-8 bytes). |
| `PATH_REPLACEMENT_CHAR` | `_` | Replaces `/` and NUL inside a value. |
| `DEFAULT_MONITOR_MODE` | `all` | New artists: `all`, `future`, or `none`. |
| `DEFAULT_QUALITY_PROFILE` | `default` | New artists' profile (`default`, `lossless`, `hires`, `max`, or a bare format id). |
| `DEFAULT_ACCEPTED_RELEASE_TYPES` | `album,ep` | CSV of `album,ep,single,live,compilation,download,other`. |
| `LIBRARY_SCAN_NIGHTLY` | `true` | Run the disk scan as part of nightly housekeeping. Local-only and one-directional, so it cannot cause a download. |
| `LIBRARY_SCAN_COMPLETE_RATIO` | `1.0` | Fraction of an album's Qobuz track count that must be on disk before it counts as complete. Clamped to (0, 1]. |
| `LIBRARY_SCAN_FOLLOW_SYMLINKS` | `false` | Descend into symlinked directories while walking. Off so a self-referential link cannot loop. |
| `TRASH_PATH` | `<DATA_PATH>/trash` | Where deleted and superseded files go. Nothing is ever unlinked from `LIBRARY_PATH`. Same filesystem as the library makes a delete a rename rather than a copy. |
| `UPGRADE_CLEANUP` | `true` | Let a *completed* upgrade move the copy it replaced to the trash. An incomplete one never does. |

### Server and logging

| Variable | Default | Meaning |
|---|---|---|
| `HOST` | `127.0.0.1` | Bind address. |
| `PORT` | `8000` | Bind port. |
| `RELOAD` | `false` | uvicorn auto-reload (development only). |
| `LOG_LEVEL` | `INFO` | `DEBUG`/`INFO`/`WARNING`/`ERROR`/`CRITICAL`. |
| `LOG_FILE_NAME` | `fonoteca.log` | Rotating log inside `DATA_PATH`. |
| `LOG_MAX_BYTES` | `5242880` | Rotation size. |
| `LOG_BACKUP_COUNT` | `5` | Rotated files kept. |
| `LOG_REDACT_SECRETS` | `true` | Mask tokens, secrets, signatures and signed URLs in every log line. |

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
| `queue-status` | Queue counts plus the head of the queue (`--all` includes finished/failed, `--limit N`). |
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
its JavaScript bundle. Fonoteca reproduces exactly what the player does, so that your own
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

If nothing validates, Fonoteca raises an actionable error asking you to set
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

`/etc/systemd/system/fonoteca.service` — replace `YOUR_USER` and the two paths with
your own:

```ini
[Unit]
Description=Fonoteca — Qobuz release monitor
After=network-online.target
Wants=network-online.target

[Service]
Type=simple
User=YOUR_USER
Group=YOUR_USER
WorkingDirectory=/path/to/fonoteca
Environment=PYTHONUNBUFFERED=1
ExecStart=/path/to/fonoteca/.venv/bin/python -m uvicorn main:app --host 127.0.0.1 --port 8000
Restart=on-failure
RestartSec=10s
TimeoutStopSec=60

# Hardening (relax if your library lives elsewhere)
NoNewPrivileges=true
PrivateTmp=true
ProtectSystem=full
ProtectHome=read-only
ReadWritePaths=/path/to/fonoteca/data /path/to/music/library

[Install]
WantedBy=multi-user.target
```

```bash
sudo systemctl daemon-reload
sudo systemctl enable --now fonoteca
journalctl -u fonoteca -f
```

`TimeoutStopSec=60` matters: on shutdown the worker finishes the file it is streaming and
leaves the album queued, so a restart resumes cleanly instead of leaving debris.

---

## Development

```bash
./.venv/bin/python -m pytest tests -q      # 591 tests, no network
./.venv/bin/python -c "import main"        # import smoke test
./.venv/bin/python -m uvicorn main:app --port 8899
```

There is no Alembic and no migration story: `init_db()` only runs `create_all`. Adding a
column means either a manual `ALTER TABLE` or deleting `data/fonoteca.db`.

Architecture notes for future work live in `CLAUDE.md`.

---

## Legal / terms of service

Fonoteca is a personal-use tool that drives the **official Qobuz API with your own
credentials**. It does not circumvent DRM, does not share files, contains no accounts or
keys of its own, and gives you nothing your subscription does not already entitle you to.

* You need your **own paid Qobuz subscription**. Fonoteca is useless without one.
* Downloading is limited to what your subscription lets you stream. The API silently
  downgrades anything above your entitlement, and Fonoteca records what it actually got.
* Redistributing the downloaded files, or using them commercially, is on you and is almost
  certainly a breach of Qobuz's terms and of copyright law.
* Automated access may not be something Qobuz's terms of service contemplate. That is why
  everything is rate limited by default and why the "very slow" and "glacial" profiles
  exist. Do not raise the limits to hammer their API; you risk your own account.
* No warranty. Use at your own risk.
