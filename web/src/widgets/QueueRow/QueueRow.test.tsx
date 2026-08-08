/**
 * The queue row.
 *
 * What is pinned here is mostly what the row REFUSES to draw: a bar under a
 * row that is not running, a 0% bar under one that is, a Retry the endpoint
 * would answer 409 to, a Cancel over work that already finished, and a stale
 * error string on a row that succeeded. Each of those looks fine in a
 * screenshot and is wrong in front of somebody waiting for a download.
 */

import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it, vi } from 'vitest'

import { makeQueueItem } from '@/test/factories'
import { QueueRow } from '@/widgets/QueueRow/QueueRow'

const noop = () => {}

describe('QueueRow', () => {
  it('names the release and credits it', () => {
    render(<QueueRow item={makeQueueItem()} onRetry={noop} onCancel={noop} />)

    expect(screen.getByText('Kind of Blue')).toBeInTheDocument()
    expect(screen.getByText(/Miles Davis/)).toBeInTheDocument()
  })

  it('draws the bar only while the item is running', () => {
    const { rerender } = render(
      <QueueRow item={makeQueueItem({ state: 'pending' })} onRetry={noop} onCancel={noop} />,
    )
    expect(screen.queryByRole('progressbar')).toBeNull()

    rerender(
      <QueueRow
        item={makeQueueItem({
          state: 'active',
          progress_tracks_done: 3,
          progress_tracks_total: 12,
        })}
        onRetry={noop}
        onCancel={noop}
      />,
    )
    const bar = screen.getByRole('progressbar', { name: /Kind of Blue/ })
    expect(bar).toHaveAttribute('aria-valuenow', '25')
    expect(screen.getByText('3 of 12 tracks')).toBeInTheDocument()
  })

  it('leaves a running item with no track count as indeterminate, never at 0%', () => {
    render(
      <QueueRow
        item={makeQueueItem({
          state: 'active',
          progress_tracks_done: 0,
          progress_tracks_total: 0,
        })}
        onRetry={noop}
        onCancel={noop}
      />,
    )

    // `aria-valuenow="0"` is a measurement and would say none of it is done.
    expect(screen.getByRole('progressbar')).not.toHaveAttribute('aria-valuenow')
  })

  it('offers Cancel while the item is waiting or running, and no Retry', () => {
    for (const state of ['pending', 'active'] as const) {
      const { unmount } = render(
        <QueueRow item={makeQueueItem({ state })} onRetry={noop} onCancel={noop} />,
      )
      expect(screen.getByRole('button', { name: 'Cancel' })).toBeInTheDocument()
      expect(screen.queryByRole('button', { name: 'Retry' })).toBeNull()
      unmount()
    }
  })

  it('offers Retry once it has failed, and no Cancel over finished work', () => {
    render(
      <QueueRow item={makeQueueItem({ state: 'failed' })} onRetry={noop} onCancel={noop} />,
    )
    expect(screen.getByRole('button', { name: 'Retry' })).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Cancel' })).toBeNull()
  })

  it('offers nothing at all on a finished row', () => {
    render(
      <QueueRow item={makeQueueItem({ state: 'done' })} onRetry={noop} onCancel={noop} />,
    )
    expect(screen.queryAllByRole('button')).toHaveLength(0)
  })

  it('shows the upstream error where the state explains it', () => {
    render(
      <QueueRow
        item={makeQueueItem({ state: 'failed', last_error: 'Qobuz returned 502' })}
        onRetry={noop}
        onCancel={noop}
      />,
    )
    expect(screen.getByText('Qobuz returned 502')).toBeInTheDocument()
  })

  it('never reports a successful download as broken on a stale error', () => {
    render(
      <QueueRow
        item={makeQueueItem({
          state: 'done',
          last_error: 'Qobuz returned 502',
          finished_at: '2026-08-01T12:00:00Z',
        })}
        onRetry={noop}
        onCancel={noop}
      />,
    )
    expect(screen.queryByText('Qobuz returned 502')).toBeNull()
  })

  it('counts attempts only above one', () => {
    const { rerender } = render(
      <QueueRow item={makeQueueItem({ attempts: 1 })} onRetry={noop} onCancel={noop} />,
    )
    expect(screen.queryByText(/attempt/)).toBeNull()

    rerender(<QueueRow item={makeQueueItem({ attempts: 3 })} onRetry={noop} onCancel={noop} />)
    expect(screen.getByText(/attempt 3/)).toBeInTheDocument()
  })

  it('hands its presses on with no argument, so nothing can pass an event through', async () => {
    const user = userEvent.setup()
    const onCancel = vi.fn()
    render(
      <QueueRow
        item={makeQueueItem({ state: 'pending' })}
        onRetry={noop}
        onCancel={onCancel}
      />,
    )

    await user.click(screen.getByRole('button', { name: 'Cancel' }))
    expect(onCancel).toHaveBeenCalledWith()
  })
})
