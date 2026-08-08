/**
 * The quality badge on a piece of artwork (design 284–286).
 *
 * `padding: 2px 6px; border-radius: 5px; background: rgba(251,250,248,.9);
 * color: #B45309; font-family: mono; font-size: 10px; font-weight: 500` — the
 * top-right corner of an album tile, carrying `24/96` or `16/44.1`.
 *
 * ── why it is a primitive of its own and not `Badge tone="warn"` ──────────
 *
 * They look alike and are not the same object. `Badge` is prose in the sans
 * face, set at the body weight of a word — a *verdict* about the release
 * (`CORRUPT`, `REPLACED`). This is a *figure*: mono, tabular, one step
 * smaller, on the canvas tint rather than paper, and always the same amber.
 * Folding it into `Badge` would mean a `mono` flag plus a `size` flag plus a
 * forced tone, which is three props to express one thing the design draws
 * exactly once.
 *
 * The amber is fixed for the same reason the colour is not a prop anywhere in
 * this pair: the badge says "this copy is better than CD", which is one claim
 * with one colour. A quality figure that could be painted red would be saying
 * something the design never says.
 *
 * Nothing here knows what a format id or a bit depth is — the caller has
 * already turned one into a string through `fmtQuality`.
 */

import type { ReactNode } from 'react'

import { cx } from '@/design/cx'
import type { StyleableProps } from '@/design/types'
import styles from '@/design/SpecBadge/SpecBadge.module.css'

export interface SpecBadgeProps extends StyleableProps {
  /** The rendered figure, e.g. `24/96`. */
  children: ReactNode
}

export function SpecBadge({ children, className }: SpecBadgeProps) {
  return <span className={cx(styles.spec, className)}>{children}</span>
}
