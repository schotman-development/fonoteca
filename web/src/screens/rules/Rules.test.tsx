/**
 * Structure & tags — the settings overlay.
 *
 * Pinned here: the allowlist comes from the payload (a key outside it renders
 * read-only rather than posting a 400), the ladder writes the whole reordered
 * list, an empty `naming_preview` is rendered as a *refusal to render* rather
 * than as a blank box, and the tag-mapping table renders exactly what
 * `GET /api/meta` published — one row per entry, no local copy of the table, and
 * nothing at all before the payload arrives.
 *
 * The re-file count is the fourth: **press-driven**, because the walk hashes
 * every file in the library. The tests below pin the two halves of that — an
 * idle screen issues zero estimate requests, and the press counts the *typed*
 * template rather than the saved one.
 */

import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { beforeEach, describe, expect, it, vi } from 'vitest'

import Rules from '@/screens/rules/Rules'
import { makeSettings } from '@/test/factories'
import { TestProviders } from '@/test/providers'

const get = vi.fn()
const patch = vi.fn()

vi.mock('@/api/client', () => ({
  get: (...args: unknown[]) => get(...args),
  post: vi.fn(),
  patch: (...args: unknown[]) => patch(...args),
  del: vi.fn(),
  getHealth: vi.fn(),
}))

const ENRICHMENT = {
  enabled: true,
  sources: ['acoustid', 'deezer'],
  paused_reason: null,
  last_run_at: null,
  last_result: null,
  gates: {},
  source_status: [
    { name: 'acoustid', enabled: true, ready: false, gated_on: 'ACOUSTID_API_KEY', gated_reason: 'no key', identifiable: false, states: {} },
    { name: 'deezer', enabled: true, ready: true, gated_on: null, gated_reason: null, identifiable: true, states: {} },
    { name: 'musicbrainz', enabled: true, ready: true, gated_on: null, gated_reason: null, identifiable: true, states: {} },
    { name: 'coverartarchive', enabled: true, ready: true, gated_on: null, gated_reason: null, identifiable: false, states: {} },
    { name: 'wikidata', enabled: true, ready: true, gated_on: null, gated_reason: null, identifiable: false, states: {} },
  ],
  states: {},
  scope: { albums: 30, artists: 2 },
  review_total: 0,
}

/** Three rows standing in for the server's thirty-three: one catalogue value,
 *  one enrichment value, and the refusal. */
const TAG_MAP = [
  {
    vorbis_field: 'TITLE',
    tag_key: 'title',
    id3_frame: 'TIT2',
    origin: 'qobuz',
    source_field: 'tracks.title (+ tracks.version)',
    conflict: 'qobuz',
    conflict_note: "Qobuz's own value, written on every download and re-tag.",
  },
  {
    vorbis_field: 'MUSICBRAINZ_ALBUMID',
    tag_key: 'musicbrainz_albumid',
    id3_frame: 'TXXX:MusicBrainz Album Id',
    origin: 'enrichment',
    source_field: 'album_metadata.mb_release_mbid',
    conflict: 'enrichment',
    conflict_note: 'Whichever rung matched exactly.',
  },
  {
    vorbis_field: 'LABEL',
    tag_key: 'label',
    id3_frame: 'TPUB',
    origin: 'qobuz',
    source_field: 'albums.label',
    conflict: 'never',
    conflict_note: 'Never overwritten. It is a naming-template token.',
  },
]

const CONSENSUS_RULE = {
  threshold: 0.51,
  tie_keeps: 'qobuz',
  writable_fields: ['release_type'],
  never_writable: ['genre', 'label'],
  note: 'More than half of the sources that had an opinion must agree.',
}

const ESTIMATE = {
  template: 'x',
  considered: 120,
  would_refile: 37,
  moves_directory: 31,
  in_place: 80,
  blocked: 2,
  frozen: 1,
  truncated: false,
  summary: '37 of 120 release(s) would be re-filed, 2 blocked, 1 frozen',
  level: 'warning',
}

let settings = makeSettings()
let meta: Record<string, unknown> = {}
let estimate: () => Promise<unknown> = () => Promise.resolve(ESTIMATE)

