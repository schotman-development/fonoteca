import { render, screen } from '@testing-library/react'
import { describe, expect, it } from 'vitest'

import { JobRow } from '@/widgets/JobRow/JobRow'

describe('JobRow', () => {
  it('draws the label, the detail and a named bar', () => {
    render(
      <JobRow
        label="Downloading Kind of Blue"
        detail="9 of 12 tracks"
        value={0.75}
        tone="ok"
      />,
    )

    expect(screen.getByText('Downloading Kind of Blue')).toBeInTheDocument()
    expect(screen.getByText('9 of 12 tracks')).toBeInTheDocument()

    const bar = screen.getByRole('progressbar', { name: 'Downloading Kind of Blue' })
    expect(bar).toHaveAttribute('aria-valuenow', '75')
  })

  it('a running job with no percentage is indeterminate, NOT a zero-width bar', () => {
    // A library scan publishes no percentage. "0% done" is false and is what
    // makes somebody press the button a second time.
    render(<JobRow label="Scanning the library" detail="running" value={null} indeterminate tone="warn" />)

    const bar = screen.getByRole('progressbar')
    expect(bar).not.toHaveAttribute('aria-valuenow')
    expect(bar).not.toHaveAttribute('aria-valuetext')
  })

  it('omits aria-valuenow for an unmeasured job rather than sending 0', () => {
    render(<JobRow label="Enriching" detail="queued" value={null} tone="accent" />)

    expect(screen.getByRole('progressbar')).not.toHaveAttribute('aria-valuenow')
  })

  it('lets `indeterminate` win over a value, so a caller need not null one out', () => {
    render(
      <JobRow label="Verifying" detail="running" value={0.4} indeterminate tone="accent" />,
    )

    expect(screen.getByRole('progressbar')).not.toHaveAttribute('aria-valuenow')
  })

  it('reads the percentage back with the detail beside it', () => {
    render(<JobRow label="Downloading" detail="9 of 12 tracks" value={0.75} tone="ok" />)

    expect(screen.getByRole('progressbar')).toHaveAttribute(
      'aria-valuetext',
      '75% — 9 of 12 tracks',
    )
  })

  it('is not a link and not a button — a job is a thing happening, not an address', () => {
    render(<JobRow label="Downloading" detail="running" value={0.2} tone="ok" />)

    expect(screen.queryByRole('link')).not.toBeInTheDocument()
    expect(screen.queryByRole('button')).not.toBeInTheDocument()
  })

  it('draws a real 0% when the job genuinely has done nothing', () => {
    // The distinction the null is protecting: this one IS measured.
    render(<JobRow label="Downloading" detail="0 of 12 tracks" value={0} tone="ok" />)

    expect(screen.getByRole('progressbar')).toHaveAttribute('aria-valuenow', '0')
  })
})
