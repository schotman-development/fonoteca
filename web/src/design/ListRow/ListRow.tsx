/**
 * The row every list in this application is made of (design 145, 327, 493, 570).
 *
 * ── why a clickable row is not a <button> ──────────────────────────────────
 *
 * The design gives several of these rows an `onClick` (168, 619) while other
 * rows of the identical shape carry their own buttons inside them (154–156:
 * Download and Ignore). Wrapping arbitrary children in a `<button>` is invalid
 * markup the moment one of those children is interactive — a button inside a
 * button is not focusable, is not announced, and is dropped by the HTML parser
 * — and `role="button"` on the row is the same defect wearing a valid-looking
 * hat: the nested Download button is still a descendant of something claiming
 * to be one control, so a screen reader reads the whole row as the button's
 * name and the nested action disappears from the tab order's meaning.
 *
 * So a clickable row renders a real `<button>` as a transparent layer over
 * itself, carrying a `.visuallyHidden` name, and the row's element children are
 * lifted above that layer by the stylesheet. The result is one focusable,
 * correctly-named control for "open this row" and, independently, one for every
 * action the caller put in it — which is exactly what the markup claims.
 *
 * The accessible name is required by the type rather than by a runtime check:
 * `onClick` and `actionLabel` arrive together or not at all, so there is no way
 * to ship a row that is clickable and anonymous.
 */

import type { ReactNode } from 'react'

import { cx } from '@/design/cx'
import type { StyleableProps } from '@/design/types'
import styles from '@/design/ListRow/ListRow.module.css'

interface ListRowBase extends StyleableProps {
  children: ReactNode
  /**
   * The track-list density (design 493): a tighter gap and a small horizontal
   * inset, for rows that sit inside a panel rather than across a column.
   */
  dense?: boolean
}

/** A row that only displays. Its children may still be interactive. */
export interface StaticListRowProps extends ListRowBase {
  onClick?: undefined
  actionLabel?: undefined
}

/** A row that is itself an action, and therefore has to have a name. */
export interface ClickableListRowProps extends ListRowBase {
  onClick: () => void
  /** What pressing the row does, e.g. `Open Down the Road Wherever`. */
  actionLabel: string
}

export type ListRowProps = StaticListRowProps | ClickableListRowProps

export function ListRow({
  children,
  dense,
  className,
  onClick,
  actionLabel,
}: ListRowProps) {
  const clickable = onClick !== undefined

  return (
    <div
      className={cx(
        styles.row,
        dense === true && styles.dense,
        clickable && styles.clickable,
        className,
      )}
    >
      {clickable ? (
        <button type="button" className={styles.hit} onClick={onClick}>
          <span className="visuallyHidden">{actionLabel}</span>
        </button>
      ) : null}
      {children}
    </div>
  )
}
