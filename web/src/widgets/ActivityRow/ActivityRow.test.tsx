import { render, screen } from '@testing-library/react'
import { describe, expect, it } from 'vitest'

import { ActivityRow } from '@/widgets/ActivityRow/ActivityRow'
import { makeActivity } from '@/test/factories'

describe('ActivityRow', () => {
  it('draws the time, the kind and the message', () => {
    render(
      <ActivityRow
        entry={makeActivity({
          event: 'download.completed',
          message: 'Downloaded Kind of Blue',
          created_at: '2026-08-01T13:41:52Z',
        })}
      />,
    )

    // Local time, so the digits depend on the runner's zone — the SHAPE is
    // what is pinned, plus the seconds the design shows and `fmtDateTime` does
    // not.
    expect(screen.getByText(/^\d{2}:\d{2}:52$/)).toBeInTheDocument()
    expect(screen.getByText('DOWNLOAD')).toBeInTheDocument()
    expect(screen.getByText('Downloaded Kind of Blue')).toBeInTheDocument()
  })

  it('answers "when exactly?" through the title, in labelled UTC', () => {
    render(<ActivityRow entry={makeActivity({ created_at: '2026-08-01T13:41:52Z' })} />)

    expect(screen.getByText(/^\d{2}:\d{2}:52$/)).toHaveAttribute(
      'title',
      '2026-08-01 13:41 UTC',
    )
  })

  it('renders the em dash for a row with no timestamp', () => {
    render(<ActivityRow entry={makeActivity({ created_at: null })} />)

    expect(screen.getByText('—')).toBeInTheDocument()
  })

  it('builds the detail line from the artist and the release', () => {
    render(
      <ActivityRow
        entry={makeActivity({ artist_name: 'Miles Davis', album_title: 'Kind of Blue' })}
      />,
    )

    expect(screen.getByText('Miles Davis · Kind of Blue')).toBeInTheDocument()
  })

  it('drops the detail line entirely when neither is known', () => {
    // A `library.scan` row names no artist and no release. An empty second
    // line reserves space in the grid and reads as half a row that failed.
    const { container } = render(
      <ActivityRow
        entry={makeActivity({
          event: 'library.scan',
          artist_name: null,
          album_title: null,
        })}
      />,
    )

    // Two mono cells left: the clock and the kind.
    expect(container.querySelectorAll('.mono')).toHaveLength(2)
  })

  it('says nothing in the fourth column for an ordinary row', () => {
    // The design's `result` column has no field behind it, and `info` is every
    // ordinary row — a chip on all of them is noise, and a fabricated result
    // string is worse.
    render(<ActivityRow entry={makeActivity({ level: 'info' })} />)

    expect(screen.queryByText('Info')).not.toBeInTheDocument()
    expect(screen.queryByText(/quarantined|files|written/)).not.toBeInTheDocument()
  })

  it('marks a warning and an error in the fourth column', () => {
    const { rerender } = render(
      <ActivityRow entry={makeActivity({ level: 'warning' })} />,
    )
    expect(screen.getByText('Warning')).toHaveAttribute('data-tone', 'warn')

    rerender(<ActivityRow entry={makeActivity({ level: 'error' })} />)
    expect(screen.getByText('Error')).toHaveAttribute('data-tone', 'bad')
  })

  it('lets the level overrule the categorical colour of the kind', () => {
    const { container } = render(
      <ActivityRow entry={makeActivity({ event: 'download.failed', level: 'error' })} />,
    )

    // Not the grabs blue: a failed download is not a download.
    const kind = screen.getByText('DOWNLOAD')
    expect(kind.className).toContain('kindBad')
    expect(container.querySelector('.kindBlue')).toBeNull()
  })

  it('carries the whole event in a title — the label is only its family', () => {
    render(<ActivityRow entry={makeActivity({ event: 'library.refiled' })} />)

    expect(screen.getByText('LIBRARY')).toHaveAttribute('title', 'library.refiled')
  })

  it('does not fall over on an event it has never heard of', () => {
    render(<ActivityRow entry={makeActivity({ event: 'teleport.engaged' })} />)

    expect(screen.getByText('TELEPORT')).toBeInTheDocument()
  })
})
