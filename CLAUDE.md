# CLAUDE.md — architecture map for Fonoteca

Read this before touching anything. It is short on purpose; the details live in module
docstrings, which are accurate.

## What this is

A Lidarr-alternative that monitors artists on Qobuz and downloads their releases through the
official API with the user's own paid account. Python 3.12, FastAPI, SQLAlchemy 2.0 async,
SQLite, APScheduler, Jinja2 + HTMX, httpx, mutagen. No Docker, no ffmpeg, no transcoding.

Run everything with the venv interpreter: `./.venv/bin/python`.
Tests: `./.venv/bin/python -m pytest tests -q` from the repo root (no conftest; the root
being cwd is what puts `app` on `sys.path`).

## Module map

```
main.py              app factory, lifespan (init_db -> init_state -> shutdown_state),
                     /static mount, both routers, HTML/JSON error handlers, uvicorn entry
cli.py               stdlib-argparse CLI: verify-credentials, add-artist, list-artists,
                     scan-now, scan-library, import-library, queue-status, serve.
                     Never starts the worker/scheduler.

app/config.py        Settings (pydantic-settings). ALWAYS use get_settings(); never os.environ.
app/models.py        ORM + enums. app/schemas.py  Pydantic I/O models. app/db.py  engine/session.
app/logging_conf.py  setup_logging + redaction. register_secret() anything sensitive.

app/qobuz/ratelimit.py  RateLimiter (min interval + rolling hourly cap) and CircuitBreaker.
app/qobuz/secrets.py    app_secret derivation + validation + data/secret.cache; sign().
app/qobuz/client.py     QobuzClient: the ONLY thing that talks to Qobuz.
app/qobuz/mapper.py     raw Qobuz JSON -> ORM-shaped dicts (pure).

app/core/naming.py      template rendering + POSIX sanitising (pure).
app/core/librarian.py   the ONLY module that changes or removes files already in
                        LIBRARY_PATH: delete (to trash), re-file, re-tag, restore.
app/core/quality.py     "is a better copy obtainable than the one on disk?" (pure).
                        Shared by the download loop and the artist table — see below.
app/core/tagger.py      mutagen tagging — SYNCHRONOUS, call via asyncio.to_thread.
app/core/downloader.py  AlbumDownloader.download_album(): the album-level state machine.
app/core/indexer.py     Indexer: one artist per tick, dedupe, mark wanted, enqueue.
app/core/scanner.py     LibraryScanner: reads LIBRARY_PATH, matches folders to albums,
                        adopts what is already on disk. Local-only, read-only, one-way.
app/core/importer.py    LibraryImporter: one Qobuz search per artist found on disk,
                        follows the exact matches. Background task + progress snapshot.
app/core/queue.py       QueueWorker: sequential download loop, retries, backoff.
app/core/scheduler.py   APScheduler jobs: indexer_tick, nightly housekeeping
                        (disk scan -> prune -> verify, in that order).
app/core/state.py       AppState singleton — THE integration seam. init_state()/get_state().

app/api/deps.py         templates + filters, defensive state accessors, read-model builders.
app/api/routes_api.py   router (prefix /api) + health_router, and the reusable service
                        functions (follow_artist, queue_album, run_search, ...).
app/api/routes_ui.py    HTML pages, HTMX partials, form POSTs. Reuses those service functions.
templates/, static/     Jinja2 + one hand-written CSS file. base.html is the app shell
                        (header / sidebar / title bar / status footer) and carries a
                        hand-written HTMX-compatible shim; NO vendored htmx, no CDN.
                        Nav lives in templates/partials/nav.html, not base.html;
                        shared render helpers (the `cover` artwork and
                        `monitor_toggle` macros) live in templates/partials/macros.html.
                        The shim also owns ALL polling: one 500ms ticker drives every
                        `every Ns` element, so a poller dies with its element, pauses
                        with the tab, and never stacks requests. Do NOT reintroduce a
                        setInterval per element.
```

Dependency direction is strictly `api -> core -> qobuz -> config/db/models`. Nothing in
`app/core` or `app/qobuz` may import `app.api`.

## Invariants — break these and things get subtly wrong

**One rate limiter.** `init_state()` builds exactly one `RateLimiter.from_settings()` and
hands it to the one `QobuzClient`. Indexer, downloader and UI search all share it. Never
construct a second `QobuzClient` or `RateLimiter` in request handlers, jobs or scripts —
reach for `get_state().client`. `acquire()` holds an asyncio.Lock for the whole wait, so it
is FIFO-fair; it is also the only place the hourly budget is counted.

**One artist, one album, at a time.** The indexer processes a single artist per tick; the
queue worker downloads a single album at a time. Do not add concurrency here — slowness is
the product requirement, not an accident.

