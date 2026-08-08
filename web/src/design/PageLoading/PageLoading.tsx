/**
 * What a screen shows while its first request is in flight.
 *
 * A title bar and `rows` lines, shaped like the block they will become. Two
 * decisions worth writing down:
 *
 *   - **`aria-busy` plus one visually hidden word.** A skeleton is invisible to
 *     a screen reader — it is a stack of empty divs — so without the word
 *     "Loading" the page is silent until the data lands, which is
 *     indistinguishable from a page that is finished and empty. `role="status"`
 *     makes it polite: it is announced when the reader gets to it, and it does
 *     not interrupt.
 *   - **no percentage, no spinner.** There is nothing to be a percentage of,
 *     and `Spinner` in this design means *the server is working* (a scan, a
 *     download, an integrity pass). Waiting for a GET is not that.
 *
 * `rows` is a shape, not a promise: pass roughly what the block holds so the
 * page does not jump when the real content replaces it.
 */

import { cx } from '@/design/cx'
import type { StyleableProps } from '@/design/types'

import styles from '@/design/PageLoading/PageLoading.module.css'

export interface PageLoadingProps extends StyleableProps {
  /** How many lines to reserve. Default 4 — about a panel's worth. */
  rows?: number
  /** Drop the wide first bar where the caller already rendered the heading. */
  title?: boolean
  /** What is being waited for, announced politely. Keep it short. */
  label?: string
}

export function PageLoading({
  rows = 4,
  title = true,
  label = 'Loading',
  className,
}: PageLoadingProps) {
  // Negative and fractional row counts come from arithmetic on a `null` count
  // often enough to be worth flooring rather than trusting.
  const count = Math.max(0, Math.floor(rows))

  return (
    <div className={cx(styles.root, className)} role="status" aria-busy="true">
      <span className="visuallyHidden">{label}</span>
      {title ? <div className={styles.title} /> : null}
      {Array.from({ length: count }, (_, i) => (
        <div key={i} className={styles.row} />
      ))}
    </div>
  )
}
