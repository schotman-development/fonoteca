import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it, vi } from 'vitest'

import { SwitchField } from '@/design/SwitchField/SwitchField'

describe('SwitchField', () => {
  it('renders the label, the note and a named switch', () => {
    render(
      <SwitchField
        checked
        label="Auto-download new releases"
        note="Poll Qobuz and grab anything new."
        onToggle={() => {}}
      />,
    )

    expect(screen.getByRole('switch', { name: 'Auto-download new releases' })).toBeChecked()
    expect(screen.getByText('Poll Qobuz and grab anything new.')).toBeInTheDocument()
  })

  it('names the switch from the visible label, once', () => {
    render(<SwitchField checked={false} label="Pin tags" onToggle={() => {}} />)

    const control = screen.getByRole('switch', { name: 'Pin tags' })
    const labelled = control.getAttribute('aria-labelledby')
    expect(labelled).not.toBeNull()
    expect(document.getElementById(labelled ?? '')?.textContent).toBe('Pin tags')
    // No second, hidden copy of the name inside the control.
    expect(control.textContent).toBe('')
  })

  it('describes the switch with its note, so the explanation is read too', () => {
    render(
      <SwitchField
        checked={false}
        label="Mute integrity"
        note="Stops the alarm; the measurement still runs."
        onToggle={() => {}}
      />,
    )

    const control = screen.getByRole('switch', { name: 'Mute integrity' })
    const describedBy = control.getAttribute('aria-describedby')
    expect(describedBy).not.toBeNull()
    expect(document.getElementById(describedBy ?? '')?.textContent).toBe(
      'Stops the alarm; the measurement still runs.',
    )
  })

  it('has no aria-describedby when there is no note', () => {
    render(<SwitchField checked={false} label="Freeze folder" onToggle={() => {}} />)
    expect(screen.getByRole('switch', { name: 'Freeze folder' })).not.toHaveAttribute(
      'aria-describedby',
    )
  })

  it('points its label at the switch, so the words are a click target', () => {
    render(<SwitchField checked={false} label="Nightly scan" onToggle={() => {}} />)

    const control = screen.getByRole('switch', { name: 'Nightly scan' })
    const label = screen.getByText('Nightly scan')
    expect(label.tagName).toBe('LABEL')
    expect(label).toHaveAttribute('for', control.id)
    expect(control.id).not.toBe('')
  })

  it('fires on a press, with no argument', async () => {
    const onToggle = vi.fn()
    render(<SwitchField checked={false} label="Write tags back" onToggle={onToggle} />)

    await userEvent.click(screen.getByRole('switch', { name: 'Write tags back' }))
    expect(onToggle).toHaveBeenCalledTimes(1)
    expect(onToggle.mock.calls[0]).toEqual([])
  })

  it('does not fire when disabled', async () => {
    const onToggle = vi.fn()
    render(
      <SwitchField checked disabled label="Upgrade cleanup" note="Trash the old copy." onToggle={onToggle} />,
    )

    await userEvent.click(screen.getByRole('switch', { name: 'Upgrade cleanup' }))
    expect(onToggle).not.toHaveBeenCalled()
  })

  it('renders the right-hand meta slot', () => {
    render(
      <SwitchField
        checked
        label="NFO files"
        meta={<span>override</span>}
        onToggle={() => {}}
      />,
    )
    expect(screen.getByText('override')).toBeInTheDocument()
  })

  it('gives two fields distinct ids, so two labels do not name one switch', () => {
    render(
      <>
        <SwitchField checked={false} label="One" onToggle={() => {}} />
        <SwitchField checked={false} label="Two" onToggle={() => {}} />
      </>,
    )

    const [first, second] = screen.getAllByRole('switch')
    expect(first?.id).not.toBe(second?.id)
  })

  it('carries the caller class', () => {
    const { container } = render(
      <SwitchField checked={false} className="placed" label="One" onToggle={() => {}} />,
    )
    expect(container.firstElementChild).toHaveClass('placed')
  })
})
