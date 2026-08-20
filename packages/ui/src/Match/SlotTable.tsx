import type { HTMLAttributes, ReactNode, Ref } from 'react'

import { Table, TableCell, type TableDensity, TableHeaderCell } from '../Table/Table.tsx'
import { Text } from '../Text/Text.tsx'
import { VisuallyHidden } from '../VisuallyHidden/VisuallyHidden.tsx'
import { describeDrift, formatDrift } from './format.ts'
import styles from './SlotTable.module.css'

export type SlotFile = {
  /** Library-relative, as the catalogue stores it. */
  readonly path: string
  readonly sizeBytes?: number
}

export type SlotRow = {
  readonly discNumber: number
  readonly position: number
  /** The printed number — "A1", "12a". Falls back to `position` when absent. */
  readonly number?: string
  readonly title: string
  /**
   * The release's printed length, already formatted `m:ss` or `h:mm:ss`.
   *
   * A string, not a number, because the API formats durations server-side "so
   * the same decision isn't in a second place" — and this component is not
   * going to be the second place.
   */
  readonly duration?: string
  /** What the file measures, same formatting. Absent when nothing landed here. */
  readonly measured?: string
  /**
   * |measured − printed| in milliseconds. `null` means one side was unknown;
   * **omitting the key entirely** means no file landed on this slot at all.
   */
  readonly driftMs?: number | null
  readonly file?: SlotFile
}

export type SlotTableProps = Omit<HTMLAttributes<HTMLTableElement>, 'children'> & {
  /** The `<caption>`. Required — a table inside a card must say whose it is. */
  readonly caption: ReactNode
  readonly rows: readonly SlotRow[]
  /** Defaults to true when any row sits on a disc above the first. */
  readonly showDiscs?: boolean
  readonly density?: TableDensity
  readonly stickyHeader?: boolean
  /** Drift at or over this reads as a warning. Defaults to the strictest rung. */
  readonly driftToleranceMs?: number
  /** Where a play control goes. Omit it and the column is not rendered at all. */
  readonly renderPlay?: (row: SlotRow) => ReactNode
  readonly ref?: Ref<HTMLTableElement>
}

/**
 * Slot by slot: what the release prints, what the library holds, and how far
 * apart the two are.
 *
 * **A slot with no file is a row, never an absent row.** "You are missing track
 * 7" is the question the full track list is stored to answer, and a table that
 * silently omitted the empty slots could not answer it.
 *
 * The number cell is a `<th scope="row">` rather than a `<td>`, so every other
 * cell in the row announces with it — "A1, Rock with You, 3:40" instead of
 * three loose values.
 */
export function SlotTable({
  caption,
  rows,
  showDiscs,
  density = 'compact',
  stickyHeader = false,
  driftToleranceMs = 750,
  renderPlay,
  className,
  ...rest
}: SlotTableProps) {
  // Decided from the rows rather than from a prop the caller has to keep in
  // sync with its own data — the same reasoning as CatalogueGrid sizing itself
  // from its children. In JS rather than CSS because a column's *existence*
  // cannot be a `:has()`.
  const discs = showDiscs ?? rows.some((row) => row.discNumber > 1)

  return (
    <Table
      className={className ? `${styles.root} ${className}` : styles.root}
      density={density}
      stickyHeader={stickyHeader}
      {...rest}
    >
      <caption className={styles.caption}>{caption}</caption>
      <thead>
        <tr>
          {discs ? <TableHeaderCell numeric>Disc</TableHeaderCell> : null}
          <TableHeaderCell numeric>#</TableHeaderCell>
          <TableHeaderCell>Title</TableHeaderCell>
          <TableHeaderCell numeric>Printed</TableHeaderCell>
          <TableHeaderCell numeric>Measured</TableHeaderCell>
          <TableHeaderCell numeric>Drift</TableHeaderCell>
          <TableHeaderCell>File</TableHeaderCell>
          {renderPlay != null ? (
            <TableHeaderCell>
              {/* An empty `<th>` is an `empty-table-header` failure. The column
                  wants no visible heading, so only the eye is spared it. */}
              <VisuallyHidden>Play</VisuallyHidden>
            </TableHeaderCell>
          ) : null}
        </tr>
      </thead>
      <tbody>
        {rows.map((row) => {
          const held = row.file != null
          const drift = row.driftMs ?? null

          return (
            <tr
              key={`${row.discNumber}:${row.position}`}
              className={styles.row}
              data-missing={held ? undefined : true}
            >
              {discs ? <TableCell numeric>{row.discNumber}</TableCell> : null}
              <TableHeaderCell scope="row" numeric>
                {row.number ?? row.position}
              </TableHeaderCell>
              <TableCell truncate>{row.title}</TableCell>
              <TableCell numeric>{row.duration ?? '—'}</TableCell>
              <TableCell numeric>{row.measured ?? '—'}</TableCell>
              <TableCell numeric data-drifted={isDrifted(row, driftToleranceMs) || undefined}>
                {held ? (
                  <>
                    {formatDrift(drift)}
                    {drift == null ? (
                      <VisuallyHidden> {describeDrift(drift)}</VisuallyHidden>
                    ) : null}
                  </>
                ) : (
                  '—'
                )}
              </TableCell>
              <TableCell truncate>
                {held ? (
                  <Text size="xs" family="mono" tone="secondary" truncate>
                    {row.file?.path}
                  </Text>
                ) : (
                  <Text size="xs" tone="tertiary">
                    missing
                  </Text>
                )}
              </TableCell>
              {renderPlay != null ? (
                <TableCell className={styles.playCell}>{held ? renderPlay(row) : null}</TableCell>
              ) : null}
            </tr>
          )
        })}
      </tbody>
    </Table>
  )
}

function isDrifted(row: SlotRow, toleranceMs: number): boolean {
  return row.file != null && row.driftMs != null && row.driftMs > toleranceMs
}
