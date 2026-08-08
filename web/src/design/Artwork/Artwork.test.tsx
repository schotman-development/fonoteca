import { render, screen } from '@testing-library/react'
import { describe, expect, it } from 'vitest'

import { Artwork } from '@/design/Artwork/Artwork'
import { initialsFontSize, squareRadiusVar } from '@/design/Artwork/geometry'
import { artGradientVar } from '@/design/artwork'

/** The tile is the render's only root element; the props all land on it. */
function tile(container: HTMLElement): HTMLElement {
  const el = container.firstElementChild
  if (!(el instanceof HTMLElement)) throw new Error('no tile rendered')
  return el
}

describe('initialsFontSize', () => {
  it('gives the design’s own size at the sizes the design draws', () => {
    expect(initialsFontSize(32, 'circle')).toBe(11) // design 571
    expect(initialsFontSize(62, 'square')).toBe(15) // design 545
    expect(initialsFontSize(108, 'square')).toBe(24) // design 429
    expect(initialsFontSize(126, 'square')).toBe(21) // design 124
  })

  it('answers per shape for a grid cell, because the design does', () => {
    expect(initialsFontSize('fill', 'circle')).toBe(21) // design 257
    expect(initialsFontSize('fill', 'square')).toBe(24) // design 281
  })

  it('has no answer for the three bare tiles', () => {
    // design 146, 620, 677 — gradient only, nothing written on it
    expect(initialsFontSize(38, 'square')).toBeNull()
    expect(initialsFontSize(40, 'square')).toBeNull()
    expect(initialsFontSize(84, 'square')).toBeNull()
  })
})

describe('squareRadiusVar', () => {
  it('snaps the design’s seven radii onto three tokens', () => {
    expect(squareRadiusVar(38)).toBe('var(--r-xs)')
    expect(squareRadiusVar(40)).toBe('var(--r-xs)')
    expect(squareRadiusVar(62)).toBe('var(--r-sm)')
    expect(squareRadiusVar(84)).toBe('var(--r-sm)')
    expect(squareRadiusVar(108)).toBe('var(--r-md)')
    expect(squareRadiusVar(126)).toBe('var(--r-md)')
    expect(squareRadiusVar('fill')).toBe('var(--r-md)')
  })
})

