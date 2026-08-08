import { render, screen } from '@testing-library/react'
import { describe, expect, it } from 'vitest'

import label from '@/design/shared/label.module.css'

import { Eyebrow } from '@/design/Eyebrow/Eyebrow'

/**
 * A CSS-module class is `string | undefined` under `noUncheckedIndexedAccess`,
 * and `undefined` reaching `toHaveClass` would assert nothing while passing.
 * Failing loudly on a renamed or misspelt recipe is the point.
 */
function recipe(name: string | undefined): string {
  if (name === undefined) throw new Error('the shared recipe class is missing')
  return name
}

describe('Eyebrow', () => {
  it('renders its label', () => {
    render(<Eyebrow>To do</Eyebrow>)
    expect(screen.getByText('To do')).toBeInTheDocument()
  })

  it('composes the shared label recipe rather than restating it', () => {
    render(<Eyebrow>Paths</Eyebrow>)
    expect(screen.getByText('Paths')).toHaveClass(recipe(label.label))
  })

  it('renders a count as the design\'s "Albums · 4" phrase', () => {
    render(<Eyebrow count={4}>Albums</Eyebrow>)
    expect(screen.getByText('Albums · 4')).toBeInTheDocument()
  })

  it('renders a real zero, because nought is a measurement', () => {
    render(<Eyebrow count={0}>Albums</Eyebrow>)
    expect(screen.getByText('Albums · 0')).toBeInTheDocument()
  })

  it('renders nothing at all for an uncounted list — never a zero', () => {
    const { container } = render(<Eyebrow count={null}>Albums</Eyebrow>)
    expect(screen.getByText('Albums')).toBeInTheDocument()
    expect(container.textContent).toBe('Albums')
    expect(container.textContent).not.toContain('0')
  })

  it('renders the action slot', () => {
    render(<Eyebrow action={<span>7</span>}>To do</Eyebrow>)
    expect(screen.getByText('7')).toBeInTheDocument()
    expect(screen.getByText('To do')).toBeInTheDocument()
  })

  it('is a div by default and a heading when asked', () => {
    const { rerender } = render(<Eyebrow>Missing</Eyebrow>)
    expect(screen.queryByRole('heading')).not.toBeInTheDocument()

    rerender(<Eyebrow as="h2">Missing</Eyebrow>)
    expect(screen.getByRole('heading', { level: 2, name: 'Missing' })).toBeInTheDocument()
  })

  it('exposes an id on the label so a panel can be labelled by it', () => {
    render(
      <Eyebrow id="src-head" action={<span>go</span>}>
        Metadata sources
      </Eyebrow>,
    )
    expect(screen.getByText('Metadata sources')).toHaveAttribute('id', 'src-head')
  })

  it('keeps the caller class on the outermost node in both shapes', () => {
    const { container, rerender } = render(<Eyebrow className="placed">Albums</Eyebrow>)
    expect(container.firstElementChild).toHaveClass('placed')

    rerender(
      <Eyebrow className="placed" action={<span>x</span>}>
        Albums
      </Eyebrow>,
    )
    expect(container.firstElementChild).toHaveClass('placed')
  })
})
