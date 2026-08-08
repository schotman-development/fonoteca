# CLAUDE.md — architecture map for Qobuzarr

Read this before touching anything. It is short on purpose; the details live in module
docstrings, which are accurate.

## What this is

A Lidarr-alternative that monitors artists on Qobuz and downloads their releases through the
official API with the user's own paid account. Python 3.12, FastAPI, SQLAlchemy 2.0 async,
SQLite, APScheduler, httpx, mutagen — and, since the rebuild, a React 19 + TypeScript
single-page app in `web/`, built by Vite into `static/app` and served from a catch-all route.
No Docker, no ffmpeg, no transcoding.

Run everything Python with the venv interpreter: `./.venv/bin/python`.
Tests: `./.venv/bin/python -m pytest tests -q` from the repo root (no conftest; the root
being cwd is what puts `app` on `sys.path`).

The front end needs Node ≥20 on `PATH` (`export PATH=~/.local/opt/node/bin:$PATH`):
`npm install --prefix web` once, `npm run build --prefix web` to produce a servable UI, and
`npm test --prefix web` for the component suite. A checkout with no build is not broken — the
server answers every page with a plain document naming the one command that fixes it, and the
JSON API is entirely usable meanwhile — but it has no UI until Node has run once.

**The old server-rendered UI still exists, one prefix deeper, at `/legacy`.** That is
transitional and its removal is a pending step, not an open question: `app/api/routes_ui.py`,
`templates/`, `static/style.css` and the single `include_router(..., prefix=LEGACY_PREFIX)`
line in `main.py` go together, once the SPA has been exercised against a real library. Until
then nothing new is added to it, its templates keep linking to the unprefixed paths they were
written for (they are not being rewritten for a layer that is going away), and **it is not to
be edited**. Everything below that names a template or an `/ui/*` route is describing that
layer, and is kept only because it is still running.

## Module map

```
main.py              app factory, lifespan (init_db -> init_state -> shutdown_state),
                     /static mount, both API routers, the /legacy UI, JSON/HTML error
                     handlers, and the SPA catch-all (spa_shell + _JSON_ONLY_PREFIXES).
                     Registration ORDER is load-bearing — the catch-all matches everything,
                     so it is added last. uvicorn entry.
cli.py               stdlib-argparse CLI: verify-credentials, add-artist, list-artists,
                     scan-now, scan-library, import-library, baseline-library,
                     verify-library, queue-status, enrich-now, enrich-purge,
                     bind-folders, folder, backfill-upc, serve.
                     Never starts the worker/scheduler.
scripts/migrate_integrity.py  the one-off ALTER TABLE + qid backfill for an existing
                     database. Idempotent, only touches rows whose qid is NULL.
scripts/migrate_album_flags.py  the same treatment for the three per-album switches
                     (pin_tags / freeze_path / mute_integrity). PRAGMA-guarded per
                     column, idempotent, --dry-run. Needed because they land on
                     `albums`, which ENRICHMENT_SCHEMA_VERSION may not rebuild —
                     a person's decision about one release is not re-derivable.
scripts/migrate_artist_attribution.py  the same again for the four attribution
                     columns: albums.guest_appearance / credit_names (both nullable, and
                     the NULL is load-bearing — see the invariant) and
                     artists.include_guest_appearances / credit_filter_json. Which of the
                     people sharing a Qobuz artist id you follow is not re-derivable.
scripts/migrate_folder_bindings.py  the exception that proves "a new table is free".
                     `folder_bindings` was free; two columns added to it hours later
                     were not, because `create_all` saw the name and stopped. A new
                     table stops being free the moment anybody has run the build that
                     created it. Rebuilds rather than ALTERs — `album_id` had to
                     become nullable and SQLite cannot drop NOT NULL — carrying rows
                     over in one transaction. Idempotent, --dry-run.
scripts/migrate_artist_tags.py  the same again for the two hand-editable artist
                     columns the Edit-tags drawer needs: artists.sort_name and
                     artists.aliases_json (a JSON array — an alias legitimately
                     contains a comma, so the accepted_release_types join would
                     split one in two). Both are typed by a person and nothing
                     re-derives either, which is why neither may live in
                     artist_metadata. ArtistTagsIn is still extra="forbid", so a
                     key with no column is a 422 rather than a silent drop. The
                     standing refusal about aliases is a different one now: they
                     are database-only — no file tag, no NFO element, and
                     app/enrich/matching.py may NEVER read them.

app/config.py        Settings (pydantic-settings), read from the environment. ALWAYS use
                     get_settings() or get_effective_settings(); never os.environ.
                     ALSO the runtime overlay: OVERRIDABLE_SETTINGS (the seven-key
                     allowlist), parse_override, install_overrides/reset_overrides,
                     setting_origins, and effective_for(base) — the one accessor for a
                     value a person can change from the Settings screen. See the
                     settings-overlay entry under "Where to add things".
app/models.py        ORM + enums. app/schemas.py  Pydantic I/O models. app/db.py  engine/session.
app/logging_conf.py  setup_logging + redaction. register_secret() anything sensitive.

app/net/ratelimit.py    RateLimiter (min interval + rolling hourly cap) and CircuitBreaker.
                        Upstream-agnostic: ONE per host, shared by every caller of it.
app/net/errors.py       HttpError hierarchy (retryable flag) for non-Qobuz upstreams.
app/net/http.py         JsonHttpClient: acquire -> GET -> map -> record, plus tenacity.
                        Subclass hooks: rate_limit_statuses, check_payload, build_headers.

app/qobuz/secrets.py    app_secret derivation + validation + data/secret.cache; sign().
app/qobuz/client.py     QobuzClient: the ONLY thing that talks to Qobuz. NOT built on
                        JsonHttpClient — its response mapping is genuinely Qobuz-specific.
app/qobuz/mapper.py     raw Qobuz JSON -> ORM-shaped dicts (pure).

app/enrich/errors.py     EnrichmentOutcome family. Each carries the enrichment_state
                         value it becomes: no_key / not_found / ambiguous / gated.
app/enrich/types.py      Snapshots + EnrichmentJob/Result + the provider Protocol, and
                         Candidate/SearchableProvider — the human-only picker, kept a
                         SEPARATE protocol so fetch() cannot reach a name search.
app/enrich/matching.py   PURE. barcode_candidates (barcodes ONLY — never the Qobuz album
                         id), barcode_from_claim/album_claimed_barcode, titles_match,
                         sole_main_credit, ISNI. Where "exact or nothing" is enforced.
app/enrich/coverage.py   PURE. solve_album(): which single release explains this whole
                         directory. Injective seating + coverage + track count, and NO
                         tie-break — one admissible release identifies, several are
                         editions of a group, none is unidentified.
app/enrich/merge.py      PURE. consensus() — the >51% rule — and each source's vocabulary.
app/enrich/deezer.py     DeezerClient + provider. Supplies a real barcode for most of
                         what AcoustID did not already pin.
app/enrich/musicbrainz.py MusicBrainzClient + provider. MBIDs, ISNI, release-group types.
                         Runs AFTER acoustid and deezer — it matches exactly, so it needs
                         a key it does not own.
app/enrich/chromaprint.py fpcalc wrapper. SYNCHRONOUS — call via asyncio.to_thread.
                         FpcalcMissing gates the rung, FingerprintTimeout and FileUnavailable
                         say nothing was measured, UnreadableAudio means the FILE is bad.
                         Only the last one is evidence — it ends with the file in the trash.
app/enrich/acoustid.py   AcoustIdClient + provider. FIRST rung: the audio is the anchor,
                         so this identifies the release via coverage.solve_album() as well
                         as confirming/denying one, finds duplicate recordings, and flags
                         corrupt files.
app/enrich/coverart.py   Cover Art Archive. Keyed by MBID, so ZERO matching risk — and
                         LAST of the artwork sources, behind Qobuz and Deezer.
app/enrich/wikidata.py   Wikidata + Wikipedia: biography (CC BY-SA), Commons portrait,
                         and the ISNI MusicBrainz usually lacks. Reached only via MBID.
app/enrich/registry.py   build_providers(settings): the ladder, one limiter per source.

app/core/naming.py      template rendering + POSIX sanitising (pure).
app/core/integrity.py   what is actually on disk, measured. FileStamp (content_hash +
                        sample_count + size + mtime), classify() -> IntegrityState,
                        album_content_digest(). PURE of ORM/session/network but does
                        blocking file I/O — SYNCHRONOUS, call via asyncio.to_thread.
app/core/nfo.py         artist.nfo / album.nfo rendering + merge, and the enrichment
                        half of the file tags. PURE — render here, write in librarian.
app/core/librarian.py   the ONLY module that changes or removes files already in
                        LIBRARY_PATH: delete (to trash), re-file, re-tag, restore,
                        quarantine (corrupt files to trash + marked missing). Re-file and
                        re-tag both fall back to reading the files themselves for albums
                        the disk scan adopted, which have no Track rows.
app/core/quality.py     "is a better copy obtainable than the one on disk?" (pure).
                        Shared by the download loop and the artist table — see below.
app/core/tagger.py      mutagen tagging — SYNCHRONOUS, call via asyncio.to_thread.
app/core/downloader.py  AlbumDownloader.download_album(): the album-level state machine.
app/core/indexer.py     Indexer: one artist per tick, dedupe, mark wanted, enqueue.
                        Also desired_status() — "do this artist's settings want this
                        release?", the one statement of it — and
                        apply_monitoring_to_backlog(), which applies that rule to the
                        rows that already exist when somebody changes their mind.
app/core/scanner.py     LibraryScanner: reads LIBRARY_PATH, matches folders to albums,
                        adopts what is already on disk. Local-only, read-only, one-way.
                        Adoption also queues the album for enrichment (mark_library_due).
                        Mints qids, records the integrity stamp — classifying against the
                        recorded one before overwriting it, and re-opening a REPLACED
                        release — and writes what the tags CLAIM to file_claims, never to
                        the verified metadata tables.
app/core/importer.py    LibraryImporter: one Qobuz search per artist found on disk,
                        follows the exact matches. Background task + progress snapshot.
app/core/binder.py      bind_unmatched_folders(): identifies the folders the scan
                        could not name, by audio, through app/core/discovery.py's
                        chain. Writes ONE thing — a models.FolderBinding row —
                        and moves no status, creates no track, queues nothing.
                        The adoption is the next scan's, unchanged. Bounded,
                        explicit, never a job.
app/core/catalogue.py   backfill_upc(): the bounded pass that fetches the barcode Qobuz
                        publishes on album/get and the indexer's getReleasesList does
                        not. Fills `albums.upc` and NOTHING else — release_type is
                        enrichment's, label/genre are naming tokens. Scoped to the
                        library, capped per invocation, explicit (a CLI command, never
                        a job), and seeds no enrichment work: `_rearm_barcoded` notices.
app/core/queue.py       QueueWorker: sequential download loop, retries, backoff.
app/core/enricher.py    Enricher: drains enrichment_state. Claim -> HTTP -> persist, in
                        three separate transactions. Owns the scope rule too —
                        library_scope/in_library (on disk only), mark_library_due
                        (a release just landed), reopen_library_albums (its audio was
                        replaced), purge_out_of_scope_enrichment and
                        enrichment_scope_counts. Also the batch loaders the sync
                        read models use (load_album_metadata / load_artist_metadata).
app/core/scheduler.py   APScheduler jobs: indexer_tick, enrichment_tick, nightly
                        housekeeping (disk scan -> prune -> verify, in that order;
                        _prune_enrichment drops orphans AND out-of-scope state rows),
                        and verify_integrity() — the tripwire pass plus a fixed slice
                        re-hashed regardless, which re-opens REPLACED files for
                        identification and queues nothing.
app/core/settings_store.py  the ONLY reader and writer of the app_setting table:
                        load_overrides / set_override / clear_override, and nothing
                        else. It holds no policy — WHICH keys may be written and how
                        a value parses is app/config.py's OVERRIDABLE_SETTINGS, and
                        this module stores the text it is handed. A row exists only
                        while a key is overridden, so deleting one IS "reset to .env".
app/core/state.py       AppState singleton — THE integration seam. init_state()/get_state(),
                        and apply_setting_overrides(), which re-points every component
                        holding a captured Settings and rebuilds the enrichment ladder.

app/api/deps.py         the read-model builders (artist_to_out / album_to_out / track_to_out,
                        list_artists / list_albums_for_artist / list_wanted_albums,
                        artist_stats, nav_counts, build_meta, build_banners,
                        naming_preview, _record_key_pairs / list_release_group) and the
                        defensive state accessors. These are DOMAIN code, not presentation
                        — the SPA reads all of it as JSON. Also still holds the Jinja
                        environment, the filters and render/render_page, which only the
                        /legacy UI uses; they go when it does.
app/api/routes_api.py   router (prefix /api) + health_router, and the reusable service
                        functions (follow_artist, queue_album, run_search, ...). Since the
                        rebuild this is the WHOLE public surface — everything the HTML layer
                        could do is reachable here.
app/api/routes_ui.py    the old server-rendered UI, mounted under /legacy. TRANSITIONAL.
                        Do not edit it and do not add to it.
templates/, static/style.css   the same layer's Jinja templates and stylesheet. Also
                        transitional; also not to be edited.

web/                    the React SPA. Vite + TypeScript (strict, noUncheckedIndexedAccess),
                        CSS Modules over CSS custom properties. No Tailwind, no component
                        library, nothing fetched at runtime — the font stack names IBM Plex
                        and falls through to system faces.
web/src/api/client.ts   the ONE fetch wrapper. Unwraps the uniform error envelope into
                        ApiError (status is the routing key: 503 not wired / 409 busy /
                        400 refusal / 404 missing / 422 validation / 502 upstream silent),
                        never sends null for an absent filter, never coerces an id, and
                        treats an HTML body from /api/* as a missing endpoint.
web/src/api/types.ts    a HAND-KEPT mirror of app/schemas.py. No code generation, on
                        purpose — see the wire-contract invariant below.
web/src/api/queries.ts  the TanStack Query layer: queryKeys (the only place a key is
                        spelled), REFETCH / LIMITS / STALE, createQueryClient, and the
                        single invalidateLive() every mutation calls.
web/src/design/         ~37 primitives over src/styles/tokens.css. Import from '@/design',
                        never from a file inside it. web/src/design/README.md is the
                        authority on tokens, spacing and the three domain rules the
                        components hold (a toggle sends no value; a Meter's null is not a
                        zero; a Chip is never tinted).
web/src/widgets/        the domain layer — ReleaseRow, MonitorToggle, CompletionCell,
                        BulkBar, StatStrip, PageError/PageLoading, RelativeTime. A widget
                        knows what a release is; a design primitive does not.
web/src/screens/        one folder per section (dashboard/, library/, radar/, missing/,
                        queue/, identify/, rules/, activity/), one default-exported
                        component per route, code-split.
web/src/shell/          AppShell, TopBar, Sidebar (nav.ts is the IA as data), TitleBar,
                        StatusFooter, ToastHost — the frame every screen renders inside.
web/src/format/         the old Jinja filters as pure functions: fmtAgo, fmtSpan, fmtSize,
                        fmtQuality, formatLabel. All return EM_DASH for an unknown, none
                        of them throws, and there is exactly one EM_DASH.
web/src/routes.tsx      the route table: Dashboard / Library / Release radar / Missing
                        releases / Download queue / Identify / Structure & tags /
                        Activity, plus NotFound. `/missing` and `/queue` were added
                        after the six-screen rebuild — they are not screens it deleted.
                        `web/src/shell/nav.ts` is the same list as data.
static/app/             BUILD ARTEFACT. Written by `npm run build --prefix web`, gitignored,
                        never edited by hand and never committed.
```

