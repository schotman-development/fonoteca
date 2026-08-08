/**
 * The bar states the selection and applies one change per press. It builds no
 * body and owns no mutation — the screen does both — so what is pinned here is
 * that each press reports exactly one intent, and that a write in flight makes
 * the bar inert instead of queueing a second one.
 */

import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it, vi } from 'vitest'

import { BulkBar } from '@/widgets/BulkBar'

function mount(props: Partial<Parameters<typeof BulkBar>[0]> = {}) {
  const handlers = {
    onSelectAll: vi.fn(),
    onDone: vi.fn(),
    onWatch: vi.fn(),
    onMode: vi.fn(),
    onEditTypes: vi.fn(),
  }
  render(
    <BulkBar count={2} shown={12} allShownSelected={false} {...handlers} {...props} />,
  )
  return handlers
}

describe('BulkBar', () => {
  it('states the selection, which is not the same number as the rows on screen', () => {
    mount({ count: 40, shown: 12 })
    // Filtering the grid unticks nobody, so the two figures differ and are
    // labelled apart rather than left to be inferred.
    expect(screen.getByRole('status')).toHaveTextContent('40 selected')
    expect(screen.getByRole('button', { name: 'Select all 12' })).toBeInTheDocument()
  })

  it('drops Select all once everything on screen is ticked', () => {
    mount({ count: 12, shown: 12, allShownSelected: true })
    expect(screen.queryByRole('button', { name: /Select all/ })).toBeNull()
    expect(screen.getByRole('button', { name: 'Done' })).toBeInTheDocument()
  })

  it('is on screen before anything is ticked, with the writes disabled', () => {
    // The bar belongs to the mode, not to the selection: Select all and the way
    // out live here. Disabled rather than absent, so the toolbar does not
    // rearrange itself under the pointer on the first tick.
    mount({ count: 0 })
    expect(screen.getByRole('status')).toHaveTextContent('0 selected')
    expect(screen.getByRole('button', { name: 'Select all 12' })).toBeEnabled()
    expect(screen.getByRole('button', { name: 'Done' })).toBeEnabled()
    expect(screen.getByRole('button', { name: 'Watch' })).toBeDisabled()
    expect(screen.getByRole('button', { name: 'Release types…' })).toBeDisabled()
  })

  it('applies watch and stop-watching as one press each', async () => {
    const user = userEvent.setup()
    const handlers = mount()

    await user.click(screen.getByRole('button', { name: 'Watch' }))
    await user.click(screen.getByRole('button', { name: 'Stop watching' }))

    expect(handlers.onWatch).toHaveBeenNthCalledWith(1, true)
    expect(handlers.onWatch).toHaveBeenNthCalledWith(2, false)
  })

  it('offers the three back-catalogue modes by their published labels', async () => {
    const user = userEvent.setup()
    const handlers = mount()

    await user.click(screen.getByRole('button', { name: 'Future releases only' }))
    expect(handlers.onMode).toHaveBeenCalledWith('future')

    await user.click(screen.getByRole('button', { name: 'All releases' }))
    await user.click(screen.getByRole('button', { name: 'Nothing' }))
    expect(handlers.onMode).toHaveBeenNthCalledWith(2, 'all')
    expect(handlers.onMode).toHaveBeenNthCalledWith(3, 'none')
  })

  it('sends the one edit that needs a verb to the drawer instead of applying it', async () => {
    const user = userEvent.setup()
    const handlers = mount()
    await user.click(screen.getByRole('button', { name: 'Release types…' }))
    expect(handlers.onEditTypes).toHaveBeenCalledTimes(1)
  })

  it('goes inert while a write is in flight rather than queueing presses', () => {
    mount({ busy: true })
    expect(screen.getByRole('button', { name: 'Watch' })).toBeDisabled()
    expect(screen.getByRole('button', { name: 'Stop watching' })).toBeDisabled()
    expect(screen.getByRole('button', { name: 'All releases' })).toBeDisabled()
    expect(screen.getByRole('button', { name: 'Release types…' })).toBeDisabled()
    // Leaving is not a write, so it stays available — a stuck request must not
    // trap somebody inside the mode.
    expect(screen.getByRole('button', { name: 'Done' })).toBeEnabled()
  })

  it('hands selecting and leaving straight back to the screen', async () => {
    const user = userEvent.setup()
    const handlers = mount()

    await user.click(screen.getByRole('button', { name: 'Select all 12' }))
    await user.click(screen.getByRole('button', { name: 'Done' }))

    expect(handlers.onSelectAll).toHaveBeenCalledTimes(1)
    expect(handlers.onDone).toHaveBeenCalledTimes(1)
  })
})
