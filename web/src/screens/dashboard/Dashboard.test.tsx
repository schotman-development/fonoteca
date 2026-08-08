/**
 * The dashboard, mounted whole against a mocked `client.ts`.
 *
 * It pins the five blocks in the design's order and the three things a
 * refactor loses silently: the `replaced` tile reading `last_result` rather
 * than `states` (and showing the em dash when no pass has run), the Ignore
 * button sending an **empty** body, and the collaborations row being a
 * placeholder rather than a fabricated count.
 */

import { render, screen, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { beforeEach, describe, expect, it, vi } from 'vitest'

import Dashboard from '@/screens/dashboard/Dashboard'
import { makeAlbum, makeQueueItem } from '@/test/factories'
import { TestProviders } from '@/test/providers'

const get = vi.fn()
const post = vi.fn()

vi.mock('@/api/client', () => ({
  get: (...args: unknown[]) => get(...args),
  post: (...args: unknown[]) => post(...args),
  patch: vi.fn(),
  del: vi.fn(),
  getHealth: vi.fn(),
}))

const INTEGRITY = {
  enabled: true,
  reverify_fraction: 0.033,
  tracks_total: 1000,
  states: { verified: 800 },
  never_baselined: 200,
  corrupt_files: 3,
  corrupt_muted: 2,
  albums_reopened: 0,
  last_run_at: null,
  last_result: null,
  running: false,
}

const STATUS = {
  version: '0.4.0',
  started_at: null,
  uptime_seconds: 1,
  library: {
    artists: 5,
    monitored_artists: 5,
    albums: 20,
    wanted_albums: 4,
    downloaded_albums: 16,
    failed_albums: 1,
    tracks: 100,
    downloaded_tracks: 90,
  },
  queue: {
    pending: 0,
    active: 0,
    done: 1,
    failed: 0,
    cancelled: 0,
    total: 1,
    current_album_id: null,
    current_album_title: null,
    worker_running: false,
  },
  indexer: {},
  rate_limit: null,
  recent_activity: [],
  credentials_ok: true,
  app_secret_ok: true,
  library_path: '/srv/music',
  banners: [],
}

function page(path: string, params?: Record<string, unknown>): unknown {
  switch (path) {
    case '/integrity':
      return INTEGRITY
    case '/status':
      return STATUS
    case '/stats':
      return { library: STATUS.library, albums_by_status: {}, queue: STATUS.queue, indexer: {}, rate_limit: null }
    case '/nav-counts':
      return { artists: 5, wanted: 4, queue: 0, trash: 0, enrichment_review: 2 }
    case '/library/scan':
      return { running: false, library_path: '/srv/music', nightly: true, complete_ratio: 1, last: null }
    case '/queue':
      return params?.state === 'done'
        ? {
            total: 1,
            limit: 12,
            offset: 0,
            sort: null,
            order: 'desc',
            unfiltered_total: null,
            items: [
              makeQueueItem({
                id: 9,
                state: 'done',
                album_title: 'Bitches Brew',
                finished_at: '2026-08-04T09:00:00Z',
              }),
            ],
          }
        : { total: 0, limit: 300, offset: 0, sort: null, order: 'desc', unfiltered_total: null, items: [] }
    case '/wanted':
      return {
        total: 1,
        limit: 6,
        offset: 0,
        sort: null,
        order: 'desc',
        unfiltered_total: 1,
        queueable_total: 1,
        items: [makeAlbum({ id: 'alb-1', title: 'On the Corner' })],
      }
    case '/meta':
      return { format_labels: {}, review_states: ['ambiguous', 'no_key'] }
    default:
      return undefined
  }
}

beforeEach(() => {
  get.mockReset()
  post.mockReset()
  get.mockImplementation((path: string, params?: Record<string, unknown>) => {
    const fixture = page(path, params)
    return fixture === undefined
      ? Promise.reject(new Error(`no fixture for ${path}`))
      : Promise.resolve(fixture)
  })
  post.mockResolvedValue({ ok: true, message: 'Queued.', level: 'success', detail: null })
})

function mount() {
  return render(
    <TestProviders route="/">
      <Dashboard />
    </TestProviders>,
  )
}

describe('Dashboard', () => {
  it('opens with the design’s greeting and summary (1201–1202)', () => {
    mount()
    expect(screen.getByRole('heading', { level: 1, name: 'Your library' })).toBeInTheDocument()
    expect(
      screen.getByText('Identified, tagged and filed automatically since you last looked.'),
    ).toBeInTheDocument()
  })

  it('renders the five blocks in the design’s order', async () => {
    mount()
    await screen.findByText('Library health')
    const headings = screen.getAllByRole('heading', { level: 2 }).map((h) => h.textContent)
    expect(headings).toEqual([
      'Library health',
      'Last downloaded',
      'Missing albums',
      'To do',
      'Running now',
    ])
  })

  it('reads `replaced` off last_result, and shows the em dash when no pass has run', async () => {
    mount()
    const tile = (await screen.findByText('changed elsewhere')).closest('button')
    expect(tile).not.toBeNull()
    expect(within(tile as HTMLElement).getByText('—')).toBeInTheDocument()
    expect(within(tile as HTMLElement).getByText('no pass yet')).toBeInTheDocument()
  })

  it('counts verified against the measured total and names the muted corrupt files', async () => {
    mount()
    expect(await screen.findByText('80.0% of measured files')).toBeInTheDocument()
    expect(screen.getByText('decode errors · 2 muted')).toBeInTheDocument()
  })

  it('fills the shelf from finished queue items', async () => {
    mount()
    expect(await screen.findByText('Bitches Brew')).toBeInTheDocument()
  })

  it('Ignore posts an EMPTY body, because the monitor endpoint toggles', async () => {
    const user = userEvent.setup()
    mount()
    await user.click(await screen.findByRole('button', { name: /Ignore/ }))
    expect(post).toHaveBeenCalledWith('/albums/alb-1/monitor', undefined)
  })

  it('queues a release only from an explicit press', async () => {
    const user = userEvent.setup()
    mount()
    expect(post).not.toHaveBeenCalled()
    await user.click(await screen.findByRole('button', { name: /Download/ }))
    expect(post).toHaveBeenCalledWith('/albums/alb-1/queue')
  })

  it('stands exactly one placeholder where the design counts symlinked collaborations', async () => {
    const { container } = mount()
    await screen.findByText('Library health')
    const gaps = container.querySelectorAll('[data-placeholder]')
    expect(gaps).toHaveLength(1)
    expect(gaps[0]?.getAttribute('data-placeholder')).toMatch(/secondary-credit/)
  })

  it('says Idle rather than drawing a job at 0%', async () => {
    mount()
    expect(await screen.findByText('Idle.')).toBeInTheDocument()
    expect(screen.queryByRole('progressbar')).toBeNull()
  })
})
