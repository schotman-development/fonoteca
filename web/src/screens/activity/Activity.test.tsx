/**
 * Activity.
 *
 * The load-bearing assertion is the one about the chips: they filter the loaded
 * page **client-side** and issue no second request, because `list_activity`
 * matches an event exactly and the design's chips are groups. The screen has to
 * say so, too — a chip that quietly searched 100 of 40 000 rows while looking
 * like a filter is the failure this replaces.
 */

import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { beforeEach, describe, expect, it, vi } from 'vitest'

import Activity from '@/screens/activity/Activity'
import { makeActivity } from '@/test/factories'
import { TestProviders } from '@/test/providers'

const get = vi.fn()

vi.mock('@/api/client', () => ({
  get: (...args: unknown[]) => get(...args),
  post: vi.fn(),
  patch: vi.fn(),
  del: vi.fn(),
  getHealth: vi.fn(),
}))

const ITEMS = [
  makeActivity({ id: 1, event: 'library.integrity', message: 'Verified 400 files' }),
  makeActivity({ id: 2, event: 'download.completed', message: 'Downloaded Kind of Blue' }),
  makeActivity({ id: 3, event: 'enrichment.matched', message: 'Matched a release', level: 'warning' }),
]

beforeEach(() => {
  get.mockReset()
  get.mockImplementation((path: string) => {
    if (path === '/activity') {
      return Promise.resolve({
        total: 3,
        limit: 100,
        offset: 0,
        sort: null,
        order: 'desc',
        unfiltered_total: 3,
        items: ITEMS,
      })
    }
    return Promise.reject(new Error(`no fixture for ${path}`))
  })
})

function mount(route = '/activity') {
  return render(
    <TestProviders route={route}>
      <Activity />
    </TestProviders>,
  )
}

describe('Activity', () => {
  it('renders the log newest first with the design’s four chips', async () => {
    mount()
    expect(await screen.findByText('Verified 400 files')).toBeInTheDocument()
    const chips = screen.getAllByRole('button').map((b) => b.textContent)
    expect(chips).toEqual(['All', 'Integrity', 'Writes', 'Grabs'])
  })

  it('derives the kind column from the event’s first segment', async () => {
    mount()
    expect(await screen.findByText('LIBRARY')).toBeInTheDocument()
    expect(screen.getByText('DOWNLOAD')).toBeInTheDocument()
    expect(screen.getByText('ENRICHMENT')).toBeInTheDocument()
  })

  it('groups client-side and issues no second request', async () => {
    const user = userEvent.setup()
    mount()
    await screen.findByText('Verified 400 files')
    const before = get.mock.calls.filter((call) => call[0] === '/activity').length

    await user.click(screen.getByRole('button', { name: 'Grabs' }))

    expect(screen.getByText('Downloaded Kind of Blue')).toBeInTheDocument()
    expect(screen.queryByText('Verified 400 files')).toBeNull()
    expect(get.mock.calls.filter((call) => call[0] === '/activity')).toHaveLength(before)
  })

  it('says honestly that a chip filtered the loaded page, not the history', async () => {
    const user = userEvent.setup()
    mount()
    await screen.findByText('Verified 400 files')
    await user.click(screen.getByRole('button', { name: 'Integrity' }))
    expect(
      screen.getByText(/this chip filters what is loaded, not the whole history/),
    ).toBeInTheDocument()
  })

  it('reads its chip out of the URL', async () => {
    mount('/activity?group=writes')
    expect(await screen.findByText('Matched a release')).toBeInTheDocument()
    expect(screen.queryByText('Downloaded Kind of Blue')).toBeNull()
  })
})
