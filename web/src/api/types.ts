/**
 * Wire types for the Qobuzarr JSON API — a hand-kept mirror of
 * `app/schemas.py`, one interface per Pydantic model, in the same order.
 *
 * Three domain rules are encoded here and must not be softened:
 *
 * 1. **Ids are strings.** `uyej1o165e870`, `0884977859300`. Every entity id is
 *    `string`; the *only* number id in the API is `QueueItemOut.id`. Never
 *    `parseInt` an album, artist or track id — several of them are barcodes and
 *    several are not numeric at all.
 * 2. **`null` is an answer, not an absence.** `AlbumOut.complete`,
 *    `tracks_on_disk`, `upgrade_format_id` and `integrity_state` are all
 *    three-valued: `null` means *nothing has measured this yet*, which renders
 *    differently from `false`/`0`/"clean". Fields are therefore declared
 *    required-and-nullable (`T | null`), never optional (`T?`) — FastAPI
 *    serialises defaults, so the key is always present, and `?` would let a
 *    meaningful `null` be confused with a key the server did not send.
 * 3. **Enum vocabularies live on the server.** The unions below mirror
 *    `app/models.py`, but `GET /api/meta` is the runtime source of truth for
 *    anything rendered as a `<select>` or a label map. Iterate `MetaOut`, do not
 *    hard-code a list of statuses in a component.
 */

// ---------------------------------------------------------------------------
// Enum vocabularies (app/models.py, app/core/integrity.py, app/enrich/errors.py)
// ---------------------------------------------------------------------------

/** How aggressively an artist's back catalogue is pursued. */
export type MonitorMode = 'all' | 'future' | 'none'

/** Lifecycle of a release inside Qobuzarr. */
export type AlbumStatus =
  | 'skipped'
  | 'wanted'
  | 'queued'
  | 'downloading'
  | 'downloaded'
  | 'failed'

/** Lifecycle of one track file. */
export type TrackStatus =
  | 'pending'
  | 'downloading'
  | 'downloaded'
  | 'failed'
  | 'skipped'

/**
 * Which half of Qobuzarr wrote a track row. Stated, never inferred: the disk
 * scan writes a row for every file it finds, so "a track row exists" no longer
 * means "Qobuzarr downloaded this".
 */
export type TrackOrigin = 'download' | 'scan'

/** State of a download-queue entry. */
export type QueueState = 'pending' | 'active' | 'done' | 'failed' | 'cancelled'

/** Severity of an activity-log line. */
export type ActivityLevel = 'debug' | 'info' | 'warning' | 'error'

/** Normalised release types Qobuzarr filters on. */
export type ReleaseType =
  | 'album'
  | 'ep'
  | 'single'
  | 'live'
  | 'compilation'
  | 'download'
  | 'other'

/** The enrichment ladder's vocabulary. The configured order is a *setting*. */
export type EnrichmentSource =
  | 'deezer'
  | 'musicbrainz'
  | 'acoustid'
  | 'coverartarchive'
  | 'wikidata'

/** What an enrichment work row is about. Tracks are never fetched on their own. */
export type EnrichmentEntity = 'artist' | 'album'

/**
 * Per (entity, source) fetch bookkeeping. `ambiguous` and `no_key` are the two
 * that reach a person; `not_found` is waiting rather than deciding and is
 * deliberately kept off the review list.
 *
 * `rejected` is the only value no matcher can produce — a person pressed
 * Reject. It is terminal in the strongest sense: `no_key` and `gated` also stop
 * retrying, but the server watches for the inputs they wait on and re-arms
 * them, and nothing re-arms this one. Off the review list and out of the nav
 * badge by default; ask for `?state=rejected` to find one, and
 * `POST /api/enrichment/{type}/{id}/reopen` to change your mind.
 */
export type EnrichmentStateValue =
  | 'pending'
  | 'ok'
  | 'not_found'
  | 'ambiguous'
  | 'no_key'
  | 'gated'
  | 'failed'
  | 'rejected'

/** What Chromaprint made of a file. Only `corrupt` is actionable. */
export type FingerprintState = 'ok' | 'corrupt' | 'unreadable'

/**
 * Where an overridable setting's live value came from. The database wins over
 * `.env` — the user pressed the switch more recently than they edited a file —
 * so this is the only thing that says *why* a value is what it is, and it is
 * what "Reset to .env" is drawn from.
 *
 * `'override'` means the value being *read* came from the overlay, not merely
 * that a row exists: a stored row this build can no longer parse is inert and
 * reports `'env'`, because that is where the live value came from.
 */
export type SettingOrigin = 'env' | 'override'

/**
 * What the disk says about one file relative to what was recorded. `unknown`
 * means *never baselined* — emphatically not a claim that the file was
 * tampered with.
 */
export type IntegrityState =
  | 'unknown'
  | 'verified'
  | 'retagged'
  | 'replaced'
  | 'missing'

/**
 * A release's rolled-up verdict. `mixed` when its files disagree; `null` (see
 * `AlbumOut.integrity_state`) when no file carries a baseline at all.
 */
export type AlbumIntegrityState = IntegrityState | 'mixed'

/** How loudly a client should announce the result of a mutation. */
export type MessageLevel = 'info' | 'success' | 'warning' | 'error'

/** Banners never say "success" — there is nothing to celebrate about a warning. */
export type BannerLevel = 'info' | 'warning' | 'error'

/** Sort direction shared by every list envelope. */
export type SortOrder = 'asc' | 'desc'

/** What `ArtistBulkUpdateIn.release_types` means. */
export type ReleaseTypesAction = 'set' | 'add' | 'remove'

// ---------------------------------------------------------------------------
// Library entities
// ---------------------------------------------------------------------------

/** A followed artist. */
export interface ArtistOut {
  id: string
  name: string
  monitored: boolean
  monitor_mode: MonitorMode
  quality_profile: string
  accepted_release_types: string[]
  /** Whether releases this artist only *guests* on count as theirs. Default
   *  `false` — Qobuz files a guest appearance under the guest, so following
   *  Joe Bonamassa otherwise offers six Black Country Communion albums as
   *  though he had made them. */
  include_guest_appearances: boolean
  /** Accepted credit names, when a person has narrowed a shared Qobuz artist id
   *  to the person they actually follow. Empty means **no filter**, which is
   *  the default — there is no unmeasured state here. */
  credit_filter: string[]
  image_url: string | null
  albums_count: number
  last_checked_at: string | null
  added_at: string | null
  qobuz_slug: string | null

  /** Roll-ups. `null` means the router did not populate them — render nothing,
   *  not a zero. */
  album_count: number | null
  wanted_count: number | null
  downloaded_count: number | null

