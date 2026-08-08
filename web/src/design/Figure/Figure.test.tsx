import { render, screen } from '@testing-library/react'
import { describe, expect, it } from 'vitest'

import label from '@/design/shared/label.module.css'
import { EM_DASH } from '@/format'

import { Figure } from '@/design/Figure/Figure'
import styles from '@/design/Figure/Figure.module.css'

/**
 * A CSS-module class is `string | undefined` under `noUncheckedIndexedAccess`,
 * and `undefined` reaching `toHaveClass` would assert nothing while passing.
 * Failing loudly on a renamed or misspelt class is the point.
 */
function recipe(name: string | undefined): string {
  if (name === undefined) throw new Error('the Figure class is missing')
  return name
}

describe('Figure', () => {
  it('renders the value over its caption', () => {
    render(<Figure value="99.1%" caption="Identified" />)

    expect(screen.getByText('99.1%')).toBeInTheDocument()
    expect(screen.getByText('Identified')).toBeInTheDocument()
  })

  it('renders a caller-supplied em dash verbatim and never a zero', () => {
    // Two of the three figures the design draws here are three-valued at the
    // source. "Nothing has counted this" is the em dash, and a `0` in its place
    // claims a measurement that was never taken.
    const { container } = render(<Figure value={EM_DASH} caption="Hi-Res" />)

    expect(screen.getByText(EM_DASH)).toBeInTheDocument()
    expect(container.textContent).not.toContain('0')
  })

  it('does not format the value — a node arrives as it was passed', () => {
    render(<Figure value={<span data-testid="composed">142</span>} caption="Flagged" />)

    expect(screen.getByTestId('composed')).toHaveTextContent('142')
  })

  it('puts the tone on the figure and never on the caption', () => {
    render(<Figure value="142" caption="Flagged" tone="warn" />)

    expect(screen.getByText('142')).toHaveClass(recipe(styles.toneWarn))
    expect(screen.getByText('Flagged')).not.toHaveClass(recipe(styles.toneWarn))
  })

  it('paints nothing by default — no colour is the resting state', () => {
    render(<Figure value="142" caption="Flagged" />)

    expect(screen.getByText('142')).toHaveClass(recipe(styles.toneNeutral))
  })

  it('is the header size by default and the stack-card size when asked', () => {
    const { rerender } = render(<Figure value="99.1%" caption="Identified" />)
    expect(screen.getByText('99.1%')).toHaveClass(recipe(styles.md))

    rerender(<Figure value="99.1%" caption="Identified" size="lg" />)
    const value = screen.getByText('99.1%')
    expect(value).toHaveClass(recipe(styles.lg))
    expect(value).not.toHaveClass(recipe(styles.md))
  })

  it('composes the shared caption recipe rather than restating it', () => {
    render(<Figure value="7" caption="Flagged" />)

    // `.labelNav` is the eyebrow at the design's tighter --ls-nav, which is what
    // it uses UNDER a number as against over a block.
    expect(screen.getByText('Flagged')).toHaveClass(recipe(label.labelNav))
  })

  it('is not a heading — a readout stands inside a section that is already headed', () => {
    render(<Figure value="7" caption="Flagged" />)

    expect(screen.queryByRole('heading')).not.toBeInTheDocument()
  })

  it('exposes an id on the caption so a group can be labelled by it', () => {
    render(<Figure value="7" caption="Flagged" id="flagged-cap" />)

    expect(screen.getByText('Flagged')).toHaveAttribute('id', 'flagged-cap')
  })

  it('passes a caller class through so a header row can place it', () => {
    const { container } = render(<Figure value="7" caption="Flagged" className="placed" />)

    expect(container.firstElementChild).toHaveClass('placed')
  })
})
