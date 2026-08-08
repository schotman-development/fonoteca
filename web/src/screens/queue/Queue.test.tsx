/**
 * Download queue.
 *
 * Pins the four things that make this a queue screen rather than a list of
 * rows: it asks `/queue` (with the state filter in the URL, so a poll cannot
 * reset it), it does not reorder what the worker returned, its presses reach
 * the two endpoints that exist, and it reads `/status` at the same
 * `activity_limit` the shell does so the screen adds no second poll of it.
 */

import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { beforeEach, describe, expect, it, vi } from 'vitest'

import Queue from '@/screens/queue/Queue'
import { makeQueueItem } from '@/test/factories'
import { TestProviders } from '@/test/providers'

const get = vi.fn()
const post = vi.fn()
const del = vi.fn()

vi.mock('@/api/client', () => ({
  get: (...args: unknown[]) => get(...args),
  post: (...args: unknown[]) => post(...args),
  patch: vi.fn(),
  del: (...args: unknown[]) => del(...args),
  getHealth: vi.fn(),
}))

const PAGE = { total: 3, limit: 300, offset: 0, sort: null, order: 'asc' }

const ITEMS = [
  makeQueueItem({
    id: 1,
    album_id: 'alb-1',
    album_title: 'Kind of Blue',
    state: 'active',
    progress_tracks_done: 3,
    progress_tracks_total: 12,
  }),
  makeQueueItem({ id: 2, album_id: 'alb-2', album_title: 'Bitches Brew', state: 'pending' }),
  makeQueueItem({
    id: 3,
    album_id: 'alb-3',
    album_title: 'In a Silent Way',
    state: 'failed',
    last_error: 'Qobuz returned 502',
  }),
]

function status(overrides: Record<string, unknown> = {}) {
  return {
    version: '0.4.0',
    library: {},
    queue: { worker_running: true, current_album_title: 'Kind of Blue' },
    indexer: {},
    rate_limit: null,
    banners: [],
    recent_activity: [],
    ...overrides,
  }
}

beforeEach(() => {
  get.mockReset()
  post.mockReset()
  del.mockReset()
  get.mockImplementation((path: string) => {
    if (path === '/queue') return Promise.resolve({ ...PAGE, unfiltered_total: 3, items: ITEMS })
    if (path === '/status') return Promise.resolve(status())
    return Promise.reject(new Error(`no fixture for ${path}`))
  })
})

function mount(route = '/queue') {
  return render(
    <TestProviders route={route}>
      <Queue />
    </TestProviders>,
  )
}

describe('Queue', () => {
  it('heads the page and names what the worker is on', async () => {
    mount()
    expect(
      screen.getByRole('heading', { level: 1, name: 'Download queue' }),
    ).toBeInTheDocument()
    expect(await screen.findByText(/Downloading Kind of Blue/)).toBeInTheDocument()
  })

  it('says the worker takes one album at a time when nothing is running', async () => {
    get.mockImplementation((path: string) => {
      if (path === '/queue') return Promise.resolve({ ...PAGE, unfiltered_total: 3, items: ITEMS })
      if (path === '/status')
        return Promise.resolve(
          status({ queue: { worker_running: false, current_album_title: null } }),
        )
      return Promise.reject(new Error(`no fixture for ${path}`))
    })
    mount()
    expect(await screen.findByText(/one album at a time/)).toBeInTheDocument()
  })

  it('keeps the worker’s order rather than sorting the rows itself', async () => {
    mount()
    await screen.findByText('Kind of Blue')
    // Ordered as the endpoint returned them: active first, then the worker's
    // own order. Nothing here re-sorts, so "when will mine start" stays true —
    // a client-side sort on any other column only looks tidier.
    const shown = ITEMS.map((item) => item.album_title as string)
    const positions = shown.map((title) =>
      Array.prototype.indexOf.call(
        document.querySelectorAll('*'),
        screen.getByText(title),
      ),
    )
    expect([...positions].sort((a, b) => a - b)).toEqual(positions)
  })

  it('draws the live bar for the running item and no other', async () => {
    mount()
    await screen.findByText('Kind of Blue')
    const bars = screen.getAllByRole('progressbar')
    expect(bars).toHaveLength(1)
    expect(bars[0]).toHaveAttribute('aria-valuenow', '25')
  })

  it('puts the state filter in the URL and sends it as `state`', async () => {
    const user = userEvent.setup()
    mount()
    await screen.findByText('Kind of Blue')

    await user.click(screen.getByRole('button', { name: /Failed/ }))
    await vi.waitFor(() => {
      const calls = get.mock.calls.filter((call) => call[0] === '/queue')
      const last = calls[calls.length - 1]?.[1] as Record<string, unknown> | undefined
      expect(last?.state).toBe('failed')
    })
  })

  it('reads the filter off the address, so a link opens the filtered view', async () => {
    mount('/queue?state=failed')
    await vi.waitFor(() => {
      const first = get.mock.calls.find((call) => call[0] === '/queue')?.[1] as Record<
        string,
        unknown
      >
      expect(first.state).toBe('failed')
    })
  })

  it('sends no `state` at all for the All chip — never a null', async () => {
    mount()
    await screen.findByText('Kind of Blue')
    const first = get.mock.calls.find((call) => call[0] === '/queue')?.[1] as Record<
      string,
      unknown
    >
    expect('state' in first).toBe(false)
  })

  it('cancels through DELETE and retries through POST', async () => {
    const user = userEvent.setup()
    del.mockResolvedValue({ ok: true, message: 'Queue item 2 cancelled.', level: 'warning' })
    post.mockResolvedValue({ ok: true, message: 'Queue item 3 will be retried.', level: 'info' })
    mount()
    await screen.findByText('Bitches Brew')

    await user.click(screen.getAllByRole('button', { name: 'Cancel' })[0] as HTMLElement)
    expect(del).toHaveBeenCalledWith('/queue/1')

    await user.click(screen.getByRole('button', { name: 'Retry' }))
    expect(post).toHaveBeenCalledWith('/queue/3/retry')
  })

  it('shares the shell’s `/status` key rather than polling it a second time', async () => {
    mount()
    await screen.findByText('Kind of Blue')
    const params = get.mock.calls.find((call) => call[0] === '/status')?.[1] as Record<
      string,
      unknown
    >
    // The footer and the sidebar both ask at `activity_limit: 0`.
    expect(params.activity_limit).toBe(0)
  })

  it('tells an empty queue apart from an empty filter', async () => {
    get.mockImplementation((path: string) => {
      if (path === '/queue')
        return Promise.resolve({ ...PAGE, total: 0, unfiltered_total: 0, items: [] })
      if (path === '/status') return Promise.resolve(status())
      return Promise.reject(new Error(`no fixture for ${path}`))
    })
    const { unmount } = mount()
    expect(await screen.findByText('The queue is empty')).toBeInTheDocument()
    unmount()

    get.mockImplementation((path: string) => {
      if (path === '/queue')
        return Promise.resolve({ ...PAGE, total: 0, unfiltered_total: 12, items: [] })
      if (path === '/status') return Promise.resolve(status())
      return Promise.reject(new Error(`no fixture for ${path}`))
    })
    mount('/queue?state=cancelled')
    expect(await screen.findByText('Nothing in this state')).toBeInTheDocument()
  })
})
