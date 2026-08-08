import { render, screen } from '@testing-library/react'
import { describe, expect, it } from 'vitest'

import { EmptyState } from '@/design/EmptyState/EmptyState'

describe('EmptyState', () => {
  it('renders the title and the note', () => {
    render(
      <EmptyState
        glyph="✓"
        title="Queue clear"
        note="12 releases written to disk and re-structured."
      />,
    )

    expect(screen.getByText('Queue clear')).toBeInTheDocument()
    expect(
      screen.getByText('12 releases written to disk and re-structured.'),
    ).toBeInTheDocument()
  })

  it('hides the glyph from assistive technology', () => {
    const { container } = render(<EmptyState glyph="✓" title="Queue clear" />)

    // `✓` announces as "check mark" ahead of the title, which buys nothing: the
    // title already says it. So the accessible text is the title alone.
    expect(container.querySelector('[aria-hidden="true"]')?.textContent).toBe(
      '✓',
    )
    expect(screen.getByText('Queue clear')).toBeInTheDocument()
  })

  it('is not a heading — it stands inside a section that is already headed', () => {
    render(<EmptyState glyph="◎" title="Nothing to identify" />)

    expect(screen.queryByRole('heading')).not.toBeInTheDocument()
  })

  it('renders nothing for an absent note rather than an empty paragraph', () => {
    const { container } = render(<EmptyState glyph="✓" title="Queue clear" />)

    expect(container.querySelectorAll('p')).toHaveLength(1)
  })

  it('renders the action slot only when one was given', () => {
    const { rerender } = render(
      <EmptyState
        glyph="✓"
        title="Queue clear"
        action={<button type="button">Run a scan</button>}
      />,
    )
    expect(
      screen.getByRole('button', { name: 'Run a scan' }),
    ).toBeInTheDocument()

    rerender(<EmptyState glyph="✓" title="Queue clear" />)
    expect(screen.queryByRole('button')).not.toBeInTheDocument()
  })

  it('is a statement, not a live region', () => {
    const { container } = render(<EmptyState glyph="✓" title="Queue clear" />)

    // The mutation that emptied the list is what announces; this is a state of
    // the page, and a status role here would re-announce on every refetch.
    expect(container.querySelector('[role="status"]')).toBeNull()
    expect(container.querySelector('[aria-live]')).toBeNull()
  })

  it('passes a caller class through so a screen can place it', () => {
    const { container } = render(
      <EmptyState glyph="✓" title="Queue clear" className="placed" />,
    )

    expect(container.firstElementChild).toHaveClass('placed')
  })
})
