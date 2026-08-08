import { render, screen } from '@testing-library/react'
import { describe, expect, it } from 'vitest'

import { Badge } from '@/design/Badge/Badge'
import { SpecBadge } from '@/design/SpecBadge/SpecBadge'

describe('SpecBadge', () => {
  it('renders the figure it was given, verbatim', () => {
    render(<SpecBadge>24/96</SpecBadge>)

    expect(screen.getByText('24/96')).toBeInTheDocument()
  })

  it('does not interpret the figure — it is already a string', () => {
    // fmtQuality answers the em dash for an unknown format, and this must show
    // exactly that rather than second-guessing it into a number or a blank.
    render(<SpecBadge>—</SpecBadge>)

    expect(screen.getByText('—')).toBeInTheDocument()
  })

  it('is a different object from Badge, not a variant of it', () => {
    const { container: spec } = render(<SpecBadge>24/96</SpecBadge>)
    const { container: badge } = render(<Badge tone="warn">24/96</Badge>)

    expect(spec.firstElementChild?.className).not.toBe(
      badge.firstElementChild?.className,
    )
  })

  it('offers no tone, so a quality figure can only ever be the one colour', () => {
    // A compile-time fact, asserted at runtime by the only observable it has:
    // the class list is the base class and whatever the caller added, never a
    // tone class.
    const { container } = render(<SpecBadge>16/44.1</SpecBadge>)

    expect(
      (container.firstElementChild?.className ?? '').split(' ').filter(Boolean),
    ).toHaveLength(1)
  })

  it('is not a control', () => {
    render(<SpecBadge>24/192</SpecBadge>)

    expect(screen.queryByRole('button')).not.toBeInTheDocument()
  })

  it('passes a caller class through so a screen can place it', () => {
    const { container } = render(<SpecBadge className="placed">24/96</SpecBadge>)

    expect(container.firstElementChild).toHaveClass('placed')
  })
})