  // --- enrichment, merged in at read time from `artist_metadata` -----------
  isni: string | null
  mb_artist_mbid: string | null
  deezer_artist_id: string | null
  wikidata_qid: string | null
  /** Coalesced, not merely derived: a sort name typed through
   *  `PATCH /api/artists/{id}/tags` wins over MusicBrainz's, so the edit drawer
   *  shows back what was saved rather than what the next pass would say. */
  sort_name: string | null
  /** What a person typed as alternative spellings, in that order. No coalesce:
   *  MusicBrainz's aliases are read live by the matcher and never stored, so
   *  the empty list genuinely means nobody typed any — not "unmeasured". */
  aliases: string[]
  disambiguation: string | null
  artist_type: string | null
  country: string | null
  area: string | null
  /** A *string*: these dates are legitimately partial (`"1975"`, `"1975-06"`). */
  formed: string | null
  disbanded: string | null
  genres: string[]
  bio: string | null
  /** Wikipedia text is CC BY-SA. `bio`, `bio_source_url` and `bio_licence`
   *  travel as a unit — rendering the text without both is a licence breach. */
  bio_source_url: string | null
  bio_licence: string | null
  portrait_url: string | null
  external_links: ExternalLink[]
  enriched: boolean
}

/** `[{label, url}]` for MusicBrainz, Wikipedia, Deezer and the artist's site. */
export interface ExternalLink {
  label: string
  url: string
}

/**
 * What has actually been measured about one file. Every field is a *recorded*
 * observation — a GET never re-measures, because hashing is ~201 ms of blocking
 * disk I/O per file.
 */
export interface TrackIntegrityOut {
  /** Stable local identity, minted once and never reissued. Survives a re-file,
   *  a re-tag or a re-match; the row's `id` cannot. */
  qid: string
  /** Only ever `unknown`, `verified` or `missing` here: `retagged`/`replaced`
   *  are verdicts a *pass* gives, and the pass re-baselines in the same
   *  transaction. Those two live on `IntegrityReportOut`. */
  state: IntegrityState
  content_hash: string | null
  sample_count: number | null
  file_size: number | null
  /** POSIX mtime as a float, exactly as `os.stat` reports it — a number, not a
   *  timestamp, because it has no timezone to be asked about. */
  file_mtime: number | null
  last_verified_at: string | null
  fingerprint_state: FingerprintState | null
}

/** A single track of a release. */
export interface TrackOut {
  id: string
  album_id: string
  title: string
  version: string | null
  track_number: number
  media_number: number
  duration: number | null
  isrc: string | null
  performer: string | null
  composer: string | null
  status: TrackStatus
  origin: TrackOrigin
  path: string | null
  format_id: number | null
  bit_depth: number | null
  sampling_rate: number | null
  file_size: number | null
  downloaded_at: string | null
  /** `null` when the caller did not ask for it — built only where the track
   *  list itself is built. */
  integrity: TrackIntegrityOut | null
}

/** A release belonging to a followed artist. `id` is a non-numeric string. */
export interface AlbumOut {
  id: string
  artist_id: string
  title: string
  version: string | null
  release_date: string | null
  release_type: ReleaseType
  tracks_count: number
  media_count: number
  hires: boolean
  max_bit_depth: number | null
  max_sampling_rate: number | null
  label: string | null
  genre: string | null
  upc: string | null
  image_url: string | null
  duration: number | null
  status: AlbumStatus
  monitored: boolean
  path: string | null
  downloaded_at: string | null
  added_at: string | null

  artist_name: string | null
  year: number | null
  /** `null` when the caller did not ask for tracks. An *empty array* is a
   *  different claim: the release has no track rows. */
  tracks: TrackOut[] | null

  /** Worst format we hold, or `null` when nothing is on disk. */
  owned_format_id: number | null
  /** Is the copy **on disk** hi-res? `null` when nothing is on disk. Not to be
   *  confused with `hires`, which is the *catalogue's* availability flag — a
   *  16/44.1 copy of a hi-res listing is `hires: true, owned_hires: false`, and
   *  a badge that reads the first calls a CD-quality file hi-res. Server-derived
   *  from `owned_format_id`; never re-derive it here. */
  owned_hires: boolean | null
  /** The format a fresh download would land, when that is strictly better.
   *  `null` on a `downloaded` album is a deliberate answer — render **no
   *  button**, not a disabled one. Never re-derive this arithmetic in TS. */
  upgrade_format_id: number | null
  /** "Is this in flight?" — `status` cannot answer it, because an album being
   *  *upgraded* stays `downloaded` for the whole download. */
  queue_state: QueueState | null

  // --- enrichment ---------------------------------------------------------
  barcode: string | null
  mb_release_mbid: string | null
  mb_release_group_mbid: string | null
  deezer_album_id: string | null
  catalog_number: string | null
  mb_country: string | null
  genres: string[]
  cover_url: string | null
  suggested_release_type: string | null
  release_type_disagrees: boolean
  qobuz_release_type: string | null
  /** The album's *own* key. Link to `/api/release-groups/{key}` with it and let
   *  the server resolve the record — never group by comparing keys in TS. */
  release_group_key: string | null
  enriched: boolean

  // --- completeness (three-valued) ----------------------------------------
  /** `null` when it cannot be known. Not zero: zero would show a complete
   *  album as empty. */
  tracks_on_disk: number | null
  /** `null` when nothing has counted this release yet. Render *unknown*, never
   *  0% and never a full bar. */
  complete: boolean | null

  /** True when no track row came from the download loop. Read alongside
   *  `status`/`tracks_on_disk`: a catalogue release nobody downloaded trivially
   *  has no download-origin rows either. */
  adopted: boolean

  // --- integrity ----------------------------------------------------------
  /** `null` when **no** track carries a baseline — "not measured", never a
   *  clean bill of health. */
  integrity_state: AlbumIntegrityState | null
  /** Reported whatever `mute_integrity` says: muting hides the alarm, never the
   *  measurement, so the muted release is exactly the one whose own page has to
   *  keep saying what was found. */
  corrupt_tracks: number

  // --- per-release switches -----------------------------------------------
  /** Enrichment's background write-back leaves this release's file tags alone.
   *  The NFO is still merged, and the explicit re-tag buttons still work. */
  pin_tags: boolean
  /** Nothing re-files this release's folder. */
  freeze_path: boolean
  /** No quarantine and no place in the Integrity screen's actionable count.
   *  Does not stop the fingerprinting or clear a verdict. */
  mute_integrity: boolean

  // --- attribution ---------------------------------------------------------
  /** Whether the owning artist only guests on this release. Three-valued:
   *  `null` is *no Qobuz payload has said yet*, and it decides nothing — show
   *  "not measured", never assert the artist is the main credit. */
  guest_appearance: boolean | null
  /** Qualified credit names on this release's tracks, or `null` when nobody has
   *  analysed it. `[]` is *analysed, and every credit was the bare artist
   *  name* — a real answer, which is why this is not `string[]`. */
  credit_names: string[] | null
}

