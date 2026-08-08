/**
 * The frame, mounted whole against a mocked `client.ts`.
 *
 * It asserts the things the design fixes and a refactor silently loses: the six
 * nav entries in order, which one is `aria-current`, the badge appearing only
 * once there is something to count, the version footer, the footer's real
 * counts, the capacity meter reading the VOLUME rather than the library, and
 * the banner stack — which the design draws nowhere and which is the one
 * deliberate addition in this layer.
 */

import { render, screen, within } from '@testing-library/react'
import { beforeEach, describe, expect, it, vi } from 'vitest'

import { AppShell } from '@/shell/AppShell'
import { TestProviders } from '@/test/providers'

const get = vi.fn()

vi.mock('@/api/client', () => ({
  get: (...args: unknown[]) => get(...args),
  post: vi.fn(),
  patch: vi.fn(),
  del: vi.fn(),
  getHealth: vi.fn(),
}))

const STATUS = {
  version: '0.4.0',
  started_at: '2026-08-04T08:00:00Z',
  uptime_seconds: 900,
  library: {
    artists: 1318,
    monitored_artists: 900,
    albums: 7412,
    wanted_albums: 40,
    downloaded_albums: 6100,
    failed_albums: 2,
    tracks: 100482,
    downloaded_tracks: 100000,
    // The library's own bytes — deliberately NOT the disk's used figure below,
    // and the tests keep them apart.
    size_bytes: 4_297_064_448_000,
  },
  queue: {
    pending: 0,
    active: 0,
    done: 12,
    failed: 0,
    cancelled: 0,
    total: 12,
    current_album_id: null,
    current_album_title: null,
    worker_running: false,
  },
  indexer: { enabled: true, running: false, paused: false },
  rate_limit: null,
  recent_activity: [],
  credentials_ok: false,
  app_secret_ok: true,
  library_path: '/srv/music',
  disk: {
    path: '/srv/music',
    total_bytes: 6_597_069_766_656,
    used_bytes: 4_683_743_612_928,
    free_bytes: 1_800_000_000_000,
  },
  banners: [
    {
      level: 'error',
      code: 'no_credentials',
      title: 'No Qobuz credentials',
      message: 'Set QOBUZ_EMAIL and QOBUZ_PASSWORD, then restart.',
    },
  ],
}

const FIXTURES: Record<string, unknown> = {
  '/status': STATUS,
  '/nav-counts': { artists: 1318, wanted: 40, queue: 0, trash: 0, enrichment_review: 5 },
  '/library/scan': {
    running: false,
    library_path: '/srv/music',
    nightly: true,
    complete_ratio: 0.9,
    last: null,
  },
  '/integrity': {
    enabled: true,
    reverify_fraction: 0.033,
    tracks_total: 100482,
    states: { verified: 90000 },
    never_baselined: 10482,
    corrupt_files: 0,
    corrupt_muted: 0,
    albums_reopened: 0,
    last_run_at: null,
    last_result: null,
    running: false,
  },
  '/settings': { host: '0.0.0.0', port: 7373, app_version: '0.4.0' },
}

function mount(route = '/') {
  return render(
    <TestProviders route={route}>
      <AppShell>
        <section data-testid="screen">the screen</section>
      </AppShell>
    </TestProviders>,
  )
}

beforeEach(() => {
  get.mockReset()
  get.mockImplementation((path: string) => {
    const fixture = FIXTURES[path]
    if (fixture === undefined) return Promise.reject(new Error(`no fixture for ${path}`))
    return Promise.resolve(fixture)
  })
})

