import { describe, expect, it } from 'vitest'

import { EM_DASH } from '@/format'
import { activityKind } from '@/widgets/activityKind'

describe('activityKind', () => {
  it('labels a row with the uppercased first segment of the event', () => {
    expect(activityKind('download.completed', 'info').label).toBe('DOWNLOAD')
    expect(activityKind('library.integrity', 'info').label).toBe('LIBRARY')
    expect(activityKind('enrichment.matched', 'info').label).toBe('ENRICHMENT')
  })

  it('gives grabs the categorical blue and enrichment the violet', () => {
    expect(activityKind('download.completed', 'info').tone).toBe('blue')
    expect(activityKind('queue.held', 'info').tone).toBe('blue')
    expect(activityKind('enrichment.identified', 'info').tone).toBe('violet')
  })

  it('gives library work the categorical amber', () => {
    expect(activityKind('library.scan', 'info').tone).toBe('amber')
  })

  it('leaves bookkeeping uncoloured', () => {
    // artist / album / indexer are records of a decision, not of traffic.
    expect(activityKind('artist.follow', 'info').tone).toBe('neutral')
    expect(activityKind('album.monitor', 'info').tone).toBe('neutral')
    expect(activityKind('indexer.sweep', 'info').tone).toBe('neutral')
  })

  it('lets the level overrule the category, in both directions', () => {
    // A failed scan is not "a scan"; a failed download is not "a download".
    expect(activityKind('library.scan', 'error')).toEqual({
      label: 'LIBRARY',
      tone: 'bad',
    })
    expect(activityKind('download.completed', 'warning')).toEqual({
      label: 'DOWNLOAD',
      tone: 'warn',
    })
    expect(activityKind('artist.follow', 'error').tone).toBe('bad')
  })

  it('treats debug exactly like info — it is not a severity, it is a volume', () => {
    expect(activityKind('queue.added', 'debug').tone).toBe('blue')
  })

  it('handles a segment with no dot rather than throwing', () => {
    expect(activityKind('indexer', 'info')).toEqual({
      label: 'INDEXER',
      tone: 'neutral',
    })
  })

  it('renders the em dash for an empty event, never an empty cell', () => {
    expect(activityKind('', 'info')).toEqual({ label: EM_DASH, tone: 'neutral' })
    expect(activityKind('   ', 'info').label).toBe(EM_DASH)
  })

  it('still says how bad an unnamed event was', () => {
    // The level is a separate field: losing the event name must not lose the
    // one thing that says something went wrong.
    expect(activityKind('', 'error')).toEqual({ label: EM_DASH, tone: 'bad' })
  })

  it('names an event it has never heard of, in neutral ink', () => {
    expect(activityKind('teleport.engaged', 'info')).toEqual({
      label: 'TELEPORT',
      tone: 'neutral',
    })
  })

  it('is case-insensitive about the segment it is matching', () => {
    expect(activityKind('Download.Completed', 'info')).toEqual({
      label: 'DOWNLOAD',
      tone: 'blue',
    })
  })
})
