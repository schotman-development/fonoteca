import { render, screen } from '@testing-library/react'
import { describe, expect, it } from 'vitest'

import type { TrackIntegrityOut } from '@/api/types'
import { EM_DASH } from '@/format'
import { makeTrack } from '@/test/factories'
import { TrackRow } from '@/widgets/TrackRow/TrackRow'

/** The integrity block the API builds beside a track, with nothing measured. */
function integrity(overrides: Partial<TrackIntegrityOut> = {}): TrackIntegrityOut {
  return {
    qid: 'qid-1',
    state: 'unknown',
    content_hash: null,
    sample_count: null,
    file_size: null,
    file_mtime: null,
    last_verified_at: null,
    fingerprint_state: null,
    ...overrides,
  }
}

describe('TrackRow', () => {
  it('draws the number, the title and the duration', () => {
    render(
      <TrackRow track={makeTrack({ title: 'So What', track_number: 1, duration: 545 })} index={0} />,
    )

    expect(screen.getByText('01')).toBeInTheDocument()
    expect(screen.getByText('So What')).toBeInTheDocument()
    expect(screen.getByText('9m 05s')).toBeInTheDocument()
  })

  it('UNKNOWN is not a pass: it renders the em dash, never a tick', () => {
    // The single worst thing this row could do is tell somebody a file is
    // verified when nothing has ever measured it.
    render(
      <TrackRow
        track={makeTrack({ integrity: integrity({ state: 'unknown' }) })}
        index={0}
      />,
    )

    expect(screen.getByText(EM_DASH)).toBeInTheDocument()
    expect(screen.queryByText('✓')).not.toBeInTheDocument()
    expect(screen.getByText(/no baseline has ever been taken/i)).toBeInTheDocument()
  })

  it('a null integrity block is unmeasured too, not verified', () => {
    render(<TrackRow track={makeTrack({ integrity: null })} index={0} />)

    expect(screen.getByText(EM_DASH)).toBeInTheDocument()
    expect(screen.queryByText('✓')).not.toBeInTheDocument()
  })

  it('renders the tick only for a real `verified`', () => {
    render(
      <TrackRow
        track={makeTrack({ integrity: integrity({ state: 'verified' }) })}
        index={0}
      />,
    )

    expect(screen.getByText('✓')).toBeInTheDocument()
    expect(screen.getByText(/verified against the recorded baseline/i)).toBeInTheDocument()
  })

  it('a corrupt fingerprint outranks whatever the hash says', () => {
    render(
      <TrackRow
        track={makeTrack({
          // The hash matches its baseline and the audio still does not decode.
          integrity: integrity({ state: 'verified', fingerprint_state: 'corrupt' }),
        })}
        index={0}
      />,
    )

    expect(screen.getByText('CRC')).toBeInTheDocument()
    expect(screen.queryByText('✓')).not.toBeInTheDocument()
  })

  it('keeps `retagged` and `replaced` apart — they license different work', () => {
    const { rerender } = render(
      <TrackRow
        track={makeTrack({ integrity: integrity({ state: 'retagged' }) })}
        index={0}
      />,
    )
    expect(screen.getByText('retagged')).toBeInTheDocument()

    rerender(
      <TrackRow
        track={makeTrack({ integrity: integrity({ state: 'replaced' }) })}
        index={0}
      />,
    )
    expect(screen.getByText('replaced')).toBeInTheDocument()
    expect(screen.queryByText('retagged')).not.toBeInTheDocument()
  })

  it('says `missing` when the row is here and the file is not', () => {
    render(
      <TrackRow
        track={makeTrack({ integrity: integrity({ state: 'missing' }) })}
        index={0}
      />,
    )

    expect(screen.getByText('missing')).toBeInTheDocument()
  })

  it('prefers the track’s own number over its position in the list', () => {
    // Disc two, track one: 01, not 13.
    render(<TrackRow track={makeTrack({ track_number: 1 })} index={12} />)

    expect(screen.getByText('01')).toBeInTheDocument()
    expect(screen.queryByText('13')).not.toBeInTheDocument()
  })

  it('falls back to the position when the file carries no number', () => {
    render(<TrackRow track={makeTrack({ track_number: 0 })} index={6} />)

    expect(screen.getByText('07')).toBeInTheDocument()
  })

  it('renders the em dash for an unknown duration, never `0s`', () => {
    render(<TrackRow track={makeTrack({ duration: null, integrity: null })} index={0} />)

    // Two of them now — the duration and the unmeasured state.
    expect(screen.getAllByText(EM_DASH)).toHaveLength(2)
  })

  it('gives the glyph a word, because a tick is announced as nothing', () => {
    const { container } = render(
      <TrackRow
        track={makeTrack({ integrity: integrity({ state: 'verified' }) })}
        index={0}
      />,
    )

    expect(container.querySelector('[aria-hidden="true"]')?.textContent).toBe('✓')
    expect(container.querySelector('.visuallyHidden')?.textContent).toMatch(/verified/i)
  })
})