// ---------------------------------------------------------------------------
// Queue and activity
// ---------------------------------------------------------------------------

/** One entry in the sequential download queue. The one numeric id in the API. */
export interface QueueItemOut {
  id: number
  album_id: string
  state: QueueState
  priority: number
  attempts: number
  last_error: string | null
  progress_tracks_done: number
  progress_tracks_total: number
  created_at: string | null
  started_at: string | null
  finished_at: string | null

  album_title: string | null
  album_image_url: string | null
  /** Whether the *release* is monitored. Unrelated to `state`: ignoring an
   *  album does not cancel a download already running. */
  album_monitored: boolean
  artist_id: string | null
  artist_name: string | null
  progress_percent: number
}

/** One line of the activity/history feed. */
export interface ActivityOut {
  id: number
  level: ActivityLevel
  event: string
  message: string
  artist_id: string | null
  album_id: string | null
  created_at: string | null
  artist_name: string | null
  album_title: string | null
}

// ---------------------------------------------------------------------------
// Search
// ---------------------------------------------------------------------------

/** An artist hit from `catalog/search`, plus whether we already follow it. */
export interface SearchArtistOut {
  id: string
  name: string
  image_url: string | null
  albums_count: number
  slug: string | null
  followed: boolean
}

/** An album hit from `catalog/search`. */
export interface SearchAlbumOut {
  id: string
  title: string
  version: string | null
  artist_id: string | null
  artist_name: string | null
  release_date: string | null
  year: number | null
  tracks_count: number
  hires: boolean
  image_url: string | null
  /** Files on disk. **Not** the same as `tracked`: the indexer writes a row for
   *  every release a followed artist has, so conflating the two marks the whole
   *  wanted backlog as owned. They render mutually exclusively. */
  in_library: boolean
  /** Qobuzarr has a row for this release — followed, and wanted or ignored. */
  tracked: boolean
}

/** Combined result of a catalogue search. */
export interface SearchResultOut {
  query: string
  artists: SearchArtistOut[]
  albums: SearchAlbumOut[]
  total_artists: number
  total_albums: number
  limit: number
  offset: number
}

// ---------------------------------------------------------------------------
// Status / dashboard
// ---------------------------------------------------------------------------

/** Current state of the one global Qobuz rate limiter. */
export interface RateLimitStatusOut {
  min_request_interval: number
  max_requests_per_hour: number
  requests_last_hour: number
  budget_remaining: number
  budget_used_percent: number
  seconds_until_next_slot: number
  last_request_at: string | null
  circuit_open: boolean
  circuit_open_until: string | null
  recent_429s: number
}

/** Current state of the slow background indexer. */
export interface IndexerStatusOut {
  enabled: boolean
  running: boolean
  paused: boolean
  artist_interval_seconds: number
  full_sweep_hours: number
  next_run_at: string | null
  seconds_until_next_run: number | null
  current_artist_id: string | null
  current_artist_name: string | null
  last_run_at: string | null
  monitored_artists: number
  artists_never_checked: number
}

/** Counts per queue state plus what the worker is doing right now. */
export interface QueueStatsOut {
  pending: number
  active: number
  done: number
  failed: number
  cancelled: number
  total: number
  current_album_id: string | null
  current_album_title: string | null
  worker_running: boolean
}

/**
 * Headline counts. `wanted_albums` is `wanted + queued + downloading` — a
 * different number from `NavCountsOut.wanted`, and both are correct. Do not
 * conflate them and do not invent a fourth definition.
 */
export interface LibraryStatsOut {
  artists: number
  monitored_artists: number
  albums: number
  wanted_albums: number
  downloaded_albums: number
  failed_albums: number
  tracks: number
  downloaded_tracks: number
  /**
   * Bytes of audio this LIBRARY holds — `SUM(tracks.file_size)` over releases
   * whose status is `DOWNLOADED`, never a directory walk. `null` when no track
   * carries a size, which is "nothing has measured this" and not zero.
   *
   * Not the disk's used bytes: see `DiskCapacityOut`. Only that one says
   * whether another discography will fit.
   */
  size_bytes: number | null
}

/**
 * One "something is not wired up" notice. Route on `code`, render `message` —
 * the prose is written to be read and changes when it reads badly, so matching
 * on it makes the sentence an interface nobody can edit.
 */
export interface BannerOut {
  level: BannerLevel
  title: string
  message: string
  /** `no_credentials` / `no_client` / `no_app_secret` / `breaker_open`. */
  code: string
}

/** Everything the shell needs in a single payload. */
/**
 * The volume `LIBRARY_PATH` sits on — one `statvfs`, no directory walk.
 *
 * `StatusOut.disk` is `null` as a whole when the path could not be probed; the
 * fields are never individually unknown. `used_bytes + free_bytes` can be less
 * than `total_bytes` (root-reserved blocks), so the meter's denominator is
 * `total_bytes` and the fraction is `used_bytes / total_bytes`.
 */
export interface DiskCapacityOut {
  path: string
  total_bytes: number
  used_bytes: number
  free_bytes: number
}

export interface StatusOut {
  version: string
  started_at: string | null
  uptime_seconds: number
  library: LibraryStatsOut
  queue: QueueStatsOut
  indexer: IndexerStatusOut
  /** `null` when no limiter is wired up — a half-started process, not an
   *  unlimited one. */
  rate_limit: RateLimitStatusOut | null
  recent_activity: ActivityOut[]
  credentials_ok: boolean
  app_secret_ok: boolean
  library_path: string
  /** `null` when the path is missing or unprobeable — never a zeroed record. */
  disk: DiskCapacityOut | null
  banners: BannerOut[]
}

/**
 * The hi-res roll-up over the releases on disk.
 *
 * `measured` is the denominator, **not** `albums`: a release whose held format
 * cannot be determined has not been found to be lossy, and counting it as one
 * would report a 24/96 rip with unreadable tags as a CD. Both are published so
 * the gap can be named — see `hiresCaption` in `screens/library/coverage.ts`.
 *
 * `hires_share` is `null`, never `0`, when nothing has been measured. Render it
 * through `fmtPercent`, which answers `EM_DASH` for a null.
 */
export interface LibraryQualityOut {
  albums: number
  measured: number
  hires: number
  hires_share: number | null
}

/** The compact counters. Same numbers as `StatusOut`, no activity, no banners. */
export interface StatsOut {
  library: LibraryStatsOut
  /** Zero-filled for every `AlbumStatus`: it drives a bar list, and a missing
   *  key would render a missing bar rather than an empty one. */
  albums_by_status: Record<string, number>
  queue: QueueStatsOut
  indexer: IndexerStatusOut
  rate_limit: RateLimitStatusOut | null
  /** The hi-res roll-up. Not on `LibraryStatsOut`, which `StatusOut` embeds
   *  and polls three times as often. */
  quality: LibraryQualityOut
}

