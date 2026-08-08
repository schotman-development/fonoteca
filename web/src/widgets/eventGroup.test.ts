import { describe, expect, it } from 'vitest'

import { eventGroup } from '@/widgets/eventGroup'

describe('eventGroup', () => {
  it('puts every library event under Integrity', () => {
    // The whole `library.*` family from the API addendum §4.
    for (const event of [
      'library.scan',
      'library.integrity',
      'library.corrupt',
      'library.refiled',
      'library.retagged',
      'library.deleted',
      'library.missing',
      'library.import',
    ]) {
      expect(eventGroup(event)).toBe('integrity')
    }
  })

  it('puts metadata writes under Writes — enrichment, artist and album alike', () => {
    expect(eventGroup('enrichment.matched')).toBe('writes')
    expect(eventGroup('enrichment.release_type')).toBe('writes')
    expect(eventGroup('artist.tags')).toBe('writes')
    expect(eventGroup('album.nfo')).toBe('writes')
  })

  it('puts downloads and the queue under Grabs', () => {
    expect(eventGroup('download.completed')).toBe('grabs')
    expect(eventGroup('download.failed')).toBe('grabs')
    expect(eventGroup('queue.held')).toBe('grabs')
    expect(eventGroup('queue.recovered')).toBe('grabs')
  })

  it('leaves the catalogue sweep in "other" rather than forcing it into a chip', () => {
    // `indexer.*` is neither a measurement nor a write nor a grab. Folding it
    // into one would make that chip claim something it did not do.
    expect(eventGroup('indexer.checked')).toBe('other')
    expect(eventGroup('indexer.sweep')).toBe('other')
  })

  it('answers "other" for an unknown family instead of throwing', () => {
    expect(eventGroup('teleport.engaged')).toBe('other')
    expect(eventGroup('')).toBe('other')
    expect(eventGroup('   ')).toBe('other')
  })

  it('groups a bare segment with no dot', () => {
    expect(eventGroup('library')).toBe('integrity')
  })

  it('is case-insensitive', () => {
    expect(eventGroup('LIBRARY.SCAN')).toBe('integrity')
  })
})
