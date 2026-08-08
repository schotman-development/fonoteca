# What is not wired up

The front end is the design at `qobuzarr.dc.html`, built against the real JSON API. Where the
design draws a number, a control or a whole block that no endpoint can answer, the layout is
built as drawn and the slot holds a `<Placeholder needs="…">` — a dashed, obviously-unfinished
box that shows the em dash where a figure would go and never a fabricated value. This file is
the list of them.

Every row below corresponds to a `data-placeholder` attribute in the built code. The list was
produced by grepping for it, and the screen tests count them, so it cannot silently drift.

---

## Already wired — do not read these as missing

Several things that *look* like they would need a backend already have one, and they are built
for real. If you are auditing this file against the design, these are done:

| Feature | Endpoint |
|---|---|
| The three per-album switches — **Pin tags**, **Freeze path**, **Mute integrity warnings** | `PATCH /api/albums/{id}`, one key per press |
| The settings overlay behind *Structure & tags* — every switch and its origin | `GET /api/settings`, `PATCH /api/settings` |
| The **path-template builder**, its token buttons, Reset, and the rendered preview | `naming_template` + `SettingsOut.naming_preview` |
| "N releases would move under this template" (737) — the library-wide re-file count | `GET /api/library/refile/estimate` — press-driven, never polled (it plans every downloaded release, which hashes each file behind the staleness gate); computed against the **candidate** template being typed, not the saved one; `frozen` and `blocked` releases are reported separately rather than folded into the headline |
| **Metadata source reordering** (▲▼), including the gated-rung notice | `enrichment_sources` + `GET /api/enrichment`'s `source_status` |
| **Artist tag editing** and the re-tag pass behind *Save & re-tag* | `PATCH /api/artists/{id}/tags`, `POST /api/artists/{id}/retag` |
| **Artist aliases** in the Edit-tags drawer | `PATCH /api/artists/{id}/tags` → `aliases`, read back as `ArtistOut.aliases`; stored only, never written to a tag or an NFO |
| Identify's **identification-progress band** — the automatic / needs-you / waiting-on-an-input split, and the review-scope filters beside it (including the dismissals) | `GET /api/enrichment` → `autonomy` (a partition of the entities on disk, bucketed by `review_picker_for()` itself so the band, the list and the nav badge cannot disagree), `GET /api/enrichment/review?state=…`, `POST /api/enrichment/{type}/{id}/reopen` |
| Identify's **Accept**, **Reject** and the candidate picker | `POST /api/enrichment/{type}/{id}/{accept,reject,identify}`, `GET …/candidates` |
| The **command bar** (⌘K): catalogue search, follow, scan, enrich, verify, re-tag, re-file | `GET /api/search` plus the five mutation endpoints |
| The **Hi-res** figure in the artist grid header | `GET /api/stats` → `quality.hires_share`, over the releases *measured* rather than all of them |
| The **disk-capacity meter**, `4.26 TB / 6.0 TB` (838–840) | `GET /api/status` → `status.disk` (one `statvfs` on `LIBRARY_PATH`, `null` when unprobeable); the library's own bytes are `status.library.size_bytes`, a `SUM` over `tracks.file_size` and never a disk walk |
| **Re-identify** on the release drawer (the design's "Re-fingerprint", 519) | `POST /api/albums/{id}/reidentify` — re-opens every album enrichment rung and drains the cascade, so AcoustID fingerprints the files again; 400 for a release that is not on disk |
| The **Tag mapping** table — Vorbis field / resolved from / on conflict (741–753) | `GET /api/meta` → `tag_map` + `consensus_rule`, derived from `tagger.VORBIS_FIELDS`, `nfo.ENRICHMENT_TAGS` and `merge.WRITABLE_FIELDS` — never hand-written. *Resolved from* names the **column** the value is read from, not the rung that wrote it: which rung matched is a per-release fact (`mb_match_method`), shown on the album detail and Identify screens. *On conflict* is `qobuz` / `enrichment` / `never` / `derived` / `consensus`, and `consensus` matches no row today because `WRITABLE_FIELDS` holds `release_type`, an `Album` column rather than a tag key. |

---

## The placeholders

| What the design draws | Where it appears | What the backend would need | Rough size |
|---|---|---|---|
| "24 collaborations without symlinks" (1219) | Dashboard ▸ To do | A secondary-credit model. A release has one `artist_id`; nothing records a guest or joint credit, so there is nothing to count and no pass to run. | large |
| The whole **Collaborations** section (296–317) | Library ▸ artist view | The same secondary-credit model, plus the symlinks the design describes. Qobuzarr never creates a symlink; files live once, under one credit. | large |
| "Freeze folder structure" as an artist switch (1268) | Library ▸ Artist settings drawer | An artist-level freeze. `freeze_path` exists **per release** and is wired on the release panel; there is no artist-level column, and faking one by writing every album would be a different action from the one the switch names. | medium |
| **AcoustID** id and **Confidence** rows (451–457) | Library ▸ release drawer | A per-release fingerprint id, and a match confidence. Neither exists: identification here is exact or nothing, and there is deliberately no score anywhere in the application. | medium |

---

## Dropped rather than placeheld

A caption that asserts something false is **rewritten**, not placeheld, and a clause about a
field that does not exist is removed. These are the ones a reviewer will notice missing:

- **The auto-accept threshold is dropped rather than placeheld.** The band (599–605) keeps its
  geometry and now carries the honest split — how much of the library was identified with
  nobody involved — because there is no confidence to threshold and a bar that auto-accepted
  above it would be the "guess dressed as a rule" `coverage.solve_album()` refuses to
  contain; the value it accepted is written into every file as `MUSICBRAINZ_ALBUMID`. What
  stands where the slider's value sat is a choice that is real: which subset of the review
  list is shown. The two neighbouring rows — **AcoustID id and Confidence** (451–457) — stay
  placeholders, because a per-release fingerprint id genuinely has no field.
- **`Announced` (a pre-order) is not a release state.** The radar draws
  `Downloading` / `Imported` / `Skipped` from real queue states (`active` / `done` /
  `cancelled`+`failed`); there is nothing in the API that models an unreleased record.
- **Per-album byte size** (`{{ a.size }}`, 290, 434, 550) has no field on `AlbumOut`. The
  clause is gone from the album tile, the release drawer header and the radar row.
- **A scan progress percentage** does not exist. `LibraryScanStatusOut` publishes `running`
  and nothing else, so the dashboard's scan job draws an *indeterminate* bar — a 0% bar reads
  as a job that has stalled.
- **SHA-256, checksum manifests and tamper quarantine** (302, 1203, 1352–1355, `HEALTH`) are
  rewritten wherever they appear. Qobuzarr hashes with blake2b-128 plus an audio sample count,
  re-hashes a fixed slice nightly, and its verdicts are `verified` / `retagged` / `replaced` /
  `missing` / never-baselined. `TAMPERED` is never rendered.
- **"the MusicBrainz discography"** (325) is the Qobuz discography, and the artist grid's
  subtitle no longer claims releases are identified by AcoustID fingerprint — AcoustID is the
  first rung of five, and most releases are matched by barcode.
- **"grabbed automatically"** (142) and **"auto-download"** (393, 582) are rewritten:
  `auto_download` defaults to false, so monitoring marks releases wanted and queues nothing.
- **The design's three extra library chips** — `Hi-res ≥ 24/88.2`, `Integrity flags`,
  `Below threshold` — are not drawn. `useArtists` supports `q` and `monitored` only; the first
  two are album-level facts with no artist-level roll-up and the third needs the confidence
  score that does not exist. A chip that filters nothing is worse than a missing chip. *A
  server-side artist filter for "has a flagged release" would buy the first two.* The
  library-wide roll-up added for the header figure (`StatsOut.quality`) does not help here: it
  is one scalar over the whole library and these chips filter a per-artist list, so the first
  two still want a server-side artist filter on `GET /api/artists`.
- **The artist cell's amber `⚑ {n}`** (264) needs a per-artist count of flagged releases;
  `ArtistOut` carries `album_count`, `wanted_count` and `downloaded_count` and no integrity
  roll-up.
- **The symlink `CodeBlock`s under the canonical path** (483–487) are not rendered. The design
  itself renders zero of them, and this program makes no symlinks.
- **`{bits}` and `{rate}` are not naming tokens.** `app/core/naming.py` supplies `{quality}`,
  which carries both; the token list offers only what the renderer knows, because an unknown
  token renders literally into a folder name. `{track:02}` is really `{track:02d}`.
- **Discogs is not a metadata source this application has.** The ladder is
  `acoustid · deezer · musicbrainz · coverartarchive · wikidata`, read from the payload.
- **The enrichment tick has no `running` flag.** `EnrichmentStatusOut` publishes `last_run_at`
  and `paused_reason` but not whether a pass is in flight, so the dashboard's "Running now"
  block cannot show an enrichment job. *A `running: bool` on that payload would complete the
  block.*

---

## Structural notes

**The nav's `rail` variant was not built.** `navLayout` is a DC prop with two options
(`sidebar`, design 58–74, and `rail`, 43–54). Only the sidebar exists: the rail is an authoring
affordance for the design tool, there is nowhere in the running application to switch to it, and
a toggle that changes nothing is worse than a missing one. `web/src/shell/nav.ts` holds the IA
as data, so a rail would be a second renderer over the same array.

**Banners were added below the TopBar, and the design draws none.** `GET /api/banners` returns
real "not wired up" notices (`no_credentials`, `no_client`, `no_app_secret`, `breaker_open`).
They had no drawn home, and a Qobuzarr with no credentials that says nothing is the worst screen
in the application, so they are a stack directly under the header in the design's own voice and
status colours. `AppShell` renders them from the `status` payload it already polls rather
than from `useBanners()`, so one data set stays behind one key.

**The Activity group chips filter the loaded page, not the whole history.** `list_activity`
matches `Activity.event == event` *exactly*, while the design's `Integrity` / `Writes` / `Grabs`
are groups spanning seven or eight event names each — no single request can produce one. So the
screen issues one unfiltered query at `LIMITS.activity` (100 rows) and the chip partitions what
came back, client-side, by the event's first dotted segment. The chip is therefore **not** part
of the query key: putting it there would run four identical requests for one data set. It is in
the URL, so a poll cannot reset it. The screen says which it searched, in a quiet mono line, on
every filtered view. *A server-side event-group filter — `?group=integrity` over a prefix — would
let these chips search the whole history.*

**A search box was added to the Library screen.** The design has none, but `useArtists` accepts
`q` and the alternative on a library of 1,300 artists is scrolling. It lives in the URL like
every other filter, debounced 300 ms so the query key does not change per keystroke.

**The command bar is reachable from the Release radar.** `+ Follow artist` (535) opens the ⌘K
palette rather than a second search box, through a small context the shell provides
(`useCommandBar`). Two boxes over one endpoint is two caches of one data set, and there is no
Add-artist screen any more.

**The header's Follow/Following button says Monitoring/Monitor.** An artist reached through the
Library screen is already followed — the row exists — so a button labelled "Following" would
change something other than what it names. The switch that does something is `monitored`.
