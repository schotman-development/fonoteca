/**
 * The quality figure in the corner of a release tile (design 284–286).
 *
 * `SpecBadge` is the pill; this is the one decision the pill must not make —
 * *which* number goes in it, and whether there is one at all.
 *
 * ── the id is the copy on disk, never the catalogue's hi-res flag ──────────
 *
 * The design gates this badge on `a.hires` (284), and `hires` on `AlbumOut` is
 * the **catalogue's** availability flag: a 16/44.1 copy of a release Qobuz also
 * sells in 24/96 is `hires: true, owned_hires: false`. A badge drawn from it
 * says the file on disk is hi-res when it demonstrably is not, which is exactly
 * the claim `plan_refile()` refuses to render into a folder name. So the caller
 * passes a **format id** — `owned_format_id`, what is actually held — and the
 * label comes from `MetaOut.format_labels`, the server's own vocabulary, so an
 * id the backend adds appears here without a client release.
 *
 * ── `null` renders nothing, and so does an id nobody has a word for ───────
 *
 * `owned_format_id` is `null` when nothing is on disk. There is no badge for
 * that: an empty pill in the corner of a tile reads as a measurement that came
 * back blank rather than as one that was never taken. `formatLabel` answers
 * `''` for an id that is absent *or* unknown, and both are the same answer here
 * — before `useMeta()` has resolved there is nothing honest to draw, and one
 * frame with no badge is better than one frame with a wrong one.
 *
 * `useMeta` is a hook and this is a grid cell, so it runs once per tile; the
 * query is cached forever (`STALE.meta`) and shared by key, so a wall of two
 * hundred tiles is still one request. It must not be lifted into a prop — that
 * would put the format vocabulary in every screen that draws a tile.
 */

import { useMeta } from '@/api/queries'
import { SpecBadge } from '@/design'
import { formatLabel } from '@/format'

import styles from '@/widgets/QualityBadge/QualityBadge.module.css'

export interface QualityBadgeProps {
  /** A Qobuz `format_id` — the copy on disk. `null` ⇒ nothing is rendered. */
  formatId: number | null
}

export function QualityBadge({ formatId }: QualityBadgeProps) {
  const { data: meta } = useMeta()
  const label = formatLabel(formatId, meta?.format_labels)

  if (label === '') return null

  return <SpecBadge className={styles.badge}>{label}</SpecBadge>
}
