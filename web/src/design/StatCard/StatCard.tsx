/**
 * The health tile — design 104–108, four of them in a
 * `repeat(auto-fit, minmax(min(100%, 128px), 1fr))` grid.
 *
 * A `<button>`, not a div with a click handler, and that is the whole reason
 * the hover edge lives on `shared/surface.module.css`'s `.hoverEdge` rather
 * than on `.surface`: every one of these four navigates somewhere, so the edge
 * darkening under the pointer is a promise the control keeps. A static tile
 * would compose `.surface` and stay still.
 *
 * **`value` is a `ReactNode` and this component never formats it.** The card
 * that matters most on the dashboard is the one reading
 * `last_result?.states?.replaced`, which is `undefined` until a pass has run —
 * and the honest rendering of that is `EM_DASH` from `@/format` with the share
 * line reading "no pass yet", never `0`. A primitive that accepted a `number`
 * and printed it would have to invent a fallback, and every fallback available
 * to it is a lie about whether anything was measured. So the caller decides,
 * and the caller is the only one who knows.
 *
 * `tone` colours the figure only. The label and the share line are always the
 * ink ramp, because a tile whose every line is amber reads as four warnings
 * rather than one number that happens to be amber. Amber here is `--c-warn`
 * (5.0:1) and not `--c-warn-solid` (3.2:1): the figure is text.
 */

import type { ReactNode } from 'react'

import surface from '@/design/shared/surface.module.css'
import { cx } from '@/design/cx'
import type { StyleableProps, Tone } from '@/design/types'

import styles from '@/design/StatCard/StatCard.module.css'

const TONE: Record<Tone, string | undefined> = {
  neutral: styles.toneNeutral,
  accent: styles.toneAccent,
  ok: styles.toneOk,
  warn: styles.toneWarn,
  bad: styles.toneBad,
}

export interface StatCardProps extends StyleableProps {
  /**
   * The figure. Pass `EM_DASH` for "nothing has measured this" — this
   * component will not substitute one, and must not.
   */
  value: ReactNode
  /** What the figure counts (design 106): `verified`, `never baselined`, … */
  label: ReactNode
  /** The share line under it (design 107): `62% of measured files`. */
  share?: ReactNode
  /** Colours the figure alone. Defaults to the ink ramp — no colour is the resting state. */
  tone?: Tone
  /** Every tile in the design goes somewhere; there is no inert variant. */
  onClick: () => void
}

export function StatCard({
  value,
  label,
  share,
  tone = 'neutral',
  onClick,
  className,
}: StatCardProps) {
  return (
    <button type="button" className={cx(surface.hoverEdge, styles.card, className)} onClick={onClick}>
      <span className={cx(styles.value, TONE[tone])}>{value}</span>
      <span className={styles.label}>{label}</span>
      {share == null ? null : <span className={styles.share}>{share}</span>}
    </button>
  )
}
