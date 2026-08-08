/* Test fixtures for the wire types.
 *
 * Every factory returns a COMPLETE object and then applies the overrides, so a
 * test says only what it is about. That matters more here than usual: the
 * three-valued fields (`complete`, `tracks_on_disk`, `upgrade_format_id`,
 * `integrity_state`) have to be *present and null* rather than absent, because
 * "the server did not send this key" and "the server said nothing has measured
 * it" are different claims and only one of them is legal. A partial fixture
 * would let a component pass a test by reading `undefined` where production
 * hands it `null`.
 */

import type {
  ActivityOut,
  AlbumOut,
  ArtistOut,
  QueueItemOut,
  SettingsOut,
  TrackOut,
} from '@/api/types'

export function makeArtist(overrides: Partial<ArtistOut> = {}): ArtistOut {
  return {
    id: 'artist-1',
    name: 'Miles Davis',
    monitored: true,
    monitor_mode: 'all',
    quality_profile: 'best',
    accepted_release_types: ['album', 'ep'],
    include_guest_appearances: false,
    credit_filter: [],
    image_url: null,
    albums_count: 12,
    last_checked_at: '2026-08-01T10:00:00Z',
    added_at: '2026-01-01T10:00:00Z',
    qobuz_slug: null,
    album_count: 12,
    wanted_count: 3,
    downloaded_count: 9,
    isni: null,
    mb_artist_mbid: null,
    deezer_artist_id: null,
    wikidata_qid: null,
    sort_name: null,
    aliases: [],
    disambiguation: null,
    artist_type: null,
    country: null,
    area: null,
    formed: null,
    disbanded: null,
    genres: [],
    bio: null,
    bio_source_url: null,
    bio_licence: null,
    portrait_url: null,
    external_links: [],
    enriched: false,
    ...overrides,
  }
}

export function makeAlbum(overrides: Partial<AlbumOut> = {}): AlbumOut {
  return {
    id: 'uyej1o165e870',
    artist_id: 'artist-1',
    title: 'Kind of Blue',
    version: null,
    release_date: '1959-08-17',
    release_type: 'album',
    tracks_count: 5,
    media_count: 1,
    hires: false,
    max_bit_depth: 24,
    max_sampling_rate: 96,
    label: 'Columbia',
    genre: 'Jazz',
    upc: null,
    image_url: null,
    duration: 2652,
    status: 'wanted',
    monitored: true,
    path: null,
    downloaded_at: null,
    added_at: '2026-01-02T10:00:00Z',
    artist_name: 'Miles Davis',
    year: 1959,
    tracks: null,
    owned_format_id: null,
    owned_hires: null,
    upgrade_format_id: null,
    queue_state: null,
    barcode: null,
    mb_release_mbid: null,
    mb_release_group_mbid: null,
    deezer_album_id: null,
    catalog_number: null,
    mb_country: null,
    genres: [],
    cover_url: null,
    suggested_release_type: null,
    release_type_disagrees: false,
    qobuz_release_type: null,
    release_group_key: 'kind of blue',
    enriched: false,
    tracks_on_disk: null,
    complete: null,
    adopted: false,
    integrity_state: null,
    corrupt_tracks: 0,
    pin_tags: false,
    freeze_path: false,
    mute_integrity: false,
    // Present and null, not absent: "nothing has measured whose release this
    // is" is a real state the server sends, and a component that reads
    // `undefined` here would pass a test production fails.
    guest_appearance: null,
    credit_names: null,
    ...overrides,
  }
}

export function makeTrack(overrides: Partial<TrackOut> = {}): TrackOut {
  return {
    id: 'track-1',
    album_id: 'uyej1o165e870',
    title: 'So What',
    version: null,
    track_number: 1,
    media_number: 1,
    duration: 545,
    isrc: null,
    performer: null,
    composer: null,
    status: 'downloaded',
    origin: 'download',
    path: null,
    format_id: 27,
    bit_depth: 24,
    sampling_rate: 96,
    file_size: null,
    downloaded_at: null,
    integrity: null,
    ...overrides,
  }
}

