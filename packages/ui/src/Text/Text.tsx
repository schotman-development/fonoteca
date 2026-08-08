import type { HTMLAttributes, Ref } from 'react'

import styles from './Text.module.css'

export type TextSize = '2xs' | 'xs' | 'sm' | 'md' | 'lg' | 'xl'
export type TextTone =
  | 'primary'
  | 'secondary'
  | 'tertiary'
  | 'disabled'
  | 'accent'
  | 'success'
  | 'warning'
  | 'danger'
export type TextWeight = 'regular' | 'medium' | 'semibold' | 'bold'

export type TextProps = HTMLAttributes<HTMLSpanElement> & {
  readonly size?: TextSize
  readonly tone?: TextTone
  readonly weight?: TextWeight
  /**
   * `mono` is not stylistic. Content hashes, AcoustID fingerprints, MusicBrainz
   * IDs and bitrates only become scannable in a column when their glyphs are
   * the same width.
   */
  readonly family?: 'sans' | 'mono'
  /** Single-line ellipsis. Needs a bounded-width ancestor to have any effect. */
  readonly truncate?: boolean
  /** Render as a block so it can take a full row. */
  readonly block?: boolean
  readonly ref?: Ref<HTMLSpanElement>
}

export function Text({
  size = 'md',
  tone = 'primary',
  weight = 'regular',
  family = 'sans',
  truncate = false,
  block = false,
  className,
  ...rest
}: TextProps) {
  return (
    <span
      className={className ? `${styles.root} ${className}` : styles.root}
      data-size={size}
      data-tone={tone}
      data-weight={weight}
      data-family={family}
      data-truncate={truncate || undefined}
      data-block={block || undefined}
      {...rest}
    />
  )
}
