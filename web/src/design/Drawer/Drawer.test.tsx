import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { useState } from 'react'
import { describe, expect, it, vi } from 'vitest'

import { Drawer } from '@/design/Drawer/Drawer'

describe('Drawer', () => {
  it('renders nothing while closed', () => {
    render(
      <Drawer open={false} onClose={() => {}} title="Blue Train">
        body
      </Drawer>,
    )
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
  })

  it('is a labelled, non-modal dialog', () => {
    render(
      <Drawer open onClose={() => {}} kicker="Edit tags" title="Blue Train">
        body
      </Drawer>,
    )
    const dialog = screen.getByRole('dialog', { name: 'Blue Train' })
    // Non-modal on purpose: the list beside it stays usable, so claiming the
    // rest of the page is inert would be false.
    expect(dialog).toHaveAttribute('aria-modal', 'false')
    expect(screen.getByText('Edit tags')).toBeInTheDocument()
    expect(screen.getByText('body')).toBeInTheDocument()
  })

  it('closes on the × and names it for a screen reader', async () => {
    const onClose = vi.fn()
    const user = userEvent.setup()
    render(
      <Drawer open onClose={onClose} title="Blue Train" closeLabel="Close release">
        body
      </Drawer>,
    )
    await user.click(screen.getByRole('button', { name: 'Close release' }))
    expect(onClose).toHaveBeenCalledTimes(1)
  })

  it('closes on Escape from inside the panel', async () => {
    const onClose = vi.fn()
    const user = userEvent.setup()
    render(
      <Drawer open onClose={onClose} title="Blue Train">
        <input aria-label="Sort name" />
      </Drawer>,
    )
    await user.click(screen.getByLabelText('Sort name'))
    await user.keyboard('{Escape}')
    expect(onClose).toHaveBeenCalledTimes(1)
  })

  it('does not swallow Escape pressed outside it', async () => {
    const onClose = vi.fn()
    const outside = vi.fn()
    const user = userEvent.setup()
    render(
      <div onKeyDown={outside}>
        <input aria-label="Filter" />
        <Drawer open onClose={onClose} title="Blue Train">
          body
        </Drawer>
      </div>,
    )
    await user.click(screen.getByLabelText('Filter'))
    await user.keyboard('{Escape}')
    expect(onClose).not.toHaveBeenCalled()
    expect(outside).toHaveBeenCalled()
  })

  it('takes focus on open and hands it back to the opener on close', async () => {
    const user = userEvent.setup()

    function Harness() {
      const [open, setOpen] = useState(false)
      return (
        <div>
          <button type="button" onClick={() => setOpen(true)}>
            Open
          </button>
          <Drawer open={open} onClose={() => setOpen(false)} title="Blue Train">
            body
          </Drawer>
        </div>
      )
    }

    render(<Harness />)
    const opener = screen.getByRole('button', { name: 'Open' })
    await user.click(opener)

    expect(screen.getByRole('dialog')).toHaveFocus()

    await user.keyboard('{Escape}')
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
    // Without the return trip the next Tab restarts at the top of the document.
    expect(opener).toHaveFocus()
  })

  it('renders the footer slot only when one is given', () => {
    const { rerender } = render(
      <Drawer open onClose={() => {}} title="Blue Train">
        body
      </Drawer>,
    )
    expect(screen.queryByRole('button', { name: 'Save' })).not.toBeInTheDocument()

    rerender(
      <Drawer
        open
        onClose={() => {}}
        title="Blue Train"
        footer={<button type="button">Save</button>}
      >
        body
      </Drawer>,
    )
    expect(screen.getByRole('button', { name: 'Save' })).toBeInTheDocument()
  })
})