beforeEach(() => {
  settings = makeSettings()
  meta = { format_labels: {}, tag_map: TAG_MAP, consensus_rule: CONSENSUS_RULE }
  estimate = () => Promise.resolve(ESTIMATE)
  get.mockReset()
  patch.mockReset()
  patch.mockImplementation(() => Promise.resolve(settings))
  get.mockImplementation((path: string) => {
    if (path === '/settings') return Promise.resolve(settings)
    if (path === '/enrichment') return Promise.resolve(ENRICHMENT)
    if (path === '/meta') return Promise.resolve(meta)
    if (path === '/library/refile/estimate') return estimate()
    return Promise.reject(new Error(`no fixture for ${path}`))
  })
})

function mount() {
  return render(
    <TestProviders route="/rules">
      <Rules />
    </TestProviders>,
  )
}

describe('Rules', () => {
  it('draws the design’s four blocks', async () => {
    mount()
    expect(await screen.findByText('Path template')).toBeInTheDocument()
    expect(screen.getByText('Tag mapping')).toBeInTheDocument()
    expect(screen.getByText('Metadata source priority')).toBeInTheDocument()
    expect(screen.getByText('Structure & integrity')).toBeInTheDocument()
  })

  it('offers only tokens the renderer knows — no {bits}, no {rate}', async () => {
    mount()
    await screen.findByText('Path template')
    expect(screen.getByRole('button', { name: '{quality}' })).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: '{bits}' })).toBeNull()
    expect(screen.queryByRole('button', { name: '{rate}' })).toBeNull()
  })

  it('saves an edited template through the settings PATCH', async () => {
    const user = userEvent.setup()
    mount()
    await screen.findByText('Path template')
    await user.click(screen.getByRole('button', { name: '{year}' }))
    await user.click(screen.getByRole('button', { name: 'Save template' }))
    expect(patch).toHaveBeenCalledWith('/settings', {
      values: { naming_template: `${makeSettings().naming_template}{year}` },
    })
  })

  it('reorders the ladder by writing the whole list', async () => {
    const user = userEvent.setup()
    mount()
    await screen.findByText('Path template')
    await user.click(screen.getByRole('button', { name: 'Move deezer up' }))
    expect(patch).toHaveBeenCalledWith('/settings', {
      values: {
        enrichment_sources: ['deezer', 'acoustid', 'musicbrainz', 'coverartarchive', 'wikidata'],
      },
    })
  })

  it('says what a gated rung is waiting for, named as .env spells it', async () => {
    mount()
    expect(await screen.findByText('waiting on ACOUSTID_API_KEY')).toBeInTheDocument()
  })

  it('renders the five real overridable switches and their origin', async () => {
    mount()
    await screen.findByText('Structure & integrity')
    expect(screen.getByRole('switch', { name: /Measure file integrity/ })).toBeInTheDocument()
    expect(screen.getByRole('switch', { name: /Scan the library nightly/ })).toBeInTheDocument()
    expect(screen.getByRole('switch', { name: /Write enrichment into files/ })).toBeInTheDocument()
    expect(screen.getByRole('switch', { name: /artist.nfo/ })).toBeInTheDocument()
    expect(screen.getByRole('switch', { name: /superseded/ })).toBeInTheDocument()
    expect(screen.getAllByText('from .env').length).toBeGreaterThan(0)
  })

  it('offers Reset only where the value really came from an override', async () => {
    settings = makeSettings({
      origins: { ...makeSettings().origins, naming_template: 'override' },
    })
    mount()
    await screen.findByText('Path template')
    expect(screen.getAllByRole('button', { name: 'Reset' })).toHaveLength(1)
  })

  it('an empty naming_preview is a refusal to render, not a blank box', async () => {
    settings = makeSettings({ naming_preview: [] })
    mount()
    expect(await screen.findByText(/could not render this template/)).toBeInTheDocument()
  })

  it('renders one row per published tag-map entry', async () => {
    mount()
    await screen.findByText('Tag mapping')
    expect(screen.getByText('Vorbis field')).toBeInTheDocument()
    expect(screen.getByText('On conflict')).toBeInTheDocument()

    expect(await screen.findByText('MUSICBRAINZ_ALBUMID')).toBeInTheDocument()
    // The column names the store the value is read from, not the rung that
    // matched — that is a per-release fact and lives on the album screen.
    expect(screen.getByText('album_metadata.mb_release_mbid')).toBeInTheDocument()
    expect(screen.getByText('MP3 TXXX:MusicBrainz Album Id')).toBeInTheDocument()
    // The refusal is stated, once, in the server's own words.
    expect(screen.getByText(/naming-template token/)).toBeInTheDocument()
    expect(screen.getByText(/genre, label/)).toBeInTheDocument()
  })

  it('says nothing about the tag map until meta arrives', async () => {
    meta = { format_labels: {} }
    mount()
    await screen.findByText('Tag mapping')
    expect(await screen.findByText('Loading the tag map…')).toBeInTheDocument()
    // No fabricated rows: there is deliberately no local copy of the table.
    expect(screen.queryByText('MUSICBRAINZ_ALBUMID')).toBeNull()
    expect(screen.queryByText(/naming-template token/)).toBeNull()
  })

  it('leaves no gaps — the re-file count was the last one', async () => {
    const { container } = mount()
    await screen.findByText('Tag mapping')
    expect(container.querySelectorAll('[data-placeholder]')).toHaveLength(0)
  })

  // ---- the library-wide re-file count -------------------------------------

  it('issues no estimate request until the button is pressed', async () => {
    mount()
    await screen.findByText('Path template')
    expect(screen.getByRole('button', { name: /Count releases/ })).toBeInTheDocument()
    expect(get.mock.calls.map((call) => call[0])).not.toContain(
      '/library/refile/estimate',
    )
  })

  it('counts the typed template, not the saved one', async () => {
    const user = userEvent.setup()
    mount()
    await screen.findByText('Path template')
    await user.click(screen.getByRole('button', { name: '{year}' }))
    await user.click(screen.getByRole('button', { name: /Count releases/ }))
    await screen.findByText(/releases would be re-filed/)
    const call = get.mock.calls.find((entry) => entry[0] === '/library/refile/estimate')
    expect(call?.[1]).toEqual({
      template: `${makeSettings().naming_template}{year}`,
    })
  })

  it('shows the count, the blocked figure and the frozen figure', async () => {
    const user = userEvent.setup()
    mount()
    await screen.findByText('Path template')
    await user.click(screen.getByRole('button', { name: /Count releases/ }))
    expect(await screen.findByText('37')).toBeInTheDocument()
    expect(await screen.findByText(/120 releases would be re-filed/)).toBeInTheDocument()
    expect(screen.getByText(/31 folders move/)).toBeInTheDocument()
    expect(screen.getByText(/2 blocked/)).toBeInTheDocument()
    expect(screen.getByText(/1 frozen/)).toBeInTheDocument()
  })

  it('says the figure is for a different template after further typing', async () => {
    const user = userEvent.setup()
    mount()
    await screen.findByText('Path template')
    await user.click(screen.getByRole('button', { name: /Count releases/ }))
    await screen.findByText(/releases would be re-filed/)
    expect(screen.queryByText(/counted for a different template/)).toBeNull()
    await user.click(screen.getByRole('button', { name: '{year}' }))
    expect(await screen.findByText(/counted for a different template/)).toBeInTheDocument()
  })

  it('takes the truncation figure off the answer, never a hard-coded cap', async () => {
    // The walk's limit is the endpoint's parameter. A screen that spelled the
    // current default in its own copy would go on reading right long after the
    // default moved, so the sentence is built from what came back.
    const user = userEvent.setup()
    estimate = () =>
      Promise.resolve({ ...ESTIMATE, considered: 40, truncated: true, level: 'warning' })
    mount()
    await screen.findByText('Path template')
    await user.click(screen.getByRole('button', { name: /Count releases/ }))
    expect(await screen.findByText(/first 40 releases only/)).toBeInTheDocument()
    expect(screen.queryByText(/first 2,?000 releases only/)).toBeNull()
  })

  it('refuses to count an emptied box rather than counting the saved template', async () => {
    // `template=` reaches the endpoint as OptStrQuery, which reads a blank as
    // *no filter* — so a press here would answer about the template still in
    // force while the box shows nothing.
    const user = userEvent.setup()
    settings = makeSettings({ naming_template: '{artist}' })
    mount()
    await screen.findByText('Path template')
    await user.click(screen.getByRole('button', { name: /token/ }))
    expect(await screen.findByText('(empty)')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: /Count releases/ })).toBeDisabled()
    expect(get.mock.calls.map((call) => call[0])).not.toContain(
      '/library/refile/estimate',
    )
  })

  it('renders the error message when the estimate fails', async () => {
    const user = userEvent.setup()
    estimate = () => Promise.reject(new Error('That template could not be rendered.'))
    mount()
    await screen.findByText('Path template')
    await user.click(screen.getByRole('button', { name: /Count releases/ }))
    expect(
      await screen.findByText('That template could not be rendered.'),
    ).toBeInTheDocument()
  })
})