describe('Artwork', () => {
  it('draws the initials it was handed, at the design’s size', () => {
    const { container } = render(
      <Artwork seed="Kind of Blue" initials="KI" shape="square" size={126} />,
    )

    expect(screen.getByText('KI')).toBeInTheDocument()
    expect(tile(container).style.getPropertyValue('--art-initials-fs')).toBe('21px')
  })

  it('draws no initials at the sizes the design leaves bare', () => {
    render(<Artwork seed="Kind of Blue" initials="KI" shape="square" size={38} />)

    expect(screen.queryByText('KI')).not.toBeInTheDocument()
  })

  it('draws no initials when the caller has none to give', () => {
    const { container } = render(
      <Artwork seed="anonymous" initials="" shape="circle" size={32} />,
    )

    // An empty tile, not a tile with an empty span in it: nothing to announce,
    // nothing to lay out, and no `--art-initials-fs` inherited by a child that
    // does not exist.
    expect(container.querySelectorAll('span').length).toBe(0)
  })

  it('hides the initials from assistive tech — the card carries the name', () => {
    render(<Artwork seed="Mark Knopfler" initials="MK" shape="circle" size={32} />)

    expect(screen.getByText('MK')).toHaveAttribute('aria-hidden', 'true')
  })

  it('hashes the gradient off the seed, and the same seed is the same colour', () => {
    const { container: a } = render(
      <Artwork seed="Local Hero" initials="LO" shape="square" size={62} />,
    )
    const { container: b } = render(
      <Artwork seed="Local Hero" initials="LO" shape="circle" size={32} />,
    )

    const gradient = tile(a).style.getPropertyValue('--art')
    expect(gradient).toBe(artGradientVar('Local Hero'))
    expect(gradient).toMatch(/^var\(--art-\d+\)$/)
    expect(tile(b).style.getPropertyValue('--art')).toBe(gradient)
  })

  it('writes a fixed size and withholds one from a fill tile', () => {
    const { container: fixed } = render(
      <Artwork seed="s" initials="S" shape="square" size={108} />,
    )
    expect(tile(fixed).style.getPropertyValue('--art-size')).toBe('108px')

    const { container: fill } = render(
      <Artwork seed="s" initials="S" shape="square" size="fill" />,
    )
    // No pixel size at all: the grid column decides, and `aspect-ratio: 1`
    // gives the height. A `0px` or an `auto` here would be a real size.
    expect(fill.querySelectorAll('[style*="--art-size"]').length).toBe(0)
  })

  it('rounds a circle to the pill token and a square to a radius token', () => {
    const { container: round } = render(
      <Artwork seed="s" initials="S" shape="circle" size="fill" />,
    )
    expect(tile(round).style.getPropertyValue('--art-radius')).toBe('var(--r-pill)')

    const { container: square } = render(
      <Artwork seed="s" initials="S" shape="square" size={62} />,
    )
    expect(tile(square).style.getPropertyValue('--art-radius')).toBe('var(--r-sm)')
  })

  it('renders a real cover over the gradient, decoratively', () => {
    const { container } = render(
      <Artwork
        seed="Down the Road Wherever"
        initials="DO"
        shape="square"
        size={126}
        src="https://example.invalid/cover.jpg"
      />,
    )

    const img = container.querySelector('img')
    expect(img).not.toBeNull()
    expect(img).toHaveAttribute('src', 'https://example.invalid/cover.jpg')
    // alt="" — decorative. The image must not be announced, and it must not be
    // reachable as an image role.
    expect(img).toHaveAttribute('alt', '')
    expect(screen.queryByRole('img')).not.toBeInTheDocument()
    // The gradient survives underneath it, so a slow or broken cover still
    // leaves this release looking like itself.
    expect(tile(container).style.getPropertyValue('--art')).toBe(
      artGradientVar('Down the Road Wherever'),
    )
  })

  it('renders no image for a null or empty src', () => {
    const { container: nul } = render(
      <Artwork seed="s" initials="S" shape="square" size={126} src={null} />,
    )
    expect(nul.querySelector('img')).toBeNull()

    const { container: empty } = render(
      <Artwork seed="s" initials="S" shape="square" size={126} src="" />,
    )
    expect(empty.querySelector('img')).toBeNull()
  })

  it('places both badge slots and leaves out the ones it was not given', () => {
    const { container } = render(
      <Artwork
        seed="s"
        initials="S"
        shape="square"
        size="fill"
        badgeStart={<span>CORRUPT</span>}
      />,
    )

    expect(screen.getByText('CORRUPT')).toBeInTheDocument()
    // One wrapper for the badge it got, none for the one it did not.
    expect(container.querySelectorAll('span').length).toBe(3) // initials + slot + badge
  })

  it('marks selection so the ring and the lifted shadow can attach', () => {
    const { container, rerender } = render(
      <Artwork seed="s" initials="S" shape="square" size="fill" />,
    )
    expect(tile(container)).not.toHaveAttribute('data-selected')
    const resting = tile(container).className

    rerender(<Artwork seed="s" initials="S" shape="square" size="fill" selected />)
    expect(tile(container)).toHaveAttribute('data-selected')
    expect(tile(container).className).not.toBe(resting)
  })

  it('marks the dimmed variant separately from the resting one', () => {
    const { container, rerender } = render(
      <Artwork seed="s" initials="S" shape="square" size="fill" />,
    )
    const resting = tile(container).className

    rerender(<Artwork seed="s" initials="S" shape="square" size="fill" dim />)
    expect(tile(container).className).not.toBe(resting)
  })

  it('reports its shape, and takes a caller class so a screen can place it', () => {
    const { container } = render(
      <Artwork
        seed="s"
        initials="S"
        shape="circle"
        size={32}
        className="placed"
      />,
    )

    expect(tile(container)).toHaveAttribute('data-shape', 'circle')
    expect(tile(container)).toHaveClass('placed')
  })
})