/** Result of the database probe. `error` is present only when it failed. */
export interface DatabaseHealthOut {
  ok: boolean
  /** WAL in a healthy install; `delete` mode is what a mysteriously slow UI
   *  during a download looks like. */
  journal_mode: string | null
  error: string | null
}

/** The unprefixed liveness probe. Missing credentials are **not** degradation. */
export interface HealthOut {
  status: 'ok' | 'degraded'
  version: string
  database: DatabaseHealthOut
  credentials_ok: boolean
  app_secret_ok: boolean
  qobuz_client: boolean
  indexer_enabled: boolean
}

// ---------------------------------------------------------------------------
// Library scan / import / tidy
// ---------------------------------------------------------------------------

/** One album folder the disk scan reported on. Every field beyond `path` is
 *  optional in practice — the same shape serves three different lists. */
export interface ScannedFolderOut {
  path: string
  artist: string
  title: string
  year: number | null
  files: number
  discs: number
  bytes: number
  /** Album folders rolled into this row (unknown-artist rows only). */
  albums: number
  /** Artist name as it appears on disk (unknown-artist rows only). */
  name: string
  artist_id: string | null
  album_id: string | null
  /** Track count Qobuz reports for the release (partial rows only). */
  expected: number | null
  status: string | null
}

/** Result of one disk scan. */
export interface LibraryScanOut {
  root: string
  started_at: string | null
  duration_seconds: number
  /** False for a dry run: everything was measured, nothing was written. */
  applied: boolean
  directories: number
  audio_files: number
  albums_found: number
  albums_matched: number
  albums_excluded: number
  albums_adopted: number
  albums_already: number
  albums_partial: number
  albums_busy: number
  tracks_linked: number
  queue_items_cancelled: number
  /** The scan started following the unknown artists it found, in the
   *  background. The cue to start polling `GET /api/library/import` — the scan
   *  request cannot wait for it, because 136 artists is one search each. */
  import_started: boolean

  // --- integrity: the only place the REPLACED verdict ever surfaces --------
  files_measured: number
  /** Different bytes, provably the same audio — only the baseline needed
   *  refreshing. */
  files_retagged: number
  /** Different audio under the same filename. Everything the database believed
   *  about those files was decided about bytes that are gone. */
  files_replaced: number
  /** Releases put back on the *enrichment* work list. Nothing is queued for
   *  download. */
  albums_reopened: number
  /** Files on disk holding no playable audio, usually zero-byte. Marked
   *  `fingerprint_state=corrupt` so they reach the Integrity screen. Nothing is
   *  trashed for it — quarantine is an explicit press. */
  files_corrupt: number
  /** Files that carried a corruption verdict and parse again, so it was
   *  cleared. Without it the alarm never goes back down. */
  files_recovered: number

  partial: ScannedFolderOut[]
  unmatched: ScannedFolderOut[]
  unknown_artists: ScannedFolderOut[]
  truncated: Record<string, number>
  errors: string[]
  summary: string
  level: MessageLevel
}

/** Whether a scan is running, plus the most recent stored report. */
export interface LibraryScanStatusOut {
  running: boolean
  library_path: string
  nightly: boolean
  complete_ratio: number
  /** `null` means nothing has ever been scanned — a distinct claim from "the
   *  last scan found nothing". */
  last: LibraryScanOut | null
}

/** One Qobuz search hit offered as a manual choice for an unresolved name. */
export interface ImportCandidateOut {
  id: string
  name: string
  albums_count: number
  image_url: string | null
}

/** An artist folder the importer could not resolve on its own. */
export interface ImportReviewOut {
  name: string
  albums: number
  files: number
  path: string
  /** `no-exact-match` or `not-found`. */
  reason: string
  candidates: ImportCandidateOut[]
}

/** Progress of an artist import. */
export interface LibraryImportOut {
  running: boolean
  cancelled: boolean
  started_at: string | null
  finished_at: string | null
  total: number
  processed: number
  remaining: number
  percent: number
  followed: number
  already_followed: number
  needs_review: number
  not_found: number
  failed: number
  current: string
  index_now: boolean
  review: ImportReviewOut[]
  truncated_review: number
  errors: string[]
  aborted_reason: string
  eta_seconds: number
  summary: string
}

/** Who a bulk import would look up, and roughly how long it would take. */
export interface LibraryImportPreviewOut {
  count: number
  /** Artist folder names as they appear on disk, in look-up order. */
  names: string[]
  eta_seconds: number
}

/** One batch in the trash. */
export interface TrashEntryOut {
  id: string
  trashed_at: string | null
  /** Where it came from, and where *Restore* would put it back. */
  original_path: string
  reason: string
  album_id: string | null
  album_title: string | null
  artist_name: string | null
  file_count: number
  size_bytes: number
}

/** Everything currently recoverable, newest first. */
export interface TrashOut {
  entries: TrashEntryOut[]
  total: number
  size_bytes: number
  /** `Settings.trash_dir` — worth showing, it may be on another disk. */
  path: string
}

/** How much is in the trash, without listing it. Same counts, same listing. */
export interface TrashSummaryOut {
  total: number
  size_bytes: number
  path: string
}

/** What re-filing one release would do. Nothing has happened yet. */
export interface RefilePlanOut {
  album_id: string
  album_title: string
  artist_name: string
  current_dir: string
  target_dir: string
  /** `[from, to]` pairs. */
  renames: [string, string][]
  moves_directory: boolean
  /** Why it cannot be applied. `null` means it can. */
  blocked: string | null
}

/** Result of a re-file, re-tag or NFO pass over one or many releases. */
export interface LibraryTidyOut {
  dry_run: boolean
  considered: number
  /** Releases whose *tags* changed, or that a re-file preview would move. */
  changed: number
  /**
   * Folders actually renamed. Zero on every pass that did not re-file, and
   * counted apart from `changed` because an artist re-tag can do both in one
   * request: adding the two would report each release twice.
   */
  moved: number
  /**
   * Releases whose `album.nfo`/`artist.nfo` was written. Also counted apart —
   * and it is where a typed `sort_name` lands, so a client reporting only
   * `changed` would say nothing happened.
   */
  described: number
  blocked: number
  failed: number
  /** Populated by re-file; the preview *is* this list. */
  plans: RefilePlanOut[]
  errors: string[]
  summary: string
  level: MessageLevel
}

/** How many releases a naming template would disturb, library-wide. */
export interface RefileEstimateOut {
  /** The template the figures were computed against — echoed back so a client
   *  can tell a stale answer from a fresh one without trusting its own state. */
  template: string
  considered: number
  would_refile: number
  /** A subset of `would_refile`: releases whose folder itself moves, rather
   *  than only having files renamed inside the folder they already occupy. */
  moves_directory: number
  in_place: number
  blocked: number
  /** Releases with `freeze_path` set. Reported rather than folded into the
   *  headline — a release somebody froze is not a release that would move. */
  frozen: number
  /** True when the walk hit `limit`: the figures are a floor, not a total. */
  truncated: boolean
  summary: string
  level: MessageLevel
}