export function makeQueueItem(overrides: Partial<QueueItemOut> = {}): QueueItemOut {
  return {
    id: 1,
    album_id: 'uyej1o165e870',
    state: 'pending',
    priority: 0,
    attempts: 1,
    last_error: null,
    progress_tracks_done: 0,
    progress_tracks_total: 5,
    created_at: '2026-08-01T10:00:00Z',
    started_at: null,
    finished_at: null,
    album_title: 'Kind of Blue',
    album_image_url: null,
    album_monitored: true,
    artist_id: 'artist-1',
    artist_name: 'Miles Davis',
    progress_percent: 0,
    ...overrides,
  }
}

export function makeActivity(overrides: Partial<ActivityOut> = {}): ActivityOut {
  return {
    id: 1,
    level: 'info',
    event: 'queue.done',
    message: 'Downloaded Kind of Blue',
    artist_id: 'artist-1',
    album_id: 'uyej1o165e870',
    created_at: '2026-08-01T10:00:00Z',
    artist_name: 'Miles Davis',
    album_title: 'Kind of Blue',
    ...overrides,
  }
}

/**
 * The effective configuration, as `GET /api/settings` sends it.
 *
 * Two things are deliberate. `auto_download` is **false**, because that is the
 * shipped default and the safety property the Settings screen exists to state —
 * a fixture that turned it on would let the opt-in copy rot untested. And
 * `enrichment_contact` is the string `"set"`, never an address: the payload is
 * redacted server-side (§6.16), so a fixture carrying a real-looking value would
 * be testing something the API cannot produce.
 */
export function makeSettings(overrides: Partial<SettingsOut> = {}): SettingsOut {
  return {
    app_name: 'Qobuzarr',
    app_version: '0.4.0',

    library_path: '/srv/music',
    data_path: '/srv/qobuzarr/data',
    trash_path: '/srv/qobuzarr/data/trash',
    default_format_id: 27,
    default_format_label: 'FLAC 24bit ≤192kHz',
    naming_template:
      '{artist}/{album} ({year})[ [{quality}]]/{disc_prefix}{track:02d} - {title}.{ext}',
    naming_preview: [
      'Test Artist/Test Album (2024) [FLAC 24-96]/01 - Movement 1.flac',
      'Test Artist/Test Album (2024) [FLAC 24-96]/02 - Movement 2.flac',
    ],
    default_monitor_mode: 'all',
    default_quality_profile: 'default',
    default_accepted_release_types: ['album', 'ep'],
    qobuz_min_request_interval: 2,
    qobuz_max_requests_per_hour: 1200,
    indexer_artist_interval: 300,
    indexer_full_sweep_hours: 6,
    indexer_enabled: true,
    auto_index_on_follow: true,
    auto_download: false,
    download_track_delay: 3,
    download_concurrency: 1,
    download_max_attempts: 3,
    upgrade_cleanup: true,
    library_scan_nightly: true,
    library_scan_complete_ratio: 1,

    integrity_enabled: true,
    integrity_reverify_fraction: 0.0333,

    host: '127.0.0.1',
    port: 8000,
    log_level: 'INFO',
    qobuz_app_id: '123456789',
    credentials_ok: true,
    app_secret_source: 'cache',

    enrichment_enabled: true,
    enrichment_sources: ['acoustid', 'deezer', 'musicbrainz', 'coverartarchive', 'wikidata'],
    enrichment_consensus_threshold: 0.51,
    enrichment_apply_release_type: true,
    enrichment_interval: 300,
    enrichment_batch_size: 25,
    enrichment_refresh_days: 90,
    enrichment_contact: 'set',
    musicbrainz_ready: true,
    acoustid_ready: false,
    enrichment_write_back: true,
    enrichment_prefer_external_cover: false,
    nfo_enabled: true,

    // The overlay. Every allowlisted key reports an origin — always all of them,
    // so a screen can render "from .env" beside an untouched switch — and
    // `pending` is empty, which is the normal state: a write that could not take
    // effect immediately is the exception, and a fixture that pre-filled it
    // would make the honest case the untested one.
    overridable: [
      'naming_template',
      'enrichment_sources',
      'upgrade_cleanup',
      'library_scan_nightly',
      'integrity_enabled',
      'enrichment_write_back',
      'nfo_enabled',
    ],
    origins: {
      naming_template: 'env',
      enrichment_sources: 'env',
      upgrade_cleanup: 'env',
      library_scan_nightly: 'env',
      integrity_enabled: 'env',
      enrichment_write_back: 'env',
      nfo_enabled: 'env',
    },
    pending: [],
    ...overrides,
  }
}
