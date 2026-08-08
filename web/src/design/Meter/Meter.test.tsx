import { render, screen } from '@testing-library/react'
import { describe, expect, it } from 'vitest'

import { clampFraction } from '@/design/Meter/fraction'
import { Meter } from '@/design/Meter/Meter'
import styles from '@/design/Meter/Meter.module.css'

/**
 * A CSS-module class is `string | undefined` under `noUncheckedIndexedAccess`,
 * and `undefined` reaching `toHaveClass` would assert nothing while passing.
 * Failing loudly on a renamed or misspelt class is the point.
 */
function recipe(name: string | undefined): string {
  if (name === undefined) throw new Error('the Meter class is missing')
  return name
}

function fillOf(container: HTMLElement): HTMLElement | null {
  return container.querySelector<HTMLElement>(`.${recipe(styles.fill)}`)
}

function sliverOf(container: HTMLElement): HTMLElement | null {
  return container.querySelector<HTMLElement>(`.${recipe(styles.sliver)}`)
}

describe('Meter', () => {
  it('is a named progressbar', () => {
    render(<Meter value={0.64} label="Download progress" />)

    const bar = screen.getByRole('progressbar', { name: 'Download progress' })
    expect(bar).toHaveAttribute('aria-valuemin', '0')
    expect(bar).toHaveAttribute('aria-valuemax', '100')
  })

  it('paints the fraction as a percentage of the track', () => {
    const { container } = render(<Meter value={0.64} label="Download progress" />)

    expect(fillOf(container)).toHaveStyle({ width: '64%' })
    expect(screen.getByRole('progressbar')).toHaveAttribute('aria-valuenow', '64')
  })

  describe('null is not zero', () => {
    it('draws no fill at all', () => {
      const { container } = render(<Meter value={null} label="Scan progress" />)

      expect(fillOf(container)).toBeNull()
      expect(sliverOf(container)).toBeNull()
    })

    it('omits aria-valuenow rather than reporting zero', () => {
      render(<Meter value={null} label="Scan progress" />)

      // `aria-valuenow="0"` is a measurement, and it says "none of it is done".
      // Its ABSENCE is what an assistive technology reads as indeterminate.
      const bar = screen.getByRole('progressbar', { name: 'Scan progress' })
      expect(bar).not.toHaveAttribute('aria-valuenow')
      expect(bar).not.toHaveAttribute('aria-valuetext')
    })

    it('is distinguishable from a real zero, which does draw a fill', () => {
      const { container } = render(<Meter value={0} label="Download progress" />)

      expect(fillOf(container)).toHaveStyle({ width: '0%' })
      expect(screen.getByRole('progressbar')).toHaveAttribute('aria-valuenow', '0')
    })
  })

  describe('indeterminate', () => {
    it('draws a sliver instead of a fill and still reports no value', () => {
      const { container } = render(<Meter value={null} indeterminate label="Library scan" />)

      expect(sliverOf(container)).not.toBeNull()
      expect(fillOf(container)).toBeNull()
      expect(screen.getByRole('progressbar')).not.toHaveAttribute('aria-valuenow')
    })

    it('wins over a value, so a caller may pass both', () => {
      // `<Meter value={pct} indeterminate={pct === null && running} />` is the
      // shape every screen wants; a stale percentage must not leak through it.
      const { container } = render(<Meter value={0.4} indeterminate label="Library scan" />)

      expect(sliverOf(container)).not.toBeNull()
      expect(fillOf(container)).toBeNull()
      expect(screen.getByRole('progressbar')).not.toHaveAttribute('aria-valuenow')
    })
  })

  describe('clamping', () => {
    it('refuses to paint outside its own track', () => {
      const { container, rerender } = render(<Meter value={1.4} label="Download progress" />)
      expect(fillOf(container)).toHaveStyle({ width: '100%' })

      rerender(<Meter value={-0.2} label="Download progress" />)
      expect(fillOf(container)).toHaveStyle({ width: '0%' })
    })

    it('treats a non-finite value as unmeasured rather than painting it', () => {
      // NaN comes out of arithmetic on a three-valued field, and "0 / 0" is not
      // a zero.
      const { container } = render(<Meter value={Number.NaN} label="Completeness" />)

      expect(fillOf(container)).toBeNull()
      expect(screen.getByRole('progressbar')).not.toHaveAttribute('aria-valuenow')
    })

    it('clampFraction is the one rule, and it is pure', () => {
      expect(clampFraction(null)).toBeNull()
      expect(clampFraction(Number.NaN)).toBeNull()
      expect(clampFraction(Number.POSITIVE_INFINITY)).toBeNull()
      expect(clampFraction(-3)).toBe(0)
      expect(clampFraction(0)).toBe(0)
      expect(clampFraction(0.5)).toBe(0.5)
      expect(clampFraction(1)).toBe(1)
      expect(clampFraction(9)).toBe(1)
    })
  })

  it('reads back the caller’s own sentence when it has one', () => {
    render(<Meter value={0.75} label="Download progress" valueText="9 of 12 tracks" />)

    expect(screen.getByRole('progressbar')).toHaveAttribute('aria-valuetext', '9 of 12 tracks')
  })

  it('falls back to the percentage when it has not', () => {
    render(<Meter value={0.5} label="Download progress" />)

    expect(screen.getByRole('progressbar')).toHaveAttribute('aria-valuetext', '50%')
  })

  it('draws both of the design’s track heights and no others', () => {
    const { rerender } = render(<Meter value={0.5} label="Progress" />)
    expect(screen.getByRole('progressbar')).toHaveClass(recipe(styles.thick))

    rerender(<Meter value={0.5} label="Progress" thickness={3} />)
    expect(screen.getByRole('progressbar')).toHaveClass(recipe(styles.thin))
  })

  it('tones the fill, not the track', () => {
    const { container } = render(<Meter value={0.5} label="Progress" tone="warn" />)

    expect(fillOf(container)).toHaveClass(recipe(styles.toneWarn))
    expect(screen.getByRole('progressbar')).not.toHaveClass(recipe(styles.toneWarn))
  })

  it('passes a caller class through so a row can size it', () => {
    render(<Meter value={0.5} label="Progress" className="placed" />)

    expect(screen.getByRole('progressbar')).toHaveClass('placed')
  })
})
