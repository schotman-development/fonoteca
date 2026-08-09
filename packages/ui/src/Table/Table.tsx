import type { HTMLAttributes, Ref, TdHTMLAttributes, ThHTMLAttributes } from 'react'

import styles from './Table.module.css'

export type TableDensity = 'compact' | 'cozy' | 'comfortable'

export type TableProps = HTMLAttributes<HTMLTableElement> & {
  readonly density?: TableDensity
  /** Sticks the header to the top of the nearest scrolling ancestor. */
  readonly stickyHeader?: boolean
  readonly ref?: Ref<HTMLTableElement>
}

/**
 * A styled `<table>`, and nothing more.
 *
 * **This is not the data table ADR 0003 has in its backlog.** That one carries
 * row virtualization, keyboard navigation and header semantics of its own, and
 * it is still unbuilt — it is the largest single piece of work the
 * no-dependencies decision creates, and it should be designed against the
 * 100,000-row catalogue screen rather than borrowed from here.
 *
 * What this is: native table semantics with the token scale on them, for the
 * lists that are hundreds of rows rather than hundreds of thousands. The
 * browser supplies the roles, the header association and the caption handling,
 * which is why there is no ARIA in this file at all — adding
 * `role="table"` to a `<table>` is how a hand-built one loses the semantics it
 * already had.
 *
 * Density comes from the row-height tokens rather than padding, so a row is a
 * known number of pixels tall. That is a requirement a virtualizer will have,
 * and honouring it now costs nothing.
 */
export function Table({ density = 'cozy', stickyHeader = false, className, ...rest }: TableProps) {
  return (
    <table
      className={className ? `${styles.root} ${className}` : styles.root}
      data-density={density}
      data-sticky={stickyHeader || undefined}
      {...rest}
    />
  )
}

export type TableCellProps = TdHTMLAttributes<HTMLTableCellElement> & {
  /** Right-aligned and tabular — for counts, durations and sizes. */
  readonly numeric?: boolean
  /** Collapse overflow to an ellipsis. Needs a bounded column width. */
  readonly truncate?: boolean
  readonly ref?: Ref<HTMLTableCellElement>
}

export function TableCell({
  numeric = false,
  truncate = false,
  className,
  ...rest
}: TableCellProps) {
  return (
    <td
      className={className ? `${styles.cell} ${className}` : styles.cell}
      data-numeric={numeric || undefined}
      data-truncate={truncate || undefined}
      {...rest}
    />
  )
}

export type TableHeaderCellProps = ThHTMLAttributes<HTMLTableCellElement> & {
  readonly numeric?: boolean
  readonly ref?: Ref<HTMLTableCellElement>
}

/**
 * A header cell, which defaults to `scope="col"`.
 *
 * The default is the point. A `<th>` without a scope is only associated with
 * its column by the browser's best guess, and the guess fails on exactly the
 * tables that need it most — the ones with a row header as well.
 */
export function TableHeaderCell({
  numeric = false,
  scope = 'col',
  className,
  ...rest
}: TableHeaderCellProps) {
  return (
    <th
      className={className ? `${styles.headerCell} ${className}` : styles.headerCell}
      scope={scope}
      data-numeric={numeric || undefined}
      {...rest}
    />
  )
}