/** Options for starting an artist import. */
export interface LibraryImportStartIn {
  /** Explicit names; a disk scan supplies them when omitted. */
  names?: string[]
  monitored?: boolean
  monitor_mode?: MonitorMode
  quality_profile?: string
  release_types?: string[]
  /** Expensive — following 500 artists with this on is tens of thousands of
   *  calls. Off by default. */
  index_now?: boolean
  limit?: number
}

// ---------------------------------------------------------------------------
// Settings
// ---------------------------------------------------------------------------

/**
 * Read-only view of the effective configuration. No secret ever appears here:
 * `enrichment_contact` is reduced to `"set"`/`""`, and the auth token, the app
 * secret and signed URLs are absent by construction.
 */
export interface SettingsOut {
  app_name: string
  app_version: string

  library_path: string
  data_path: string
  trash_path: string
  default_format_id: number
  default_format_label: string
  naming_template: string
  /** **An empty list is meaningful**: the template could not be rendered.
   *  Render that, not a blank box. */
  naming_preview: string[]
  default_monitor_mode: string
  default_quality_profile: string
  default_accepted_release_types: string[]
  qobuz_min_request_interval: number
  qobuz_max_requests_per_hour: number
  indexer_artist_interval: number
  indexer_full_sweep_hours: number
  indexer_enabled: boolean
  auto_index_on_follow: boolean
  /** The opt-in rule's visible half. False — the default — means indexing marks
   *  releases wanted and queues nothing. */
  auto_download: boolean
  download_track_delay: number
  download_concurrency: number
  download_max_attempts: number
  /** Whether a *complete* upgrade trashes the folder it superseded. The upgrade
   *  confirm copy depends on it. */
  upgrade_cleanup: boolean
  library_scan_nightly: boolean
  library_scan_complete_ratio: number

  integrity_enabled: boolean
  integrity_reverify_fraction: number

  host: string
  port: number
  log_level: string
  qobuz_app_id: string
  credentials_ok: boolean
  app_secret_source: string

  enrichment_enabled: boolean
  /** The ladder, in the order it runs. */
  enrichment_sources: string[]
  enrichment_consensus_threshold: number
  enrichment_apply_release_type: boolean
  enrichment_interval: number
  enrichment_batch_size: number
  enrichment_refresh_days: number
  /** `"set"` or `""` — never the address itself. */
  enrichment_contact: string
  musicbrainz_ready: boolean
  acoustid_ready: boolean
  enrichment_write_back: boolean
  enrichment_prefer_external_cover: boolean
  nfo_enabled: boolean

  /** The keys `PATCH /api/settings` accepts, in display order. Everything else
   *  on this payload is environment-only: render it read-only rather than
   *  keeping a second copy of this list, which would go stale into a control
   *  whose write is a 400. */
  overridable: string[]
  /** `key -> 'env' | 'override'` for every key in `overridable`, always all of
   *  them. The database wins over `.env`, so this is the only thing that says
   *  *why* a value is what it is — and it is what "Reset to .env" is drawn
   *  from. */
  origins: Record<string, SettingOrigin>
  /** Overridden keys whose stored value is not yet the one the running process
   *  uses. Normally empty; `enrichment_sources` lands here when the ladder
   *  could not be rebuilt on the spot and takes effect on the next enrichment
   *  tick. A control that cannot take effect immediately has to say so. */
  pending: string[]
}

// ---------------------------------------------------------------------------
// Requests
// ---------------------------------------------------------------------------

/**
 * A partial write of the overridable settings.
 *
 * Two verbs, and they are different writes: `values` sets an override, `reset`
 * deletes the row so the value returns to whatever `.env` says. A key in
 * neither list is left alone; a key outside `SettingsOut.overridable` is a
 * **400**, never a silent no-op.
 *
 * A switch may send `true` or `"true"`; the enrichment ladder may be a list (in
 * ladder order) or a comma-separated string.
 */
export interface SettingsUpdateIn {
  values?: Record<string, string | boolean | string[]>
  reset?: string[]
}

/** Payload for following a new artist. Idempotent — following twice updates. */
export interface ArtistCreateIn {
  artist_id: string
  name?: string
  monitored?: boolean
  monitor_mode?: MonitorMode
  quality_profile?: string
  accepted_release_types?: string[]
  search_now?: boolean
}

/**
 * Partial update of one artist. Omission means "leave alone".
 *
 * The single-artist form is the *opposite* of the bulk one: an unchecked
 * `monitored` box genuinely means `false`, and `accepted_release_types` is
 * always sent as a list (possibly empty). Do not copy either rule onto the
 * other form.
 */
export interface ArtistUpdateIn {
  monitored?: boolean
  monitor_mode?: MonitorMode
  quality_profile?: string
  accepted_release_types?: string[]
  /** Want releases this artist only guests on. Here rather than beside the
   *  credit filter because it is what the others are: a field `desired_status`
   *  reads, so changing it moves the backlog already on file. */
  include_guest_appearances?: boolean
}

/**
 * Hand-edited identity for one artist — `PATCH /api/artists/{id}/tags`.
 *
 * Deliberately not part of `ArtistUpdateIn`: these name every folder the artist
 * owns and are written into every file of theirs on the next re-tag.
 * Omitted means **leave alone**. `sort_name: ''` is a real value and clears the
 * override back to MusicBrainz's; `name: ''` and `mb_artist_mbid: ''` are 400s,
 * not clears. The server still **forbids unknown keys** (422) — that refusal is
 * why `aliases` waited for a column on `artists` rather than being accepted and
 * dropped.
 *
 * `mb_artist_mbid` accepts a musicbrainz.org link as well as a bare id, and is
 * recorded as a *manual* match so no automatic pass overwrites it.
 */
export interface ArtistTagsIn {
  name?: string
  sort_name?: string
  /** Alternative spellings, one per entry, order kept. Omitted leaves them
   *  alone; `[]` is a real value and clears the list. Blanks and duplicates are
   *  dropped server-side and an entry over 512 characters is a 400. Recorded
   *  and read back and nothing else — no tag, no NFO, no automatic match. */
  aliases?: string[]
  mb_artist_mbid?: string
}

/**
 * One set of monitoring changes applied to many artists.
 *
 * Every field is optional and **omitted means leave alone** — a bulk `<select>`
 * left on "no change" must be omitted, never sent as `false`. Ticked release
 * types do nothing unless `release_types_action` is chosen.
 */
export interface ArtistBulkUpdateIn {
  artist_ids: string[]
  monitored?: boolean
  monitor_mode?: MonitorMode
  /** Tri-state, like every other field here: leaving the control alone must
   *  send **no key**, never `false`. */
  include_guest_appearances?: boolean
  release_types?: string[]
  release_types_action?: ReleaseTypesAction
}

