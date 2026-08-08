import { render, screen } from '@testing-library/react'
import type { ReactNode } from 'react'
import { MemoryRouter } from 'react-router-dom'
import { describe, expect, it } from 'vitest'

import { TodoRow } from '@/widgets/TodoRow/TodoRow'

function routed(children: ReactNode) {
  return render(<MemoryRouter>{children}</MemoryRouter>)
}

describe('TodoRow', () => {
  it('draws the count, what they are and what to do', () => {
    routed(
      <TodoRow
        count={4}
        title="identities to confirm"
        action="Review queue"
        tone="warn"
        to="/identify"
      />,
    )

    expect(screen.getByText('4')).toBeInTheDocument()
    expect(screen.getByText('identities to confirm')).toBeInTheDocument()
    expect(screen.getByText('Review queue')).toBeInTheDocument()
  })

  it('is one link over the whole row, not a button that navigates', () => {
    routed(
      <TodoRow count={3} title="corrupt files" action="Quarantine" tone="bad" to="/activity" />,
    )

    const links = screen.getAllByRole('link')
    expect(links).toHaveLength(1)
    expect(links[0]).toHaveAttribute('href', '/activity')
    // Middle-click, copy-link and the back button all keep working.
    expect(screen.queryByRole('button')).not.toBeInTheDocument()
  })

  it('renders a zero rather than hiding itself — the caller decides that', () => {
    routed(<TodoRow count={0} title="downloads failed" action="Retry" tone="bad" to="/activity" />)

    expect(screen.getByText('0')).toBeInTheDocument()
  })

  it('colours the count and leaves the title alone', () => {
    const { container } = routed(
      <TodoRow count={7} title="releases missing" action="Download" tone="warn" to="/library" />,
    )

    const count = screen.getByText('7')
    expect(count.className).toMatch(/countWarn/)
    // A five-row list must not read as an alarm however small the numbers are.
    expect(container.querySelector('[class*="title"]')?.className).not.toMatch(/Warn/)
  })

  it('takes every tone without inventing a sixth', () => {
    const tones = ['neutral', 'accent', 'ok', 'warn', 'bad'] as const

    for (const tone of tones) {
      const { unmount } = routed(
        <TodoRow count={1} title="t" action="a" tone={tone} to="/" />,
      )
      expect(screen.getByText('1')).toBeInTheDocument()
      unmount()
    }
  })
})
