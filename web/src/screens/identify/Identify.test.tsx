/**
 * Identification review.
 *
 * Three assertions carry the design's corrections: the auto-accept band carries
 * a **partition** and never a confidence (there is no confidence anywhere in
 * this application, and nothing here may be compared against a bar), the
 * explanation shown is the **server's** sentence rather than one hard-coded per
 * state, and Accept over a row holding no proposal opens the picker instead of
 * promising to apply something.
 *
 * The band's filters are the other half: they are a real choice — which subset
 * of the review list is shown — and the one that matters is `state=rejected`,
 * because a dismissal is otherwise unreachable and is the only decision a
 * person can take back.
 */

import { render, screen, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { beforeEach, describe, expect, it, vi } from 'vitest'

import Identify from '@/screens/identify/Identify'
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

const AMBIGUOUS = {
  entity_type: 'album',
  entity_id: 'alb-1',
  source: 'musicbrainz',
  state: 'ambiguous',
  name: 'Kind of Blue',
  artist_id: 'a1',
  artist_name: 'Miles Davis',
  reason: 'four releases carry this barcode',
  attempts: 2,
  last_attempt_at: '2026-08-04T08:00:00Z',
  is_actionable: true,
  identify_sources: ['musicbrainz'],
  state_explanation: 'Several releases matched and the audio does not tell them apart.',
  suggested_release_type: null,
}

const AMBIGUOUS_2 = {
  ...AMBIGUOUS,
  entity_id: 'alb-9',
  name: 'Bitches Brew',
}

const REJECTED = {
  ...AMBIGUOUS,
  entity_id: 'alb-2',
  state: 'rejected',
  name: 'Blues of Desperation',
  is_actionable: false,
  identify_sources: [],
  state_explanation: 'You said there is no answer here. Reopen it to change your mind.',
}

const REJECTED_2 = {
  ...REJECTED,
  entity_id: 'alb-3',
  name: 'Redemption',
}

const AUTONOMY = {
  entities: 40,
  automatic: 30,
  waiting_person: 4,
  waiting_input: 3,
  dismissed: 2,
  unstarted: 1,
}

function statusPayload(autonomy: Record<string, number> = AUTONOMY) {
  return {
    enabled: true,
    sources: ['musicbrainz'],
    paused_reason: null,
    last_run_at: null,
    last_result: null,
    gates: {},
    source_status: [],
    states: {},
    scope: { albums: 32, artists: 8, catalogue_albums: 3313, catalogue_artists: 291 },
    autonomy,
    review_total: 1,
  }
}

beforeEach(() => {
  get.mockReset()
  post.mockReset()
  get.mockImplementation((path: string, params?: Record<string, unknown>) => {
    if (path === '/enrichment') {
      return Promise.resolve(statusPayload())
    }
    if (path === '/enrichment/review') {
      if (params?.state === 'rejected') {
        return Promise.resolve({
          total: 2,
          limit: 100,
          offset: 0,
          sort: null,
          order: 'desc',
          unfiltered_total: 2,
          items: [REJECTED, REJECTED_2],
        })
      }
      return Promise.resolve({
        total: 2,
        limit: 100,
        offset: 0,
        sort: null,
        order: 'desc',
        unfiltered_total: 2,
        items: [AMBIGUOUS, AMBIGUOUS_2],
      })
    }
    if (path === '/meta') {
      return Promise.resolve({ review_states: ['ambiguous', 'no_key'], format_labels: {} })
    }
    if (path.endsWith('/candidates')) {
      return Promise.resolve({
        entity_type: 'album',
        entity_id: 'alb-1',
        source: 'musicbrainz',
        query: 'release:(kind of blue) AND artist:(miles davis)',
        items: [
          {
            external_id: '984f8239-8fe1-4683-9c54-10ffb14439e9',
            title: 'Kind of Blue',
            subtitle: 'Miles Davis',
            detail: '1959 · 5 tracks',
            disambiguation: 'Legacy Edition',
            image_url: null,
            url: 'https://musicbrainz.org/release/984f8239',
          },
        ],
      })
    }
    return Promise.reject(new Error(`no fixture for ${path}`))
  })
  post.mockResolvedValue({ ok: true, message: 'Recorded.', level: 'success', detail: null })
})

function mount(route = '/identify') {
  return render(
    <TestProviders route={route}>
      <Identify />
    </TestProviders>,
  )
}

describe('Identify', () => {
  it('carries the real split in the band, and no placeholder is left standing', async () => {
    const { container } = mount()
    await screen.findAllByText('Kind of Blue')
    expect(await screen.findByText('75%')).toBeInTheDocument()
    expect(screen.getByText('30 entities of 40 in the library')).toBeInTheDocument()
    // Beside the chips and not among them: nobody can press these rows, so
    // there is no filter that shows them. A counted zero is shown as a zero.
    expect(screen.getByText('3 waiting on an input')).toBeInTheDocument()
    expect(container.querySelectorAll('[data-placeholder]')).toHaveLength(0)
  })

  it('an uncounted library reports an em dash, never 0%', async () => {
    get.mockImplementation((path: string) => {
      if (path === '/enrichment') {
        return Promise.resolve(
          statusPayload({
            entities: 0,
            automatic: 0,
            waiting_person: 0,
            waiting_input: 0,
            dismissed: 0,
            unstarted: 0,
          }),
        )
      }
      if (path === '/meta') {
        return Promise.resolve({ review_states: ['ambiguous', 'no_key'], format_labels: {} })
      }
      return Promise.resolve({
        total: 0,
        limit: 100,
        offset: 0,
        sort: null,
        order: 'desc',
        unfiltered_total: 0,
        items: [],
      })
    })
    mount()
    expect(await screen.findByText('—')).toBeInTheDocument()
    expect(screen.queryByText('0%')).toBeNull()
    // A Meter's null omits `aria-valuenow` rather than sending a measured zero.
    expect(screen.getByRole('progressbar')).not.toHaveAttribute('aria-valuenow')
  })

  it('builds the middle chips from meta.review_states, not from a second copy', async () => {
    mount()
    const needs = await screen.findByRole('button', { name: /Needs you/ })
    expect(needs).toHaveAttribute('aria-pressed', 'true')
    expect(screen.getByRole('button', { name: 'Ambiguous' })).toBeInTheDocument()
    expect(
      screen.getByRole('button', { name: 'Nothing to match on' }),
    ).toBeInTheDocument()
  })

  it('the two chips with a published figure carry it, and the subsets carry none', async () => {
    mount()
    // The counts are the whole reason the band is server-computed: a client
    // deriving them from `states` would count rows against a denominator of
    // entities. Asserted on the accessible name so dropping the `count` prop
    // cannot pass — matching on /Needs you/ alone would.
    expect(
      await screen.findByRole('button', { name: `Needs you ${AUTONOMY.waiting_person}` }),
    ).toBeInTheDocument()
    expect(
      screen.getByRole('button', { name: `Dismissed ${AUTONOMY.dismissed}` }),
    ).toBeInTheDocument()
    // A subset chip publishes no figure — there is none, and a Chip renders
    // nothing for a null count rather than a zero.
    expect(screen.getByRole('button', { name: 'Ambiguous' })).toBeInTheDocument()
  })

  it('a chip with no figure yet renders no count, never a zero', async () => {
    get.mockImplementation((path: string) => {
      if (path === '/enrichment') return new Promise(() => {})
      if (path === '/meta') {
        return Promise.resolve({ review_states: ['ambiguous'], format_labels: {} })
      }
      return Promise.resolve({
        total: 0,
        limit: 100,
        offset: 0,
        sort: null,
        order: 'desc',
        unfiltered_total: 0,
        items: [],
      })
    })
    mount()
    const needs = await screen.findByRole('button', { name: 'Needs you' })
    expect(needs.textContent).not.toMatch(/\d/)
  })

  it('“Needs you” sends no state key at all, so it shares the default’s cache entry', async () => {
    mount()
    await screen.findAllByText('Kind of Blue')
    const call = get.mock.calls.find((args) => args[0] === '/enrichment/review')
    expect(call?.[1]).not.toHaveProperty('state')
  })

  it('“Dismissed” filters to state=rejected and resets the selection to the top', async () => {
    const user = userEvent.setup()
    mount()
    await screen.findAllByText('Kind of Blue')

    // Select the *second* row first, so a surviving index would be visible:
    // both lists are two long, and a stale `1` would leave the selection on
    // "Redemption" rather than on the top of the new list.
    await user.click(screen.getByRole('button', { name: /Bitches Brew/ }))
    expect(
      screen.getByRole('button', { name: /Bitches Brew/ }),
    ).toHaveAttribute('aria-current', 'true')

    await user.click(screen.getByRole('button', { name: /Dismissed/ }))
    await screen.findAllByText('Blues of Desperation')

    const call = get.mock.calls
      .filter((args) => args[0] === '/enrichment/review')
      .at(-1)
    expect(call?.[1]).toMatchObject({ state: 'rejected' })

    // Index 0, asserted as the index and not merely as "the list changed".
    expect(
      screen.getByRole('button', { name: /Blues of Desperation/ }),
    ).toHaveAttribute('aria-current', 'true')
    expect(
      screen.getByRole('button', { name: /Redemption/ }),
    ).not.toHaveAttribute('aria-current')
  })

  it('the stack’s three key hints press the same three buttons', async () => {
    const user = userEvent.setup()
    mount('/identify?review=stack')
    await screen.findByRole('button', { name: /Reject X/ })

    // `x` — the row is `ambiguous`, so Reject is live and posts the row's own
    // source, exactly as the button does.
    await user.keyboard('x')
    expect(post).toHaveBeenCalledWith('/enrichment/album/alb-1/reject', {
      source: 'musicbrainz',
    })

    // `a` — the primary. This row holds no proposal, so it opens the picker
    // rather than promising to apply something.
    post.mockClear()
    await user.keyboard('a')
    expect(await screen.findByRole('dialog')).toBeInTheDocument()
    expect(post).not.toHaveBeenCalled()
  })

  it('the arrow key skips to the next card, and the hints are dead while the picker is open', async () => {
    const user = userEvent.setup()
    mount('/identify?review=stack')
    expect(await screen.findByText('Kind of Blue')).toBeInTheDocument()

    await user.keyboard('{ArrowRight}')
    expect(await screen.findByText('Bitches Brew')).toBeInTheDocument()

    // Open the picker; `x` must not reach the screen behind it — a drawer with
    // a search box in it cannot have a letter key mean "reject".
    await user.keyboard('a')
    await screen.findByRole('dialog')
    post.mockClear()
    await user.keyboard('x')
    expect(post).not.toHaveBeenCalled()
  })

  it('a dismissed row offers Reopen, not Reject, and posts the reopen', async () => {
    const user = userEvent.setup()
    mount('/identify?state=rejected')
    await screen.findAllByText('Blues of Desperation')
    expect(screen.getByRole('button', { name: 'Reject' })).toBeDisabled()
    await user.click(screen.getByRole('button', { name: 'Reopen' }))
    expect(post).toHaveBeenCalledWith('/enrichment/album/alb-2/reopen', undefined, {
      source: 'musicbrainz',
    })
  })

  it('renders the server’s own explanation rather than a sentence per state', async () => {
    mount()
    expect(
      await screen.findByText('Several releases matched and the audio does not tell them apart.'),
    ).toBeInTheDocument()
  })

  it('titles the evidence honestly and shows what the work row holds', async () => {
    mount()
    await screen.findAllByText('Kind of Blue')
    expect(screen.getByRole('heading', { name: 'Evidence' })).toBeInTheDocument()
    expect(screen.getByText('Attempts')).toBeInTheDocument()
    expect(screen.queryByText(/Fingerprint evidence/)).toBeNull()
  })

  it('a row holding no proposal offers to find a match, and does not promise to apply one', async () => {
    mount()
    expect(await screen.findByRole('button', { name: 'Find a match…' })).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: /Accept & apply/ })).toBeNull()
  })

  it('Accept over a null proposal opens the picker and posts nothing', async () => {
    const user = userEvent.setup()
    mount()
    await user.click(await screen.findByRole('button', { name: 'Find a match…' }))
    const dialog = await screen.findByRole('dialog')
    expect((await within(dialog).findAllByText('Kind of Blue')).length).toBeGreaterThan(0)
    expect(post).not.toHaveBeenCalled()
  })

  it('identifies from a candidate card, which is the only search a person reads', async () => {
    const user = userEvent.setup()
    mount()
    await user.click(await screen.findByRole('button', { name: 'Find a match…' }))
    const dialog = await screen.findByRole('dialog')
    await user.click(await within(dialog).findByText('Legacy Edition', { exact: false }))
    expect(post).toHaveBeenCalledWith('/enrichment/album/alb-1/identify', {
      source: 'musicbrainz',
      external_id: '984f8239-8fe1-4683-9c54-10ffb14439e9',
    })
  })

  it('switches to the stack from the URL', async () => {
    mount('/identify?review=stack')
    expect(await screen.findByRole('button', { name: /Reject X/ })).toBeInTheDocument()
  })

  it('rejects with the row’s own source, because a work item is one pair', async () => {
    const user = userEvent.setup()
    mount()
    await user.click(await screen.findByRole('button', { name: 'Reject' }))
    expect(post).toHaveBeenCalledWith('/enrichment/album/alb-1/reject', {
      source: 'musicbrainz',
    })
  })
})
