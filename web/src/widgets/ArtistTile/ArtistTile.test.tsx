import { render, screen } from '@testing-library/react'
import type { ReactNode } from 'react'
import { MemoryRouter } from 'react-router-dom'
import { describe, expect, it } from 'vitest'

import { ArtistTile } from '@/widgets/ArtistTile/ArtistTile'
import { makeArtist } from '@/test/factories'

function routed(children: ReactNode) {
  return render(<MemoryRouter>{children}</MemoryRouter>)
}

describe('ArtistTile', () => {
  it('is one link over the whole cell', () => {
    routed(<ArtistTile artist={makeArtist()} to="/library/artist-1" />)

    const links = screen.getAllByRole('link')
    expect(links).toHaveLength(1)
    expect(links[0]).toHaveAttribute('href', '/library/artist-1')
    expect(links[0]).toHaveTextContent('Miles Davis')
  })

  it('has no anchor at all when there is no address', () => {
    // What a screen selecting artists renders. A covering overlay is only ever
    // as good as the stacking order beneath it — with no link there is nothing
    // for a click to reach, whatever ends up painted over what.
    routed(<ArtistTile artist={makeArtist()} to={null} />)

    expect(screen.queryAllByRole('link')).toHaveLength(0)
    expect(screen.getByText('Miles Davis')).toBeInTheDocument()
    expect(screen.getByText('12 albums')).toBeInTheDocument()
  })

  it('draws the initials by the WORDS rule — a person, not a release', () => {
    routed(<ArtistTile artist={makeArtist({ name: 'Mark Knopfler' })} to="/x" />)

    // `MK`. `MA` would read as a different person entirely.
    expect(screen.getByText('MK')).toBeInTheDocument()
  })

  it('is round, which is what tells this grid from the album grid', () => {
    const { container } = routed(<ArtistTile artist={makeArtist()} to="/x" />)

    expect(container.querySelector('[data-shape="circle"]')).not.toBeNull()
  })

  it('counts the albums, pluralised', () => {
    routed(<ArtistTile artist={makeArtist({ album_count: 12 })} to="/x" />)
    expect(screen.getByText('12 albums')).toBeInTheDocument()

    routed(<ArtistTile artist={makeArtist({ album_count: 1 })} to="/x" />)
    expect(screen.getByText('1 album')).toBeInTheDocument()
  })

  it('renders NOTHING where album_count is null — not a zero, not a dash', () => {
    // `null` means the router did not populate the roll-up, which is not a
    // claim that the artist has no albums.
    routed(<ArtistTile artist={makeArtist({ album_count: null })} to="/x" />)

    expect(screen.queryByText(/album/)).not.toBeInTheDocument()
    expect(screen.queryByText('0')).not.toBeInTheDocument()
    expect(screen.queryByText('—')).not.toBeInTheDocument()
  })

  it('shows a zero honestly when the roll-up really is zero', () => {
    routed(<ArtistTile artist={makeArtist({ album_count: 0 })} to="/x" />)

    expect(screen.getByText('0 albums')).toBeInTheDocument()
  })

  it('draws no flag count — nothing in the API rolls integrity up per artist', () => {
    routed(
      <ArtistTile artist={makeArtist({ wanted_count: 7, album_count: 12 })} to="/x" />,
    )

    // `wanted_count` is a backlog, not an integrity verdict. Reading one as the
    // other would flag a perfectly healthy artist.
    expect(screen.queryByText(/⚑/)).not.toBeInTheDocument()
    expect(screen.queryByText(/7/)).not.toBeInTheDocument()
  })

  it('uses the portrait the server coalesced, when there is one', () => {
    const { container } = routed(
      <ArtistTile
        artist={makeArtist({ portrait_url: 'https://example.invalid/p.jpg' })}
        to="/x"
      />,
    )

    expect(container.querySelector('img')).toHaveAttribute(
      'src',
      'https://example.invalid/p.jpg',
    )
  })

  it('clips a long name visually and nowhere else', () => {
    // Four lines in a 132px column, in a wall next to `AC/DC` — the wrap that
    // made every cell a different height. The CSS clamps it to the two rows
    // the tile reserves; what must NOT happen is the string being cut in the
    // DOM, because it is the link's only accessible name.
    const name = 'The Bach Choir & Orchestra of the Netherlands'
    routed(<ArtistTile artist={makeArtist({ name })} to="/x" />)

    expect(screen.getByRole('link').textContent ?? '').toContain(name)
    // And within reach of a pointer, which cannot read a clipped line.
    expect(screen.getByText(name)).toHaveAttribute('title', name)
  })
})