The endpoints the rebuild added, all in `routes_api.py`: `GET /api/nav-counts`,
`/api/banners`, `/api/meta` (every enum vocabulary and the format labels, so nothing is
hand-copied into TypeScript — including the two that are not enums at all:
`review_states`, which is `enricher.REVIEW_STATES` and therefore a *rule* the client would
otherwise re-implement, and `setting_origins`, whose `env`/`override` pair is a `Literal`
and so never reaches the OpenAPI document for `tests/test_wire_contract.py` to police),
`/api/stats`, `/api/health/summary`,
`/api/artists/{id}/stats`, `/api/albums/{id}/detail`, `/api/albums/{id}/consensus`,
`/api/release-groups/{key}`, `/api/integrity`, `/api/integrity/corrupt`,
`POST /api/integrity/verify` and `POST /api/library/quarantine`. The four features that
landed after it add: `PATCH /api/albums/{id}` (now carrying `pin_tags` / `freeze_path` /
`mute_integrity` as well), `POST /api/enrichment/{entity_type}/{entity_id}/reject`,
`.../accept` and `.../reopen`, `PATCH /api/artists/{id}/tags` with
`POST /api/artists/{id}/retag` beside it, and `PATCH /api/settings`. Several capabilities became
*fields* rather than routes, and each is load-bearing rather than decorative:
`WantedListOut.queueable_total` (what "Download all (N)" will really queue, invariant under
every filter), `PageOut.unfiltered_total` (so a client can tell "no match" from "nothing here
yet"), `SettingsOut.naming_preview`, `MessageOut.level`, `TrackOut.origin` and
`TrackOut.integrity`, `AlbumOut.adopted` / `integrity_state` / `corrupt_tracks`,
`EnrichmentReviewOut.identify_sources` and `state_explanation`, and — from the settings
overlay — `SettingsOut.overridable` / `origins` / `pending`, which is how a switch says
where its value came from and whether it has taken effect yet. `StatsOut.quality` is the
same idea again: the Library header's hi-res roll-up rides on a payload three screens already
share rather than becoming a route (and deliberately **not** on `LibraryStatsOut`, which
`StatusOut` embeds and the shell polls three times as often).

Dependency direction is strictly `api -> core -> {qobuz, enrich} -> net -> config/db/models`.
Nothing in `app/core`, `app/qobuz`, `app/enrich` or `app/net` may import `app.api`.

## Invariants — break these and things get subtly wrong

**One rate limiter per upstream.** `init_state()` builds exactly one
`RateLimiter.from_settings()` and hands it to the one `QobuzClient`. Indexer, downloader and
UI search all share it. Never construct a second `QobuzClient` or Qobuz `RateLimiter` in
request handlers, jobs or scripts — reach for `get_state().client`. `acquire()` holds an
asyncio.Lock for the whole wait, so it is FIFO-fair; it is also the only place that host's
hourly budget is counted. Each *enrichment* source gets its own limiter, built by
`app/enrich/registry.py` — that is the same rule, not an exception to it: MusicBrainz's
published allowance has nothing to do with what a paid Qobuz account can safely spend.

**One artist, one album, at a time.** The indexer processes a single artist per tick; the
queue worker downloads a single album at a time. Do not add concurrency here — slowness is
the product requirement, not an accident.

**Downloading is opt-in.** `Settings.auto_download` defaults to **False**. `Indexer._enqueue_wanted`
returns early (logging a `queue.held` activity row) unless it is called with `force=True`, so
indexing marks albums `WANTED` but queues nothing. The only paths that queue are explicit
user actions: `queue_album()` (one album), `Indexer.queue_wanted()` via
`queue_wanted_for_artist()` (one artist's backlog), and `queue_all_wanted()` (the whole
library's backlog, from Library ▸ Releases — capped at 500 per press). If you add a new code
path that enqueues, make it an explicit user action or gate it on `auto_download` — do not
"helpfully" auto-queue. This exists because a single follow of a prolific artist once
pulled 1.7 GB unprompted.

**The disk scan only moves one way.** `app/core/scanner.py` may promote an album to
`DOWNLOADED`; it may never mark one `WANTED`, `QUEUED`, or enqueue anything. That is what
makes it safe on the nightly job and safe to run unattended — a scan can only ever *reduce*
the work Qobuzarr would do. It also never writes to the filesystem (no rename, move, tag
repair or delete) and never calls Qobuz. Demoting an album whose files vanished is the
*other* job, `scheduler._verify_library()`; housekeeping runs the scan first and the verify
pass second, and swapping that order would let verify mark an album wanted a moment before
the scan proved it was there. The scan also leaves `DOWNLOADING` albums and `ACTIVE` queue
items alone — the worker owns those (see queue-item ownership below). The one thing it writes
outside its own rows is an enrichment work row: adoption calls `mark_library_due`, and a file
it measured as `REPLACED` calls `reopen_library_albums`. Both are SQL, make no request, start
no download and change no album status, which is why they do not break the one-way rule.

**A folder is bound by name, and `folder_bindings` is the exception that keeps the scan
offline.** `_pick_album` normalises the tagged title and the folder name and looks for a
release of that artist with a matching `dedupe_key`. That has a ceiling no tuning removes —
a folder whose name is a *different string* for the same record cannot match, and loosening
the normaliser buys false positives, which are worse. Measured: 104 of 611 folders unmatched,
every confirmed case a subtitle (`Play: The Guitar Album` against a catalogue calling it
`Play`). Each was a release the user owns, sitting in the **wanted** list, ready to be
downloaded again — so the ceiling is not a metadata problem, it makes the missing list wrong.

`models.FolderBinding` is the answer and the split is the whole design: **`app/core/binder.py`
decides and the scan only consults.** The binder runs `app/core/discovery.py`'s chain
(AcoustID → release group → its barcodes → the Qobuz album one of them picks) and writes a
single row; `_match_one` reads that table before it normalises anything, and adopts through
the same `_adopt` every other folder takes. Four things ride on it:

* **The scan still never calls Qobuz.** It reads a local table. Deciding an identity costs a
  fingerprint per file plus two requests, which is why that lives in an explicit command and
  not in a pass that runs nightly.
* **The binding is consulted first and outside the artist loop.** After the name match a worse
  answer would win; inside the loop it would depend on the folder artist matching, which is a
  second name test the binding exists to replace.
* **It supplies identity, not a licence.** `only_artist_id`, the `DOWNLOADING` guard and the
  one-way rule all still apply — a bound `DOWNLOADING` album is still left to the worker.
* **No foreign key, on purpose.** A binding outlives an `albums` row the indexer drops and
  re-creates; a cascade would silently spend the identification again. A row naming an album
  that is not there is skipped, which leaves the folder unmatched and is exactly what puts it
  back in front of the binder to be re-derived.

`FolderBinding` is **not** in `ENRICHMENT_TABLES` even though it is derived. Those five are
*re-fetchable*; this is re-derivable only at the cost of re-fingerprinting a few hundred
directories, so a schema bump that quietly spent that again would be a surprise measured in
hours. It is a plain new table, so `create_all` adds it and no migration script is needed.

**"There is no such release" is a decision only a person may make.** `FolderBinding.state` is
`bound` or `not_in_catalogue`, and nothing in `app/core/binder.py`'s automatic pass can write
the second — only `mark_folder()`, reached from `cli.py folder`. This is
`integrity.classify()`'s *UNKNOWN is not CHANGED* rule again: the chain failing to find a
release is not evidence that none exists, and a machine allowed to conclude otherwise would
quietly retire folders it merely could not identify — a compilation `solve_album` cannot
solve, an artist nobody follows, a gap in MusicBrainz. Those are exactly the folders somebody
still wants to see. What a *person* knows and no upstream can confirm is that a directory
holds covers never released anywhere, a game soundtrack, or a radio bootleg; on one real
library that is most of the residue (9 of the first 40 unmatched folders were game
soundtracks). Three rules:

* **It leaves `unmatched` and lands in `albums_excluded`.** `unmatched` is the number somebody
  reads to know how much identification work is left, and a permanently unmatchable folder
  left in it makes that figure unable to fall — the same failure as a corruption count that
  never goes down. It is *counted*, never hidden: a folder absent from every figure is
  indistinguishable from one the scan never saw.
* **It is always `manual`, which is what stops the binder re-deriving it.** Otherwise the
  chain spends a fingerprint and two requests reaching the same "no" on every run, forever.
* **It is reversible** — `clear_folder()`, `cli.py folder <path> --clear` — which is what
  makes it safe to apply liberally. It adopts nothing and moves no status, so a marker can
  never promote or demote a release.

**The scan records every file it finds, as a `Track` row — including the ones it cannot
read.** Not just the ones an existing row claims. A file is offered to the album's current
rows first — by `(disc, track)`, then by normalised title — because a row the download loop
wrote knows the Qobuz track id, the ISRC and the format actually delivered, none of which a
file carries; whatever is left becomes a `TrackOrigin.SCAN` row with a synthetic id from
`scanner.scanned_track_id()`. A file nothing can parse is not an exception to this and used
to be: it was dropped, which made it invisible everywhere. It gets a row too, keyed on its
filename (it has no track number to key on) and marked corrupt — see the corruption invariant
below. Before this,
every release Qobuzarr had not downloaded itself was an opaque folder: nothing to fingerprint
(AcoustID answered `no_key` with "nothing on disk to fingerprint" about perfectly readable
audio), no per-file quality for `app/core/quality.py` to compare, and nowhere for a
corruption verdict to land — on exactly the releases whose provenance is unknown and
therefore most worth checking. Three rules keep it honest:

1. **The id is keyed on position, not path.** `plan_refile()` renames every file in an album,
   and a path-keyed id would delete and recreate the whole album's rows on the next scan,
   taking every fingerprint verdict and recording id in `track_metadata` with it. A retagger
   that renumbers a track does get a new row — that is the honest answer, because the track's
   identity *within the release* is what changed.
2. **Only `SCAN` rows are ever retired.** A file that is no longer there under that number
   drops its row, so a library that shrank stops reporting the tracks it lost. A `DOWNLOAD`
   row is the record of work this program did, and an unmounted share looks exactly like a
   deleted one — demoting is `_verify_library()`'s job, at the album level, where it reverses.
3. **A real download supersedes them.** `AlbumDownloader._sync_tracks()` drops the album's
   `SCAN` rows before upserting Qobuz's. Keeping both doubles the track count, which is what
   completeness and the quality comparison are computed from.

Nothing may infer provenance from the *presence* of a track row, or from the shape of its id.
`Track.origin` is the answer, and `enricher._write_back()` is the caller that must have it
right — see the write-back invariant below.

**Verify the disk before trusting the database, and UNKNOWN is not CHANGED.**
`app/core/integrity.py` has **three** states where the obvious design has two, and the third
is what makes the feature shippable. Everything the database says about a file — the recording
id, the release the folder holds, the fingerprint verdict — is a *claim about a file*, and a
tag editor, a redone rip or a media server with write access falsifies it silently. `classify()`
answers `UNKNOWN` for a file nothing has ever baselined, and that is a first-class answer, not a
degenerate one: collapse it into `CHANGED` and the day this ships every file in the library
reports as tampered with, which is indistinguishable from noise, and the one real edit in it is
invisible. `VERIFIED`, `RETAGGED` and `REPLACED` are only ever reached by disagreeing with a
measurement genuinely taken. `RETAGGED` and `REPLACED` are kept apart because they license
different work — a retag changed the metadata around provably identical audio, so the
identification still holds and only the stamp needs refreshing, while a replacement is
different audio wearing the same filename and the release has to be solved again from scratch.
That last one is the only one that re-opens enrichment, and it re-opens **enrichment**, never
the download queue.

**Whoever re-baselines a file has to classify it first.** Two passes write the stamp — the disk
scan and `verify_integrity` — and a write that skipped `classify()` does not produce a wrong
verdict, it produces *no* verdict, silently. Housekeeping runs the scan first and the integrity
rotation last, and the rotation reaches 1/30 of the library a night, so a scan that re-stamped
quietly made `REPLACED` unreachable for any change that moved size or mtime, which is
essentially all of them: measured on the fixtures, replacing a file's audio and re-scanning
gave `{verified: 13}` where scanning once gave `{verified: 12, replaced: 1}`.
`LibraryScanner._apply_file` therefore classifies before it assigns and reports on `ScanResult`
(`files_measured`/`files_retagged`/`files_replaced`/`albums_reopened`), the same way
`scheduler._record_measurements` does. **And the stamp is written as one statement about one
read**: `content_hash`, `sample_count`, `file_size` and `file_mtime` come from the same
measurement or none of them is written. Splitting them silences the tripwire for good — a fresh
size and mtime beside a stale hash means size and mtime match forever after, and the sample
count the eventual re-hash is compared against has already been copied from the new file, so a
replacement classifies as a retag.

**`mark_library_due` cannot re-open an identified release; `reopen_library_albums` is what
does.** `Enricher.mark_due` moves `pending`/`not_found`/`failed` rows and nothing else, which is
right for a release that has just landed — an `ok` row has nothing to learn. A release whose
audio was *replaced* is the case where that inverts: every row is `ok` precisely because it was
matched, and what it was matched against is gone. Both callers of the replacement verdict
(`scheduler._reopen_replaced` and the scan) go through `enricher.reopen_library_albums`, which
is `Enricher.reopen` — the review page's "run now" — filtered by `library_scope()`, and they
count what it really touched, because the old call reported N releases queued having queued
none.

**One hash plus `sample_count`, not two digests.** The tempting design is a whole-file hash
*and* an audio-only hash, so a retag shows up as "the container moved but the audio did not".
It was considered and rejected: an audio-only digest means walking FLAC metadata blocks, ID3v2
sizes and trailing APE tags to find where the audio starts, which is format-specific code that
is subtly wrong for *years*, because a wrong answer is a hash that is merely different and
never a crash. A whole-file blake2b-128 plus `integrity.audio_sample_count()` gives the same
answer for less — a tag edit cannot change the number of audio samples, and every container
mutagen parses already publishes the count. FLAC's `STREAMINFO` MD5 would have been the free
version and is not usable: in this user's library it is zeroed in **185 of 200** files.
`sample_count` of `None` is a refusal to guess and is never read as agreement.
"Already publishes the count" is where that argument nearly failed, and MP3 is the exception
that had to be handled: a file with no Xing header publishes no duration, so mutagen derives
one from the file size and a **trailing** ID3v1 or APEv2 tag counts as audio. Measured on the
suite's own frame fixture, `ID3().save(path, v1=2)` — a pure metadata write, and the default in
mutagen, mp3tag, foobar2000 and EasyTAG — moved the count by 128 bytes' worth of samples and
classified an ordinary retag as `REPLACED`, which re-identified the release and locked it out
of every librarian operation through the fifth gate. `audio_sample_count()` measures those
files over the audio region, stripping trailing tags in a loop rather than in the documented
order (mutagen's own `APEv2.save()` writes *after* an existing ID3v1).
**The tripwire comes first**: hashing costs ~201 ms a file, so ~30 000 files is an hour and a
half of disk. `st_size` + `st_mtime` is one `os.stat` and no read, and it decides only whether
opening the file is worth it — it is never itself evidence, the verdict always comes from the
hash. Because an mtime can be restored, `INTEGRITY_REVERIFY_FRACTION` re-hashes a fixed slice
(1/30, so everything monthly) regardless of what the tripwire said.

**A `qid` is minted once and never changes.** `models.mint_qid()` is a Python-side column
default, so a row arrives with one; `scanner._ensure_qid()` fills the gap for rows that predate
the column and **never touches one that exists**. That is the load-bearing half. A qid is the
only key that survives everything else about a row changing — a re-file, a re-tag, a re-match
to a different release — and every measurement made about a file hangs off it. Re-minting on a
rescan silently orphans all of it, and the symptom is not an error anybody can see: it is a
library that quietly forgets its fingerprints every night. `scripts/migrate_integrity.py` obeys
the same rule, only ever updating rows where `qid IS NULL OR qid = ''`.

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
client side must keep that distinction alive: a bulk dropdown left on *no change* must
produce an **omitted key**, never `false`. The old HTML layer had two helpers for this and
using the wrong one — `_parse_bool()`, which turns `""` into `False` — is what silently
unmonitored every artist in a selection. In the SPA the same rule is two pure, tested
functions: `web/src/widgets/BulkBar/bulkPayload.ts` (tri-state; a ticked list of release
types does nothing without a verb, and an unparseable `monitor_mode` is dropped rather than
raised) and `web/src/screens/library/artistPatch.ts` (the single-artist form, which is the
**opposite**: an unchecked box genuinely means `false`, and the empty release-type list is a
real value, not an absence). Do not build one on the other, and never let a serialiser
coerce `null` into `false` — that is the same bug in a new costume.

**The monitor endpoint toggles, and `monitored` is a filter name.** Every table that lists
albums carries the same control, and they all reach one endpoint,
`POST /api/albums/{album_id}/monitor`: an **empty body toggles**, a body sets an explicit
value. The reason that distinction is written down is a bug the old layer shipped — the
calling screen used to carry its active filters on the mutating URL so the refreshed table
was still the one the user was looking at, one of those filters is called `monitored`, and
reading it as the album's new value made the Wanted page's "ignored only" filter unmonitor
whatever row was pressed. The SPA designs the collision out (filters live in query keys and
never on a mutating URL) and `ToggleProps.onToggle` is `() => void` so a handler cannot
receive a value to pass on; that signature is not to be widened. `set_album_monitored()`
still collapses `WANTED` to `SKIPPED` on the way off (and back on the way on), which is what
makes an ignored row leave the backlog rather than sit there inert, and it deliberately
leaves `QUEUED`/`DOWNLOADING` alone because the worker owns those. The toast is the only
place that collapse is ever explained, so it has to say so.

**Changing an artist's monitoring settings moves the backlog that artist already has.**
The album-level collapse above had no artist-level counterpart, and the gap was not
cosmetic: `Indexer` only ever visits **monitored** artists, and a visit re-derives the
status of *newly discovered* albums only, so a release already marked `WANTED` stayed
wanted forever the moment its artist was switched off. Nothing was coming to clear it. One
real library sat at 4,000-odd wanted releases belonging to artists it had been told to stop
watching, and the only exit was to unfollow them — which throws the artist away too.
`indexer.apply_monitoring_to_backlog()` is the answer and both writers call it:
`apply_artist_update` (one artist) and `bulk_update_artists` (a selection). It applies
`desired_status()` — the same rule the indexer uses for a new album, extracted so there is
one statement of it — to the rows that already exist:

* **Demote** — a `WANTED` release the settings no longer want becomes `SKIPPED`. Nothing
  else moves. `QUEUED` and `DOWNLOADING` are the worker's, `DOWNLOADED` is a fact about the
  disk, and `FAILED` is the record of an attempt rather than an intention.
* **Restore** — a `SKIPPED` release becomes `WANTED` again *only* when the artist's own
  settings want it, so `monitor_mode=future` and a narrowed `accepted_release_types` still
  decide. Two exclusions, each a decision being preserved rather than an optimisation: an
  album whose `monitored` is false was ignored by a **person** from a screen — which is
  precisely why the demotion above leaves that flag alone, since it is the only thing
  telling the two cases apart — and a **duplicate edition** stays skipped, chosen by the
  same `dedupe_key`/`edition_rank` pair `_dedupe_editions` uses, because promoting a whole
  group hands somebody three chances to download the wrong one. A group holding anything
  that is not `SKIPPED` promotes nothing at all.

**Demotion is keyed on `desired_status()`, not on the `monitored` flag, and the trigger is
all three settings that rule reads.** It was the flag on both counts for as long as
unmonitoring was the only way to stop wanting things, and that left the other two settings
able to strand a backlog in exactly the way this exists to prevent — silently, because
nothing revisits a row once it has a status. Switching an artist to `monitor_mode='none'`
marked nothing new and demoted nothing old; narrowing `accepted_release_types` left every
release of a dropped type wanted for good. The same library that produced the rule then
reached **4,693** wanted releases through the first of those, every one under an artist
whose mode was already `none` — a backlog belonging to artists that would never mark
another release. So both callers fire on a change to `monitored`, `monitor_mode` **or**
`accepted_release_types`; `quality_profile` is deliberately not among them, because it
decides how good a copy to fetch and never whether to want one.

Two things inside the function are load-bearing rather than tidy. `wants_nothing()` names
the two clauses of `desired_status()` that never read the album — not monitored, and mode
`none` — so those artists demote in one `UPDATE` instead of loading four thousand ORM
objects to be told the same thing; it is an optimisation and has to stay a faithful one, so
a new album-independent refusal belongs there as well as in `desired_status()`, and an
album-*dependent* one belongs only in `desired_status()`. And **demotion runs before the
restore pass reads a group's statuses**: the other order has a group holding one
newly-unwanted `WANTED` release look "already represented", so restore skips it and only
then does the demotion empty it, leaving a group a *second* call would promote. One call
has to reach a fixed point, or a repeated bulk edit walks the backlog up and down.

It moves statuses and **queues nothing** — downloading stays opt-in. The counts come back
as `demoted`/`restored` on `MessageOut.detail` and in the sentence, because the largest
thing a bulk edit does is otherwise invisible: the Releases count drops by thousands and
nothing said why.

**A Qobuz artist id is not always one artist, and a release under it is not always
theirs.** Two separate failures with two separate answers, both landing in
`desired_status()` because that is the only thing that re-derives an existing row's status.

*Guest appearances* are exact and need no judgement. Qobuz files a release under everybody
credited on it, so `map_album(raw, str(artist.id))` — which stamps the followed artist as
owner regardless — filed six Black Country Communion albums under Joe Bonamassa and 46 rap,
gospel and afrobeats releases under a Dutch country singer. `mapper.album_guest_appearance()`
reads `artists[].roles` out of the *free* `getReleasesList` payload (no extra call) and
`Album.guest_appearance` carries it. **The phrasing is the whole thing**: a guest is
`featured-artist` **and never** `main-artist`. "Lacks `main-artist`" is the reading that
looks equivalent and is not — that array lists *performers*, so a composer is routinely
absent from their own release (Samuel Barber's id appears on 13 of his 169), and the loose
reading demotes 92% of a composer's catalogue. Absent therefore answers `False`. A
co-credited `main-artist` is `False` too: two names on the sleeve is a collaboration.

*Conflation* is not exact and must not pretend to be. Id `322476` "Boaz" holds releases by
at least eleven people, all stamped `main-artist: 322476`, so nothing about the release
separates them — and no upstream does either: **Deezer merges the identical set** into
artist `1482943`, and MusicBrainz carries 12% of the ISRCs (7 of 58), which cannot classify
a backlog. Every partition anyone reaches for was measured and fails: genre trips 63 of 154
artists, label *name* collides (`"Boaz"` is four label ids), label *id* mixes two people at
`2919185`, and the ISRC registrant is a *distributor* code that fragments real artists —
seeded from owned albums it keeps 13% of Beth Hart and 35% of Carly Pearce. So the machine
classifies on the one thing that is about the person — the qualified credit line in Qobuz's
own `performers` string — and **a human picks**. `Artist.credit_filter_json` remembers what
they picked; `Album.credit_names` is what was read.

Four rules hold it together:

* **Both columns are three-valued, and `NULL` is a no-op.** `NULL` means *nobody has
  measured this release*, and `desired_status()` must behave exactly as it did before the
  column existed. This is `integrity.classify()`'s `UNKNOWN is not CHANGED` rule, and the
  cost of getting it wrong is worse here: every row is `NULL` the day the column ships, and
  nothing would bring them back — `_apply_metadata` never touches `status`, and
  `apply_monitoring_to_backlog` has only two callers, both HTTP handlers. `credit_names`
  draws the line at the *column* rather than the parsed list, because `"[]"` — *read, and
  every credit was the bare artist name* — is a real answer about 20 of Boaz's 58 releases.
  Hence `set_credit_names()` writing `json.dumps` directly and never `join_name_list()`,
  which collapses empty to `NULL`.
* **Both clauses are album-dependent, so neither belongs in `wants_nothing()`.** They are
  conjunctive — they can only turn `WANTED` into `SKIPPED` — so faithfulness holds.
* **An empty credit filter is no filter**, not "accept nothing". A filter refusing every
  release is indistinguishable from a broken artist, and `monitor_mode='none'` already says
  that deliberately.
* **The credit lookup costs an `album/get` per release**, so it is bounded per press,
  explicitly requested per artist, and never done by the indexer — which is why
  `map_album()` writes `guest_appearance` and pointedly not `credit_names`. Every request is
  made with no write pending, for the SQLite one-writer reason `Enricher` splits its tick.

A credit may **reject** a release and never selects anything else: it decides `WANTED` vs
`SKIPPED`, which is reversible and writes no tag, no NFO and no id. It must never reach
`app/enrich/matching.py`, exactly as `aliases_json` must not.

**One query key, one cache — a screen and its own refresh are the same query.** This is the
old rule translated, not a new one. The HTML app rendered a live region's first frame inline
and re-fetched the identical fragment on a timer, and the failure it kept producing was that
the *context* differed: give `/partials/dashboard/queue` a different row limit from the page
that rendered it and the screen silently rearranges itself five seconds after it loads.
In React the equivalent is that **a component must never fetch a narrowed copy of data
another component already has**. A key is `[endpoint, params]` and every one of them comes
from `queryKeys` in `web/src/api/queries.ts`; row limits that used to be Python constants
(`DASHBOARD_WANTED = 6`) are the exported `LIMITS` object, read by both the caller and the
key. `cleanParams()` drops `null`/`undefined` so `{q: undefined}` and `{}` hash the same and
two components asking for the same effective list genuinely share one entry instead of
quietly running two. The shell's `useStatus()` asks for `activity_limit=0` for exactly this
reason: the footer's payload must not be a narrowed copy of the Activity screen's.

Three rules ride with it, each the direct descendant of an HTMX one.
**Mutations invalidate; GETs never do.** Every `/ui/*` POST used to fire `qobuzarr:refresh`
and every live region listened, and *no GET ever fired it*, because a GET that claimed a
change made every region refresh every other region forever. Now: `invalidateLive()` from a
mutation's `onSuccess`, and **no query callback may invalidate anything**. It is
deliberately broad rather than clever — every failure this ever had (a queue row still
saying "pending" after a retry, a badge disagreeing with the page it links to) came from
somebody deciding a particular press could not possibly affect a particular counter. Only
`meta` and `settings` are exempt, because they describe the build rather than the database.
**Filters are part of the key**, so a poll can never reset what the user typed — that is what
`hx-include` was for. And **one scheduler, not one timer per component**: `refetchInterval`
per query with `refetchIntervalInBackground: false`, which reproduces the shim's behaviour of
pausing with the tab and firing exactly one catch-up tick on return rather than a queued
burst. No `setInterval` anywhere, no module-level timers, and a poller dies with its
component. Two intervals are *conditional* — import progress and the integrity pass poll only
while `running` is true — because an idle screen must issue **zero** requests, not one every
four seconds forever.

**A path under `/api` must answer JSON, even when it does not exist.** The SPA catch-all in
`main.py` matches literally everything, so it is registered last and refuses two families of
path outright: `_JSON_ONLY_PREFIXES` (`api`, `openapi`) raise a 404 through the ordinary
envelope, and `/legacy` keeps its own error page for as long as it is mounted. The bug this
prevents is quiet in a way that matters: a mistyped endpoint answered with the shell comes
back `200 text/html`, which no client reads as "no such endpoint" — it reads as an object
that will not parse, raised from wherever the response was eventually used, with the URL that
caused it nowhere in sight. `client.ts` holds the other end of the same rule and treats a
non-JSON body from `/api/*` as a missing endpoint rather than a payload. `/health` is
deliberately **not** reserved: it is one exact path with no siblings, so nothing can reach the
catch-all by mistyping one.

**The client's Health section is addressed `/system`, and the JSON probe is the reason.**
`GET /health` is the liveness probe and it is registered before the catch-all, so it wins —
which means a hard reload on `/health` would hand the browser JSON instead of the
application, and a hard reload is precisely what somebody does when a health screen looks
wrong. The nav **label** stays "Health"; only the address moved. `web/src/routes.tsx` and
`web/src/shell/nav.ts` are the authority on client addresses, and `tests/test_spa_shell.py`
pins both halves.

**`static/app` is a build artefact and nothing else.** It is Vite's output, it is gitignored,
and editing a file in it means editing something the next build overwrites. `SPA_INDEX` is
checked per request rather than at startup so a build that lands while the server is running
is simply picked up — that costs one `stat` on a cached path and removes the instruction
everybody forgets. The shell is served `Cache-Control: no-store` while the hashed bundles
beside it are cached for as long as anyone likes: the shell *names* the bundles, so a cached
shell is a client pinned to a deploy that no longer exists, which presents as a blank page and
a 404 on a filename nobody recognises.

**`web/src/api/types.ts` is hand-kept, and `tests/test_wire_contract.py` is what makes that
safe.** There is no code generation, deliberately — a generator's output is not the place to
write down that `AlbumOut.complete` being `null` means *nothing has counted this release
yet*, and that sentence is the reason the field exists. The cost is that the two halves can
drift, invisibly in both directions: a field the server gained is a field the client never
renders, and a field the server dropped is `undefined` reaching a component the type checker
promised would get a value, surfacing as a blank cell or a `NaN` three layers from the
change. So the test walks the live OpenAPI document and asserts every response model's field
names round-trip. It checks *names*, not types, because TypeScript legitimately narrows
several fields the server declares loosely. Add a field to `app/schemas.py` and you add it to
`types.ts` in the same commit.

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
`deps.library_quality()` is the third caller and asks the same two functions
(`owned_format_id`, then `is_hires`) rather than a third implementation, which is what makes
the Library header's hi-res figure and the `owned_hires` on the rows beneath it unable to
disagree. It counts a release it cannot measure as *unmeasured*, never as *not hi-res* — the
denominator is `measured`, and on a real library nine of seventy-five releases sit outside it.

**One module writes to the library, and it never unlinks.** `app/core/librarian.py` is the
only place allowed to move, rename, re-tag or remove files that are already in
`LIBRARY_PATH` — the scanner is read-only and the downloader only adds. Deleting means
`move_to_trash()`: the tree goes to `Settings.trash_dir` under a timestamped batch holding a
`manifest.json` and a `payload/` copy. `empty_trash()` is the only `rmtree` in the codebase,
it only ever runs inside `trash_dir`, and only on an explicit request. If you add an
operation that touches the library, put it here and make it go through the same four gates:

1. `resolve_in_library()` — resolves symlinks **before** testing containment (resolve-then-
   check, never check-then-resolve) and refuses the library root itself, so one empty
   `album.path` is not a whole-collection delete.
2. `_assert_exclusive()` — refuses a directory that is not *this release's own*: one another
   album's `path` points at or sits under, or the artist's own folder by name. Containment is
   not ownership. `scanner.collect_albums()` deliberately supports a flat layout — audio
   sitting directly in the artist folder — and for those albums `album.path` **is** the artist
   folder, shared by every release in it. Neither other gate notices: an artist folder is not
   the root, and `_assert_not_busy()` is asked about this album alone, never about the
   siblings that would travel with it. Without this, one Delete press trashes a discography.
3. `_assert_not_busy()` — a `DOWNLOADING` album or an `ACTIVE` queue item is refused. The
   worker is writing `.part` files into that directory.
4. The trash, not `os.remove`.

**Upgrade cleanup is conditional, and the conditions are the safety argument.** The default
naming template puts `{quality}` in the album folder, so an upgrade lands in a *new*
`album_dir` and the old one is a duplicate. `AlbumDownloader._clear_superseded()` trashes it
only when `Settings.upgrade_cleanup` is on **and** the run was complete — not cancelled, no
failed tracks, no unstreamable ones. A release that finished with a missing track has a new
folder that is worse than the old one; that check is why the old copy survives it.

**Re-file names a folder after what is in it, and refuses when it cannot tell.**
`plan_refile()` takes the `{quality}` tag from `quality.owned_format_id(album.tracks)` — the
files on disk — not from the catalogue maximum. Use the catalogue figure and re-filing
oscillates: it would rename a 16/44.1 folder to `[FLAC 24-96]` and then want to rename it
back. An album that has never been scanned *or* downloaded has no `Track` rows at all, so the
tag has to come from the files themselves: `librarian._disk_quality()` reads them through
`scanner.read_track`, and when *that* yields nothing (MP3s have no bit depth, and
`quality.format_for_quality` answers `None` rather than guessing) the plan is **blocked**
rather than rendered. Both alternatives are wrong in the same way — a fabricated tag renames
a folder of 128 kbps MP3s `[FLAC 24-44.1]`, and an empty one strips a correct tag off a
folder that already carries it. `naming.album_values()` holds the other half of this: it
substitutes the catalogue maximum for a missing bit depth **only when a format id is known**,
because `clamp_to_format` then holds the figure down to what that format can carry.
`naming.template_uses_quality()` is what keeps the refusal from firing on templates that
never render the tag.

**Three per-album flags say "leave this one alone", and each is enforced in exactly one
place.** `pin_tags`, `freeze_path` and `mute_integrity` are `Boolean, nullable=False,
default=False` on `albums`, written through `PATCH /api/albums/{id}` under the usual
partial-update rule (`None` leaves alone). They exist because a library is not uniform: one
release is a hand-curated rip whose tags are better than anything an upstream would supply,
one sits in a folder another tool indexes by path, one fails a strict decode for a reason
its owner already understands. The alternative to a per-album exception is turning the
feature off for the whole library, which is a much bigger loss.

Each is a **guard clause where the work happens**, never a condition sprinkled through the
callers:

- **`pin_tags`** → `enricher._write_back()` skips the album's tags. That function is already
  the single writer of enrichment tags into files, so this is one clause and not a new
  concept — it is the per-release version of the adopted-album rule, said by a person rather
  than inferred from provenance. It gates the tags **only**: the NFO is still written,
  because `nfo.merge_nfo()` merges element by element rather than replacing, so nothing
  somebody put in one is at risk. And it gates only the **background** pass — the explicit
  re-tag buttons are a different decision, made by someone looking at the release.
- **`freeze_path`** → `plan_refile()` returns a **blocked** plan (the same shape as the
  unknown-quality refusal, so the UI already renders it), `plan_library_refile()` skips the
  album, and `refile_album()` raises before it consults a plan at all. That last one is the
  load-bearing half: a caller that built a good plan and then froze the album must not be
  able to apply it, so the check cannot live only in the planner.
- **`mute_integrity`** → the album is excluded from `librarian.quarantine_corrupt_files()`
  and from `corrupt_files`, the actionable figure behind the nav badge and the health
  screens. This is the one flag that suppresses a **safety** action, so it is deliberately
  narrower than the others: **muting hides the alarm, never the fact.** The measurement
  still runs; the album still reports its real `integrity_state` and `corrupt_tracks` on its
  own detail screen; and the muted files are counted separately as `corrupt_muted` and
  published beside the figure they were removed from — a suppressed alarm that leaves no
  trace is indistinguishable from a check that never ran. So `corrupt_files` excludes muted
  albums (or the screen offers to quarantine and then does nothing) while
  `deps.album_corrupt_counts` does not. If you find yourself making those two agree, you
  have removed the distinction the flag is made of.

**`AlbumOut.queue_state` is not `AlbumOut.status`.** `queue_album()` only promotes
`SKIPPED`/`FAILED`/`WANTED` to `QUEUED`, so an album being *upgraded* stays `DOWNLOADED`
for the whole download. Anything asking "is this album in flight?" must read `queue_state`
(built from the `queue_items` relationship by `deps._live_queue_state`), not `status`. And
because a `QueueItem` is inserted by `album_id`, it does not backfill the album's loaded
collection — `queue_album()` calls `_reload_queue_items()` so the caller re-renders from
the truth. Sessions use `expire_on_commit=False`; a commit will not do it for you.

**Enrichment's subject is the library, not the catalogue.** `enricher.library_scope()` is the
single definition: an album is in scope when its status is `DOWNLOADED` — the one thing a
scanner-adopted album (no `Track` rows at all) and a downloaded one have in common — and an
artist is in scope when they own at least one such album. `_seed`, `_claim`,
`_rearm_stranded`, `mark_due`, `status` and `list_review_items` are all held to it, and
`deps.nav_counts` counts the review badge over `in_library()` so the badge and the list agree.
Following one prolific artist puts their whole discography in `albums`, and enriching that is
work nobody asked for: metadata is written into files, rendered into NFOs and read off the
artist page, and a release that is not on disk has no files, no NFO and nothing to correct.
One real database made it arithmetic — 3313 albums, **32** of them downloaded, 11418 pending
state rows — so the thirty-two releases that mattered sat behind three thousand that did not.
What replaces "enrich on the way in" is `mark_library_due()`, called at the moment a release
lands: `downloader._finalise`, `queue._mark_enrichable` (the branch where the worker itself
promotes the album) and `scanner._adopt`. Each swallows its own failure — `_seed` reaches the
same album from its status next tick, so losing one delays enrichment, never prevents it.
`purge_out_of_scope_enrichment()` — nightly housekeeping via `_prune_enrichment`, and
`cli.py enrich-purge [--dry-run]` — deletes what has fallen out, idempotently, and **deletes
work, not knowledge**: `enrichment_state` is a to-do list `_seed` rebuilds the moment the
album is back, while the three metadata tables are left alone, because
`album_metadata.qobuz_release_type` is the only thing that makes an applied `release_type`
reverse and `mb_release_group_mbid` is what edition grouping keys on. Dropping either would
change which release Qobuzarr thinks it is holding, which is the one thing narrowing the
scope must not do. Two traps if you touch this: an `IN` against a subquery containing NULL
evaluates to NULL rather than false (hence `Album.artist_id.is_not(None)`), and `mark_due`
must `flush()` before its scope query — these sessions are `autoflush=False`, so the status
the caller has just written, the very thing that puts the album in scope, would be invisible
and every album that had just landed would read as out of scope. The coverage readouts take
their denominator from `enrichment_scope_counts()` (`albums`/`artists` on disk, with
`catalogue_albums`/`catalogue_artists` beside them) — a percentage against the catalogue
reports a fully enriched library as 1%.

**Enrichment writes side tables, and one column.** `app/core/enricher.py` may write
`artist_metadata`, `album_metadata`, `track_metadata` and `enrichment_state`, plus
**`Album.release_type`** and nothing else on `artists`/`albums`/`tracks`. That single
exception exists because Qobuz genuinely guesses the release type (its mapper falls back to
counting tracks) and goes through `app/enrich/merge.py:consensus()` on a majority vote. It
changes the type and **never `status`**, so reclassifying can neither create a `WANTED` nor
destroy one — symmetric with how widening `accepted_release_types` already behaves. The
pre-enrichment value is kept in `album_metadata.qobuz_release_type` so it reverses, and
`_apply_metadata` protects `release_type` for enriched albums so the indexer cannot stomp it
back and oscillate. `label` and `genre` are **not** writable and must not be added: both are
naming-template tokens, so a "better" value would make `plan_refile()` want to move every
folder they appear in. Everything else enrichment learns is merged at read time.

**More than half, and Qobuz gets a vote.** `consensus()` returns a value only when *more
than* half the non-null opinions agree. Exactly half is a tie, and a tie keeps what Qobuz
said — so with a single rung enabled nothing is ever overwritten, which is the intended
behaviour, not a bug. Qobuz always votes with its **original** value (from
`qobuz_release_type` once anything has been applied); voting with the applied value would
make one early majority look unanimous forever.

**Exact or nothing, again.** `app/enrich/matching.py` is the metadata version of the
importer's doctrine, and for a worse failure: a wrong MusicBrainz artist id does not stay in
the database, it is written into every file as `MUSICBRAINZ_ARTISTID` and Picard, beets and
Roon then believe it. So `artist?query=<name>` must never exist in the automatic path — the
artist id is *derived* from a release's sole clean artist-credit, with Various Artists
hard-rejected by id and the credited name checked against `scanner.artist_key()`. Barcode
hits are filtered on the returned `barcode` string, **never** on the Lucene `score`: that
index returns confident-looking neighbours. Two survivors is `ambiguous`, not "pick the
first". A single barcode hit must corroborate on track count or title before it is trusted,
because MusicBrainz barcodes are hand-typed. Unresolved goes to a human.
Stated as one rule: **a name may REJECT a candidate, never SELECT one.** `titles_match()` and
`names_match()`/`verify_artist_name()` exist to throw candidates away; nothing may search on a
name and take what comes back. That is why `Candidate`/`SearchableProvider` is a separate
protocol from `EnrichmentProvider` — it makes "only a person reads a name search" a fact the
type checker holds, rather than a convention.

**The Qobuz album id is not a barcode.** It looks like one often enough to be tempting —
`0804879535645` really is Joe Bonamassa's *Blues Of Desperation* — and it was used as one,
justified by arithmetic (6 matchable albums against 791) computed back when enrichment covered
the whole 4489-album catalogue. Scoping enrichment to the library reverses that sum: the
subject is the ~71 albums on disk, and there the fallback was not supplying keys, it was
supplying **wrong** keys. Measured on the live database, every one of the 25 "no MusicBrainz
release carries barcode …" failures came from it, and 6 of the 13 matches that did work rested
on it — working for Joe Bonamassa's label and failing for Mark Knopfler's, purely by how each
numbered its releases. `0060249867260` is not the barcode of *Shangri-La*; `602498672600` is,
and the two are one digit-shift apart. Note the Qobuz id also *passes* the GS1 check digit, so
validating harder would not have caught it — the fix is to stop reading a field as something it
is not. Generalised: **never use a value as something it is not.** Only fields that *are*
barcodes go to `barcode_candidates()`, and only barcode-shaped tag names go to
`barcode_from_claim()` (`CATALOGNUMBER` is deliberately excluded — `JRA-2016` is a label's own
reference, and reading one as a barcode is the same category error).

**Claims are hypotheses, and `file_claims` is the table that keeps them apart from facts.**
The tags in a file are the one input that arrives with nothing vouching for it: a hand-typed
barcode, an MBID copied off the wrong release, an ISRC from a different mix, a `qobuzarr_qid`
that came along when a file was duplicated rather than moved. `scanner._record_claims()` writes
them to `models.FileClaim` and **nowhere else**; `track_metadata`/`album_metadata`/
`artist_metadata` hold only what was *verified* — matched exactly on a barcode, chosen by a
human, or agreed to by the audio. Conflating the two is the whole bug class this exists to
prevent: a claim written into the verified tables is indistinguishable from a fact five minutes
later, whereupon it is read back as evidence, propagated to the release and the artist, and
written into every other file on the next re-tag, with nothing downstream able to tell the
chain rests on a string a stranger typed. Two tables makes promotion a deliberate, different
write. So a claim may **narrow** a search (cheap — a tag parse, not a request) and may
**reject** a candidate; it may never select one. It reaches a provider only as
`AlbumSnapshot.claimed_barcode` — its own field, lowest of the three in `barcode_candidates()`,
never folded into the verified `barcode` — and `matching.album_claimed_barcode()` supplies it
only when the files agree: two barcodes under one directory means the folder is not one
release, and taking the majority would write a barcode into files it was never true of.

**The audio is the anchor, so AcoustID runs first.** `ENRICHMENT_SOURCES` defaults to
`acoustid,musicbrainz,deezer,coverartarchive,wikidata` and the order is the setting's, not
`registry.py`'s — that file only builds what it is asked for. The reasoning is that every other
input to an identification is something a person typed, and the library being identified is
exactly the one whose typing cannot be trusted; the waveform is the only thing in it that
cannot be mistagged. So the rungs below corroborate what the audio said rather than proposing
answers of their own, and the keys flow downward: AcoustID pins the release, MusicBrainz joins
on the release id it was handed, Deezer picks up what is left. Run MusicBrainz *first* and most
of a library is unreachable — it can only match exactly, so it needs something above it to
produce a key. Identity then propagates **upward**: release → its first artist credit, with the
name used only as the rejection test above.

**MusicBrainz moved ahead of Deezer, and the order is no longer load-bearing.** It sat behind
Deezer for as long as the only keys were Qobuz's `upc` and a barcode Deezer supplied. AcoustID
falsified that premise: it writes `mb_release_mbid` onto the album, which is a *stronger* key
than a barcode and one MusicBrainz owns, so the rung the ordering protected was the one already
holding a key. Measured on a 422-release library, MusicBrainz resolved **253** releases off the
audio's answer (107 on a pinned release, 146 on a release group) while Deezer, running ahead,
recorded `no_key` — "no barcode on this release and no Deezer artist to browse" — on **197 of
those same albums**.

The swap does give something up, and it is worth naming rather than pretending otherwise: for a
release AcoustID could **not** pin, Deezer-first is still better, because it answers on an
artist browse and its payloads carry the barcodes MusicBrainz then matches (41 of that library's
117 learned barcodes came from Deezer). One sequence cannot serve both populations. What makes
that loss temporary rather than permanent is `_rearm_barcoded` below — with it, the order decides
how many ticks an answer takes, not what is reachable at all. **So do not re-litigate this by
reordering; if the two populations ever need different sequences, the answer is a better re-arm,
not a better guess about which one is bigger.**

**Solve the album, never the track, and there is no tie-break.** A fingerprint identifies a
*recording*, which is a hopeless answer alone: "Sultans of Swing" sits on the studio album, on
four live records and a dozen compilations, so resolving files one at a time scatters a
directory across a dozen releases and then asks which won. `app/enrich/coverage.py:solve_album()`
asks about the directory instead, and a release is **admissible** only on three requirements —
none of them weighted, all of them necessary:

1. **Injective.** Every file it explains must seat at a *distinct* position. Two files claiming
   one slot does not mean one wins; it means the hypothesis is wrong. Checked as a matching
   problem, not a first-fit walk, because first-fit answers differently depending on how the
   caller sorted the directory.
2. **Coverage.** It must explain essentially all the files (`MIN_COVERAGE`, not 1.0 — a
   fingerprint index has holes).
3. **Track count.** Its own track count must equal the number of files. This is what keeps a box
   set out: a 60-track anthology really does hold all fifteen of these recordings at fifteen
   distinct positions, so it passes the other two and only counting rules it out.

Then: exactly one admissible release is **identified**; none is **unidentified**; several are
**editions of one release group** and go to a human, with `mb_release_mbid` left empty rather
than a pressing invented. Measured, this is not close — for one Knopfler file *On the Road to
Milano* explains 1 of 15 files and *Down the Road Wherever* explains 15 of 15. **There is no
highest-coverage-wins branch and there must never be one**: every tie-break anyone reaches for
(most files explained, earliest date, the user's own country) is a guess dressed as a rule, and
this one gets written into every file on disk as `MUSICBRAINZ_ALBUMID`. Fourteen of fifteen
does not beat fifteen of fifteen — if both are admissible the audio does not distinguish them,
and a person decides.

**A dead end must be able to end.** `no_key` and `gated` store
`next_attempt_at = NULL` — retrying them on a timer is pure noise, because neither
improves until its *inputs* change. But the inputs do change, later and elsewhere, and
nothing was watching. An album with no barcode is matched by browsing its artist's
discography, and the artist's id is derived from whichever of their releases matches on a
barcode *first* — so every album reached before that moment recorded "no artist to browse"
and went permanently silent while being answerable minutes later. Likewise `gated`: 266 rows
sat waiting for an `fpcalc` that was by then installed. `Enricher._rearm_stranded()` runs
each tick, before `_claim`, and asks the state rather than remembering an event: is this
rung ready now, does this album's artist have the id it wanted, and — `_rearm_fingerprintable`
— does this album have a file to fingerprint yet? That third one is the same failure a third
time: AcoustID finds files through track rows, the scan did not write any, so every adopted
release recorded "nothing on disk to fingerprint" about audio that was sitting right there.
Event-based re-opening
(`EnrichmentResult.reopen`) still exists and fires sooner, but it only ever helps rows
stranded *after* it ships and depends on catching a moment exactly once — which is why the
repair is state-based, idempotent, and converges (a re-armed row ends `ok`/`not_found`/
`ambiguous`, never back where it started). It also clears `last_error`, because a row
waiting on a tick must not sit on the review list claiming it is waiting on a human.

**`_rearm_barcoded` is the fourth input, and it is what makes the ladder's order stop
mattering.** A rung that consumes `barcode_candidates()` — Deezer and MusicBrainz, named in
`_BARCODE_SOURCES` — answers `no_key` on a release with no barcode *yet*, and a barcode
arrives from more places than the rung that wanted it: another rung verifying one, a person
identifying the release, or `cli.py backfill-upc` fetching the `upc` Qobuz has held all
along. 480 album rows on one library said "no barcode on this release and no … artist to
browse", and nothing was ever coming for them. Two rules:

* **The test is `barcode_candidates()` itself, run in Python over a bounded set, not
  approximated in SQL.** Same function the providers call, so this pass cannot disagree with
  them about whether there is anything to search with — `app/core/quality.py`'s "one
  comparison, two callers" rule again. And it is what keeps the pass **convergent**: a column
  being non-NULL is not the same claim as a *usable* barcode (`normalize_barcode` rejects the
  `N/A` somebody typed into a tag editor), and a row re-armed into the identical refusal
  re-arms every tick forever, billed to the upstream. The guarantee rests on a property of
  the two providers — with candidates in hand neither can reach its `NoMatchKey` branch, so a
  failed retry lands on `not_found` — so **widening `_BARCODE_SOURCES` to a source that can
  still answer `no_key` with a barcode in hand loses it**.
* **`claimed_barcode` is deliberately not read here.** `album_claimed_barcode()` returns
  `None` the moment two files under one directory disagree, so a folder holding two barcodes
  has a `FileClaim` row and no usable candidate — exactly the non-convergent shape. The claim
  still reaches the provider through the snapshot; it just cannot be what *wakes* a row.

**Wikidata is in `_ARTIST_ID_COLUMN` too, and it is the fourth instance of that same
failure.** The map reads "the `artist_metadata` column this source waits on", and it holds two
different waits: Deezer and MusicBrainz wait on an id they *browse a discography* by (which is
why only they drive the album half of the re-arm — that half is gated on the provider actually
handling albums), while Wikidata waits on one it needs for the **artist alone**. The QID is not
a browse key, it is the whole subject, and it arrives second-hand from MusicBrainz's URL
relations long after the Wikidata row went `no_key`. Leaving it out made Wikidata the one rung
with no state-based backstop: its only route back was the single `reopen` MusicBrainz appends
on the tick that discovers the QID — one shot, at a moment that may have passed before the code
existed, and never fired at all when the QID was already in the snapshot. Cover Art Archive is
still absent and still correct, because it is keyed by the *album's* MBID, which is not a
column on `artist_metadata` at all.

**Enrichment never holds a session across a network call.** SQLite has one writer, and
SQLAlchemy's SQLite dialect holds the transaction from the first write to the commit. An
enricher that wrote, awaited a two-second MusicBrainz call and then committed would block
`AlbumDownloader`'s per-track commits until `busy_timeout` expired, at which point
`QueueWorker`'s broad `except` marks a perfectly good download **failed**. Hence the three
phases: claim (one short transaction), all HTTP (no session open), persist (another). The
snapshots in `app/enrich/types.py` exist to make that possible, and `Enricher.mark_due()` is
pure SQL for the same reason — it is what "enrich on the way in" actually is, called from
inside the caller's own transaction by `mark_library_due()` (a release landing) and by the
indexer (a release being re-visited, where corrected UPCs and track counts arrive). It makes
no request, and it drops ids outside `library_scope()`, so no caller has to know the scope
rule to be safe.

**No ORM relationship from Artist/Album/Track to their metadata.** `deps.artist_to_out` and
`album_to_out` are synchronous, and `lazy="selectin"` does not save them: a row that was
committed but never refreshed still raises `MissingGreenlet` on attribute access — exactly
what `Indexer.add_artist` hands to `artist_to_out` on every first follow. Metadata is
batch-loaded by the async collection builders and passed in as `meta=`. A caller that forgets
gets an un-enriched entity, not a 500.

**Artists are enriched before their albums.** Both album *browse* paths — Deezer's discography
listing and MusicBrainz's release-group browse — need the artist's id, and browsing is how a
release with no barcode is matched once the audio has not settled it. `Enricher._claim()`
orders on it explicitly.

**The enrichment tables carry a version.** `create_all` checks that a table *exists*, never
its shape, so adding a column to one of them would surface as `no such column` at runtime.
Bump `models.ENRICHMENT_SCHEMA_VERSION` and `init_db()` drops and rebuilds every table in
`models.ENRICHMENT_TABLES` — **five** of them now, `file_claims` having joined the family,
which qualifies on exactly the test the others do: it is re-read from the files in a single
pass over the disk. Safe because every row in all five is derived, re-fetchable data. Read
the tuple rather than counting from memory; it is the list `init_db()` actually drops.

It is **not** the codebase's only migration story any more, and reaching for it outside
these five is the mistake it now invites — see "No migration framework" below.

**A file that holds no playable audio is a broken file, and it is reported, never
automatically trashed.** `track_metadata.fingerprint_state == CORRUPT` is the one statement of
it, read by `librarian.corrupt_file_count`/`quarantine_corrupt_files`, `deps.album_corrupt_counts`,
the nav badge and the Integrity screen. The column is *named* for the fingerprinter because
that used to be the only thing that could reach the verdict; that is not what it means. It
means the audio is unusable, and **two** things establish that.

`app/enrich/chromaprint.py` is the first and distinguishes **four** failures, only one of
which is evidence about the file. `FpcalcMissing` says the tool is absent — the rung gates.
`FingerprintTimeout` says the decode did not finish, which is a fact about the machine: a
contended box or a stalled mount, and the file may be perfectly good. `FileUnavailable` says
the path did not resolve, which is a fact about the *mount* — a share that blipped, a folder
another tagger renamed a second ago, a row the scan has not caught up with — and nothing was
decoded at all; it used to be raised as `UnreadableAudio`, which meant a share going away for
a minute ended with every file of the album recorded `corrupt` and the nightly quarantine
moving a healthy release into the trash. `UnreadableAudio` says the tool ran, finished, and
the audio would not decode, which no music player could either. **Only the last** may reach
`fingerprint_state`.

`scanner.read_track` is the second, and it makes the *same* split for the same reason.
A file nothing can parse is stronger evidence than a failed decode — fpcalc never gets to
run, because there is nothing to run on — but only once it is a statement about the file
rather than about the mount, so `ScannedTrack.unreadable` is set only when the path stat-ed
before **and** after the failed parse at an unchanged size. A stat that raises returns `None`
and the file is skipped, exactly as `FileUnavailable` is. Before this a zero-byte `.flac`
produced no track row at all and was therefore invisible to the entire application: nothing
to fingerprint, nothing to baseline, no row for a verdict to hang off, no count it appeared
in, and no trace anywhere but the transient error list of whichever scan last ran. One real
library held **30 of them across 15 releases**. Such a file now gets a `SCAN` row with
`TrackStatus.FAILED` — deliberately not `DOWNLOADED`, because `deps._apply_completeness`
counts exactly that, and calling a broken file present is what let a release report 13 of 13
while six of them were silent — and `scanner._record_corruption` writes the verdict. It is
never hashed: a digest of it would be a perfectly good baseline for a file with no audio, and
the row would then read `VERIFIED` forever. And it clears **both ways** — a file that parses
again loses the mark on the next scan, because the fix for a corrupt file is to replace it,
so that is the common case and a count that never falls is a count people stop reading.

**Nothing acts on the verdict unattended.** `librarian.quarantine_corrupt_files()` still
exists, still goes to the trash through the same four gates as every other library write, and
still puts the track back to `PENDING` and the album to `WANTED` while queueing nothing — but
it is reached only from `POST /api/library/quarantine`, a press. The nightly job counts
(`scheduler._count_corrupt` → `corrupt_files_waiting`) and acts on nothing. The objection to
the old behaviour is not that the quarantine was unsafe; it is that it **erased its own
alarm**. `corrupt_files` is the number that brings somebody to the Integrity screen, moving
the file out clears it, and what is left is a release that says only that it is incomplete —
indistinguishable, on a real library, from the thousands nobody has ever downloaded. The
person who could press *download again* was never told there was anything to press it for.
An automatic fix that deletes the evidence of what it fixed is worse than no fix.

**What a rung learned before it said "no" is still written.** "Not identified" and "learned
nothing" are different claims, and `EnrichmentOutcome.partial` carries the difference:
`Enricher._persist` applies it before recording the refusal. AcoustID that could not decode a
single file has not identified the release, but it has proved every file is broken — and
dropping that made the *worst* case, an album where nothing plays, the one case that recorded
no corruption and was never quarantined, while one bad file among good ones worked fine.

**The audio may overrule the metadata, but only by a majority.** `AcoustIdProvider._confirm`
compares AcoustID's recordings against the ones MusicBrainz mapped. One track differing is
ordinary (a bonus track, a different mix); most of an album differing means the barcode
match was wrong, and it becomes `ambiguous` for a human. With nothing mapped yet it says
nothing rather than rubber-stamping. Without an AcoustID key the lookups are skipped but the
corruption check still runs — that half is local and needs no account.

**An NFO is merged into, never replaced.** Real libraries already have these
files — this one's were written by Jellyfin — and they hold a biography from
TheAudioDB, artwork paths, that server's own ids and a `dateadded`, none of which
Qobuzarr knows. `nfo.merge_nfo()` updates only the elements Qobuzarr generated,
keeps every other element in place, and prefers the spelling already in the file
(Jellyfin's `musicbrainzalbumartistid` over Kodi's `musicBrainzArtistID`) so the
document does not end up with two elements claiming the same fact.
`<lockdata>true</lockdata>` is honoured by not touching the file at all — that is
how someone tells their media server to stop editing it, and Qobuzarr is not an
exception to the instruction. An unchanged result is not rewritten, so a stable
mtime does not make a media server re-scan for nothing.

**Enrichment reaches the tagger as a plain dict, keyword-only.**
`build_tags(..., extra=...)` and `tag_file(..., extra_tags=...)`. The caller
resolves the values on the event loop (`nfo.tag_values()`); the tagger runs in a
worker thread, where reading a relationship raises `MissingGreenlet` — which
`tagger._get` does **not** swallow, so it would fail the whole track. A new
*positional* parameter would also break the six tests that patch `tag_file` with
`lambda path, *a, **k`. Tag names follow Picard's spelling exactly (`TXXX`
descriptions, `UFID` with owner `http://musicbrainz.org`): a tag spelled
differently is a tag no other tool will read.

**Completeness is three-valued, and the third value matters.** `AlbumOut.complete`
is `None` when nothing has counted the release yet — neither the download loop nor a disk
scan — because rendering that as 0% would report a complete album as empty, which is the one
wrong answer available. `deps._apply_completeness()` answers `True` from the status in that
case. A release either half has walked *is* counted, including when the count is bad news:
nine files where the catalogue says twelve is a real gap, and the blanket `True` this used to
give every adopted album hid exactly that.

**Editions group by release group, falling back to `dedupe_key(title)`.** The fallback is not
a nicety: most of a library has no MusicBrainz match, and without it grouping would do
nothing for them. Ordering is `indexer.edition_rank`, the de-duplicator's own — track count
before audio quality, so a one-track hi-res promo never outranks the album it was cut from.

**Both kinds of key belong to one record, and `deps._record_key_pairs()` is the only thing
that decides which.** `list_release_group()` and `group_editions()` must ask it rather than
comparing keys themselves — one comparison, two callers, exactly like `app/core/quality.py`.
Scoping enrichment to the library is what forced this: only albums on disk are ever enriched,
so the copy you own carries an MBID and its catalogue-only twins never will, and a naive match
files them under different keys **permanently** (it used to be a state a library passed
through). The symptom was precise and backwards — `/albums/{owned}` showed no other editions
while `/albums/{unowned}` showed them all, and the artist page listed one record twice, which
is the "six rows for six editions is six chances to download the wrong one" failure grouping
exists to prevent. The rule is deliberately asymmetric: two albums that **both** carry an MBID
are one record only if the MBIDs match (MusicBrainz has already said they differ, and a shared
title is not evidence against that), an album with **no** MBID joins the record whose
normalised title it shares, and a title claimed by two MBID-keyed records is ambiguous, so the
unmatched album stays on its own rather than guessing. Assign with `next(iter(owners))`, never
`.pop()` — the set is shared, and popping hands the record to the first sibling and strands
the rest.

**Enrichment that never reaches the files is enrichment nobody asked for.** Scoping closed a
loop that used to be open by accident: a release is enrichable only once it is `DOWNLOADED`,
and `AlbumDownloader` resolves its tags *before* writing the first file, so for a first
download the ids now arrive strictly after the only thing that would have written them — and
nothing else revisits the files. `Enricher._write_back()` is phase 4 of the tick (its own
session, after the persist has committed, failures swallowed), gated on
`Settings.enrichment_write_back`. It re-tags **only releases this program downloaded** —
an album the scan adopted arrived with somebody else's tags, possibly hand-made, and those
are not ours to overwrite. Adopted albums still get their NFO, which is merged rather than
replaced. The test is `Track.origin is TrackOrigin.DOWNLOAD`, **not** the existence of a
track row: that inference held only while the download loop was the sole writer of them, and
the disk scan writes rows now, so reading it the old way turns one setting into a licence to
rewrite the tags of every hand-curated album in the library.

**Editing an artist's tags is two endpoints on purpose, and the retag pass is three ordered
passes.** `PATCH /api/artists/{id}/tags` (`update_artist_tags`) writes `name`, `sort_name`
and `mb_artist_mbid` and touches **no file**; `POST /api/artists/{id}/retag`
(`retag_artist`) is the half that reaches disk. They are separate because a typo in a name
must be correctable without committing to a thousand file writes in the same press.

Five things hold it together:

- **A field with no column is refused, not swallowed — so the aliases list earned one.**
  `ArtistTagsIn` is `extra="forbid"` and a key nothing can store comes back 422
  saying so, because accepting a key and dropping the value is the one outcome worse than
  not offering the field: the person watched their typing be saved. *aliases* answered 422
  on exactly that rule until it was given the other honest answer — `artists.aliases_json`,
  a JSON array (an alias legitimately contains a comma, so `accepted_release_types`' comma
  join would turn one into two), carried by `scripts/migrate_artist_tags.py`, cleared by
  `[]` and left alone by omission like every other single-artist field. What survives is a
  narrower refusal, written into the column's own docstring: aliases are **database-only**.
  Nothing writes one to a file tag (Picard has no such field, and a tag spelled differently
  is a tag no other tool will read), nothing writes one to `artist.nfo` (no media server
  reads one), and **`app/enrich/matching.py` may never read them** — widening the accepted
  name set with a string a person typed would let a name *select* a candidate at the last
  gate before an MBID is written into every file.

- **`mb_artist_mbid` goes through `Enricher.identify()`, not a write of its own.** Typing an
  id by hand is a manual override of a derived id, so it has to be marked `manual` — that is
  what `_MANUAL_OWNS` reads to stop the next automatic derivation replacing it. A field that
  is silently re-derived next tick is worse than no field, because the id is written into
  every file as `MUSICBRAINZ_ARTISTID` and Picard, beets and Roon then believe it. Validation
  runs before any write, so a rejected edit leaves the row exactly as it was.
- **It re-tags through `librarian.retag_album()`.** There is no second tagging path, and
  there must not be. Unlike `_write_back()`, this **may** touch albums the disk scan adopted:
  the rule there is "not ours to overwrite", and this is an explicit press by the person who
  owns the library. `retag_album` already stands the files in for the `Track` rows those
  albums do not have.
- **The order is re-tag → re-file → NFO, and each step is placed by a failure.** Re-filing
  is opt-in (`refile: bool = False`) because a tag fix and a thousand-file move are
  different-sized decisions, and `Album.freeze_path` is honoured — but frozen releases are
  *reported* rather than skipped silently, because saying nothing about a folder somebody
  asked to rename reads as a rename that happened. Re-file runs **after** the re-tag because
  re-tagging changes every file's hash: the other order has the re-file's staleness gate read
  our own edit as tampering and refuse everything. The NFO pass runs **last**, after any
  move: `artist.nfo` is written into the parent of the album folder, so describing first puts
  it in the folder the release is about to leave, leaves the renamed folder — the one a media
  server reads — with no `artist.nfo` at all, and strands a file that stops
  `_prune_empty_parents` from removing the abandoned directory. Safe last because an NFO is
  not audio and cannot disturb the baseline the re-file gate reads.
- **Its counts are scoped to what it actually considered.** `librarian.albums_on_disk` is
  `DOWNLOADED` *and* has a path; `write_library_nfo` is any album with a path, whatever its
  status. `_verify_library` demotes an album it cannot see to `WANTED` **without clearing
  `Album.path`**, so on an unmounted share an unscoped tally reports "0 of 0 re-tagged, 12
  blocked" about twelve releases the pass never touched. A release refused by more than one
  pass is counted **once** — "3 blocked" about one album is a figure nobody can act on —
  while `errors` still carries every reason, so that list is legitimately longer than the
  count beside it.

**The nightly purge stands down whenever the verify pass demoted anything.** `_verify_library`
flips every album whose files it cannot see back to `WANTED`, which is exactly what an
unmounted library produces, and a purge reading that as "nothing is on disk" deletes every
`enrichment_state` row there is — including the `ok` rows whose `next_attempt_at` is the record
of *already asked, do not ask again*. The mount returns and the whole library re-enriches from
zero. Same transient `requeue_missing=False` already exists to survive, same answer: the purge
is idempotent, so waiting a night costs nothing.

**The review list is the visible half of "exact or nothing".** A matcher that refuses to
guess is indistinguishable from one doing nothing unless somebody is told. `/system/enrichment`
and its nav badge are that telling, `enricher.list_review_items()` builds it with real names
rather than bare ids, and `enricher.identify()` is the escape hatch — a human deciding is not
fuzzy matching. `not_found` is deliberately **not** on the list: the upstream has no such
record yet, which is waiting rather than deciding.

**And nothing is on it that nobody can act on.** A review list is a to-do list, so a row
that no press can settle does not belong on it however interesting it is.
`enricher.actionable_review_clause()` is the SQL half of `review_picker_for()`, applied by
both `list_review_items()` and `deps.nav_counts` — same pairing as `REVIEW_STATES`, and for
the same reason: a badge counting rows the page filters out is a number that never goes down
no matter how much of the list you work through. What it removes is the rungs waiting on an
id **another rung will hand them** — the Cover Art Archive on a release MBID, Wikidata on the
QID MusicBrainz publishes. Those are not stuck and they need nobody; they clear themselves,
they still show their state on the entity's own screen, and `_rearm_stranded` is what
notices. Measured on one real library this was **71 of 186 rows**, so two thirds of the list
was unpressable, which is how a list teaches people to stop reading it.

**"Takes no id" and "nobody can act on this" are different questions, and AcoustID is where
they came apart.** Judging a row by its source alone hid the single largest block of real
work in that same library: an AcoustID `ambiguous` says *several releases explain this
directory equally well*, and those releases are the MusicBrainz releases `solve_album()`
weighed and refused to choose between. The decision is genuinely a person's; it is simply
recorded against MusicBrainz, because that is where the id lives. Filtering on the old rule
would have dropped **51 rows, 26 of them on albums with no other review row at all** — and 34
of those sat on `musicbrainz=ok` with no `mb_release_mbid`, matched to a release *group* and
never to a pressing, which is the exact question AcoustID was asking. So `_DELEGATED_PICKERS`
maps a refusing source to the one picker that settles it, **and to the states it settles**:
AcoustID `ambiguous` delegates to MusicBrainz, AcoustID `no_key` (no files to fingerprint) is
still dead, because no id anybody types conjures a file. It is a delegation, never a
widening — each entry names exactly one picker, chosen because that is where the rung's own
candidates came from, so "a person resolving a MusicBrainz ambiguity is asked about
MusicBrainz's records, not offered Deezer's" still holds.

`ReviewItem.identify_sources` is now the primary property and `is_actionable` is
`bool(identify_sources)` — the reverse of how the pair started. The old direction *made* the
assumption AcoustID broke, by asking about the source rather than about the answer. Deriving
the flag from the picker list means the two cannot drift, and a client is never told to draw
a button it has to work out the target of for itself.

**A person has to be able to say no, and `rejected` is the only state waiting on nobody.**
A review list nothing ever leaves is a review list nobody reads, and before this the only
way off it was to identify a release — which is exactly the thing a person may not be able
to do. `rejected` is a **value** in the `enrichment_state.state` vocabulary
(`app/enrich/errors.py`, published through `deps.build_meta`), not a column, so it needed no
schema bump. It is the one value **no matcher can produce**: nothing in `app/enrich` raises
its way there and no outcome class carries it, so its presence in a row is proof a person
pressed the button. That is what lets everything downstream trust it. Five rules keep it
meaning that:

- `next_attempt_at = NULL`, like `no_key` and `gated` — but for a *different* reason, and
  the difference is the whole design. Those two are waiting on their **inputs** (a barcode,
  an identified artist, an installed `fpcalc`) and `_rearm_stranded()` exists to notice one
  arriving. This one is waiting on nobody, so **`_rearm_stranded()` must never re-arm it**
  and **`_seed()` must never reset it to `pending`**. Either would put back on the list the
  row somebody just took off it, every tick, forever — which is the nagging Reject exists to
  stop.
- `list_review_items()` excludes it and `deps.nav_counts` excludes it, so the badge and the
  list keep agreeing. `enricher.REVIEW_STATES` is the single tuple both read, and it is also
  what `reject` validates against and what `MetaOut.review_states` publishes to the client —
  one rule, four readers, no copy.
- `purge_out_of_scope_enrichment()` still deletes it when the album leaves the library. It
  is **work state, not knowledge**: the same reason `_seed` rebuilds a to-do list while the
  three metadata tables are left alone.
- It is **reversible**, because a person changing their mind has no other route back:
  `Enricher.reopen()` accepts a rejected row, and `POST /api/enrichment/{type}/{id}/reopen`
  is the endpoint. Like `identify()`, both `reject` and `reopen` **commit themselves** — the
  request-scoped session from `get_session` never commits, so the caller-commits convention
  would make a rejection a green toast and a rollback. And like `identify`, `reopen` must
  `session.get` its subject first: `_reopen` *inserts* when there is no row, so without the
  404 a mistyped id answers "queued 5 sources" and commits five rows about nothing.
- **A dismissal made while the tick is running survives it.** `_claim` commits before any
  request goes out and the review list polls every 15s, so the window in which somebody can
  press Reject on a row whose lookup is in flight is the whole length of the tick.
  `_persist` therefore captures the dismissal before its branches and restores it after: the
  outcome's **knowledge** is still written (a rejection is a claim about the work item, not
  about the release), only its **schedule** is not.

**Accept applies a proposal; it never makes one.** `POST /api/enrichment/{type}/{id}/accept`
works only where the system is already *holding* an answer, and there is exactly one such
thing: `AlbumMetadata.suggested_release_type`, the majority verdict of `merge.consensus()`,
which is recorded whether or not `ENRICHMENT_APPLY_RELEASE_TYPE` allows it to be applied.
That setting is what makes the button useful — with it off, every disagreement is recorded
and none acted on, and Accept is how a person acts on *one* without switching the automatic
behaviour on for the whole library. Applying it is the same single column as the automatic
path (`Album.release_type`, never `status`, with `qobuz_release_type` holding the reversal).
Where nothing is held it answers `applied: false` and the client opens the identify picker.
That is the correct answer rather than a compromise: "accept" implemented as *take the top
search hit* would make this the one place in the codebase that selects a candidate by name,
breaking `matching.py`'s deepest rule and defeating the `Candidate`/`SearchableProvider`
split that makes "only a person reads a name search" a fact the type checker holds.

It also deliberately **does not touch the `enrichment_state` row**. The release type and the
match are different questions — agreeing that a record is an EP says nothing about which
MusicBrainz release it is — and silently closing an ambiguity nobody resolved is the exact
failure this layer exists to prevent. Reject is what closes a row; Accept only ever applies
a value.

**The escape hatch has to be usable by somebody who does not know the id.** It asked for
`984f8239-8fe1-4683-9c54-10ffb14439e9`, and the only people who can answer that are the ones
who never needed the review list. `Enricher.candidates()` searches the source on their behalf
and `web/src/screens/health/IdentifyPicker.tsx` renders the results as cards — artwork, title,
credit, year, track count, **barcode**, and the upstream's own disambiguation, which is the
field that actually settles it. Four things about it:

- **It is the only search in `app/enrich`, and only a person may read it.** `Candidate` and
  `SearchableProvider` are separate from `EnrichmentProvider` so that is a type-level fact:
  `fetch()` never calls the search endpoints, and a candidate list is never resolved by taking
  the top row. Two tests assert the automatic path makes no request at all.
- **The query is loose on purpose, and it was measured.** `musicbrainz._release_query` builds
  `release:(a b c) AND artist:(d e)`, not `release:"<exact title>"`. Qobuz names one release
  *Wagner: Ouvertures, Preludes & Orchestral Works*; MusicBrainz names it *Ouvertures,
  Preludes & Orchestral Works* with the composer in the credit. The phrase form returned zero
  for a release MusicBrainz demonstrably holds — it had already matched by barcode. Escape
  Lucene's operators (`_lucene`): an unescaped colon makes everything before it a field name
  and the search silently returns nothing, which reads exactly like "never heard of it".
- **The picker must survive the list's refetch.** The review list polls every 15s, and the
  old rule ("the panel lives outside the polled region") is now a `Drawer` mounted outside
  the polling region's tree: a background refetch re-renders the list underneath and never
  unmounts a query somebody is halfway through editing. The candidate search is a GET and so
  invalidates nothing; the identify POST invalidates, which is how the list behind the drawer
  catches up.
- **A URL is an identifier.** `_id_from_url` takes the id out of a pasted link before
  validation, so the error messages only fire on something that really is not an id.

Four further things make the recording itself work, and each was once missing:

- **`identify()` commits.** Both HTTP entry points run on the request-scoped session from
  `get_session`, which never commits, so the caller-commits convention would turn a manual
  identification into a green toast and a rollback. It commits itself, like
  `delete_album_files` does. Test fixtures overriding `get_session` must **not** commit
  either, or they hide the whole class of bug.
- **`identify()` then cascades, in the same request.** One manual match is rarely one answer:
  an artist's id is what their barcode-less albums are all waiting on, and MusicBrainz's URL
  relations are where Wikidata's QID comes from. Left to the scheduler that took one rung per
  five-minute tick — each tick claims its batch *before* the previous tick's discoveries are
  written — so a person answered one question and was then shown the consequences of their own
  answer as further questions, a quarter of an hour apart. `Enricher.cascade()` drains it now:
  same four phases as `_run_once` and for the same reasons, just scoped by `_cascade_scope()`
  and looped, with `_rearm_stranded()` re-run each round because that is what notices the id
  landing. **After the commit, never before** — it opens its own sessions and reads back what
  was just written, and running it inside the caller's transaction would hold a SQLite write
  open across a MusicBrainz call, which is the exact stall that makes `QueueWorker` mark a
  live download failed. Three things bound it and all three are honest about stopping: the
  tick lock (a cascade stands down rather than double-spending the request budget — it waits
  `_CASCADE_LOCK_WAIT`, not a whole tick budget), `_CASCADE_BUDGET`/`_CASCADE_MAX_ROUNDS`, and
  running out of work. Nothing is lost at any of them, because every row it does not reach is
  *due*, which is what the scheduler was always going to take. `CascadeResult` keeps `skipped`
  (nothing ran) and `exhausted` (work done, more remains) apart because they say different
  things to the person who pressed the button, and the returned message says which. Scope is
  **one hop** — an artist's albums, or an album's artist — never a transitive closure, because
  two hops from an album is a whole discography and that is a tick's job.
- **The stored id is used.** `MusicBrainzProvider._resolve_album` prefers
  `album.mb_release_mbid` and `DeezerProvider._resolve_album` prefers `album.deezer_album_id`
  before deriving anything from a barcode. A provider that re-derives ignores the answer it
  was given and lands the row straight back on the review list.
- **`manual` beats an automatic derivation, and `_MANUAL_OWNS` is what enforces it.** The
  marker is only worth writing if something reads it, and nothing did: `_upsert` now drops
  incoming keys a manual match owns — the ids *and* the ids derived from them, because keeping
  one and replacing the other leaves the row describing two different records. `identify()`
  passes `force=True`, since only a person may overrule a person.
- **Only `_IDENTIFIABLE_SOURCES` can be identified.** MusicBrainz and Deezer have ids someone
  can type. Cover Art Archive is keyed by MusicBrainz's MBID, Wikidata by the QID MusicBrainz
  publishes, AcoustID by the audio — for those there is nothing to supply and no column to put
  it in, so `identify()` refuses. What a row *offers* is a separate question answered by
  `review_picker_for()`: a source that takes no id may still **delegate** to the one that
  settles its refusals, which is how an AcoustID ambiguity opens the MusicBrainz picker
  without AcoustID becoming identifiable. See the review-list section above. A missing entity
  is a `LookupError` → 404: unfollowing an artist leaves its review rows behind, and an upsert
  against a row that is gone fails the foreign key and renders the raw INSERT to the user.

**Artwork precedence is a quality judgement, not the ladder's order.** Qobuz first (it is the
exact release being sold, and in a real 3313-album library *every* album had one), then
Deezer's official 1000x1000, then the Cover Art Archive — whatever a contributor uploaded,
which ranges from a press-kit scan to a photograph of a jewel case. `AlbumMetadata.cover_url`
is `deezer_cover_url or caa_front_url`; it was the other way round because the Archive runs
later in the ladder, and position was mistaken for preference. `deps.album_to_out` coalesces
the pair behind `Album.image_url` unless `ENRICHMENT_PREFER_EXTERNAL_COVER` says otherwise.

**A biography is stored with its licence or not at all.** Wikipedia text is CC BY-SA,
which is share-alike: republishing it without crediting the source is a licence breach.
`WikidataProvider` writes `bio`, `bio_source_url` and `bio_licence` together or writes none
of them, and both the artist page and the NFO render the attribution alongside the text.

**Enum columns** store `.value` with `native_enum=False` but read back as Python members:
compare `album.status is AlbumStatus.WANTED`, and write enum members, not strings.

**`Settings` is still environment-only; the overlay is a separate object, and reading the
wrong one is silent.** `get_settings()` is unchanged, cached, and correct for everything
that runs before the database exists — the CLI's early phase, scripts, tests. But seven keys
can now be changed from the Settings screen, and for those `get_settings()` answers what
`.env` said rather than what the user chose. **Every reader of an allowlisted key must go
through `config.effective_for(base)`** (or `get_effective_settings()` with no base). Nothing
mutates the cached singleton in place — an overlay produces a *new* `Settings` — so a stale
read raises nothing and looks exactly like a working feature. The full rules, and the one
call site that was missed, are under "Making a setting writable from the UI" below.

**No migration framework — and the gap that leaves is the reason each new column ships a
script.** `init_db()` still only does `create_all`, and the important half of that sentence
is what `create_all` does *not* do: it adds a missing **table**, and it silently ignores a
missing **column** on a table that already exists. So a new table is free — `app_setting`
needed nothing — while a new column on `albums` or `artists` is invisible until the first
query touches it, and then it is `no such column: albums.pin_tags` raised out of whatever
page happened to read it rather than anything at startup. There is no Alembic, and adding
one is not the answer here: three releases in this program's life have added a column, which
does not justify a dependency that then has to be right about every table nobody versions.
What replaces it is one script per release that needs one, all three the same shape —
`PRAGMA table_info` guard per column so it is idempotent, `--dry-run` that writes nothing,
and a docstring saying which release wanted it and why the column could not be re-derived:
`scripts/migrate_integrity.py`, `scripts/migrate_album_flags.py`,
`scripts/migrate_artist_tags.py`. A fresh database needs none of them.

The **enrichment side tables are the exception in the other direction** — they carry
`ENRICHMENT_SCHEMA_VERSION` and get dropped and rebuilt, which is safe only because every
row in them is re-fetchable. Do not reach for that mechanism for a column on `albums`,
`artists` or `tracks`: those hold things Qobuzarr cannot re-derive, and a person's decision
about one release (`pin_tags`, `freeze_path`, `mute_integrity`) is the clearest case of it.

## Where to add things

**A new JSON endpoint** → `app/api/routes_api.py`. Put the actual work in a module-level
service function (like `follow_artist`, `queue_album`, `run_search`), then have the route be
a thin wrapper. Add a response model to `app/schemas.py` — the list envelopes live there too,
not in the router — **and mirror it in `web/src/api/types.ts` in the same commit**, or
`tests/test_wire_contract.py` fails and tells you so. Register nothing in `main.py`: the
router is already included, and it is included *before* the catch-all, which is what makes
the path reachable at all.

**A new closed set of strings — or a new rule about which of them matter** → publish it
through `deps.build_meta` and add a field to `MetaOut`, then read it in the client from
`useMeta()` with the local list as the *fallback for the first frame only*. `_VOCABULARIES`
picks up a plain enum automatically; anything that is not one (a `Literal`, or a tuple like
`enricher.REVIEW_STATES`) has to be passed explicitly, and those are exactly the ones worth
being careful about — a `Literal` produces no OpenAPI enum, so a hand-typed copy of it in
`types.ts` is the one union the wire-contract test cannot see drift in. Pin it there anyway,
beside `SettingOrigin`. The failure this prevents is never an exception: a filter chip
offering a state the endpoint refuses opens an empty pane, and a value the server added that
the client has never heard of falls through to whatever the default branch does.

**A new screen** → a component in `web/src/screens/<section>/`, default-exported and lazily
imported from `web/src/routes.tsx`, plus an entry in `web/src/shell/nav.ts` if it belongs in
the sidebar (nav is data; the sidebar renders it and the badges follow). The frame is
`web/src/screens/screen.module.css` — `composes:` its `section`/`header`/`headerMain`/
`title`/`lede`, and its `filters`/`search`/`shown` if the screen has a filter row, rather
than restating six declarations a seventh time. One `<h1>` per screen. Reuse the query hook the
other screens use rather than calling `client.ts` directly; a screen that fetches its own
narrowed copy of a list is the one-query-key rule broken.

Where the old pages went: the dashboard was dissolved (its blocks are the Library stat strip,
the Releases backlog, the Activity queue pane and the Health overview), `/enrichment` is
`/system/enrichment`, `/library/scan` is `/system/scan`, `/library/tidy` is `/system/tidy`,
integrity is the new `/system/integrity`, and per-artist download settings are a tab on the
artist screen rather than a global page. The Health section is addressed `/system` — see the
invariant; do not "fix" it back to `/health`.

**A new component** → `web/src/design/` if it is generic (and then it obeys that directory's
README: values from tokens, recipes composed from `shared/`, a test, an export from
`index.ts`), `web/src/widgets/` if it knows what a release or a queue item is. The dividing
line is whether the component would mean anything in another application.

**A new live region** (anything showing a count, a state or a progress meter) → a query with
an interval in the `REFETCH` table in `web/src/api/queries.ts`, keyed through `queryKeys`.
Do not add a `setInterval`, do not invalidate from a GET, and if the region is filterable put
the filters in the key. An interval that should only run while something is happening is a
function of the data (`refetchInterval: (query) => query.state.data?.running ? … : false`),
because an idle screen must make zero requests.

**A new Qobuz call** → a method on `QobuzClient` (it handles limiter, retries, backoff,
breaker and error mapping), plus a `map_*` function in `mapper.py` if it returns entities.
Never call httpx directly from `app/core` or `app/api`.

**A new enrichment source** → a module in `app/enrich/` with a `JsonHttpClient` subclass and
a provider satisfying `app.enrich.types.EnrichmentProvider`, a factory in `registry.py`, a
name in `Settings.enrichment_source_list`'s known set, and its own pacing settings. The
provider does request-and-map only: it never touches the database, never decides precedence,
and raises an `EnrichmentOutcome` rather than returning a guess. Implement
`SearchableProvider` **only** if the source has ids a person could look up and a column to
put one in — and add it to `_IDENTIFIABLE_SOURCES`, or the picker will never open. If it has
no id of its own but its refusals name another source's records, that is a
`_DELEGATED_PICKERS` entry instead, naming the states it holds for; without one, its rows are
correctly filtered off the review list as unpressable. A provider knows nothing about scope:
`library_scope()` decides what is ever handed to one.

**A new moment at which an album becomes part of the library** → call
`enricher.mark_library_due(session, album_ids)` from inside that write transaction, and
nothing else. Do not seed `enrichment_state` by hand and do not widen `library_scope()`:
scope is "the album's status is `DOWNLOADED`", and anything that wants enriching has to
become that first.

**Anything asking "is this the same release?"** → `app/enrich/matching.py`. Pure, no I/O.
Reuse `indexer.dedupe_key()` for titles and `scanner.artist_key()` for names rather than
writing a third normaliser. If the question is "which release is this *directory*?", it is
`app/enrich/coverage.py` instead — and the answer must stay a coverage test, never a score.

**Anything that measures a file rather than remembering it** → `app/core/integrity.py`.
Pure of ORM/session/network, synchronous, blocking I/O — always `asyncio.to_thread`. Do not
add a second digest; see the one-hash invariant. A new state would need the three-state
argument answered first.

**Anything read out of a file's tags** → `models.FileClaim`, via `scanner._record_claims()`.
Never straight into `track_metadata`/`album_metadata`/`artist_metadata`. To let a rung use one,
add a field to the snapshot in `app/enrich/types.py` that is visibly a claim (as
`claimed_barcode` is) and pass it as a lookup key or a rejection test — never as a value to
write.

**Anything deciding whether to overwrite Qobuz** → `app/enrich/merge.py`. Pure. Adding a
field to `WRITABLE_FIELDS` needs the naming-template argument answered first.

**A new setting** → a field on `Settings` **and** a documented line in `.env.example` **and**
a row in the README table. Do not edit `.env` (it holds the user's real credentials). If the
Settings screen should show it, that is a fourth place — a field on `SettingsOut`, mirrored
in `types.ts` — and the redaction rule is absolute: `enrichment_contact` is reduced to
`"set"`/`""`, and no token, app secret or signed URL may ever appear in that payload.

**Making a setting writable from the UI is a fifth place, and a deliberate one.**
`Settings` is still read from the environment and `get_settings()` is still env-only and
cached — that is what works before the database exists, and what every script and test
constructs. On top of it sits a narrow overlay: one `app_setting` row per overridden key,
an allowlist in `app/config.py` (`OVERRIDABLE_SETTINGS`) that is **exactly seven keys** and
lives nowhere else, and one accessor — `config.effective_for(base)`, or
`get_effective_settings()` when the caller has no base. Callers that must read a value
*live* go through it (`deps.get_settings_dep`, `librarian`, `scheduler`, and `cli.py`'s
`session_ctx`, which loads the overlay right after `init_db()` so the CLI and the server
never run different configurations against the same library); everything else keeps reading
`get_settings()`. **Every reader of an allowlisted key is a reader of the overlay** — the
one that was missed, `POST /api/integrity/verify`, read `get_settings()` and so kept hashing
the library after the switch was turned off, and told a user who had just turned it on to
edit `.env` and restart. Four rules: the database wins over `.env` but the **origin
of every overridable value is reported** (`SettingsOut.origins`, which says `"override"`
only when the value in force really came from a row — a stored row this build cannot parse
is inert, and reporting it otherwise badges `.env`'s own value as overridden); a key outside the
allowlist is a **400, never a silent no-op** — that is the only thing that makes "no
credential, no path, no rate-limit figure is writable" a guarantee; nothing mutates the
cached `Settings` in place, an overlay produces a **new** object and
`AppState.apply_setting_overrides` re-points every component that captured one; and a value
that cannot take effect immediately says so — `enrichment_sources` rebuilds the provider
ladder through `Enricher.reconfigure`, which disposes the rungs it replaces (two ladders
alive is two limiters per upstream) and defers to the next tick if one is draining, which
is what `SettingsOut.pending` reports.

Adding an eighth key is therefore an entry in `OVERRIDABLE_SETTINGS` with a `parse` that
refuses a bad value **with a sentence**, plus the two markers: `[OVERRIDABLE]` in
`.env.example` at the line that key appears on, and `**[OVERRIDABLE]**` on its row in the
README table. Both are marked *in place* rather than only in a list at the top, and that is
the point of them — somebody editing `.env` is reading the line they are about to change,
not a summary six screens up, and the thing they need to be told is that their edit may
change nothing. Then check the new key's readers: every one of them must be on
`effective_for`, and the way that goes wrong is silent, because a stale read looks exactly
like a working feature.

**A new column on `artists`, `albums` or `tracks`** → the ORM attribute **and** a script in
`scripts/`, modelled on `migrate_album_flags.py`: `PRAGMA table_info` guard per column,
idempotent, `--dry-run`, a docstring naming the release and saying why the value cannot be
re-derived. `create_all` will not add it, and the symptom of forgetting is `no such column`
raised out of a page rather than at startup. Add the script to the README's list in the same
commit. Do **not** bump `ENRICHMENT_SCHEMA_VERSION` for it — that drops and rebuilds the
five tables in `models.ENRICHMENT_TABLES`, which is only safe because everything in them is
re-fetchable, and nothing on these three is. A new *table* needs none of this: `create_all`
adds one for free, which is why `app_setting` shipped without a script.

**A new reason a release is not really this artist's** → a clause in
`indexer.desired_status()` and nowhere else, plus whatever column it reads on `albums` or
`artists` (and therefore a migration script — see above). Not a filter in `_upsert_albums`
or `_fetch_releases`: `_status_for_new_album` runs only in the `album is None` branch, so a
filter there fixes new rows and strands every existing one permanently, which is the
4,693-wanted-releases failure in a new costume. Make the column nullable and make `NULL` a
no-op. If the answer is a name rather than an id, it may **reject** and a person must be the
one who chose it — `app/enrich/matching.py` is where that rule comes from and it may not
read your column.

**A new per-album exception ("leave this one alone")** → a boolean on `albums`, the migration
script above, a field on `AlbumUpdateIn`/`AlbumOut` mirrored in `types.ts`, and **one guard
clause in the module that does the work** — not a condition threaded through its callers. If
the flag suppresses a safety action rather than a convenience, answer the `mute_integrity`
question first: what still gets measured, and where the real figure is still shown. Hiding
the alarm is a decision somebody can revisit; hiding the fact is not.

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
./.venv/bin/python -m pytest tests -q            # 1651 tests, no network
./.venv/bin/python -c "import main; import cli"  # import smoke test

export PATH=~/.local/opt/node/bin:$PATH          # Node >= 20, Tier 0
npm run typecheck --prefix web                   # tsc -b, strict
npm test --prefix web                            # 1050 component tests, jsdom, no network
npm run lint --prefix web                        # oxlint
npm run build --prefix web                       # -> static/app

./.venv/bin/python cli.py verify-credentials     # real Qobuz handshake
./.venv/bin/python -m uvicorn main:app --port 8899   # then open the pages
```

`tests/test_wire_contract.py` is the one that fails when the Python and TypeScript halves
drift, and it *skips* when `web/src/api/types.ts` is absent, so a checkout without the front
end still runs green. For front-end work, run uvicorn on 8000 and
`npm run dev --prefix web` beside it — Vite proxies `/api`, `/health` and `/static` at the
real server, so there is no mock layer to keep honest and no second copy of the API.

Be careful with live end-to-end tests: following an artist triggers an immediate background
index, and anything the indexer marks `wanted` is queued and **downloaded for real** by the
running server. Use `monitor_mode=none`, or unfollow, or cancel the queue items first.
