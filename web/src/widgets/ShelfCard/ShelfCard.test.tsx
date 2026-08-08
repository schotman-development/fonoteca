import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it, vi } from 'vitest'

import { artIndex } from '@/design'
import { ShelfCard } from '@/widgets/ShelfCard/ShelfCard'

const PROPS = {
  title: 'Down the Road Wherever',
  subtitle: 'Mark Knopfler',
  seed: 'uyej1o165e870',
  imageUrl: null,
  tag: '2h 00m ago',
  onClick: () => {},
}

describe('ShelfCard', () => {
  it('draws the title, the credit and the tag', () => {
    render(<ShelfCard {...PROPS} />)

    expect(screen.getByText('Down the Road Wherever')).toBeInTheDocument()
    expect(screen.getByText('Mark Knopfler')).toBeInTheDocument()
    expect(screen.getByText('2h 00m ago')).toBeInTheDocument()
  })

  it('is ONE control for the whole card', () => {
    render(<ShelfCard {...PROPS} />)

    // Not two tab stops for one destination: the design gives the card a
    // single onClick (123).
    expect(screen.getAllByRole('button')).toHaveLength(1)
  })

  it('calls onClick with nothing when pressed', async () => {
    const onClick = vi.fn()
    render(<ShelfCard {...PROPS} onClick={onClick} />)

    await userEvent.click(screen.getByRole('button'))

    expect(onClick).toHaveBeenCalledTimes(1)
    // With NOTHING: a handler that is never handed a value cannot pass one on.
    expect(onClick).toHaveBeenCalledWith()
  })

  it('takes its initials from the title by the CHARS rule', () => {
    render(<ShelfCard {...PROPS} title="Kind of Blue" />)

    // `KI`, not `KOB` — a release is not a person.
    expect(screen.getByText('KI')).toBeInTheDocument()
  })

  it('colours the artwork from the seed, not from the title', () => {
    const { container, rerender } = render(<ShelfCard {...PROPS} />)
    const first = container.querySelector('[data-shape="square"]')
    const firstArt = (first as HTMLElement | null)?.style.getPropertyValue('--art')

    // Same release, retitled by an enrichment pass: the colour must not move.
    rerender(<ShelfCard {...PROPS} title="Down The Road Wherever (Deluxe)" />)
    const second = container.querySelector('[data-shape="square"]')

    expect((second as HTMLElement | null)?.style.getPropertyValue('--art')).toBe(firstArt)
    expect(firstArt).toBe(`var(--art-${artIndex(PROPS.seed)})`)
  })

  it('renders a real cover over the gradient when there is one', () => {
    const { container } = render(
      <ShelfCard {...PROPS} imageUrl="https://example.invalid/cover.jpg" />,
    )

    const img = container.querySelector('img')
    expect(img).toHaveAttribute('src', 'https://example.invalid/cover.jpg')
    // Decorative: the card around it carries the name.
    expect(img).toHaveAttribute('alt', '')
  })
})
