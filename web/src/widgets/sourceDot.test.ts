import { describe, expect, it } from 'vitest'

import { sourceDot } from '@/widgets/sourceDot'

describe('sourceDot', () => {
  it('gives each known source its own categorical token', () => {
    expect(sourceDot('musicbrainz')).toBe('var(--c-src-musicbrainz)')
    expect(sourceDot('MusicBrainz')).toBe('var(--c-src-musicbrainz)')
  })

  it('does not lend a colour to a source it has never heard of', () => {
    expect(sourceDot('discogs')).toBe('var(--c-ink-mark)')
  })

  it('never answers with a status colour — the hue says which, not how bad', () => {
    for (const source of [
      'musicbrainz',
      'qobuz',
      'acoustid',
      'deezer',
      'coverartarchive',
      'wikidata',
      'discogs',
    ]) {
      expect(sourceDot(source)).not.toMatch(/--c-(warn|bad|ok)\b/)
    }
  })
})