/**
 * One credited person under a Qobuz artist id — a row in the credit picker.
 *
 * A Qobuz artist id is not always one artist: id `322476` "Boaz" holds releases
 * by at least eleven people, all stamped `main-artist: 322476`, and Deezer
 * merges them identically. The track credits are what tell them apart.
 */
export interface CreditClusterOut {
  /** The credit exactly as Qobuz spells it, e.g. `"Boaz Roelevink"`. */
  name: string
  releases: number
  accepted: boolean
  /** A few release titles carrying it, newest first — what makes the name
   *  recognisable to somebody who knows the music but not the credit. */
  sample_titles: string[]
}

/** Every credit found under one artist id — `GET/POST /api/artists/{id}/credits`. */
export interface ArtistCreditsOut {
  artist_id: string
  artist_name: string
  /** Releases whose credits have been read. `analysed < total` means the pass
   *  is unfinished, **not** that the remainder carry no credits — POST again. */
  analysed: number
  total: number
  /** Analysed releases whose every credit was the bare artist name. Refused
   *  while a filter is on; the per-release monitor toggle is the way back. */
  unattributed: number
  filter_active: boolean
  clusters: CreditClusterOut[]
}

/**
 * The credits a person accepts as this artist — `PUT /api/artists/{id}/credit-filter`.
 *
 * `PUT` and not `PATCH`: the list is replaced wholesale, because that is what
 * the screen shows. An **empty list clears the filter** — it does not mean
 * "accept nothing", which is what `monitor_mode: 'none'` already says.
 */
export interface ArtistCreditFilterIn {
  credits: string[]
}

/** Partial update of one release. Every key is optional and an **omitted** key
 *  means *leave alone* — a tri-state control must never serialise its untouched
 *  state as `false`, or it turns three switches off.
 *
 *  Two routes take this body and read `monitored` differently:
 *  `PATCH /albums/{id}` is the partial update above; `POST /albums/{id}/monitor`
 *  **toggles** `monitored` when the body is empty, and refuses (400) a body
 *  carrying any of the three switches — set those through the PATCH. */
export interface AlbumUpdateIn {
  monitored?: boolean
  status?: AlbumStatus
  pin_tags?: boolean
  freeze_path?: boolean
  mute_integrity?: boolean
}

/** A human saying "it is this one" — the escape hatch from the review list. */
export interface EnrichmentIdentifyIn {
  source: string
  /** The upstream id, **or a link containing it**: a pasted browser URL is what
   *  somebody looking at the record actually has. */
  external_id: string
}

// ---------------------------------------------------------------------------
// Enrichment
// ---------------------------------------------------------------------------

/** One entity the matchers refused to resolve. */
export interface EnrichmentReviewOut {
  entity_type: EnrichmentEntity
  entity_id: string
  source: EnrichmentSource
  state: EnrichmentStateValue
  name: string
  artist_id: string | null
  artist_name: string | null
  reason: string | null
  attempts: number
  last_attempt_at: string | null
  /** False for `not_found`: waiting on the upstream, not on a decision. */
  is_actionable: boolean
  /** Which sources this row can be handed an id for — **empty is the whole
   *  answer** for a row nobody can act on. Never re-derive this set. */
  identify_sources: string[]
  /** One server-authored sentence saying what this state means. Domain
   *  knowledge, not copy — do not hard-code it in a component. */
  state_explanation: string
  /** What **Accept** would apply to this row, or `null`.
   *
   *  Non-null: the server is holding a proposal (a release type the sources
   *  agreed on that this album is not carrying) and Accept writes it. `null`:
   *  it is holding nothing and Accept opens the identify picker. Label the
   *  button from this — Accept never searches, so "Accept & apply" over a
   *  `null` row would be describing the wrong action. Always `null` for artist
   *  rows. */
  suggested_release_type: string | null
}

/**
 * A person saying "there is no answer here" about one work item.
 *
 * `source` is required: a work item is one *(entity, source)* pair, so
 * rejecting MusicBrainz for a release says nothing about Deezer. 404 if the row
 * is gone, 400 if it is in a state nobody is being asked about (only `ambiguous`
 * and `no_key` can be rejected; re-rejecting is a no-op, not an error).
 */
export interface EnrichmentRejectIn {
  source: EnrichmentSource
  /** Why, in the person's own words. Stored where the matcher's reason was. */
  reason?: string | null
}

/**
 * What `POST /api/enrichment/{type}/{id}/accept` did.
 *
 * `applied: false` is a **success**, not a failure: it means nothing was held
 * for this entity and `identify_sources` names the pickers to open instead.
 * Accept applies a stored proposal or reports there is none — it never searches
 * an upstream and takes a hit, which is the rule that keeps a wrong MusicBrainz
 * id out of every file on disk.
 */
export interface EnrichmentAcceptOut {
  entity_type: EnrichmentEntity
  entity_id: string
  applied: boolean
  /** Which held proposal was applied — `'release_type'`, or `null`. */
  proposal: string | null
  /** What the field held before and holds now. Both `null` when nothing was
   *  applied, so a client cannot render a change that did not happen. */
  previous_value: string | null
  applied_value: string | null
  /** Pickers to offer when there was nothing to apply. */
  identify_sources: string[]
  message: string
  level: MessageLevel
}

/** One rung of the ladder: whether it runs, and if not, what it waits for. */
export interface EnrichmentSourceStatusOut {
  name: string
  /** Present in the configured ladder. */
  enabled: boolean
  /** Enabled **and** not gated. Not the same as `enabled`. */
  ready: boolean
  /** The missing thing, named exactly as `.env` spells it: `ACOUSTID_API_KEY`,
   *  `ENRICHMENT_CONTACT`, or the binary `fpcalc`. */
  gated_on: string | null
  gated_reason: string | null
  identifiable: boolean
  /** `{enrichment_state: count}` for this source, over the library only. */
  states: Record<string, number>
}

/**
 * How much identification is happening without a person — a **partition**, not
 * a score. The five buckets sum to `entities`, and there is deliberately no
 * confidence and no threshold: nothing here may be compared against a bar.
 */
export interface EnrichmentAutonomyOut {
  /** `scope.albums + scope.artists` — the honest denominator, never the catalogue. */
  entities: number
  /** At least one rung answered `ok` and nothing makes a stronger claim. */
  automatic: number
  /** A decision is available to a human — the review list's own rule. */
  waiting_person: number
  /** Waiting on an input (a gated rung, an id another rung supplies), not on anybody. */
  waiting_input: number
  /** A person said no. The one state no matcher can produce. */
  dismissed: number
  /** Seeded but not attempted, or not seeded yet. */
  unstarted: number
}