describe('AppShell', () => {
  it('renders the wordmark, the command trigger and the scan button (design 28–41)', async () => {
    mount()
    expect(screen.getByRole('link', { name: 'qobuzarr' })).toHaveAttribute('href', '/')
    expect(
      screen.getByRole('button', { name: /Search or run a command/ }),
    ).toBeInTheDocument()
    expect(await screen.findByRole('button', { name: /Scan library/ })).toBeInTheDocument()
  })

  it('renders every nav entry in order and marks the current one', async () => {
    mount('/radar')
    const nav = screen.getByRole('navigation', { name: 'Sections' })
    expect(within(nav).getAllByRole('link').map((a) => a.textContent?.trim())).toEqual([
      '◱Dashboard',
      '▤Library',
      '◎Release radar',
      '◌Missing releases',
      '⇣Download queue',
      '⌗Identify',
      '⌥Structure & tags',
      '≡Activity',
    ])
    expect(within(nav).getByRole('link', { name: /Release radar/ })).toHaveAttribute(
      'aria-current',
      'page',
    )
  })

  it('keeps the Library entry current on an artist page', () => {
    mount('/library/uyej1o165e870')
    const nav = screen.getByRole('navigation', { name: 'Sections' })
    expect(within(nav).getByRole('link', { name: /Library/ })).toHaveAttribute(
      'aria-current',
      'page',
    )
    expect(within(nav).getByRole('link', { name: /Dashboard/ })).not.toHaveAttribute(
      'aria-current',
    )
  })

  it('badges Identify from nav-counts once they arrive, and nothing before', async () => {
    mount()
    const nav = screen.getByRole('navigation', { name: 'Sections' })
    // First frame: the counts are in flight, so no badge exists at all.
    expect(within(nav).queryByText('5')).toBeNull()
    expect(await within(nav).findByText('5')).toBeInTheDocument()
    expect(within(nav).getByRole('link', { name: /Identify/ })).toHaveTextContent('5')
  })

  it('shows the server’s version in the sidebar footer (design 72)', async () => {
    mount()
    expect(await screen.findByText('v0.4.0')).toBeInTheDocument()
  })

  it('renders the banner payload the design draws nowhere', async () => {
    mount()
    expect(await screen.findByText('No Qobuz credentials')).toBeInTheDocument()
    expect(
      screen.getByText('Set QOBUZ_EMAIL and QOBUZ_PASSWORD, then restart.'),
    ).toBeInTheDocument()
  })

  it('renders the footer’s real counts and the configured address (design 843–844)', async () => {
    mount()
    expect(await screen.findByText(/100,482 tracks/)).toHaveTextContent(
      '100,482 tracks · 6,100 of 7,412 albums · 1,318 artists · 3.9 TiB',
    )
    expect(screen.getByText('0.0.0.0:7373')).toBeInTheDocument()
  })

  it('renders the volume’s fullness from statvfs, not the library’s own size', async () => {
    const { container } = mount()
    // 4.68 TB used of 6.6 TB — the DISK. The library's own 4.30 TB is a
    // different fact and sits in the counts group; if this bar ever reads
    // 3.9 TiB somebody has made one number stand in for the other.
    expect(await screen.findByText('4.3 TiB / 6.0 TiB')).toBeInTheDocument()
    expect(screen.queryByText('3.9 TiB / 6.0 TiB')).toBeNull()
    const meter = screen.getByRole('progressbar', { name: 'Disk capacity' })
    expect(meter).toHaveAttribute('aria-valuenow', '71')
    expect(container.querySelectorAll('[data-placeholder]')).toHaveLength(0)
  })

  it('draws an empty meter and no figures when the disk cannot be probed', async () => {
    FIXTURES['/status'] = { ...STATUS, disk: null }
    try {
      mount()
      // `— / —`: nothing measured the volume, and a 0%-full disk would be an
      // invention rather than a reading.
      expect(await screen.findByText('— / —')).toBeInTheDocument()
      const meter = screen.getByRole('progressbar', { name: 'Disk capacity' })
      expect(meter).not.toHaveAttribute('aria-valuenow')
    } finally {
      FIXTURES['/status'] = STATUS
    }
  })

  it('says the machine is idle, and never invents a last-scan time', async () => {
    mount()
    expect(await screen.findByText('Idle · never scanned')).toBeInTheDocument()
  })

  it('renders the outlet and offers a skip link to it', () => {
    mount()
    expect(screen.getByTestId('screen')).toBeInTheDocument()
    const skip = screen.getByRole('link', { name: 'Skip to content' })
    expect(skip).toHaveAttribute('href', '#main-content')
    expect(screen.getByRole('main')).toHaveAttribute('id', 'main-content')
  })
})
