import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it, vi } from 'vitest'

import chip from '@/design/shared/chip.module.css'

import { Chip } from '@/design/Chip/Chip'

/**
 * A CSS-module class is `string | undefined` under `noUncheckedIndexedAccess`,
 * and `undefined` reaching `toHaveClass` would assert nothing while passing.
 * Failing loudly on a renamed or misspelt recipe is the point.
 */
function recipe(name: string | undefined): string {
  if (name === undefined) throw new Error('the shared recipe class is missing')
  return name
}

describe('Chip', () => {
  it('renders its label', () => {
    render(
      <Chip selected={false} onClick={() => {}}>
        Monitored
      </Chip>,
    )
    expect(screen.getByRole('button', { name: 'Monitored' })).toBeInTheDocument()
  })

  it('composes the shared chip geometry rather than restating it', () => {
    render(
      <Chip selected={false} onClick={() => {}}>
        All
      </Chip>,
    )
    expect(screen.getByRole('button', { name: 'All' })).toHaveClass(recipe(chip.chip))
  })

  it('announces its on state as a pressed toggle, not just as a colour', () => {
    const { rerender } = render(
      <Chip selected={false} onClick={() => {}}>
        Has wanted
      </Chip>,
    )
    expect(screen.getByRole('button', { name: 'Has wanted' })).toHaveAttribute(
      'aria-pressed',
      'false',
    )

    rerender(
      <Chip selected onClick={() => {}}>
        Has wanted
      </Chip>,
    )
    expect(screen.getByRole('button', { name: 'Has wanted' })).toHaveAttribute(
      'aria-pressed',
      'true',
    )
  })

  it('fires on a press', async () => {
    const onClick = vi.fn()
    render(
      <Chip selected={false} onClick={onClick}>
        Integrity
      </Chip>,
    )

    await userEvent.click(screen.getByRole('button', { name: 'Integrity' }))
    expect(onClick).toHaveBeenCalledTimes(1)
  })

  it('renders a trailing count', () => {
    render(
      <Chip selected={false} count={12} onClick={() => {}}>
        Monitored
      </Chip>,
    )
    expect(screen.getByRole('button', { name: 'Monitored 12' })).toBeInTheDocument()
  })

  it('renders no count at all when there is none — never a zero', () => {
    const { container } = render(
      <Chip selected={false} onClick={() => {}}>
        Monitored
      </Chip>,
    )
    expect(container.textContent).toBe('Monitored')
  })

  it('renders a real zero, because nought matches is a measurement', () => {
    render(
      <Chip selected={false} count={0} onClick={() => {}}>
        Failed
      </Chip>,
    )
    expect(screen.getByRole('button', { name: 'Failed 0' })).toBeInTheDocument()
  })

  it('does not fire when disabled', async () => {
    const onClick = vi.fn()
    render(
      <Chip selected={false} disabled onClick={onClick}>
        Grabs
      </Chip>,
    )

    await userEvent.click(screen.getByRole('button', { name: 'Grabs' }))
    expect(onClick).not.toHaveBeenCalled()
  })

  it('as="span" is inert: no button, no tab stop, no promise of a press', () => {
    render(<Chip as="span">acoustid</Chip>)

    expect(screen.queryByRole('button')).not.toBeInTheDocument()
    expect(screen.getByText('acoustid')).toBeInTheDocument()
  })

  it('takes the small geometry for a token button', () => {
    render(
      <Chip size="sm" mono accent selected={false} onClick={() => {}}>
        {'{artist}'}
      </Chip>,
    )
    expect(screen.getByRole('button', { name: '{artist}' })).toHaveClass(recipe(chip.chipSm))
  })

  it('is reachable and operable from the keyboard', async () => {
    const onClick = vi.fn()
    render(
      <Chip selected={false} onClick={onClick}>
        Writes
      </Chip>,
    )

    await userEvent.tab()
    expect(screen.getByRole('button', { name: 'Writes' })).toHaveFocus()

    await userEvent.keyboard(' ')
    expect(onClick).toHaveBeenCalledTimes(1)
  })

  it('carries the caller class', () => {
    render(
      <Chip selected={false} className="placed" onClick={() => {}}>
        All
      </Chip>,
    )
    expect(screen.getByRole('button', { name: 'All' })).toHaveClass('placed')
  })
})