/** Coverage and health for the enrichment screen. */
export interface EnrichmentStatusOut {
  enabled: boolean
  sources: string[]
  paused_reason: string | null
  last_run_at: string | null
  last_result: Record<string, unknown> | null
  /** The flat pair this has always carried. `source_status` is the same
   *  information with the *reason* attached — read that instead. */
  gates: Record<string, boolean>
  source_status: EnrichmentSourceStatusOut[]
  /** `{source: {state: count}}`, over the library only. */
  states: Record<string, Record<string, number>>
  /** `albums`/`artists` on disk with `catalogue_albums`/`catalogue_artists`
   *  beside them. Show **both** — a percentage against the catalogue reports a
   *  fully enriched library as 1%. */
  scope: Record<string, number>
  /** The same library counted by **entity** and by who the work waits on.
   *  `states` counts rows per source and cannot be shared out of a denominator
   *  of albums; this can. */
  autonomy: EnrichmentAutonomyOut
  review_total: number
}

/** One upstream record offered for a person to pick. */
export interface EnrichmentCandidateOut {
  external_id: string
  title: string
  subtitle: string | null
  detail: string | null
  disambiguation: string | null
  image_url: string | null
  url: string | null
}

/** Candidates for one entity on one source, with the query that found them. */
export interface EnrichmentCandidatesOut {
  entity_type: string
  entity_id: string
  source: string
  /** Echoed back so the box can show it and it can be edited. */
  query: string
  items: EnrichmentCandidateOut[]
}

// ---------------------------------------------------------------------------
// Generic responses and list envelopes
// ---------------------------------------------------------------------------

/**
 * Generic acknowledgement returned by every mutating endpoint.
 *
 * `ok` says the request was honoured; `level` says how it went, and only the
 * server has the counts that answer depends on (deleting a release and emptying
 * the trash are both `ok: true` and neither is good news).
 */
export interface MessageOut {
  ok: boolean
  message: string
  level: MessageLevel
  detail: Record<string, unknown> | null
}

/** Pagination envelope inherited by every list endpoint. */
export interface PageOut {
  total: number
  limit: number
  offset: number
  sort: string | null
  order: SortOrder
  /** The same count with the *filters* dropped — the difference between "no
   *  release matches that search" and "this artist has no releases". `null`
   *  means the endpoint takes no filters. */
  unfiltered_total: number | null
}

export interface ArtistListOut extends PageOut {
  items: ArtistOut[]
}

export interface AlbumListOut extends PageOut {
  items: AlbumOut[]
}

/** The backlog, plus the number *Download all* would actually queue. */
export interface WantedListOut extends AlbumListOut {
  /** **Not `total`.** `POST /api/wanted/download` queues every *monitored*
   *  `wanted` release and ignores this list's filters, so a button labelled
   *  from `total` promises one download and delivers the whole backlog.
   *  Invariant: this number does not move when `q`/`status`/`monitored` do. */
  queueable_total: number
}

export interface QueueListOut extends PageOut {
  items: QueueItemOut[]
}

export interface EnrichmentReviewListOut extends PageOut {
  items: EnrichmentReviewOut[]
}

export interface ActivityListOut extends PageOut {
  items: ActivityOut[]
}

/** One artist plus their known releases. */
export interface ArtistDetailOut extends ArtistOut {
  albums: AlbumOut[]
}

/** The artist a follow request produced, and whether it was new. */
export interface ArtistFollowOut extends ArtistOut {
  /** False when the artist was already followed and this request updated them.
   *  Drives "Now following X." vs "X was already followed." — it cannot be
   *  derived afterwards, because a second press looks identical to the first. */
  created: boolean
}

// ---------------------------------------------------------------------------
// Shell payloads
// ---------------------------------------------------------------------------

/**
 * The five sidebar badges.
 *
 * `wanted` is `(wanted + failed)` **and** monitored — the backlog you could act
 * on. That is deliberately not `LibraryStatsOut.wanted_albums`
 * (`wanted + queued + downloading`). Both are real; keep them apart.
 */
export interface NavCountsOut {
  artists: number
  wanted: number
  queue: number
  trash: number
  enrichment_review: number
}

/**
 * Every enum vocabulary and label map the client renders. Cache it forever — it
 * changes only on deploy. Nothing downstream may keep its own copy.
 */
export interface MetaOut {
  app_name: string
  app_version: string
  release_types: string[]
  monitor_modes: string[]
  album_statuses: string[]
  queue_states: string[]
  activity_levels: string[]
  track_statuses: string[]
  track_origins: string[]
  /** The whole vocabulary, not the configured ladder — that is
   *  `SettingsOut.enrichment_sources`. */
  enrichment_sources: string[]
  enrichment_entities: string[]
  enrichment_states: string[]
  /** The subset of `enrichment_states` the review list is *about*, and therefore
   *  the ones `reject` accepts. A rule, not a vocabulary, which is why it is
   *  fetched rather than typed here: a client copy that drifts is a filter tab
   *  offering a state the endpoint refuses. Read it from `useMeta()`. */
  review_states: string[]
  identifiable_sources: string[]
  integrity_states: string[]
  fingerprint_states: string[]
  /** `env` / `override` — the two answers `SettingsOut.origins` gives. Sent
   *  because `origins` is a `Record<string, string>` on the wire, so this pair
   *  never becomes an OpenAPI enum and a hand-typed copy would be the one union
   *  the wire-contract test cannot police. */
  setting_origins: string[]
  /** `{"7": "FLAC 24bit 96kHz"}` — keys are stringified `format_id`s, because
   *  JSON object keys are strings and pretending otherwise gives the client an
   *  integer-keyed map it cannot actually have. */
  format_labels: Record<string, string>
  /** Every Vorbis field the tagger writes, in write order — derived server-side
   *  from `tagger.VORBIS_FIELDS`, `nfo.ENRICHMENT_TAGS` and
   *  `merge.WRITABLE_FIELDS`. There is deliberately **no** client-side fallback
   *  copy: a local table is exactly the drift this replaces. */
  tag_map: TagMapRowOut[]
  /** The >51% rule, stated once instead of once per `tag_map` row. */
  consensus_rule: ConsensusRuleOut
}

/**
 * One row of the Structure & tags mapping table.
 *
 * `source_field` names the **column** a value is read from, not the rung that
 * wrote it: which rung matched is a per-release fact (`mb_match_method`), shown
 * on the album and Identify screens, and a per-field constant claiming otherwise
 * would be wrong the moment the ladder is reordered — which this very screen
 * lets you do.
 */
export interface TagMapRowOut {
  vorbis_field: string
  tag_key: string
  /** What an MP3 gets for the same value. `null` means MP3 files carry nothing
   *  for it — a real answer, not a gap. */
  id3_frame: string | null
  origin: 'qobuz' | 'enrichment' | 'derived'
  source_field: string
  /** `consensus` matches no row today: `WRITABLE_FIELDS` holds `release_type`,
   *  an `Album` column rather than a tag key. It lights up by itself the day a
   *  tag key enters that set. */
  conflict: 'qobuz' | 'enrichment' | 'consensus' | 'never' | 'derived'
  /** Authored server-side, like the gate reasons: the rule is the server's own
   *  and a client that re-implements ">51%" drifts from it silently. */
  conflict_note: string
}

