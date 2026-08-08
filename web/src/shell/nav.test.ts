/**
 * The IA as data, so the assertions are about the *contract* rather than about
 * markup: the screens, in order, and which of them badge.
 *
 * The list is the design's six plus the two added after the rebuild — Missing
 * releases and Download queue. Their badges are pinned by *key* as well as by
 * presence, because the two counts they read are each one of a pair with a
 * near-identical name: `NavCountsOut.wanted` is the actionable backlog while
 * `LibraryStatsOut.wanted_albums` is everything not on disk, and reading the
 * wrong one gives a badge that never agrees with the page it links to.
 */

import { describe, expect, it } from 'vitest'

import type { NavCountsOut } from '@/api/types'
import { NAV_ITEMS, navBadgeCount } from '@/shell/nav'

const counts: NavCountsOut = {
  artists: 12,
  wanted: 4,
  queue: 1,
  trash: 0,
  enrichment_review: 7,
}

describe('NAV_ITEMS', () => {
  it('is the design’s six screens in the design’s order, plus the two added after it', () => {
    expect(NAV_ITEMS.map((item) => [item.label, item.path])).toEqual([
      ['Dashboard', '/'],
      ['Library', '/library'],
      ['Release radar', '/radar'],
      ['Missing releases', '/missing'],
      ['Download queue', '/queue'],
      ['Identify', '/identify'],
      ['Structure & tags', '/rules'],
      ['Activity', '/activity'],
    ])
  })

  it('keeps the design’s glyphs', () => {
    expect(NAV_ITEMS.map((item) => item.glyph)).toEqual([
      '◱',
      '▤',
      '◎',
      '◌',
      '⇣',
      '⌗',
      '⌥',
      '≡',
    ])
  })

  it('badges exactly the three destinations that hold outstanding work', () => {
    expect(
      NAV_ITEMS.filter((item) => item.badge !== undefined).map((item) => [
        item.id,
        item.badge,
      ]),
    ).toEqual([
      ['missing', 'wanted'],
      ['queue', 'queue'],
      ['identify', 'enrichment_review'],
    ])
  })

  it('marks only the dashboard `end`, so Library stays lit on an artist page', () => {
    expect(NAV_ITEMS.filter((item) => item.end).map((item) => item.path)).toEqual(['/'])
  })
})

describe('navBadgeCount', () => {
  const identify = NAV_ITEMS.find((item) => item.id === 'identify')!
  const library = NAV_ITEMS.find((item) => item.id === 'library')!

  it('reads the item’s own key out of the counts', () => {
    expect(navBadgeCount(identify, counts)).toBe(7)
  })

  it('is null for an item with no badge', () => {
    expect(navBadgeCount(library, counts)).toBeNull()
  })

  it('is null before the counts have arrived — a blank badge that grows a number later reads as a fault', () => {
    expect(navBadgeCount(identify, undefined)).toBeNull()
  })

  it('is null at zero rather than rendering a 0', () => {
    expect(navBadgeCount(identify, { ...counts, enrichment_review: 0 })).toBeNull()
  })
})
