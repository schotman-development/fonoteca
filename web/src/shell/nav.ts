/**
 * The information architecture, as data.
 *
 * Six entries, in the design's own order and with the design's own glyphs
 * (`renderVals`, design lines 1064–1069). The sidebar renders this list; it does
 * not contain it. That is the whole point of the file: "which screens exist and
 * in what order" is one array, so the nav, the router and the tests cannot
 * disagree about it.
 *
 * The badge is deliberately a **key into `NavCountsOut`** rather than a number
 * or a hook. A nav item is a static description of a destination; how many
 * things are waiting there is live data that arrives twenty seconds later, and
 * baking a fetch into this module would make the IA impossible to render in a
 * test without a query client. Design line 1068 puts a badge on exactly one
 * entry — Identify — and it counts the enrichment review list.
 *
 * ── the two entries the design does not draw ──────────────────────────────
 *
 * *Missing releases* and *Download queue* were added after the rebuild. Both
 * badge a count `NavCountsOut` has carried since before either screen existed,
 * and both counts were already the right shape for a badge, which is the test
 * a new one has to pass: `wanted` is `MISSING_STATUSES` **and monitored** — the
 * backlog you can act on, not everything that is not on disk — and `queue` is
 * `pending + active`, so it is work outstanding and not every row the queue has
 * ever held. A badge counting finished work would never reach zero, and a badge
 * that never reaches zero is one people stop reading.
 */

import type { NavCountsOut } from '@/api/types'

/**
 * A field of `NavCountsOut` a nav item may badge itself with. Typed against the
 * wire payload rather than as a free string: a renamed count is then a compile
 * error here instead of a badge that silently reads `undefined`.
 */
export type NavBadgeKey = keyof NavCountsOut

export interface NavItem {
  /** Stable identity — the design's own (`home`, `library`, …). Used for keys. */
  id: string
  label: string
  /** The mono glyph in the sidebar's 20px column. */
  glyph: string
  path: string
  /**
   * `NavLink`'s `end`. Only the dashboard needs it: its path is a prefix of
   * every other one, so without it "Dashboard" is active on all six screens.
   * `/library` deliberately does **not** set it — `/library/:artistId` is the
   * same section and its nav item must stay lit.
   */
  end: boolean
  badge?: NavBadgeKey
}

/**
 * The screens. Order is the design's, with the two later entries placed where
 * they belong in the reading rather than appended: *Missing releases* and
 * *Download queue* are the two halves of "what is not on disk and what is being
 * done about it", so they sit together, after the radar (which is what is *new*)
 * and before Identify (which is what needs a person). Do not sort it.
 */
export const NAV_ITEMS: readonly NavItem[] = [
  { id: 'home', label: 'Dashboard', glyph: '◱', path: '/', end: true },
  { id: 'library', label: 'Library', glyph: '▤', path: '/library', end: false },
  { id: 'radar', label: 'Release radar', glyph: '◎', path: '/radar', end: false },
  {
    id: 'missing',
    label: 'Missing releases',
    glyph: '◌',
    path: '/missing',
    end: false,
    badge: 'wanted',
  },
  {
    id: 'queue',
    label: 'Download queue',
    glyph: '⇣',
    path: '/queue',
    end: false,
    badge: 'queue',
  },
  {
    id: 'identify',
    label: 'Identify',
    glyph: '⌗',
    path: '/identify',
    end: false,
    badge: 'enrichment_review',
  },
  { id: 'rules', label: 'Structure & tags', glyph: '⌥', path: '/rules', end: false },
  { id: 'activity', label: 'Activity', glyph: '≡', path: '/activity', end: false },
] as const

/**
 * What a nav item's badge should show, or `null` for "show nothing".
 *
 * `null` covers three different situations on purpose, because they all have
 * the same right answer: the item carries no badge, the counts have not arrived
 * yet, or the count is zero. A badge that appears blank and grows a number
 * twenty seconds later is worse than no badge — it reads as a rendering fault
 * for as long as it is empty, and the sidebar is on screen the entire time.
 */
export function navBadgeCount(
  item: NavItem,
  counts: NavCountsOut | undefined,
): number | null {
  if (item.badge === undefined || counts === undefined) return null
  const value = counts[item.badge]
  return typeof value === 'number' && value > 0 ? value : null
}
