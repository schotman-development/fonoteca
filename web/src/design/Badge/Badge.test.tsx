import { render, screen } from '@testing-library/react'
import { describe, expect, it } from 'vitest'

import { Badge } from '@/design/Badge/Badge'

describe('Badge', () => {
  it('renders the word it was given', () => {
    render(<Badge>CORRUPT</Badge>)

    expect(screen.getByText('CORRUPT')).toBeInTheDocument()
  })

  it('rests at neutral, so a merely descriptive badge is not a warning', () => {
    render(<Badge>MONO</Badge>)

    expect(screen.getByText('MONO')).toHaveAttribute('data-tone', 'neutral')
  })

  it('takes a tone from the caller and marks it', () => {
    const { rerender } = render(<Badge tone="bad">CORRUPT</Badge>)
    const bad = screen.getByText('CORRUPT')
    expect(bad).toHaveAttribute('data-tone', 'bad')
    const badClasses = bad.className

    rerender(<Badge tone="warn">CORRUPT</Badge>)
    const warn = screen.getByText('CORRUPT')
    expect(warn).toHaveAttribute('data-tone', 'warn')
    // Two tones are two classes: if they collapsed to one, every status on the
    // artwork wall would be painted the same.
    expect(warn.className).not.toBe(badClasses)
  })

  it('gives each tone its own class and neutral none of them', () => {
    const seen = new Set<string>()
    for (const tone of ['accent', 'ok', 'warn', 'bad'] as const) {
      const { container, unmount } = render(<Badge tone={tone}>X</Badge>)
      const cls = container.firstElementChild?.className ?? ''
      expect(seen.has(cls)).toBe(false)
      seen.add(cls)
      unmount()
    }

    const { container } = render(<Badge>X</Badge>)
    const neutral = container.firstElementChild?.className ?? ''
    expect(seen.has(neutral)).toBe(false)
    // Neutral is the base class alone — no tone class is added for it.
    expect(neutral.split(' ').filter(Boolean).length).toBe(1)
  })

  it('is not a control — it labels, it does not act', () => {
    render(<Badge tone="bad">CORRUPT</Badge>)

    expect(screen.queryByRole('button')).not.toBeInTheDocument()
  })

  it('passes a caller class through so a screen can place it', () => {
    const { container } = render(<Badge className="placed">X</Badge>)

    expect(container.firstElementChild).toHaveClass('placed')
  })
})
