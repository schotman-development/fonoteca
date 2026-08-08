import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it, vi } from 'vitest'

import sw from '@/design/shared/switch.module.css'

import { Toggle } from '@/design/Toggle/Toggle'

/**
 * A CSS-module class is `string | undefined` under `noUncheckedIndexedAccess`,
 * and `undefined` reaching `toHaveClass` would assert nothing while passing.
 */
function recipe(name: string | undefined): string {
  if (name === undefined) throw new Error('the shared recipe class is missing')
  return name
}

describe('Toggle', () => {
  it('is a switch, named, with its state announced', () => {
    render(<Toggle checked label="Auto-download new releases" onToggle={() => {}} />)

    const control = screen.getByRole('switch', { name: 'Auto-download new releases' })
    expect(control).toBeInTheDocument()
    expect(control).toBeChecked()
  })

  it('reports off as off', () => {
    render(<Toggle checked={false} label="Pin tags" onToggle={() => {}} />)
    expect(screen.getByRole('switch', { name: 'Pin tags' })).not.toBeChecked()
  })

  it('composes the shared switch recipe rather than restating it', () => {
    render(<Toggle checked label="Pin tags" onToggle={() => {}} />)
    const control = screen.getByRole('switch', { name: 'Pin tags' })
    expect(control).toHaveClass(recipe(sw.track))
    expect(control).toHaveClass(recipe(sw.trackOn))
  })

  it('fires on a press', async () => {
    const onToggle = vi.fn()
    render(<Toggle checked={false} label="Freeze folder" onToggle={onToggle} />)

    await userEvent.click(screen.getByRole('switch', { name: 'Freeze folder' }))
    expect(onToggle).toHaveBeenCalledTimes(1)
  })

  /**
   * The one test this component exists for.
   *
   * `POST /api/albums/{id}/monitor` toggles on an empty body and SETS on a
   * non-empty one. The old UI carried the calling screen's filters — one of
   * which is named `monitored` — on the mutating URL, read one as the album's
   * new value, and so made the "ignored only" filter unmonitor whatever row was
   * pressed.
   *
   * A handler that is never handed anything cannot pass anything on. If this
   * assertion ever fails, the cause is `onClick={onToggle}` (React's MouseEvent
   * arrives as argument one) or a widened `onToggle` signature. Neither is a
   * refactor; both are that bug coming back.
   */
  it('hands its handler NOTHING — the signature is () => void and stays that way', async () => {
    const onToggle = vi.fn()
    render(<Toggle checked={false} label="Monitored" onToggle={onToggle} />)

    await userEvent.click(screen.getByRole('switch', { name: 'Monitored' }))

    expect(onToggle).toHaveBeenCalledTimes(1)
    expect(onToggle.mock.calls[0]).toEqual([])
  })

  it('does not fire when disabled', async () => {
    const onToggle = vi.fn()
    render(<Toggle checked={false} disabled label="Mute integrity" onToggle={onToggle} />)

    await userEvent.click(screen.getByRole('switch', { name: 'Mute integrity' }))
    expect(onToggle).not.toHaveBeenCalled()
  })

  it('is reachable and operable from the keyboard', async () => {
    const onToggle = vi.fn()
    render(<Toggle checked={false} label="Nightly scan" onToggle={onToggle} />)

    await userEvent.tab()
    expect(screen.getByRole('switch', { name: 'Nightly scan' })).toHaveFocus()

    await userEvent.keyboard('{Enter}')
    expect(onToggle).toHaveBeenCalledTimes(1)
  })

  it('takes its name from a visible element when one is given, and does not repeat it', () => {
    render(
      <>
        <span id="cap">Write tags back</span>
        <Toggle checked={false} label="Write tags back" labelledBy="cap" onToggle={() => {}} />
      </>,
    )

    const control = screen.getByRole('switch', { name: 'Write tags back' })
    expect(control).toHaveAttribute('aria-labelledby', 'cap')
    // The hidden copy is dropped, so the name is announced once rather than twice.
    expect(control.textContent).toBe('')
  })

  it('carries the caller class', () => {
    render(<Toggle checked={false} className="placed" label="Monitored" onToggle={() => {}} />)
    expect(screen.getByRole('switch', { name: 'Monitored' })).toHaveClass('placed')
  })
})
