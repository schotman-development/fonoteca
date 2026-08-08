import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import type { ReactNode } from 'react'
import { describe, expect, it, vi } from 'vitest'

import { queryKeys } from '@/api/queries'
import { AlbumTile } from '@/widgets/AlbumTile/AlbumTile'
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

describe('AlbumTile', () => {
  it('draws the title, the credit and the year/spec line', () => {
    mount(
      <AlbumTile
        album={makeAlbum({ year: 1959, owned_format_id: 7 })}
        selected={false}
        onSelect={noop}
      />,
    )

    expect(screen.getByText('Kind of Blue')).toBeInTheDocument()
    expect(screen.getByText('Miles Davis')).toBeInTheDocument()
    expect(screen.getByText('1959 · FLAC 24bit 96kHz')).toBeInTheDocument()
  })

  it('drops the year clause on its own and keeps the spec', () => {
    mount(
      <AlbumTile
        album={makeAlbum({ year: null, owned_format_id: 6 })}
        selected={false}
        onSelect={noop}
      />,
    )

    // The label appears twice on a tile — the corner badge and the meta line —
    // which is the design's own arrangement (`a.spec` at 285 and at 290). The
    // line is the last child, and it must carry no orphaned separator.
    const line = screen.getByRole('button').lastElementChild
    expect(line).toHaveTextContent('FLAC 16bit 44.1kHz')
    expect(line?.textContent).not.toContain('·')
  })

  it('renders no meta line at all when neither clause is known', () => {
    mount(
      <AlbumTile
        album={makeAlbum({ year: null, owned_format_id: null })}
        selected={false}
        onSelect={noop}
      />,
    )

    // Never `— · —`, and never a fabricated size: there is no per-album byte
    // count in the API at all (design 290's third clause).
    expect(screen.queryByText(/·/)).not.toBeInTheDocument()
  })

  it('badges the quality from the copy on disk, never from the catalogue flag', () => {
    mount(
      <AlbumTile
        // A CD-quality copy of a release Qobuz also sells in hi-res.
        album={makeAlbum({ hires: true, owned_hires: false, owned_format_id: 6 })}
        selected={false}
        onSelect={noop}
      />,
    )

    expect(screen.getByText('FLAC 16bit 44.1kHz')).toBeInTheDocument()
    expect(screen.queryByText('FLAC 24bit 96kHz')).not.toBeInTheDocument()
  })

  it('draws no quality badge, and no empty corner, when nothing is on disk', () => {
    const { container } = mount(
      <AlbumTile
        album={makeAlbum({
          hires: true,
          owned_format_id: null,
          // Verified, so the OTHER corner is empty too and the count below is
          // unambiguous.
          integrity_state: 'verified',
        })}
        selected={false}
        onSelect={noop}
      />,
    )

    // `Artwork` renders the positioned wrapper for ANY badgeEnd it is handed,
    // so "the component returned null" is not the same as "no badge".
    const tile = container.querySelector('[data-shape="square"]')
    expect(tile?.childElementCount).toBe(1) // the initials, and nothing else
  })

  it('flags the release through albumFlag — including the unverified case', () => {
    mount(
      <AlbumTile
        album={makeAlbum({ integrity_state: null })}
        selected={false}
        onSelect={noop}
      />,
    )
    expect(screen.getByText('UNVERIFIED')).toBeInTheDocument()

    mount(
      <AlbumTile
        album={makeAlbum({ corrupt_tracks: 1 })}
        selected={false}
        onSelect={noop}
      />,
    )
    expect(screen.getByText('CORRUPT')).toHaveAttribute('data-tone', 'bad')
  })

  it('draws no flag on a verified release', () => {
    mount(
      <AlbumTile
        album={makeAlbum({ integrity_state: 'verified', corrupt_tracks: 0 })}
        selected={false}
        onSelect={noop}
      />,
    )

    expect(screen.queryByText('UNVERIFIED')).not.toBeInTheDocument()
    expect(screen.queryByText('CORRUPT')).not.toBeInTheDocument()
    expect(screen.queryByText('REPLACED')).not.toBeInTheDocument()
  })

  it('is a button, not a link — the press opens a panel, it does not navigate', () => {
    mount(<AlbumTile album={makeAlbum()} selected={false} onSelect={noop} />)

    expect(screen.getByRole('button')).toBeInTheDocument()
    expect(screen.queryByRole('link')).not.toBeInTheDocument()
  })

  it('calls onSelect with nothing when pressed', async () => {
    const onSelect = vi.fn()
    mount(<AlbumTile album={makeAlbum()} selected={false} onSelect={onSelect} />)

    await userEvent.click(screen.getByRole('button'))

    expect(onSelect).toHaveBeenCalledTimes(1)
    expect(onSelect).toHaveBeenCalledWith()
  })

  it('marks the selected release, on the button and on the artwork', () => {
    const { container } = mount(
      <AlbumTile album={makeAlbum()} selected onSelect={noop} />,
    )

    expect(screen.getByRole('button')).toHaveAttribute('aria-current', 'true')
    expect(container.querySelector('[data-selected]')).not.toBeNull()
  })

  it('takes its initials from the title by the CHARS rule', () => {
    mount(<AlbumTile album={makeAlbum()} selected={false} onSelect={noop} />)

    expect(screen.getByText('KI')).toBeInTheDocument()
  })

  it('clips a long title visually and nowhere else', () => {
    // Three lines in a ~150px column, against one for `Wild` in the cell
    // beside it — the wrap that made every card in the wall a different
    // height. The CSS clamps it to the two rows the tile reserves; what must
    // NOT happen is the string being cut in the DOM, because the button has
    // no other accessible name.
    const title = 'Live at Carnegie Hall - An Acoustic Evening'
    mount(<AlbumTile album={makeAlbum({ title })} selected={false} onSelect={noop} />)

    expect(screen.getByRole('button').textContent ?? '').toContain(title)
    // And within reach of a pointer, which cannot read a clipped line.
    expect(screen.getByText(title)).toHaveAttribute('title', title)
  })
})
