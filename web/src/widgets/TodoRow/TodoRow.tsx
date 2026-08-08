/**
 * One line of the dashboard's "To do" list (design 170–178).
 *
 * A coloured count, what those things are, and what to do about them — and the
 * whole row is one link, because that is what the design's `onClick` (171) is:
 * every one of its four rows navigates to the screen that can deal with the
 * count. A `<button onClick={navigate}>` would cost middle-click, copy-link and
 * the browser's own history on a row whose entire purpose is to be followed.
 *
 * ── the count is a datum, and the widget does not judge it ────────────────
 *
 * `count` is rendered as it arrives. This row does **not** hide itself when the
 * count is zero: which rows are worth showing is the dashboard's decision (a
 * zero row is dropped, and all-zero becomes the single line "Nothing needs
 * you"), and a widget that silently rendered nothing would make an empty list
 * indistinguishable from a broken one. It also does not compute a count — a
 * figure nothing measured never reaches this component, because the caller
 * holds the three-valued fields and knows which of them is `null`.
 *
 * ── the colour is a verdict, so it comes from the caller ──────────────────
 *
 * `tone` is the design system's status vocabulary and the design's own
 * `t.fg` (DCLogic 1216–1220): amber for things waiting on a person, red for
 * files that are broken. It sits on the count alone. Painting the title too
 * would make a five-row list read as an alarm however small the numbers are,
 * which is the opposite of what a to-do list is for.
 */

import { Link } from 'react-router-dom'

import { cx } from '@/design'
import type { Tone } from '@/design'

import styles from '@/widgets/TodoRow/TodoRow.module.css'

export interface TodoRowProps {
  /** How many of them there are. Rendered as given — see the note above. */
  count: number
  /** What they are: `identities to confirm`. */
  title: string
  /** What to do about them: `Review queue`. */
  action: string
  /** Colours the count only. */
  tone: Tone
  /** Where the row goes — the caller owns the address. */
  to: string
}

const TONE: Readonly<Record<Tone, string | undefined>> = {
  neutral: styles.countNeutral,
  accent: styles.countAccent,
  ok: styles.countOk,
  warn: styles.countWarn,
  bad: styles.countBad,
}

export function TodoRow({ count, title, action, tone, to }: TodoRowProps) {
  return (
    <Link to={to} data-plain="" className={styles.row}>
      <span className={cx(styles.count, 'mono', TONE[tone])}>{count}</span>
      <span className={styles.body}>
        <span className={styles.title}>{title}</span>
        <span className={styles.action}>{action}</span>
      </span>
    </Link>
  )
}
