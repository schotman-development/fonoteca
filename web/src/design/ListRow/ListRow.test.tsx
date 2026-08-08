import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it, vi } from 'vitest'

import { ListRow } from '@/design/ListRow/ListRow'

describe('ListRow', () => {
  it('renders what it was given', () => {
    render(
      <ListRow>
        <span>Down the Road Wherever</span>
      </ListRow>,
    )

    expect(screen.getByText('Down the Road Wherever')).toBeInTheDocument()
  })

  it('is not a control when it has no onClick', () => {
    render(
      <ListRow>
        <span>quiet</span>
      </ListRow>,
    )

    expect(screen.queryByRole('button')).not.toBeInTheDocument()
  })

  it('exposes a real, named button when it is clickable', async () => {
    const onClick = vi.fn()
    render(
      <ListRow onClick={onClick} actionLabel="Open Local Hero">
        <span>Local Hero</span>
      </ListRow>,
    )

    const row = screen.getByRole('button', { name: 'Open Local Hero' })
    expect(row.tagName).toBe('BUTTON')

    await userEvent.click(row)
    expect(onClick).toHaveBeenCalledTimes(1)
  })

  it('is reachable and firable from the keyboard', async () => {
    const onClick = vi.fn()
    render(
      <ListRow onClick={onClick} actionLabel="Open Local Hero">
        <span>Local Hero</span>
      </ListRow>,
    )

    await userEvent.tab()
    expect(screen.getByRole('button', { name: 'Open Local Hero' })).toHaveFocus()

    await userEvent.keyboard('{Enter}')
    expect(onClick).toHaveBeenCalledTimes(1)
  })

  it('keeps a nested action independent of the row it sits in', async () => {
    const onClick = vi.fn()
    const onDownload = vi.fn()
    render(
      <ListRow onClick={onClick} actionLabel="Open Local Hero">
        <span>Local Hero</span>
        <button type="button" onClick={onDownload}>
          Download
        </button>
      </ListRow>,
    )

    await userEvent.click(screen.getByRole('button', { name: 'Download' }))

    expect(onDownload).toHaveBeenCalledTimes(1)
    // The row's own handler must NOT fire: the two controls are siblings, not
    // one nested inside the other, which is the whole reason for the hit layer.
    expect(onClick).not.toHaveBeenCalled()
  })

  it('marks the dense variant with a class of its own', () => {
    const { container, rerender } = render(
      <ListRow dense>
        <span>01</span>
      </ListRow>,
    )
    const dense = container.firstElementChild?.className ?? ''

    rerender(
      <ListRow>
        <span>01</span>
      </ListRow>,
    )
    const normal = container.firstElementChild?.className ?? ''

    expect(dense).not.toBe(normal)
    expect(dense.split(' ').length).toBeGreaterThan(normal.split(' ').length)
  })

  it('passes a caller class through so a screen can place it', () => {
    const { container } = render(
      <ListRow className="placed">
        <span>x</span>
      </ListRow>,
    )

    expect(container.firstElementChild).toHaveClass('placed')
  })
})
