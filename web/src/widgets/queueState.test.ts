/**
 * The queue's three rules.
 *
 * Two of these are about refusing to draw something: a button on a row the
 * press cannot help, and a 0% bar under a download that is genuinely working.
 * Both are the sort of thing that looks right in a screenshot and is wrong in
 * front of somebody waiting.
 */

import { describe, expect, it } from 'vitest'

import type { QueueState } from '@/api/types'
import { makeQueueItem } from '@/test/factories'
import {
  queueActions,
  queueProgress,
  queueProgressText,
  queueStateWord,
} from '@/widgets/queueState'

const ALL: QueueState[] = ['pending', 'active', 'done', 'failed', 'cancelled']

describe('queueStateWord', () => {
  it('answers for every state in the vocabulary', () => {
    for (const state of ALL) {
      const word = queueStateWord(state)
      expect(word.label).not.toBe('')
      expect(word.tone).toBeDefined()
    }
  })

  it('calls a pending item Waiting — the queue is sequential and that is the answer', () => {
    expect(queueStateWord('pending').label).toBe('Waiting')
  })

  it('paints only the live row and the failed one', () => {
    expect(queueStateWord('active').tone).toBe('ok')
    expect(queueStateWord('failed').tone).toBe('bad')
    // Finished work is not news on a screen about outstanding work.
    expect(queueStateWord('done').tone).toBe('quiet')
    expect(queueStateWord('cancelled').tone).toBe('quiet')
    expect(queueStateWord('pending').tone).toBe('resting')
  })
})

describe('queueActions', () => {
  it('never offers Retry on an active item — the endpoint answers 409', () => {
    expect(queueActions('active').canRetry).toBe(false)
  })

  it('offers Cancel only where there is something to stop', () => {
    expect(queueActions('active').canCancel).toBe(true)
    expect(queueActions('pending').canCancel).toBe(true)
    // `cancel_queue_item` would accept these and write `cancelled` over a row
    // it reverses nothing about.
    expect(queueActions('done').canCancel).toBe(false)
    expect(queueActions('failed').canCancel).toBe(false)
    expect(queueActions('cancelled').canCancel).toBe(false)
  })

  it('offers Retry exactly where the entry is finished and did not succeed', () => {
    expect(queueActions('failed').canRetry).toBe(true)
    expect(queueActions('cancelled').canRetry).toBe(true)
    expect(queueActions('done').canRetry).toBe(false)
    // Nothing has been attempted, so there is nothing to re-attempt.
    expect(queueActions('pending').canRetry).toBe(false)
  })

  it('leaves a done row with no press at all', () => {
    expect(queueActions('done')).toEqual({ canRetry: false, canCancel: false })
  })
})

describe('queueProgress', () => {
  it('is null — never 0 — before anything has counted the tracks', () => {
    const item = makeQueueItem({ progress_tracks_done: 0, progress_tracks_total: 0 })
    expect(queueProgress(item)).toBeNull()
    expect(queueProgressText(item)).toBeNull()
  })

  it('is the fraction of tracks done once there is a count', () => {
    const item = makeQueueItem({ progress_tracks_done: 3, progress_tracks_total: 12 })
    expect(queueProgress(item)).toBe(0.25)
    expect(queueProgressText(item)).toBe('3 of 12 tracks')
  })

  it('reads the pair, not `progress_percent`, so the bar and the sentence agree', () => {
    // A server that rounded its percent the other way must not be able to make
    // the bar disagree with the words beside it.
    const item = makeQueueItem({
      progress_tracks_done: 1,
      progress_tracks_total: 3,
      progress_percent: 0,
    })
    expect(queueProgress(item)).toBeCloseTo(1 / 3)
    expect(queueProgressText(item)).toBe('1 of 3 tracks')
  })

  it('is 1 for a finished item rather than anything special-cased', () => {
    expect(
      queueProgress(makeQueueItem({ progress_tracks_done: 9, progress_tracks_total: 9 })),
    ).toBe(1)
  })
})
