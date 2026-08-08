/**
 * Release radar.
 *
 * Pins three things. **The left column is fed by release date**, not by the
 * download queue — it used to call `/queue`, which meant a page headed *New &
 * upcoming* was ordered by whatever finished downloading most recently. The
 * switch controls *monitoring* rather than auto-download, so following queues
 * nothing. And the design's `Announced` state is still not invented: an
 * upcoming release is a date, drawn as a note, not a fabricated status word.
 */

import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { beforeEach, describe, expect, it, vi } from 'vitest'

import Radar from '@/screens/radar/Radar'
import { makeAlbum, makeArtist, makeSettings } from '@/test/factories'
import { TestProviders } from '@/test/providers'

const get = vi.fn()
const patch = vi.fn()
const post = vi.fn()

vi.mock('@/api/client', () => ({
  get: (...args: unknown[]) => get(...args),
  post: (...args: unknown[]) => post(...args),
  patch: (...args: unknown[]) => patch(...args),
  del: vi.fn(),
  getHealth: vi.fn(),
}))

const PAGE = { total: 1, limit: 300, offset: 0, sort: null, order: 'desc', unfiltered_total: null }

beforeEach(() => {
  get.mockReset()
  patch.mockReset()
  post.mockReset()
  patch.mockResolvedValue(makeArtist({ id: 'a1', name: 'Bon Iver', monitored: false }))
  get.mockImplementation((path: string) => {
    switch (path) {
      case '/releases/recent':
        return Promise.resolve({
          ...PAGE,
          total: 2,
          limit: 20,
          items: [
            // Dated ahead — the design's `Announced`, which is reachable now
            // that the ordering is by release date.
            makeAlbum({ id: 'alb-1', title: 'Sable, Fable', release_date: '2099-04-11', status: 'wanted' }),
            makeAlbum({ id: 'alb-2', title: 'Kind of Blue', release_date: '1959-08-17', status: 'downloaded', queue_state: 'active' }),
          ],
        })
      case '/artists':
        return Promise.resolve({
          ...PAGE,
          limit: 1000,
          items: [makeArtist({ id: 'a1', name: 'Bon Iver', monitored: true })],
        })
      case '/stats':
        return Promise.resolve({
          library: { artists: 7, monitored_artists: 5, albums: 0, wanted_albums: 0, downloaded_albums: 0, failed_albums: 0, tracks: 0, downloaded_tracks: 0 },
          albums_by_status: {},
          queue: {},
          indexer: {},
          rate_limit: null,
        })
      case '/settings':
        return Promise.resolve(makeSettings())
      case '/meta':
        return Promise.resolve({ format_labels: {} })
      default:
        return Promise.reject(new Error(`no fixture for ${path}`))
    }
  })
})

function mount() {
  return render(
    <TestProviders route="/radar">
      <Radar />
    </TestProviders>,
  )
}

describe('Radar', () => {
  it('heads the page and counts what is monitored', async () => {
    mount()
    expect(screen.getByRole('heading', { level: 1, name: 'Release radar' })).toBeInTheDocument()
    expect(await screen.findByText(/5 of 7 artists monitored/)).toBeInTheDocument()
  })

  it('draws both columns in the design’s order', async () => {
    mount()
    await screen.findByText('Sable, Fable')
    expect(screen.getAllByRole('heading', { level: 2 }).map((h) => h.textContent)).toEqual([
      'New & upcoming',
      'Followed artists',
    ])
  })

  it('asks for releases by release date, never for the queue', async () => {
    mount()
    await screen.findByText('Sable, Fable')

    const paths = get.mock.calls.map((call) => call[0] as string)
    expect(paths).toContain('/releases/recent')
    expect(paths).not.toContain('/queue')
  })

  it('states what Qobuzarr has done with each release, reading queue_state first', async () => {
    mount()
    // `queue_state: 'active'` over `status: 'downloaded'` — an upgrade in
    // flight is not "In library".
    expect(await screen.findByText('Downloading')).toBeInTheDocument()
    expect(screen.queryByText('In library')).toBeNull()
    // Still no invented status word for the release dated ahead.
    expect(screen.queryByText('Announced')).toBeNull()
  })

  it('the switch writes `monitored` and queues nothing', async () => {
    const user = userEvent.setup()
    mount()
    await user.click(await screen.findByRole('switch', { name: /Monitor Bon Iver/ }))
    expect(patch).toHaveBeenCalledWith('/artists/a1', { monitored: false })
    expect(post).not.toHaveBeenCalled()
  })

  it('says the switch controls monitoring, not auto-download', async () => {
    mount()
    expect(await screen.findByText(/The switch controls monitoring/)).toBeInTheDocument()
  })
})
