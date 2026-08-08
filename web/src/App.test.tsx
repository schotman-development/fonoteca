/**
 * The whole application, mounted the way the browser mounts it.
 *
 * THIS TEST EXISTS BECAUSE THE APP ONCE SHIPPED A BLANK PAGE WITH 587 TESTS
 * PASSING, and nothing in the suite could have caught it.
 *
 * `TopBar` calls `useToast()` — the scan button acknowledges its press — and
 * `useToast()` throws by design outside a `ToastProvider`, because a toast that
 * goes nowhere is a mutation nobody was told about. The provider was mounted
 * nowhere at all: `AppShell` rendered `ToastHost`, the outlet, without the
 * context that feeds it. So the first render of the first screen threw, React
 * unmounted the tree, and the browser showed white.
 *
 * Every other test in this suite mounts through `@/test/providers`, which wraps
 * its children in `ToastProvider`. They therefore proved that every screen works
 * *inside a correct frame* and said nothing whatever about whether the real
 * frame was correct. The bug lived in the seam between the two, which no
 * component test can see.
 *
 * So this file deliberately does NOT use `TestProviders`. It mounts `<App/>`
 * under nothing but a QueryClient and a router — exactly what `main.tsx` does —
 * and walks every address in the route table. A missing provider, a hook called
 * above the context that serves it, a screen that throws on its first paint with
 * real-shaped data: all of them fail here and only here.
 *
 * The payloads below are deliberately realistic rather than minimal. A screen
 * that renders `PageError` because its fixture was the wrong shape is a screen
 * this test did not actually check.
 */

import { render, screen } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { MemoryRouter } from 'react-router-dom'
import { afterEach, describe, expect, it, vi } from 'vitest'

import {
  makeActivity,
  makeAlbum,
  makeArtist,
  makeQueueItem,
  makeSettings,
  makeTrack,
} from '@/test/factories'

const artist = makeArtist({ id: 'a1', name: 'Mark Knopfler' })
const album = makeAlbum({ id: 'uyej1o165e870', artist_id: 'a1', title: 'Sailing to Philadelphia' })

const LIBRARY = {
  artists: 5,
  monitored_artists: 4,
  albums: 20,
  wanted_albums: 4,
  downloaded_albums: 16,
  failed_albums: 1,
  tracks: 100,
  downloaded_tracks: 90,
}

const QUEUE_STATS = {
  pending: 1,
  active: 1,
  done: 3,
  failed: 0,
  cancelled: 0,
  total: 5,
  current_album_id: album.id,
  current_album_title: album.title,
  worker_running: true,
}

const INDEXER = {
  enabled: true,
  running: false,
  paused: false,
  artist_interval_seconds: 60,
  full_sweep_hours: 6,
  next_run_at: null,
  seconds_until_next_run: null,
  current_artist_id: null,
  current_artist_name: null,
  last_run_at: null,
  monitored_artists: 4,
  artists_never_checked: 0,
}