/** `merge.consensus()`'s rule, published once. */
export interface ConsensusRuleOut {
  threshold: number
  tie_keeps: string
  writable_fields: string[]
  never_writable: string[]
  note: string
}

// ---------------------------------------------------------------------------
// Consensus and release groups
// ---------------------------------------------------------------------------

/** What one source said about one field. */
export interface ConsensusVoteOut {
  source: string
  value: unknown
}

/**
 * The vote record for one field. `value` is `null` when no majority formed —
 * sources disagreed and nothing was written, which is what the "no majority"
 * chip renders. `majority` is **not** `agreed / total > 0.5`: a joint-first
 * placing is a tie however many sources voted, and the tie test is the
 * matcher's to make.
 */
export interface ConsensusFieldOut {
  value: unknown
  agreed: number
  total: number
  share: number
  majority: boolean
  votes: ConsensusVoteOut[]
}

/** Which record a release belongs to, and what identified it. */
export interface ReleaseGroupRefOut {
  /** A MusicBrainz release-group MBID when known, the normalised title
   *  otherwise. Both kinds round-trip through `GET /api/release-groups/{key}`. */
  key: string
  title: string
  /** `null` means the grouping is by normalised title — nothing has identified
   *  this record yet. Say that rather than showing a bare key. */
  mb_release_group_mbid: string | null
}

/** Every edition of one record, best first. Keying is **server-side**. */
export interface ReleaseGroupOut extends ReleaseGroupRefOut {
  artist_id: string | null
  artist_name: string | null
  total: number
  /** Ordered by `indexer.edition_rank` — track count before audio quality, so a
   *  one-track hi-res promo never outranks the album it was cut from.
   *  `editions[0]` is "best" only because the server sorted it. */
  editions: AlbumOut[]
}

/** One release with everything its own screen needs, in one request. */
export interface AlbumDetailOut {
  album: AlbumOut
  release_group: ReleaseGroupRefOut
  /** Includes the subject. One edition means there is nothing to show. */
  editions: AlbumOut[]
  consensus: Record<string, ConsensusFieldOut>
}

/**
 * One artist's release roll-up, per status.
 *
 * `counts` is zero-filled for every `AlbumStatus` — those labels drive a status
 * `<select>`, and an option whose count is absent renders as a hole.
 * `wanted_count` here is `wanted + queued`, the third of the three definitions
 * in this codebase. Stated rather than derived so nobody invents a fourth.
 */
export interface ArtistStatsOut {
  artist: ArtistOut
  counts: Record<string, number>
  total_albums: number
  wanted_count: number
  downloaded_count: number
  /** `downloaded / total`, or `null` when the artist has no releases at all —
   *  a ratio of nothing is not zero. */
  on_disk_ratio: number | null
}

// ---------------------------------------------------------------------------
// Integrity
// ---------------------------------------------------------------------------

/**
 * What one verification pass found. `states` counts every track by the verdict
 * it was given **before** anything was written — the only moment those counts
 * mean anything, and the only place `retagged`/`replaced` are ever visible.
 */
export interface IntegrityReportOut {
  checked: number
  hashed: number
  baselined: number
  albums_restamped: number
  /** Enrichment, never the download queue. */
  albums_reopened: number
  elapsed: number
  /** False for a dry run: measured, nothing written. */
  applied: boolean
  states: Record<string, number>
  finished_at: string | null
}

/**
 * The Integrity screen's landing payload.
 *
 * Two histograms, two questions: `states` is what the **rows** record (so only
 * `unknown`/`verified`/`missing`), `last_result.states` is what the last
 * **pass** saw (where `retagged`/`replaced` appear). `never_baselined` is not a
 * fault — until something measures a file the database makes no claim about it.
 */
export interface IntegrityStatusOut {
  enabled: boolean
  reverify_fraction: number
  /** Rows that claim to have a file. The denominator for everything else. */
  tracks_total: number
  states: Record<string, number>
  never_baselined: number
  /** The only actionable verdict in this payload — and therefore *excluding*
   *  releases with `mute_integrity` set, which the quarantine skips. A figure
   *  the button will not act on is an offer to do nothing. */
  corrupt_files: number
  /** Unplayable files on releases the user has muted. Published rather than
   *  swallowed: a suppression with no trace anywhere reads exactly like a check
   *  that never ran. Zero until somebody presses the switch. */
  corrupt_muted: number
  albums_reopened: number
  last_run_at: string | null
  last_result: IntegrityReportOut | null
  /** Passes do not overlap; a second request is a 409, not a queue. */
  running: boolean
}

/** Acknowledgement of a verification pass, with the report it produced. */
export interface IntegrityRunOut extends MessageOut {
  report: IntegrityReportOut
}

/** One file the fingerprinter could not decode, named well enough to act on. */
export interface CorruptTrackOut {
  track_id: string
  qid: string
  title: string
  album_id: string
  album_title: string
  artist_id: string | null
  artist_name: string | null
  path: string
  fingerprint_state: string
  last_attempt_at: string | null
}

/** Everything currently recorded as unplayable, oldest verdict first. */
export interface CorruptListOut extends PageOut {
  items: CorruptTrackOut[]
}

// ---------------------------------------------------------------------------
// Health overview
// ---------------------------------------------------------------------------

/**
 * Every subsystem's own payload, gathered once. Nothing here is recomputed —
 * each field is the identical model the sub-endpoint returns, so the overview
 * cannot disagree with the page it links to. Adding a field means adding it to
 * the sub-endpoint, never here.
 */
export interface HealthSummaryOut {
  status: StatusOut
  scan: LibraryScanStatusOut
  /** `library_import`, not `import` — that is a reserved word on one side of
   *  the wire and an artefact on the other. */
  library_import: LibraryImportOut
  enrichment: EnrichmentStatusOut
  integrity: IntegrityStatusOut
  trash: TrashSummaryOut
}

// ---------------------------------------------------------------------------
// Error envelope
// ---------------------------------------------------------------------------

/** One entry of FastAPI's 422 body. */
export interface ValidationErrorItem {
  loc: (string | number)[]
  msg: string
  type: string
}

/**
 * The uniform error envelope. `error` is **always prose** and is what a screen
 * shows verbatim; `detail` is present only when there is more to say — a dict
 * for a structured refusal (`routes_api.ErrorDetail`), a list for a 422.
 */
export interface ErrorEnvelope {
  ok: false
  error: string
  status_code?: number
  detail?: Record<string, unknown> | ValidationErrorItem[] | string
}
