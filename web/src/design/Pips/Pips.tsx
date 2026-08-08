/**
 * The stack-mode progress dashes — design 699–703, under the Identify screen's
 * one-card-at-a-time review.
 *
 * Twenty-six by three, six apart, the current one `--c-accent` and the rest
 * `--c-toggle-off`. What the design cannot show is that this appears in two
 * genuinely different jobs, and rendering either as the other is a real bug
 * rather than a nicety:
 *
 *   - **navigable** (`onSelect` given) — a row of real `<button>`s in a real
 *     list, each named "Go to item 3 of 5", the current one carrying
 *     `aria-current`. Nine unlabelled dashes are one of the few genuinely
 *     unusable things a UI can be: a keyboard user gets nine tab stops with
 *     nine identical names, and colour is the entire visible answer.
 *   - **decorative** (no `onSelect`) — a plain row that is `aria-hidden`, with
 *     the position stated once as text for a reader. Dashes that cannot be
 *     pressed must not be tab stops promising a press that does nothing.
 *
 * ── the press target is bigger than the dash ───────────────────────────────
 *
 * A 26×3 button is three pixels of vertical target. The button is padded to a
 * 27px-tall hit area and pulled back with an equal negative margin, so the row
 * still occupies the design's 3px and the thing you have to hit is nine times
 * taller than the thing you can see. The dash itself is a `<span>` inside, not
 * the button's own background, which is what makes that possible.
 *
 * ── indices are 0-based ────────────────────────────────────────────────────
 *
 * `current` and `onSelect` both speak the array index a caller already holds;
 * the "3 of 5" in the accessible name is the only place the human numbering
 * appears. Mixing the two conventions in one component is how a stack lands on
 * the wrong card by one.
 */

import { cx } from '@/design/cx'
import type { StyleableProps } from '@/design/types'

import styles from '@/design/Pips/Pips.module.css'

export interface PipsProps extends StyleableProps {
  /** How many items there are. Zero or fewer renders nothing at all. */
  total: number
  /** The 0-based index of the current item. Clamped into range. */
  current: number
  /**
   * Given, every dash becomes a real button and receives the 0-based index it
   * stands for. Absent, the row is decoration and takes no tab stops.
   */
  onSelect?: (index: number) => void
  /**
   * What the row is a position in — "Review queue". Required: it is the
   * accessible name of the list, and the only thing that says what "3 of 5"
   * counts.
   */
  label: string
}

export function Pips({ total, current, onSelect, label, className }: PipsProps) {
  const count = Math.max(0, Math.floor(total))
  if (count === 0) return null

  // A stack that has just shrunk under a rejection can hold an index past its
  // own end for one render. Clamping is cheaper than every caller remembering.
  const index = Math.min(Math.max(0, Math.floor(current)), count - 1)
  const items = Array.from({ length: count }, (_, i) => i)

  if (onSelect === undefined) {
    return (
      <div className={cx(styles.row, className)}>
        {/* The position, said once, in words. The dashes below carry no meaning
            for a reader — they are the same sentence drawn. */}
        <span className="visuallyHidden">{`${label}: item ${index + 1} of ${count}`}</span>
        {items.map((i) => (
          <span
            key={i}
            aria-hidden="true"
            className={cx(styles.pip, i === index ? styles.pipCurrent : undefined)}
          />
        ))}
      </div>
    )
  }

  return (
    <ul className={cx(styles.row, className)} aria-label={label}>
      {items.map((i) => (
        <li key={i} className={styles.item}>
          <button
            type="button"
            className={styles.button}
            // `true`, never `false`: `aria-current="false"` is a valid value
            // that some readers still announce, so the other four dashes say
            // nothing at all.
            aria-current={i === index ? 'true' : undefined}
            onClick={() => {
              onSelect(i)
            }}
          >
            <span className="visuallyHidden">{`Go to item ${i + 1} of ${count}`}</span>
            <span
              aria-hidden="true"
              className={cx(styles.pip, i === index ? styles.pipCurrent : undefined)}
            />
          </button>
        </li>
      ))}
    </ul>
  )
}
