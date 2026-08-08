/**
 * The route table.
 *
 * The application is the design's six screens, the two added after them
 * (`/missing`, `/queue`) and a `NotFound`, and this is what says so: every
 * address resolves to a real default-exported screen (a lazy import of a stub
 * would resolve too, and render nothing, which is why the assertion is on each
 * screen's own `h1` rather than on "something rendered").
 */

import { render, screen } from '@testing-library/react'
import { beforeEach, describe, expect, it, vi } from 'vitest'

import { AppRoutes } from '@/routes'
import { makeSettings } from '@/test/factories'
import { TestProviders } from '@/test/providers'

const get = vi.fn()

vi.mock('@/api/client', () => ({
  get: (...args: unknown[]) => get(...args),
  post: vi.fn(),
  patch: vi.fn(),
  del: vi.fn(),
  getHealth: vi.fn(),
}))

const EMPTY_PAGE = {
  total: 0,
  limit: 100,
  offset: 0,
  sort: null,
  order: 'desc',
  unfiltered_total: 0,
  queueable_total: 0,
  items: [],
}

beforeEach(() => {
  get.mockReset()
  get.mockImplementation((path: string) => {
    if (path === '/settings') return Promise.resolve(makeSettings())
    if (path === '/meta') return Promise.resolve({ format_labels: {}, review_states: [] })
    if (path === '/integrity') {
      return Promise.resolve({
        enabled: true,
        reverify_fraction: 0,
        tracks_total: 0,
        states: {},
        never_baselined: 0,
        corrupt_files: 0,
        corrupt_muted: 0,
        albums_reopened: 0,
        last_run_at: null,
        last_result: null,
        running: false,
      })
    }
    if (path === '/enrichment') {
      return Promise.resolve({
        enabled: true,
        sources: [],
        paused_reason: null,
        last_run_at: null,
        last_result: null,
        gates: {},
        source_status: [],
        states: {},
        scope: {},
        review_total: 0,
      })
    }
    if (path === '/library/scan') {
      return Promise.resolve({
        running: false,
        library_path: '/srv/music',
        nightly: true,
        complete_ratio: 1,
        last: null,
      })
    }
    if (path === '/stats' || path === '/status') {
      return Promise.resolve({
        version: '0.4.0',
        library: {
          artists: 0,
          monitored_artists: 0,
          albums: 0,
          wanted_albums: 0,
          downloaded_albums: 0,
          failed_albums: 0,
          tracks: 0,
          downloaded_tracks: 0,
        },
        albums_by_status: {},
        queue: { worker_running: false, current_album_title: null },
        indexer: {},
        rate_limit: null,
        banners: [],
        recent_activity: [],
      })
    }
    if (path === '/nav-counts') {
      return Promise.resolve({ artists: 0, wanted: 0, queue: 0, trash: 0, enrichment_review: 0 })
    }
    return Promise.resolve(EMPTY_PAGE)
  })
})

function mount(route: string) {
  return render(
    <TestProviders route={route}>
      <AppRoutes />
    </TestProviders>,
  )
}

describe('AppRoutes', () => {
  it.each([
    ['/', 'Your library'],
    ['/library', 'Artists'],
    ['/radar', 'Release radar'],
    ['/missing', 'Missing releases'],
    ['/queue', 'Download queue'],
    ['/identify', 'Identification review'],
    ['/rules', 'Structure & tags'],
    ['/activity', 'Activity'],
  ])('%s renders its screen', async (route, heading) => {
    mount(route)
    expect(await screen.findByRole('heading', { level: 1, name: heading })).toBeInTheDocument()
  })

  it('an artist address resolves to the artist screen', async () => {
    mount('/library/uyej1o165e870')
    // The header renders before the data does; the back link is the screen's
    // own and nothing else in the app has one.
    expect(await screen.findByRole('link', { name: '← Artists' })).toBeInTheDocument()
  })

  it('anything else is the NotFound screen, not a blank frame', async () => {
    mount('/system/scan')
    expect(await screen.findByRole('heading', { level: 1 })).toBeInTheDocument()
    expect(screen.getByRole('link', { name: /dashboard/i })).toBeInTheDocument()
  })
})