const page = (path: string): unknown => {
  if (path === '/status')
    return {
      version: '0.4.0',
      started_at: null,
      uptime_seconds: 1,
      library: LIBRARY,
      queue: QUEUE_STATS,
      indexer: INDEXER,
      rate_limit: null,
      recent_activity: [],
      credentials_ok: true,
      app_secret_ok: true,
      library_path: '/srv/music',
      banners: [],
    }
  if (path === '/nav-counts')
    return { artists: 5, wanted: 4, queue: 2, trash: 0, enrichment_review: 7 }
  if (path === '/banners') return []
  if (path === '/meta')
    return {
      app_name: 'Qobuzarr',
      app_version: '0.4.0',
      release_types: ['album', 'ep', 'single', 'live', 'compilation', 'download', 'other'],
      monitor_modes: ['all', 'future', 'none'],
      album_statuses: ['skipped', 'wanted', 'queued', 'downloading', 'downloaded', 'failed'],
      queue_states: ['pending', 'active', 'done', 'failed', 'cancelled'],
      activity_levels: ['debug', 'info', 'warning', 'error'],
      track_statuses: ['pending', 'downloading', 'downloaded', 'failed', 'skipped'],
      track_origins: ['download', 'scan'],
      enrichment_sources: ['acoustid', 'deezer', 'musicbrainz', 'coverartarchive', 'wikidata'],
      enrichment_entities: ['artist', 'album'],
      enrichment_states: [
        'pending', 'ok', 'not_found', 'ambiguous', 'no_key', 'gated', 'failed', 'rejected',
      ],
      review_states: ['ambiguous', 'no_key', 'gated', 'failed'],
      identifiable_sources: ['musicbrainz', 'deezer'],
      integrity_states: ['unknown', 'verified', 'retagged', 'replaced', 'missing'],
      fingerprint_states: ['ok', 'corrupt', 'unreadable'],
      setting_origins: ['env', 'override'],
      format_labels: {
        '5': 'MP3 320',
        '6': 'FLAC 16bit 44.1kHz',
        '7': 'FLAC 24bit 96kHz',
        '27': 'FLAC 24bit 192kHz',
      },
    }
  if (path === '/settings') return makeSettings()
  if (path === '/stats')
    return {
      library: LIBRARY,
      albums_by_status: { skipped: 1, wanted: 3, queued: 0, downloading: 0, downloaded: 16, failed: 1 },
      queue: QUEUE_STATS,
      indexer: INDEXER,
      rate_limit: null,
    }
  if (path === '/integrity')
    return {
      enabled: true,
      reverify_fraction: 0.033,
      tracks_total: 1000,
      states: { verified: 800, unknown: 200 },
      never_baselined: 200,
      corrupt_files: 3,
      corrupt_muted: 1,
      albums_reopened: 0,
      last_run_at: '2026-08-04T08:00:00Z',
      // `retagged`/`replaced` live ONLY here — the top-level `states` reports
      // them as a literal 0, because a pass re-baselines whatever it classifies.
      last_result: {
        checked: 100, hashed: 30, baselined: 30, albums_restamped: 2, albums_reopened: 1,
        elapsed: 12, applied: true,
        states: { verified: 27, retagged: 2, replaced: 1 },
        finished_at: '2026-08-04T08:00:00Z',
      },
      running: false,
    }
  if (path === '/library/scan')
    return { running: false, library_path: '/srv/music', nightly: true, complete_ratio: 0.9, last: null }
  if (path === '/enrichment')
    return {
      enabled: true,
      sources: ['acoustid', 'deezer', 'musicbrainz'],
      paused_reason: null,
      last_run_at: null,
      last_result: null,
      gates: {},
      source_status: [
        { name: 'acoustid', enabled: true, ready: false, gated_on: 'ACOUSTID_API_KEY', gated_reason: 'no key', identifiable: false, states: { ok: 10 } },
        { name: 'deezer', enabled: true, ready: true, gated_on: null, gated_reason: null, identifiable: true, states: { ok: 14 } },
        { name: 'musicbrainz', enabled: true, ready: true, gated_on: null, gated_reason: null, identifiable: true, states: { ok: 12, ambiguous: 4 } },
      ],
      states: { musicbrainz: { ok: 12, ambiguous: 4 }, deezer: { ok: 14 } },
      scope: { albums: 16, artists: 5, catalogue_albums: 20, catalogue_artists: 5 },
      review_total: 7,
    }
  if (path === '/enrichment/review')
    return {
      items: [
        {
          entity_type: 'album', entity_id: album.id, source: 'musicbrainz', state: 'ambiguous',
          name: album.title, artist_id: 'a1', artist_name: 'Mark Knopfler',
          reason: 'two releases carry this barcode', attempts: 2,
          last_attempt_at: '2026-08-03T10:00:00Z', is_actionable: true,
          identify_sources: ['musicbrainz'],
          state_explanation: 'More than one upstream release matched; a person has to choose.',
          suggested_release_type: 'album',
        },
      ],
      total: 1, limit: 100, offset: 0, sort: null, order: 'desc', unfiltered_total: 1,
    }
  if (path === '/artists')
    return { items: [artist], total: 1, limit: 1000, offset: 0, sort: 'name', order: 'asc', unfiltered_total: 1 }
  if (path === '/artists/a1') return { ...artist, albums: [album] }
  if (path === '/artists/a1/stats')
    return {
      artist,
      counts: { skipped: 0, wanted: 1, queued: 0, downloading: 0, downloaded: 3, failed: 0 },
      total_albums: 4, wanted_count: 1, downloaded_count: 3, on_disk_ratio: 0.75,
    }
  if (path === '/artists/a1/albums')
    return { items: [album], total: 1, limit: 500, offset: 0, sort: null, order: 'desc', unfiltered_total: 1 }
  if (path === '/wanted')
    return {
      items: [makeAlbum({ id: 'w1', title: 'Privateering', status: 'wanted' })],
      total: 1, limit: 6, offset: 0, sort: null, order: 'desc', unfiltered_total: 1,
      queueable_total: 4,
    }
  if (path === '/queue')
    return {
      items: [makeQueueItem({ id: 1, state: 'done', album_title: 'Tracker', artist_name: 'Mark Knopfler' })],
      total: 1, limit: 300, offset: 0, sort: null, order: 'desc', unfiltered_total: 1,
    }
  if (path === '/activity')
    return {
      items: [makeActivity({ id: 1, event: 'download.completed', message: 'Downloaded Tracker' })],
      total: 1, limit: 100, offset: 0, sort: null, order: 'desc', unfiltered_total: 1,
    }
  if (path.endsWith('/detail'))
    return {
      album: { ...album, tracks: [makeTrack({ id: 't1', album_id: album.id })] },
      release_group: { key: 'k', title: album.title, mb_release_group_mbid: null },
      editions: [album],
      consensus: {},
    }
  if (path.startsWith('/albums/')) return album
  return {}
}

