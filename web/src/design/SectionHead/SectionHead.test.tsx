import { render, screen } from '@testing-library/react'
import { describe, expect, it } from 'vitest'

import { SectionHead } from '@/design/SectionHead/SectionHead'
import styles from '@/design/SectionHead/SectionHead.module.css'

/**
 * A CSS-module class is `string | undefined` under `noUncheckedIndexedAccess`,
 * and `undefined` reaching `toHaveClass` would assert nothing while passing —
 * which would make the negative assertion below pass for the wrong reason.
 * Failing loudly on a renamed or misspelt class is the point.
 */
function recipe(name: string | undefined): string {
  if (name === undefined) throw new Error('the SectionHead class is missing')
  return name
}

describe('SectionHead', () => {
  it('renders the title as the section heading', () => {
    render(<SectionHead title="Library health" />)
    expect(screen.getByRole('heading', { level: 2, name: 'Library health' })).toBeInTheDocument()
  })

  it('renders the subtitle when there is one and no element when there is not', () => {
    const { container, rerender } = render(<SectionHead title="Missing albums" sub="gaps" />)
    expect(screen.getByText('gaps')).toBeInTheDocument()

    rerender(<SectionHead title="Missing albums" />)
    expect(container.querySelector('p')).toBeNull()
  })

  it('renders the action slot only when given one', () => {
    const { container, rerender } = render(
      <SectionHead title="Library health" action={<button type="button">Activity</button>} />,
    )
    expect(screen.getByRole('button', { name: 'Activity' })).toBeInTheDocument()

    rerender(<SectionHead title="Library health" />)
    expect(container.querySelector('button')).toBeNull()
  })

  it('draws the rule by default and drops it when asked — design 114 has no rule', () => {
    const { container, rerender } = render(<SectionHead title="Library health" />)
    expect(container.firstElementChild).toHaveClass(recipe(styles.ruled))

    rerender(<SectionHead title="Last downloaded" ruled={false} />)
    expect(container.firstElementChild).not.toHaveClass(recipe(styles.ruled))
  })

  it('exposes an id on the heading so a section can be labelled by it', () => {
    render(<SectionHead title="Library health" id="health-head" />)
    expect(screen.getByRole('heading', { level: 2 })).toHaveAttribute('id', 'health-head')
  })
})
