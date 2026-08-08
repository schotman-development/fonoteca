import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { render, screen } from '@testing-library/react'
import type { ReactNode } from 'react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

import { queryKeys } from '@/api/queries'
import { ReleaseRow } from '@/widgets/ReleaseRow/ReleaseRow'
import { makeAlbum } from '@/test/factories'

const LABELS = { '6': 'FLAC 16bit 44.1kHz', '7': 'FLAC 24bit 96kHz' }

function withMeta({ children }: { children: ReactNode }) {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  client.setQueryData(queryKeys.meta(), { format_labels: LABELS })
  return <QueryClientProvider client={client}>{children}</QueryClientProvider>
}

function mount(node: ReactNode) {
  return render(node, { wrapper: withMeta })
}

/* The note line is a function of *today*, so the clock is frozen. Every date in
   this file is read against 2026-08-05. */
const TODAY = new Date('2026-08-05T12:00:00Z')

describe('ReleaseRow', () => {
  beforeEach(() => {
    vi.useFakeTimers()
    vi.setSystemTime(TODAY)
  })

  afterEach(() => {
    vi.useRealTimers()
  })

  it('draws the release, the credit and the facts line', () => {
    mount(
      <ReleaseRow
        album={makeAlbum({
          title: 'Down the Road Wherever',
          artist_name: 'Mark Knopfler',
          release_date: '2018-11-16',
          tracks_count: 14,
          owned_format_id: 6,
        })}
      />,
    )

    expect(screen.getByText('Down the Road Wherever')).toBeInTheDocument()
    expect(screen.getByText('Mark Knopfler')).toBeInTheDocument()
    expect(
      screen.getByText('2018-11-16 · 14 tracks · FLAC 16bit 44.1kHz'),
    ).toBeInTheDocument()
  })

  it('dates the row from its RELEASE date, never from when it was downloaded', () => {
    // The bug this row shipped with: the column was the download queue, so its
    // facts line was the moment a transfer finished. A 1959 record fetched this
    // morning has to read 1959 here or the ordering above it is unreadable.
    mount(
      <ReleaseRow
        album={makeAlbum({
          release_date: '1959-08-17',
          downloaded_at: '2026-08-05T09:00:00Z',
          added_at: '2026-08-01T09:00:00Z',
          status: 'downloaded',
          tracks_count: 0,
        })}
      />,
    )

    expect(screen.getByText('1959-08-17')).toBeInTheDocument()
    expect(screen.queryByText(/2026-08/)).not.toBeInTheDocument()
  })

  it('omits the tracks clause rather than saying "0 tracks"', () => {
    mount(<ReleaseRow album={makeAlbum({ tracks_count: 0, release_date: '2018-11-16' })} />)

    expect(screen.getByText('2018-11-16')).toBeInTheDocument()
    expect(screen.queryByText(/0 tracks/)).not.toBeInTheDocument()
  })

  it('reads queue_state before status, so an upgrade is not drawn as "In library"', () => {
    // `queue_album()` leaves a downloaded album's status alone while it is being
    // upgraded, so `status` alone says "In library" over a live download.
    mount(
      <ReleaseRow
        album={makeAlbum({ status: 'downloaded', queue_state: 'active' })}
      />,
    )

    expect(screen.getByText('Downloading')).toBeInTheDocument()
    expect(screen.queryByText('In library')).not.toBeInTheDocument()
  })

  it('names each album status', () => {
    const cases = [
      ['downloaded', 'In library'],
      ['downloading', 'Downloading'],
      ['queued', 'Queued'],
      ['failed', 'Failed'],
      ['skipped', 'Ignored'],
      ['wanted', 'Wanted'],
    ] as const

    for (const [status, label] of cases) {
      const { unmount } = mount(<ReleaseRow album={makeAlbum({ status })} />)
      expect(screen.getByText(label)).toBeInTheDocument()
      unmount()
    }
  })

  it('notes an upcoming release rather than pretending it is out', () => {
    mount(<ReleaseRow album={makeAlbum({ release_date: '2026-08-16' })} />)

    expect(screen.getByText('out in 11 days')).toBeInTheDocument()
  })

  it('says "out today" for a release dated today, in the local calendar', () => {
    // `new Date('2026-08-05')` is midnight UTC, which by lunchtime is hours in
    // the past — `fmtAgo` would call that "1h ago" and a naive comparison would
    // call it yesterday west of Greenwich.
    mount(<ReleaseRow album={makeAlbum({ release_date: '2026-08-05' })} />)

    expect(screen.getByText('out today')).toBeInTheDocument()
  })

  it('says nothing about an old release', () => {
    const { container } = mount(
      <ReleaseRow album={makeAlbum({ release_date: '1959-08-17' })} />,
    )

    expect(screen.queryByText(/^out /)).not.toBeInTheDocument()
    // The state word survives; only the note is dropped.
    expect(container.textContent).toContain('Wanted')
  })

  it('draws no progress bar — that data lives on a queue item', () => {
    mount(<ReleaseRow album={makeAlbum({ queue_state: 'active' })} />)

    expect(screen.queryByRole('progressbar')).not.toBeInTheDocument()
  })

  it('prefers the enriched cover over the catalogue image', () => {
    const { container } = mount(
      <ReleaseRow
        album={makeAlbum({
          cover_url: 'https://example.invalid/front.jpg',
          image_url: 'https://example.invalid/qobuz.jpg',
        })}
      />,
    )

    expect(container.querySelector('img')).toHaveAttribute(
      'src',
      'https://example.invalid/front.jpg',
    )
  })

  it('colours the artwork from the album id, and renders the em dash for no credit', () => {
    const { container } = mount(
      <ReleaseRow album={makeAlbum({ artist_name: null, tracks_count: 0 })} />,
    )

    expect(container.querySelector('[data-shape="square"]')).not.toBeNull()
    expect(screen.getAllByText('—').length).toBeGreaterThan(0)
  })
})
