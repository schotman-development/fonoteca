import { describe, expect, it } from 'vitest'

import { makeAlbum } from '@/test/factories'
import { releaseNote, releaseState, releaseTiming } from '@/widgets/releaseState'

/* Midday, so a date parsed as UTC midnight is several hours "in the past" and a
   naive comparison would get today wrong. */
const NOW = new Date('2026-08-05T12:00:00Z').getTime()

describe('releaseTiming', () => {
  it('counts whole days ahead and behind', () => {
    expect(releaseTiming('2026-08-16', NOW)).toEqual({ when: 'future', days: 11 })
    expect(releaseTiming('2026-08-04', NOW)).toEqual({ when: 'past', days: 1 })
  })

  it('calls today today, however late in the local day it is', () => {
    expect(releaseTiming('2026-08-05', NOW)).toEqual({ when: 'today', days: 0 })

    // 23:00 **local** on the same local date. Built from local parts rather
    // than from a `Z` string, because the whole point of this function is that
    // the comparison happens in the reader's calendar: `new Date('2026-08-05')`
    // is UTC midnight, which is 23 hours in the past by now and which a naive
    // instant comparison would call yesterday.
    const late = new Date(2026, 7, 5, 23, 0, 0).getTime()
    expect(releaseTiming('2026-08-05', late)).toEqual({ when: 'today', days: 0 })

    // And one minute later it is tomorrow's problem, not today's.
    const justAfter = new Date(2026, 7, 6, 0, 1, 0).getTime()
    expect(releaseTiming('2026-08-05', justAfter)).toEqual({ when: 'past', days: 1 })
  })

  it('answers null for a release with no date, and for a malformed one', () => {
    expect(releaseTiming(null, NOW)).toBeNull()
    expect(releaseTiming(undefined, NOW)).toBeNull()
    expect(releaseTiming('', NOW)).toBeNull()
    expect(releaseTiming('sometime in the spring', NOW)).toBeNull()
  })

  it('reads the date part of a full timestamp', () => {
    expect(releaseTiming('2026-08-16T00:00:00Z', NOW)).toEqual({
      when: 'future',
      days: 11,
    })
  })
})

describe('releaseNote', () => {
  it('says how far off an upcoming release is', () => {
    expect(releaseNote('2026-08-06', NOW)).toBe('out tomorrow')
    expect(releaseNote('2026-08-16', NOW)).toBe('out in 11 days')
  })

  it('says "out today" on the day', () => {
    expect(releaseNote('2026-08-05', NOW)).toBe('out today')
  })

  it('marks a recent release and stays quiet about an old one', () => {
    expect(releaseNote('2026-08-04', NOW)).toBe('out yesterday')
    expect(releaseNote('2026-07-20', NOW)).toBe('out 16 days ago')
    // A note on every row is a column of noise. 30 days is the edge.
    expect(releaseNote('2026-07-06', NOW)).toBe('out 30 days ago')
    expect(releaseNote('2026-07-05', NOW)).toBeNull()
    expect(releaseNote('1959-08-17', NOW)).toBeNull()
  })

  it('answers null for no date', () => {
    expect(releaseNote(null, NOW)).toBeNull()
  })
})

describe('releaseState', () => {
  it('lets an in-flight queue entry beat the album status', () => {
    // The invariant: `queue_album()` only promotes skipped/failed/wanted, so an
    // album being upgraded keeps `downloaded` for the whole download.
    expect(
      releaseState(makeAlbum({ status: 'downloaded', queue_state: 'active' })),
    ).toEqual({ label: 'Downloading', tone: 'ok' })
    expect(
      releaseState(makeAlbum({ status: 'downloaded', queue_state: 'pending' })),
    ).toEqual({ label: 'Queued', tone: 'resting' })
  })

  it('answers for every album status', () => {
    const expected = {
      downloaded: 'In library',
      downloading: 'Downloading',
      queued: 'Queued',
      failed: 'Failed',
      skipped: 'Ignored',
      wanted: 'Wanted',
    } as const

    for (const [status, label] of Object.entries(expected)) {
      const album = makeAlbum({
        status: status as keyof typeof expected,
        queue_state: null,
      })
      expect(releaseState(album).label).toBe(label)
    }
  })

  it('paints only the two verdicts that are news', () => {
    expect(releaseState(makeAlbum({ status: 'downloaded' })).tone).toBe('ok')
    expect(releaseState(makeAlbum({ status: 'failed' })).tone).toBe('bad')
    // Wanted is the resting state of most of a library, and ignored is a
    // decision somebody already made. Neither is coloured.
    expect(releaseState(makeAlbum({ status: 'wanted' })).tone).toBe('resting')
    expect(releaseState(makeAlbum({ status: 'skipped' })).tone).toBe('quiet')
  })
})
