/**
 * The one rule worth a test file of its own: an untouched control writes no key.
 *
 * Every assertion below is really the same assertion — `false` must never reach
 * the wire unless somebody chose it — checked once per control, because that is
 * how the old layer's version of this bug got in: one helper of two was right.
 */

import { describe, expect, it } from 'vitest'

import {
  buildBulkPayload,
  EMPTY_BULK_DRAFT,
  KEEP,
  type BulkDraft,
} from '@/widgets/BulkBar/bulkPayload'

function draft(overrides: Partial<BulkDraft> = {}): BulkDraft {
  return { ...EMPTY_BULK_DRAFT, ...overrides }
}

describe('buildBulkPayload', () => {
  it('is null when every control is on "leave alone" — there is nothing to post', () => {
    expect(buildBulkPayload(['a', 'b'], draft())).toBeNull()
  })

  it('is null with no artists, however much the draft asks for', () => {
    expect(buildBulkPayload([], draft({ monitored: 'on' }))).toBeNull()
    expect(buildBulkPayload(['', '  '], draft({ monitored: 'on' }))).toBeNull()
  })

  it('omits the keys nobody touched rather than sending false', () => {
    const payload = buildBulkPayload(['a'], draft({ monitored: 'on' }))
    expect(payload).toEqual({ artist_ids: ['a'], monitored: true })
    expect(payload).not.toHaveProperty('monitor_mode')
    expect(payload).not.toHaveProperty('release_types')
    expect(payload).not.toHaveProperty('release_types_action')
  })

  it('sends monitored: false only when somebody chose it', () => {
    expect(buildBulkPayload(['a'], draft({ monitored: 'off' }))).toEqual({
      artist_ids: ['a'],
      monitored: false,
    })
  })

  it('carries a monitor mode, and treats "none" as a mode rather than as absence', () => {
    expect(buildBulkPayload(['a'], draft({ monitorMode: 'none' }))).toEqual({
      artist_ids: ['a'],
      monitor_mode: 'none',
    })
  })

  it('drops a monitor mode it does not recognise instead of raising', () => {
    // A vocabulary the server has since changed must degrade into "that one
    // control did nothing", never into a 422 that loses the settings beside it.
    expect(
      buildBulkPayload(['a'], draft({ monitorMode: 'weekly', monitored: 'on' })),
    ).toEqual({ artist_ids: ['a'], monitored: true })
    expect(buildBulkPayload(['a'], draft({ monitorMode: 'weekly' }))).toBeNull()
  })

  it('does nothing with ticked release types until a verb is chosen', () => {
    expect(
      buildBulkPayload(['a'], draft({ typesAction: KEEP, releaseTypes: ['ep', 'single'] })),
    ).toBeNull()
  })

  it('sends the list and its verb together, never one without the other', () => {
    expect(
      buildBulkPayload(['a'], draft({ typesAction: 'add', releaseTypes: ['single'] })),
    ).toEqual({
      artist_ids: ['a'],
      release_types: ['single'],
      release_types_action: 'add',
    })
  })

  it('drops a verb with an empty list — including "set", which would empty every selection', () => {
    expect(buildBulkPayload(['a'], draft({ typesAction: 'set', releaseTypes: [] }))).toBeNull()
    expect(
      buildBulkPayload(['a'], draft({ typesAction: 'remove', releaseTypes: ['  '] })),
    ).toBeNull()
  })

  it('cleans the ids: trimmed, de-duplicated, order kept', () => {
    expect(
      buildBulkPayload([' a ', 'b', 'a', ''], draft({ monitored: 'on' })),
    ).toEqual({ artist_ids: ['a', 'b'], monitored: true })
  })

  it('cleans the release types the same way', () => {
    expect(
      buildBulkPayload(['a'], draft({ typesAction: 'set', releaseTypes: ['ep', 'ep', ' live '] })),
    ).toEqual({
      artist_ids: ['a'],
      release_types: ['ep', 'live'],
      release_types_action: 'set',
    })
  })

  it('carries all three verbs at once when all three were chosen', () => {
    expect(
      buildBulkPayload(['a', 'b'], {
        monitored: 'on',
        monitorMode: 'future',
        typesAction: 'remove',
        releaseTypes: ['live'],
      }),
    ).toEqual({
      artist_ids: ['a', 'b'],
      monitored: true,
      monitor_mode: 'future',
      release_types: ['live'],
      release_types_action: 'remove',
    })
  })
})
