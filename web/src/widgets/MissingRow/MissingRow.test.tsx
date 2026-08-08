import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import type { ReactNode } from 'react'
import { describe, expect, it, vi } from 'vitest'

import { queryKeys } from '@/api/queries'
import { MissingRow } from '@/widgets/MissingRow/MissingRow'
import { makeAlbum } from '@/test/factories'

const LABELS = { '6': 'FLAC 16bit 44.1kHz', '7': 'FLAC 24bit 96kHz' }

function withMeta({ children }: { children: ReactNode }) {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  client.setQueryData(queryKeys.meta(), { format_labels: LABELS })
  return <QueryClientProvider client={client}>{children}</QueryClientProvider>
}

function mount(node: ReactNode) {
  return render(node, { wrapper: withMeta })
}

const noop = () => {}

describe('MissingRow', () => {
  it('names the release and credits it', () => {
    mount(
      <MissingRow album={makeAlbum()} onDownload={noop} onIgnore={noop} />,
    )

    expect(screen.getByText('Kind of Blue')).toBeInTheDocument()
    expect(screen.getByText(/Miles Davis/)).toBeInTheDocument()
  })

  it('builds the sub line as artist · year · obtainable quality', () => {
    mount(
      <MissingRow
        album={makeAlbum({ year: 1959, upgrade_format_id: 7 })}
        onDownload={noop}
        onIgnore={noop}
      />,
    )

    expect(
      screen.getByText('Miles Davis · 1959 · FLAC 24bit 96kHz'),
    ).toBeInTheDocument()
  })

  it('falls back to what is already held when there is no upgrade', () => {
    mount(
      <MissingRow
        album={makeAlbum({ year: 1959, upgrade_format_id: null, owned_format_id: 6 })}
        onDownload={noop}
        onIgnore={noop}
      />,
    )

    expect(
      screen.getByText('Miles Davis · 1959 · FLAC 16bit 44.1kHz'),
    ).toBeInTheDocument()
  })

  it('drops the quality clause rather than guessing at it', () => {
    mount(
      <MissingRow
        album={makeAlbum({ year: 1959, upgrade_format_id: null, owned_format_id: null })}
        onDownload={noop}
        onIgnore={noop}
      />,
    )

    // No dash mid-sentence, and no invented format.
    expect(screen.getByText('Miles Davis · 1959')).toBeInTheDocument()
  })

  it('says "last download failed" on a failed release, and nothing about attempts', () => {
    mount(
      <MissingRow album={makeAlbum({ status: 'failed' })} onDownload={noop} onIgnore={noop} />,
    )

    expect(screen.getByText('last download failed')).toBeInTheDocument()
    // The design says "download failed twice"; `AlbumOut` carries no attempt
    // count, so the number would be fiction.
    expect(screen.queryByText(/twice/)).not.toBeInTheDocument()
  })

  it('gives a merely-wanted release no reason at all', () => {
    mount(
      <MissingRow album={makeAlbum({ status: 'wanted' })} onDownload={noop} onIgnore={noop} />,
    )

    expect(screen.queryByText(/failed/)).not.toBeInTheDocument()
  })

  it('always offers Download — there is no "not on Qobuz" state to hide it for', () => {
    for (const status of ['wanted', 'failed', 'skipped'] as const) {
      const { unmount } = mount(
        <MissingRow album={makeAlbum({ status })} onDownload={noop} onIgnore={noop} />,
      )
      expect(screen.getByRole('button', { name: 'Download' })).toBeEnabled()
      unmount()
    }
  })

  it('names the toggle after what the press will do', async () => {
    const onIgnore = vi.fn()
    const { unmount } = mount(
      <MissingRow
        album={makeAlbum({ status: 'skipped', monitored: false })}
        onDownload={noop}
        onIgnore={onIgnore}
      />,
    )

    // Already ignored: offering "Ignore" would be a press that appears to do
    // nothing and in fact undoes the decision.
    expect(screen.queryByRole('button', { name: 'Ignore' })).not.toBeInTheDocument()
    await userEvent.click(screen.getByRole('button', { name: 'Monitor' }))
    expect(onIgnore).toHaveBeenCalledWith()
    unmount()

    // `status` is not what decides it: a skipped release can still be monitored.
    mount(
      <MissingRow
        album={makeAlbum({ status: 'skipped', monitored: true })}
        onDownload={noop}
        onIgnore={noop}
      />,
    )
    expect(screen.getByRole('button', { name: 'Ignore' })).toBeInTheDocument()
  })

  it('calls the two handlers with nothing', async () => {
    const onDownload = vi.fn()
    const onIgnore = vi.fn()
    mount(
      <MissingRow album={makeAlbum()} onDownload={onDownload} onIgnore={onIgnore} />,
    )

    await userEvent.click(screen.getByRole('button', { name: 'Download' }))
    await userEvent.click(screen.getByRole('button', { name: 'Ignore' }))

    expect(onDownload).toHaveBeenCalledTimes(1)
    expect(onDownload).toHaveBeenCalledWith()
    expect(onIgnore).toHaveBeenCalledTimes(1)
    // The monitor endpoint toggles on an empty body: a handler that is never
    // handed a value cannot pass one on.
    expect(onIgnore).toHaveBeenCalledWith()
  })

  it('blocks both presses while one is in flight', async () => {
    const onDownload = vi.fn()
    const onIgnore = vi.fn()
    mount(
      <MissingRow
        album={makeAlbum()}
        onDownload={onDownload}
        onIgnore={onIgnore}
        busy
      />,
    )

    await userEvent.click(screen.getByRole('button', { name: 'Download' }))
    await userEvent.click(screen.getByRole('button', { name: 'Ignore' }))

    expect(onDownload).not.toHaveBeenCalled()
    expect(onIgnore).not.toHaveBeenCalled()
    expect(screen.getByRole('button', { name: 'Download' })).toHaveAttribute(
      'aria-busy',
      'true',
    )
  })

  it('draws a bare 38px thumb — no initials, as the design has it', () => {
    const { container } = mount(
      <MissingRow album={makeAlbum()} onDownload={noop} onIgnore={noop} />,
    )

    const art = container.querySelector('[data-shape="square"]')
    expect(art).not.toBeNull()
    expect(art?.textContent).toBe('')
  })

  it('is a static row: its buttons are the only controls in it', () => {
    mount(<MissingRow album={makeAlbum()} onDownload={noop} onIgnore={noop} />)

    // A clickable ListRow lays a third button over itself; this row must not
    // be one, or every press would land on two handlers.
    expect(screen.getAllByRole('button')).toHaveLength(2)
  })
})
