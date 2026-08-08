import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it, vi } from 'vitest'

import { FollowRow } from '@/widgets/FollowRow/FollowRow'
import { makeArtist } from '@/test/factories'

describe('FollowRow', () => {
  it('draws the artist and their meta line', () => {
    render(
      <FollowRow
        artist={makeArtist({
          name: 'Mark Knopfler',
          album_count: 14,
          added_at: '2026-01-01T10:00:00Z',
        })}
        onToggle={() => {}}
      />,
    )

    expect(screen.getByText('Mark Knopfler')).toBeInTheDocument()
    expect(screen.getByText(/14 albums/)).toBeInTheDocument()
    expect(screen.getByText(/followed/)).toBeInTheDocument()
  })

  it('renders NOTHING for a null album_count — never "0 albums"', () => {
    // `null` is "the router did not populate this", not "no albums".
    render(
      <FollowRow
        artist={makeArtist({ album_count: null, added_at: '2026-01-01T10:00:00Z' })}
        onToggle={() => {}}
      />,
    )

    expect(screen.queryByText(/albums/)).not.toBeInTheDocument()
    expect(screen.queryByText(/^0/)).not.toBeInTheDocument()
    expect(screen.getByText(/followed/)).toBeInTheDocument()
  })

  it('says nothing rather than "followed never" when added_at is unset', () => {
    render(
      <FollowRow
        artist={makeArtist({ album_count: 4, added_at: null })}
        onToggle={() => {}}
      />,
    )

    expect(screen.getByText(/4 albums/)).toBeInTheDocument()
    expect(screen.queryByText(/never/)).not.toBeInTheDocument()
  })

  it('drops the meta line entirely when it would be empty', () => {
    const { container } = render(
      <FollowRow
        artist={makeArtist({ album_count: null, added_at: null })}
        onToggle={() => {}}
      />,
    )

    // An empty second line is a gap in the row, not information.
    expect(container.querySelectorAll('.mono')).toHaveLength(0)
  })

  it('reflects `monitored` on a named switch', () => {
    render(
      <FollowRow
        artist={makeArtist({ name: 'Miles Davis', monitored: true })}
        onToggle={() => {}}
      />,
    )

    const toggle = screen.getByRole('switch', { name: 'Monitor Miles Davis' })
    expect(toggle).toHaveAttribute('aria-checked', 'true')
  })

  it('calls onToggle with NOTHING — the endpoint toggles on an empty body', async () => {
    const onToggle = vi.fn()
    render(<FollowRow artist={makeArtist()} onToggle={onToggle} />)

    await userEvent.click(screen.getByRole('switch'))

    expect(onToggle).toHaveBeenCalledTimes(1)
    expect(onToggle).toHaveBeenCalledWith()
  })

  it('holds the switch while a write is in flight', async () => {
    const onToggle = vi.fn()
    render(<FollowRow artist={makeArtist()} onToggle={onToggle} busy />)

    const toggle = screen.getByRole('switch')
    expect(toggle).toBeDisabled()
    await userEvent.click(toggle)
    expect(onToggle).not.toHaveBeenCalled()
  })

  it('takes its initials from the name by the WORDS rule', () => {
    render(<FollowRow artist={makeArtist({ name: 'Mark Knopfler' })} onToggle={() => {}} />)

    // `MK` — an artist is a person, not a title.
    expect(screen.getByText('MK')).toBeInTheDocument()
  })

  it('draws the artist as a circle', () => {
    const { container } = render(
      <FollowRow artist={makeArtist()} onToggle={() => {}} />,
    )

    // A circle is a person and a square is a release; the distinction is what
    // tells the two grids apart at a glance.
    expect(container.querySelector('[data-shape="circle"]')).not.toBeNull()
  })
})