**Downloading is opt-in.** `Settings.auto_download` defaults to **False**. `Indexer._enqueue_wanted`
returns early (logging a `queue.held` activity row) unless it is called with `force=True`, so
indexing marks albums `WANTED` but queues nothing. The only paths that queue are explicit
user actions: `queue_album()` (one album), `Indexer.queue_wanted()` via
`queue_wanted_for_artist()` (one artist's backlog), and `queue_all_wanted()` (the whole
library's backlog, from the Wanted page — capped at 500 per press). If you add a new code
path that enqueues, make it an explicit user action or gate it on `auto_download` — do not
"helpfully" auto-queue. This exists because a single follow of a prolific artist once
pulled 1.7 GB unprompted.

**The disk scan only moves one way.** `app/core/scanner.py` may promote an album to
`DOWNLOADED`; it may never mark one `WANTED`, `QUEUED`, or enqueue anything. That is what
makes it safe on the nightly job and safe to run unattended — a scan can only ever *reduce*
the work Fonoteca would do. It also never writes to the filesystem (no rename, move, tag
repair or delete) and never calls Qobuz. Demoting an album whose files vanished is the
*other* job, `scheduler._verify_library()`; housekeeping runs the scan first and the verify
pass second, and swapping that order would let verify mark an album wanted a moment before
the scan proved it was there. The scan also leaves `DOWNLOADING` albums and `ACTIVE` queue
items alone — the worker owns those (see queue-item ownership below).

**Bulk import matches exactly or not at all.** `app/core/importer.py` follows an artist only
when a Qobuz search hit equals the folder name under `scanner.artist_key()` normalisation.
No fuzzy scoring, no "closest hit wins" — Qobuz answers `Joanne Shaw Taylor` with a
`Joanna Shaw Taylor` too, and the cost of a wrong guess is monitoring a stranger's
discography. Unresolved names go to the review list for a human. Two other rules the
importer must keep: **one API call per artist** (the search hit is passed to
`Indexer.add_artist(payload=...)` so `artist/get` is never called), and **it does not index**
— following 500 artists with `index_now=True` is tens of thousands of calls, so the
scheduled tick picks them up instead.

**Album ids are NOT integers.** Real ids look like `uyej1o165e870`, `wxl78pvfqlm3b`,
`0884977859300`. Every id column is `String(64)`. Never `int()` an album id; coerce artist
and track ids with `str()` before querying or you silently miss rows.

**SQLite returns naive datetimes** from `DateTime(timezone=True)` columns. Comparing one to
an aware `utcnow()` raises `TypeError`. Re-stamp with `app.core.indexer.utc()` (services) or
`app.api.deps.as_utc()` (HTTP layer) before any Python-side comparison.

**Trust the returned format.** `getFileUrl` silently downgrades a request above the
account's entitlement. Name and tag from the `format_id`/`bit_depth`/`sampling_rate` in the
response, never from what was requested.

**Downloads use a credential-free HTTP client.** Signed file URLs point at a CDN; sending
`X-User-Auth-Token` there would leak it. Keep `client.downloader` credential-free.

**Never log** the auth token, the app secret, or signed URLs. `setup_logging()` masks them;
call `register_secret()` for anything new.

**Queue-item ownership.** `AlbumDownloader.download_album()` commits repeatedly (that is what
makes downloads resumable) and owns the terminal queue state (DONE/FAILED/back-to-PENDING).
It does **not** raise on ordinary failure — it returns a `DownloadResult` with
`status=AlbumStatus.FAILED`. `QueueWorker` inspects the return value; a caller that only
catches exceptions will record failures as successes. `attempts` is incremented exactly once
per attempt, by `QueueWorker._claim_next()`; `_mark_started()` skips the increment when the
item is already ACTIVE.

**Partial updates mean partial.** `ArtistUpdateIn` and `ArtistBulkUpdateIn` use `None` for
"leave alone", and `bulk_update_artists()` writes only the fields that are not `None`. The
HTML side must keep that distinction alive: a bulk dropdown left on *no change* submits an
empty string, so it goes through `routes_ui._tristate()`, **not** `_parse_bool()` — the
latter turns `""` into `False` and would silently unmonitor every artist in the selection.
Bulk form fields are prefixed `set_` (`set_monitored`) to keep them apart from the
like-named filter fields carried in the same POST.

**Row actions carry filters, and `monitored` on a row action is a filter.** Every table that
lists albums shows the monitor toggle (`templates/partials/macros.html:monitor_toggle`), and
they all post to one endpoint, `POST /ui/albums/{id}/monitor?view=…`. `view` picks which
fragment comes back — `wanted` / `queue` / `dashboard` / `artist`; the rest of the query
string is the calling screen's *active filters*, built by `routes_ui._scope()` so the
refreshed table is still the one the user was looking at. One of those filters is called
`monitored`, and the endpoint must never read it as the album's new value: that would make
the Wanted page's "ignored only" filter unmonitor whatever row was pressed. The endpoint
**toggles**, full stop — `POST /api/albums/{id}/monitor` with a body is where an
explicit value goes.
`set_album_monitored()` also collapses `WANTED` to `SKIPPED` on the way off (and back on the
way on), which is what makes an ignored row leave the backlog rather than sit there inert;
it deliberately leaves `QUEUED`/`DOWNLOADING` alone, because the worker owns those.

**A live region renders from the same template AND the same context as its page.** Every
counter and status readout polls itself: the page renders the first frame with
`{% include %}`, and a wrapper with `hx-trigger="every Ns, fonoteca:refresh from:body"`
re-fetches the identical partial. Sharing the template is only half of it — the fragment
route has to build the same context too. Give `/partials/dashboard/queue` a different row
limit from `page_dashboard` and the page silently rearranges itself five seconds after it
loads, which is why `tests/test_live_updates.py` compares the whole fragment against the
page rather than sampling lines out of it. Constants like `DASHBOARD_WANTED` exist for
exactly this; add one rather than repeating a literal.
Two further rules. **A filterable region polls through `hx-include`**, not a fixed URL —
`#album-rows` includes `#artist-filters`, `#wanted-rows` includes `#wanted-filters`,
`#activity-rows` includes `#activity-filters` — because a poll that dropped the filters
would wipe out whatever the user had typed. And **only `/ui/*` may claim something
changed**: `_fragment()` fires `fonoteca:refresh` on every mutating response and that is
what makes the regions update on a press instead of at the next tick. A `GET /partials/*`
that fired it would make every region refresh every other region, forever.

**One quality comparison, two callers.** `app/core/quality.py` answers *is a better copy
obtainable than the one on disk?* and both sides must ask it. `deps.album_to_out()` uses it
to set `AlbumOut.upgrade_format_id`, which is what makes the artist row show **Upgrade**
instead of Download (and show **nothing at all** when the copy on disk is already maximal —
that is deliberate, there is no honest button there). `AlbumDownloader._is_upgradable()`
uses it to decide whether a file the resume logic found may be reused or has to be
re-fetched. If they ever diverge, Upgrade queues a download that skips every track and
changes nothing. Three ceilings each veto an upgrade and the lowest wins: the account
(`client.best_format_id`), the artist's `quality_profile`, and the release's own
`max_bit_depth`/`max_sampling_rate` — drop the last one and a CD master looks upgradable
forever, because it can never reach a hi-res format id. **Unknown always means no**: every
function returns `None` rather than a guess, the loop keeps the file it has and the UI shows
no button. Guessing the other way costs a redundant album download.

**One module writes to the library, and it never unlinks.** `app/core/librarian.py` is the
only place allowed to move, rename, re-tag or remove files that are already in
`LIBRARY_PATH` — the scanner is read-only and the downloader only adds. Deleting means
`move_to_trash()`: the tree goes to `Settings.trash_dir` under a timestamped batch holding a
`manifest.json` and a `payload/` copy. `empty_trash()` is the only `rmtree` in the codebase,
it only ever runs inside `trash_dir`, and only on an explicit request. If you add an
operation that touches the library, put it here and make it go through the same three gates:

1. `resolve_in_library()` — resolves symlinks **before** testing containment (resolve-then-
   check, never check-then-resolve) and refuses the library root itself, so one empty
   `album.path` is not a whole-collection delete.
2. `_assert_not_busy()` — a `DOWNLOADING` album or an `ACTIVE` queue item is refused. The
   worker is writing `.part` files into that directory.
3. The trash, not `os.remove`.

**Upgrade cleanup is conditional, and the conditions are the safety argument.** The default
naming template puts `{quality}` in the album folder, so an upgrade lands in a *new*
`album_dir` and the old one is a duplicate. `AlbumDownloader._clear_superseded()` trashes it
only when `Settings.upgrade_cleanup` is on **and** the run was complete — not cancelled, no
failed tracks, no unstreamable ones. A release that finished with a missing track has a new
folder that is worse than the old one; that check is why the old copy survives it.

**Re-file names a folder after what is in it.** `plan_refile()` takes the `{quality}` tag
from `quality.owned_format_id(album.tracks)` — the files on disk — not from the catalogue
maximum. Use the catalogue figure and re-filing oscillates: it would rename a 16/44.1 folder
to `[FLAC 24-96]` and then want to rename it back.

**`AlbumOut.queue_state` is not `AlbumOut.status`.** `queue_album()` only promotes
`SKIPPED`/`FAILED`/`WANTED` to `QUEUED`, so an album being *upgraded* stays `DOWNLOADED`
for the whole download. Anything asking "is this album in flight?" must read `queue_state`
(built from the `queue_items` relationship by `deps._live_queue_state`), not `status`. And
because a `QueueItem` is inserted by `album_id`, it does not backfill the album's loaded
collection — `queue_album()` calls `_reload_queue_items()` so the caller re-renders from
the truth. Sessions use `expire_on_commit=False`; a commit will not do it for you.

**Enum columns** store `.value` with `native_enum=False` but read back as Python members:
compare `album.status is AlbumStatus.WANTED`, and write enum members, not strings.

**No migrations.** `init_db()` only does `create_all`. A new column means a manual
`ALTER TABLE` or deleting `data/fonoteca.db`.

## Where to add things

**A new JSON endpoint** → `app/api/routes_api.py`. Put the actual work in a module-level
service function (like `follow_artist`, `queue_album`, `run_search`), then have the route be
a thin wrapper. Add a response model to `app/schemas.py`. Register nothing in `main.py` — the
router is already included.

**A new HTML page** → a route in `app/api/routes_ui.py` returning
`render(request, "page.html", ctx, active="<nav-key>")`, a template extending `base.html`,
and a nav entry in `templates/partials/nav.html` (which is both included by the shell and
re-fetched by `/partials/nav` to keep its badges live — so a new entry needs no wiring).
Reuse the service function the JSON route uses; do not duplicate logic.

A page template fills four blocks: `title`, `heading`, `subtitle` (optional, one line under
the heading) and `actions` (optional, buttons on the right of the title bar). The last two
are captured with `{% set x %}{% block x %}{% endblock %}{% endset %}` so an empty block
renders no container at all — do not add your own title-bar markup.

**A new HTMX fragment** → `GET /partials/...` in `routes_ui.py` + `templates/partials/*.html`.
Mutating actions live under `POST /ui/...`, return a fragment, and set an `HX-Trigger`
`fonoteca:toast` header. Only the attribute subset the shim in `base.html` implements is
available (`hx-get/post/delete`, `hx-target`, `hx-swap`, `hx-trigger` with `every Ns` /
`keyup changed delay:Nms` / `<event> from:body`, `hx-confirm`, `hx-include`,
`hx-indicator`). Extend the shim, or drop a real `htmx.min.js` into `static/` and delete
the shim — the markup is stock htmx.

**A new live region** (anything showing a count, a state or a progress meter) → a wrapper
with an id, `hx-get` at the partial, `hx-swap="innerHTML"`, and
`hx-trigger="every Ns, fonoteca:refresh from:body"`. See the live-region invariant below
for the two rules that keep one honest.

**A new Qobuz call** → a method on `QobuzClient` (it handles limiter, retries, backoff,
breaker and error mapping), plus a `map_*` function in `mapper.py` if it returns entities.
Never call httpx directly from `app/core` or `app/api`.

**A new setting** → a field on `Settings` **and** a documented line in `.env.example` **and**
a row in the README table. Do not edit `.env` (it holds the user's real credentials).

**A new background job** → `app/core/scheduler.py`, registered in `build_scheduler()`.
Nightly maintenance that is local-only can instead fold into `housekeeping()`.

**Anything that turns a disk artist into a followed one** → `app/core/importer.py`.
It is the only place allowed to follow artists in bulk, and it must keep costing exactly
one API call per artist and must not index.

**Anything that asks "is this file good enough?"** → `app/core/quality.py`. Pure, no I/O.
Do not re-derive format arithmetic in a route, a template or the download loop — the whole
point is that the button and the worker cannot disagree.

**Anything that changes or removes a file already in the library** → `app/core/librarian.py`,
through the three gates above. Never `shutil.move`/`os.remove`/`rmtree` from a route, a job
or the download loop.

**Anything that reads the library folder** → `app/core/scanner.py`. `collect_albums()` and
`read_track()` are synchronous and pure; the async `LibraryScanner` is what talks to the
database. Call the synchronous half through `asyncio.to_thread`, never on the event loop.

**A new CLI subcommand** → `cli.py`: a `cmd_*` coroutine plus a `subparsers.add_parser(...)`
block with `set_defaults(func=..., needs_async=True)`.

## Verifying a change

```bash
./.venv/bin/python -m pytest tests -q            # 591 tests, no network
./.venv/bin/python -c "import main; import cli"  # import smoke test
./.venv/bin/python cli.py verify-credentials     # real Qobuz handshake
./.venv/bin/python -m uvicorn main:app --port 8899   # then curl the pages
```

Be careful with live end-to-end tests: following an artist triggers an immediate background
index, and anything the indexer marks `wanted` is queued and **downloaded for real** by the
running server. Use `monitor_mode=none`, or unfollow, or cancel the queue items first.