vi.mock('@/api/client', () => ({
  get: (path: string) => Promise.resolve(page(path)),
  post: () => Promise.resolve({ ok: true, message: 'ok', level: 'success', detail: null }),
  patch: () => Promise.resolve({}),
  del: () => Promise.resolve({}),
  getHealth: () => Promise.resolve({}),
}))

const App = (await import('@/App')).default

/**
 * Everything `main.tsx` wraps `<App/>` in, and NOTHING ELSE. In particular no
 * `ToastProvider` — `<App/>` has to bring its own, and the day it stops doing
 * so this is the test that says so.
 */
function mountApp(route: string) {
  const client = new QueryClient({
    defaultOptions: { queries: { retry: false }, mutations: { retry: false } },
  })
  return render(
    <QueryClientProvider client={client}>
      <MemoryRouter initialEntries={[route]}>
        <App />
      </MemoryRouter>
    </QueryClientProvider>,
  )
}

describe('the application mounts the way the browser mounts it', () => {
  let errors: string[] = []
  let spy: ReturnType<typeof vi.spyOn> | null = null

  function captureConsole() {
    errors = []
    spy = vi.spyOn(console, 'error').mockImplementation((...args: unknown[]) => {
      errors.push(args.map(String).join(' '))
    })
  }

  afterEach(() => {
    spy?.mockRestore()
    spy = null
  })

  const ROUTES: Array<[string, string]> = [
    ['/', 'Your library'],
    ['/library', 'Artists'],
    ['/library/a1', 'Mark Knopfler'],
    ['/radar', 'Release radar'],
    ['/identify', 'Identification review'],
    ['/rules', 'Structure & tags'],
    ['/activity', 'Activity'],
  ]

  it.each(ROUTES)('renders %s without crashing', async (route, heading) => {
    captureConsole()
    mountApp(route)
    expect(await screen.findByRole('heading', { level: 1, name: heading })).toBeInTheDocument()
    // The frame came up too — a screen that renders while the shell throws is
    // the same blank page one component later.
    expect(screen.getByRole('navigation')).toBeInTheDocument()
    expect(errors.filter((e) => /not wrapped in act/.test(e) === false && /Error/.test(e))).toEqual([])
  })

  it('answers an unknown address with the NotFound screen, not a crash', async () => {
    captureConsole()
    mountApp('/there-is-no-such-screen')
    expect(await screen.findByRole('heading', { level: 1 })).toBeInTheDocument()
    expect(screen.getByRole('navigation')).toBeInTheDocument()
  })

  it('opens the release drawer from ?album= without crashing', async () => {
    captureConsole()
    mountApp(`/library/a1?album=${album.id}`)
    expect(await screen.findByRole('dialog')).toBeInTheDocument()
  })
})
