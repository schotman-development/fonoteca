/**
 * Missing releases.
 *
 * Pins the three things that make this screen honest rather than merely
 * present: the bulk button is labelled from `queueable_total` and does not move
 * when the filters do, the `monitored` filter is never sent as `null` (a 422)
 * and never sent as a body on the toggle (the collision that once unmonitored
 * whatever row was pressed), and the two empty states are told apart by
 * `unfiltered_total`.
 */

import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { beforeEach, describe, expect, it, vi } from 'vitest'

import Missing from '@/screens/missing/Missing'
import { makeAlbum } from '@/test/factories'
import { TestProviders } from '@/test/providers'

const get = vi.fn()
const post = vi.fn()
const patch = vi.fn()

vi.mock('@/api/client', () => ({
  get: (...args: unknown[]) => get(...args),
  post: (...args: unknown[]) => post(...args),
  patch: (...args: unknown[]) => patch(...args),
  del: vi.fn(),
  getHealth: vi.fn(),
}))

const PAGE = { total: 2, limit: 500, offset: 0, sort: null, order: 'asc' }

function wanted(overrides: Record<string, unknown> = {}) {
  return {
    ...PAGE,
    unfiltered_total: 9,
    queueable_total: 7,
    items: [
      makeAlbum({ id: 'alb-1', title: 'Kind of Blue', status: 'wanted' }),
      makeAlbum({ id: 'alb-2', title: 'Bitches Brew', status: 'failed' }),
    ],
    ...overrides,
  }
}

beforeEach(() => {
  get.mockReset()
  post.mockReset()
  patch.mockReset()
  get.mockImplementation((path: string) => {
    if (path === '/wanted') return Promise.resolve(wanted())
    if (path === '/meta') return Promise.resolve({ format_labels: {} })
    return Promise.reject(new Error(`no fixture for ${path}`))
  })
})

function mount(route = '/missing') {
  return render(
    <TestProviders route={route}>
      <Missing />
    </TestProviders>,
  )
}

describe('Missing', () => {
  it('heads the page and lists the backlog', async () => {
    mount()
    expect(
      screen.getByRole('heading', { level: 1, name: 'Missing releases' }),
    ).toBeInTheDocument()
    expect(await screen.findByText('Kind of Blue')).toBeInTheDocument()
    expect(screen.getByText('Bitches Brew')).toBeInTheDocument()
  })

  it('labels Download all from `queueable_total`, not from the list total', async () => {
    mount()
    // 7 is queueable_total; 2 is `total` and 9 is `unfiltered_total`. Only one
    // of the three is what one press would really queue.
    expect(await screen.findByRole('button', { name: 'Download all (7)' })).toBeInTheDocument()
  })

  it('keeps that label still when a filter narrows the list', async () => {
    const user = userEvent.setup()
    get.mockImplementation((path: string, params?: Record<string, unknown>) => {
      if (path === '/wanted') {
        // The endpoint's own invariant: `queueable_total` ignores the filters.
        return Promise.resolve(
          params?.status === 'failed'
            ? wanted({ total: 1, items: [makeAlbum({ id: 'alb-2', status: 'failed' })] })
            : wanted(),
        )
      }
      if (path === '/meta') return Promise.resolve({ format_labels: {} })
      return Promise.reject(new Error(`no fixture for ${path}`))
    })

    mount()
    await screen.findByRole('button', { name: 'Download all (7)' })
    await user.click(screen.getByRole('button', { name: 'Failed' }))

    expect(await screen.findByRole('button', { name: 'Download all (7)' })).toBeInTheDocument()
  })

  it('sends the status filter and never a null `monitored`', async () => {
    const user = userEvent.setup()
    mount()
    await screen.findByText('Kind of Blue')

    await user.click(screen.getByRole('button', { name: 'Ignored' }))
    await vi.waitFor(() => {
      const calls = get.mock.calls.filter((call) => call[0] === '/wanted')
      const last = calls[calls.length - 1]?.[1] as Record<string, unknown>
      expect(last.monitored).toBe(false)
    })

    for (const call of get.mock.calls) {
      const params = call[1] as Record<string, unknown> | undefined
      if (params && 'monitored' in params) expect(params.monitored).not.toBeNull()
    }
  })

  it('omits `monitored` for the monitored views — the endpoint already defaults to it', async () => {
    mount()
    await screen.findByText('Kind of Blue')
    const first = get.mock.calls.find((call) => call[0] === '/wanted')?.[1] as Record<
      string,
      unknown
    >
    expect('monitored' in first).toBe(false)
  })

  it('toggles the monitor flag with an EMPTY body, never with the filter’s value', async () => {
    const user = userEvent.setup()
    post.mockResolvedValue({ ...makeAlbum({ id: 'alb-1' }), monitored: false })
    // On the `ignored` view — the exact collision: a filter called `monitored`
    // is a filter, and binding it as the row's new value is the bug.
    mount('/missing?filter=ignored')
    await screen.findByText('Kind of Blue')

    await user.click(screen.getAllByRole('button', { name: /^(Ignore|Monitor)$/ })[0] as HTMLElement)

    expect(post).toHaveBeenCalledWith('/albums/alb-1/monitor', undefined)
  })

  it('queues one release from its row', async () => {
    const user = userEvent.setup()
    post.mockResolvedValue({ ok: true, message: 'Queued Kind of Blue.', level: 'info' })
    mount()
    await screen.findByText('Kind of Blue')

    await user.click(screen.getAllByRole('button', { name: 'Download' })[0] as HTMLElement)
    expect(post).toHaveBeenCalledWith('/albums/alb-1/queue')
  })

  it('tells "nothing is missing" apart from "nothing matches that"', async () => {
    get.mockImplementation((path: string) => {
      if (path === '/wanted')
        return Promise.resolve(wanted({ total: 0, items: [], unfiltered_total: 0, queueable_total: 0 }))
      if (path === '/meta') return Promise.resolve({ format_labels: {} })
      return Promise.reject(new Error(`no fixture for ${path}`))
    })
    const { unmount } = mount()
    expect(await screen.findByText('Nothing is missing')).toBeInTheDocument()
    unmount()

    get.mockImplementation((path: string) => {
      if (path === '/wanted')
        return Promise.resolve(wanted({ total: 0, items: [], unfiltered_total: 9 }))
      if (path === '/meta') return Promise.resolve({ format_labels: {} })
      return Promise.reject(new Error(`no fixture for ${path}`))
    })
    mount('/missing?filter=failed')
    expect(await screen.findByText('Nothing matches that')).toBeInTheDocument()
  })

  it('names the 500-per-press cap only when the backlog is bigger than it', async () => {
    const { unmount } = mount()
    await screen.findByText('Kind of Blue')
    expect(screen.queryByText(/One press queues 500/)).toBeNull()
    unmount()

    get.mockImplementation((path: string) => {
      if (path === '/wanted') return Promise.resolve(wanted({ queueable_total: 4693 }))
      if (path === '/meta') return Promise.resolve({ format_labels: {} })
      return Promise.reject(new Error(`no fixture for ${path}`))
    })
    mount()
    expect(await screen.findByText(/One press queues 500/)).toBeInTheDocument()
  })

  it('has one bulk press and it is the one endpoint allowed to queue the backlog', async () => {
    const user = userEvent.setup()
    post.mockResolvedValue({ ok: true, message: 'Queued 7 releases.', level: 'info' })
    mount()

    await user.click(await screen.findByRole('button', { name: 'Download all (7)' }))
    expect(post).toHaveBeenCalledWith('/wanted/download', undefined, {})
  })
})
