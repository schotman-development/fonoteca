/**
 * The ⌘K palette.
 *
 * Four things are worth a test and the rest is markup: the chord opens it from
 * anywhere, Escape closes it, the keyboard walks the list and Enter runs the
 * active row — and, the one that costs real money if it regresses, **the search
 * is debounced**. `/api/search` is a live call to Qobuz through the one shared
 * rate limiter, so a request per keystroke spends a paid account's hourly
 * budget on prefixes nobody meant to search.
 */

import { render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { useState } from 'react'
import { useLocation } from 'react-router-dom'
import { beforeEach, describe, expect, it, vi } from 'vitest'

import { CommandPalette } from '@/shell/CommandPalette'
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

const SEARCH = {
  query: 'knopfler',
  artists: [
    { id: '12345', name: 'Mark Knopfler', image_url: null, albums_count: 9, slug: null, followed: false },
  ],
  albums: [],
  total_artists: 1,
  total_albums: 0,
  limit: 25,
  offset: 0,
}

/** Shows the address, so a navigation is observable without a router spy. */
function Here() {
  const { pathname } = useLocation()
  return <div data-testid="here">{pathname}</div>
}

function Harness() {
  const [open, setOpen] = useState(false)
  return (
    <>
      <Here />
      <button type="button" onClick={() => setOpen(true)}>
        open it
      </button>
      <CommandPalette open={open} onOpenChange={setOpen} />
    </>
  )
}

function mount() {
  return render(
    <TestProviders>
      <Harness />
    </TestProviders>,
  )
}

beforeEach(() => {
  get.mockReset()
  post.mockReset()
  get.mockResolvedValue(SEARCH)
  post.mockResolvedValue({ artist: { id: '12345' }, created: true })
})

describe('CommandPalette', () => {
  it('opens on the platform chord from anywhere in the document', async () => {
    const user = userEvent.setup()
    mount()
    expect(screen.queryByRole('dialog')).toBeNull()
    await user.keyboard('{Control>}k{/Control}')
    expect(screen.getByRole('dialog', { name: 'Search or run a command' })).toBeInTheDocument()
  })

  it('closes on Escape', async () => {
    const user = userEvent.setup()
    mount()
    await user.click(screen.getByRole('button', { name: 'open it' }))
    await user.keyboard('{Escape}')
    expect(screen.queryByRole('dialog')).toBeNull()
  })

  it('offers every screen in the nav, and Enter navigates to the active one', async () => {
    const user = userEvent.setup()
    mount()
    await user.click(screen.getByRole('button', { name: 'open it' }))

    const options = screen.getAllByRole('option')
    expect(options.slice(0, 8).map((o) => o.textContent)).toEqual([
      '◱Dashboard/',
      '▤Library/library',
      '◎Release radar/radar',
      '◌Missing releases/missing',
      '⇣Download queue/queue',
      '⌗Identify/identify',
      '⌥Structure & tags/rules',
      '≡Activity/activity',
    ])

    await user.keyboard('{ArrowDown}{ArrowDown}{Enter}')
    expect(screen.getByTestId('here')).toHaveTextContent('/radar')
  })

  it('names the active option through aria-activedescendant', async () => {
    const user = userEvent.setup()
    mount()
    await user.click(screen.getByRole('button', { name: 'open it' }))
    const input = screen.getByRole('combobox')
    const first = screen.getAllByRole('option')[0]
    expect(input).toHaveAttribute('aria-activedescendant', first?.id)
  })

  it('filters the local entries as you type, with no request for them', async () => {
    const user = userEvent.setup()
    mount()
    await user.click(screen.getByRole('button', { name: 'open it' }))
    await user.type(screen.getByRole('combobox'), 'retag')
    expect(screen.getAllByRole('option').map((o) => o.textContent)).toEqual([
      '›Re-tag librarywrites',
    ])
  })

  it('does not search Qobuz on every keystroke', async () => {
    const user = userEvent.setup()
    mount()
    await user.click(screen.getByRole('button', { name: 'open it' }))
    await user.type(screen.getByRole('combobox'), 'knopfler')

    // Straight after typing: nothing has been asked of the rate-limited upstream.
    expect(get).not.toHaveBeenCalled()

    // And exactly one request once the debounce lands — not one per letter.
    await waitFor(() => expect(get).toHaveBeenCalledTimes(1), { timeout: 3000 })
    expect(get.mock.calls[0]?.[0]).toBe('/search')
    expect(get.mock.calls[0]?.[1]).toMatchObject({ q: 'knopfler' })
  })

  it('follows an unfollowed artist rather than navigating nowhere', async () => {
    const user = userEvent.setup()
    mount()
    await user.click(screen.getByRole('button', { name: 'open it' }))
    await user.type(screen.getByRole('combobox'), 'knopfler')

    const hit = await screen.findByText('Mark Knopfler', {}, { timeout: 3000 })
    await user.click(hit)

    await waitFor(() =>
      expect(post).toHaveBeenCalledWith('/artists', {
        artist_id: '12345',
        name: 'Mark Knopfler',
      }),
    )
    // Following queues nothing, so the address must not have moved either.
    expect(screen.getByTestId('here')).toHaveTextContent('/')
  })
})
