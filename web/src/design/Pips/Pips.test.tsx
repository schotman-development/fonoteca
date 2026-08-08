import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it, vi } from 'vitest'

import { Pips } from '@/design/Pips/Pips'
import styles from '@/design/Pips/Pips.module.css'

/**
 * A CSS-module class is `string | undefined` under `noUncheckedIndexedAccess`,
 * and `undefined` reaching `toHaveClass` would assert nothing while passing.
 * Failing loudly on a renamed or misspelt class is the point.
 */
function recipe(name: string | undefined): string {
  if (name === undefined) throw new Error('the Pips class is missing')
  return name
}

function pipsOf(container: HTMLElement): HTMLElement[] {
  return Array.from(container.querySelectorAll<HTMLElement>(`.${recipe(styles.pip)}`))
}

describe('Pips', () => {
  describe('navigable — onSelect given', () => {
    it('renders a real list of named buttons, one per item', () => {
      render(<Pips total={5} current={2} label="Review queue" onSelect={() => {}} />)

      expect(screen.getByRole('list', { name: 'Review queue' })).toBeInTheDocument()
      expect(screen.getAllByRole('button')).toHaveLength(5)
      expect(screen.getByRole('button', { name: 'Go to item 3 of 5' })).toBeInTheDocument()
      expect(screen.getByRole('button', { name: 'Go to item 5 of 5' })).toBeInTheDocument()
    })

    it('marks the current position, and says nothing on the others', () => {
      render(<Pips total={5} current={2} label="Review queue" onSelect={() => {}} />)

      expect(screen.getByRole('button', { name: 'Go to item 3 of 5' })).toHaveAttribute(
        'aria-current',
        'true',
      )
      // `aria-current="false"` is a valid value some readers still announce, so
      // the rest carry no attribute at all.
      expect(screen.getByRole('button', { name: 'Go to item 1 of 5' })).not.toHaveAttribute(
        'aria-current',
      )
    })

    it('hands back the 0-based index it stands for', async () => {
      const onSelect = vi.fn()
      render(<Pips total={5} current={0} label="Review queue" onSelect={onSelect} />)

      await userEvent.click(screen.getByRole('button', { name: 'Go to item 4 of 5' }))
      expect(onSelect).toHaveBeenCalledWith(3)
    })

    it('is reachable from the keyboard', async () => {
      const onSelect = vi.fn()
      render(<Pips total={3} current={0} label="Review queue" onSelect={onSelect} />)

      await userEvent.tab()
      expect(screen.getByRole('button', { name: 'Go to item 1 of 3' })).toHaveFocus()

      await userEvent.tab()
      await userEvent.keyboard('{Enter}')
      expect(onSelect).toHaveBeenCalledWith(1)
    })

    it('paints exactly one dash as current', () => {
      const { container } = render(
        <Pips total={4} current={1} label="Review queue" onSelect={() => {}} />,
      )

      const current = pipsOf(container).filter((pip) =>
        pip.classList.contains(recipe(styles.pipCurrent)),
      )
      expect(pipsOf(container)).toHaveLength(4)
      expect(current).toHaveLength(1)
    })
  })

  describe('decorative — no onSelect', () => {
    it('takes no tab stops and promises no press', () => {
      render(<Pips total={5} current={2} label="Review queue" />)

      expect(screen.queryAllByRole('button')).toHaveLength(0)
      expect(screen.queryByRole('list')).not.toBeInTheDocument()
    })

    it('states the position once, in words, for a reader', () => {
      render(<Pips total={5} current={2} label="Review queue" />)

      expect(screen.getByText('Review queue: item 3 of 5')).toBeInTheDocument()
    })

    it('hides the dashes themselves, which are that sentence drawn', () => {
      const { container } = render(<Pips total={3} current={0} label="Review queue" />)

      for (const pip of pipsOf(container)) {
        expect(pip).toHaveAttribute('aria-hidden', 'true')
      }
      expect(pipsOf(container)).toHaveLength(3)
    })
  })

  describe('edges', () => {
    it('renders nothing for an empty stack', () => {
      const { container } = render(<Pips total={0} current={0} label="Review queue" />)

      expect(container.firstElementChild).toBeNull()
    })

    it('clamps an index past the end — a stack shrinks under a rejection', () => {
      const { container } = render(
        <Pips total={3} current={9} label="Review queue" onSelect={() => {}} />,
      )

      expect(screen.getByRole('button', { name: 'Go to item 3 of 3' })).toHaveAttribute(
        'aria-current',
        'true',
      )
      expect(
        pipsOf(container).filter((pip) => pip.classList.contains(recipe(styles.pipCurrent))),
      ).toHaveLength(1)
    })

    it('clamps a negative index rather than marking nothing', () => {
      render(<Pips total={3} current={-2} label="Review queue" onSelect={() => {}} />)

      expect(screen.getByRole('button', { name: 'Go to item 1 of 3' })).toHaveAttribute(
        'aria-current',
        'true',
      )
    })

    it('ignores a fractional total rather than rendering half a dash', () => {
      const { container } = render(<Pips total={3.7} current={0} label="Review queue" />)

      expect(pipsOf(container)).toHaveLength(3)
    })
  })

  it('passes a caller class through so a screen can place it', () => {
    const { container } = render(<Pips total={3} current={0} label="Review queue" />)
    expect(container.firstElementChild).toHaveClass(recipe(styles.row))

    const withClass = render(
      <Pips total={3} current={0} label="Review queue" className="placed" />,
    )
    expect(withClass.container.firstElementChild).toHaveClass('placed')
  })
})
