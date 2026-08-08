import { render, screen } from '@testing-library/react'
import { describe, expect, it } from 'vitest'

import { SourceChip } from '@/design/SourceChip/SourceChip'

describe('SourceChip', () => {
  it('names the source', () => {
    render(<SourceChip>musicbrainz</SourceChip>)

    expect(screen.getByText('musicbrainz')).toBeInTheDocument()
  })

  it('is a statement, not a control', () => {
    render(<SourceChip>deezer</SourceChip>)

    expect(screen.queryByRole('button')).not.toBeInTheDocument()
    expect(screen.queryByRole('checkbox')).not.toBeInTheDocument()
  })

  it('writes the dot colour into a custom property, leaving the text alone', () => {
    const { container } = render(
      <SourceChip dot="var(--c-src-musicbrainz)">musicbrainz</SourceChip>,
    )
    const marker = container.querySelector('[aria-hidden="true"]')

    expect(marker).not.toBeNull()
    expect(marker?.getAttribute('style')).toContain('--source-dot')
    expect(marker?.getAttribute('style')).toContain('var(--c-src-musicbrainz)')
  })

  it('still draws a marker when no colour was supplied', () => {
    const { container } = render(<SourceChip>wikidata</SourceChip>)
    const marker = container.querySelector('[aria-hidden="true"]')

    expect(marker).not.toBeNull()
    expect(marker?.getAttribute('style')).toBeNull()
  })

  it('hides the marker from assistive technology — the name carries the meaning', () => {
    render(<SourceChip dot="var(--c-src-acoustid)">acoustid</SourceChip>)

    expect(screen.getByText('acoustid').parentElement?.textContent).toBe(
      'acoustid',
    )
  })

  it('renders the trailing detail only when there is one', () => {
    const { rerender } = render(<SourceChip detail="4 fields">deezer</SourceChip>)
    expect(screen.getByText('4 fields')).toBeInTheDocument()

    rerender(<SourceChip>deezer</SourceChip>)
    expect(screen.queryByText('4 fields')).not.toBeInTheDocument()
  })

  it('composes the shared small-chip geometry rather than forking it', () => {
    const { container } = render(<SourceChip>qobuz</SourceChip>)

    // `composes:` puts BOTH class names on the element; one class name would
    // mean the geometry had been copied into this module instead.
    const classes = (container.firstElementChild?.className ?? '').split(' ')
    expect(classes.length).toBeGreaterThan(1)
  })
})
