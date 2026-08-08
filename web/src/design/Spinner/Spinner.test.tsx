import { render, screen } from '@testing-library/react'
import { describe, expect, it } from 'vitest'

import { Spinner } from '@/design/Spinner/Spinner'
import styles from '@/design/Spinner/Spinner.module.css'

/**
 * A CSS-module class is `string | undefined` under `noUncheckedIndexedAccess`,
 * and `undefined` reaching `toHaveClass` would assert nothing while passing.
 * Failing loudly on a renamed or misspelt class is the point.
 */
function recipe(name: string | undefined): string {
  if (name === undefined) throw new Error('the Spinner class is missing')
  return name
}

describe('Spinner', () => {
  it('renders the ring', () => {
    const { container } = render(<Spinner />)

    expect(container.firstElementChild).toHaveClass(recipe(styles.spinner))
  })

  describe('the accessibility contract: it is decoration, the caller names the region', () => {
    it('is hidden from assistive technology', () => {
      const { container } = render(<Spinner />)

      expect(container.firstElementChild).toHaveAttribute('aria-hidden', 'true')
    })

    it('is not a status region and contributes no accessible name', () => {
      // Both of the design's spinners sit beside a live sentence that already
      // says what is running. A second, vaguer announcement would arrive first.
      const { container } = render(
        <div>
          <Spinner />
          <span>Scanning library, 412 of 1,284 files.</span>
        </div>,
      )

      expect(screen.queryByRole('status')).not.toBeInTheDocument()
      expect(container.querySelector('[aria-live]')).toBeNull()
      expect(container.querySelector('[aria-label]')).toBeNull()
      expect(container).toHaveTextContent('Scanning library, 412 of 1,284 files.')
    })

    it('renders no text of its own for a reader to pick up', () => {
      const { container } = render(<Spinner />)

      expect(container.textContent).toBe('')
    })
  })

  it('draws both of the design’s diameters and no others', () => {
    const { container, rerender } = render(<Spinner />)
    expect(container.firstElementChild).toHaveClass(recipe(styles.sm))

    rerender(<Spinner size="md" />)
    expect(container.firstElementChild).toHaveClass(recipe(styles.md))
    expect(container.firstElementChild).not.toHaveClass(recipe(styles.sm))
  })

  it('rests on the design’s accent ring and adds no tone class for it', () => {
    const { container } = render(<Spinner />)

    expect(container.firstElementChild).not.toHaveClass(recipe(styles.toneCurrent))
  })

  it('offers the inherited ring the filled controls need, so there is one spinner', () => {
    // `Button` draws this on a primary, where the accent cap on a --c-fill ring
    // is very nearly invisible. Before it existed, Button carried a second,
    // near-identical spinner in its own module.
    const { container } = render(<Spinner tone="current" />)

    expect(container.firstElementChild).toHaveClass(recipe(styles.toneCurrent))
  })

  it('passes a caller class through so a row can place it', () => {
    const { container } = render(<Spinner className="placed" />)

    expect(container.firstElementChild).toHaveClass('placed')
  })
})
