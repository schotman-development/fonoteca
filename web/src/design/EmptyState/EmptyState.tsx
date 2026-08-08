/**
 * "There is nothing here, and that is the good news" (design 607–613).
 *
 * Three things it does not do, each on purpose:
 *
 * - The glyph is `aria-hidden`. `✓` announces as "check mark" ahead of the
 *   title, which turns "Queue clear" into "check mark Queue clear" and buys
 *   nothing: the title already says it.
 * - It is not a heading. An empty state stands *inside* a section that has
 *   already been headed, and promoting its title to an `<h2>` puts a second
 *   entry in the document outline for a block that appears and disappears with
 *   the data.
 * - It is not a live region. It renders when a list is empty, which is a state
 *   of the page and not an announcement — the mutation that emptied the list is
 *   what raises the toast.
 *
 * `action` is a slot rather than a button prop so the caller supplies a real
 * `Button` with a real destination; an EmptyState that built its own control
 * would need to know where the app goes, and this layer does not.
 */

import type { ReactNode } from 'react'

import { cx } from '@/design/cx'
import type { StyleableProps } from '@/design/types'
import styles from '@/design/EmptyState/EmptyState.module.css'

export interface EmptyStateProps extends StyleableProps {
  /** A single decorative mark: `✓`, `◎`, `⌗`. Hidden from assistive tech. */
  glyph: ReactNode
  title: ReactNode
  /** The sentence under the title. Absent ⇒ nothing is rendered for it. */
  note?: ReactNode
  /** Optional control — a `Button`, supplied by the caller. */
  action?: ReactNode
}

export function EmptyState({
  glyph,
  title,
  note,
  action,
  className,
}: EmptyStateProps) {
  return (
    <div className={cx(styles.root, className)}>
      <span aria-hidden="true" className={styles.glyph}>
        {glyph}
      </span>
      <p className={styles.title}>{title}</p>
      {note === undefined || note === null ? null : (
        <p className={styles.note}>{note}</p>
      )}
      {action === undefined || action === null ? null : (
        <div className={styles.action}>{action}</div>
      )}
    </div>
  )
}
