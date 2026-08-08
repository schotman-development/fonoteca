/**
 * A metadata source, named, with its categorical marker (design 470–475).
 *
 * It is not a control and never becomes one. The design draws it in the release
 * drawer to say *which sources voted on this release, and on how many fields* —
 * a statement, not a filter — and the moment it grew an on-state it would be a
 * `Chip`, which is a different primitive with a different job.
 *
 * `dot` takes a CSS value, and the only values that belong in it are the
 * `--c-src-*` family: `dot="var(--c-src-musicbrainz)"`. It is written into a
 * custom property rather than a `color` declaration so that the caller's value
 * stays a token reference — a hex passed here would be a raw colour in a screen,
 * which is the thing tokens.css exists to make impossible. Omitting it leaves
 * the marker in `--c-ink-mark`, which is honest: a source with no colour of its
 * own is a source nobody has assigned one to, not a source in trouble.
 *
 * The hue is categorical. It says which source, never how bad — a SourceChip
 * may not be painted `--c-warn` or `--c-bad`.
 */

import type { CSSProperties, ReactNode } from 'react'

import { cx } from '@/design/cx'
import type { StyleableProps } from '@/design/types'
import styles from '@/design/SourceChip/SourceChip.module.css'

export interface SourceChipProps extends StyleableProps {
  /**
   * The source's own name: `musicbrainz`, `deezer`, `acoustid`.
   *
   * `children`, not `name`, because every other pill in this layer — `Chip`,
   * `Badge`, `SpecBadge` — takes its word that way, and one of the four
   * spelling it differently is a thing to look up rather than to know.
   */
  children: ReactNode
  /** A `var(--c-src-*)` reference. Never a literal colour. */
  dot?: string
  /** The mono tail — the design shows a field count here (473). */
  detail?: ReactNode
}

export function SourceChip({ children, dot, detail, className }: SourceChipProps) {
  const marker: CSSProperties | undefined =
    dot === undefined
      ? undefined
      : ({ '--source-dot': dot } as CSSProperties)

  return (
    <span className={cx(styles.chip, className)}>
      <span aria-hidden="true" className={styles.marker} style={marker} />
      <span>{children}</span>
      {detail === undefined || detail === null ? null : (
        <span className={styles.detail}>{detail}</span>
      )}
    </span>
  )
}
