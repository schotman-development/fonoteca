/**
 * The space-between fact rows (design 449–466 and 654–660).
 *
 * It is a real `<dl>` of `<dt>`/`<dd>` pairs rather than a stack of flex divs,
 * because that is what the markup means: "Release MBID" is the *name* of the
 * value beside it, and a screen reader that knows so can read the pair, jump
 * between pairs, and tell this list from the row of buttons underneath it. The
 * `<div>` wrapper around each pair is what makes the two halves one flex row and
 * has been valid inside a `<dl>` since HTML 5.2.
 *
 * `KeyValueList` owns only the spacing and the `ruled` variant; the pair owns
 * everything about itself. The variant is applied by descendant selector rather
 * than by cloning children, so a caller can put a `<Placeholder/>` or a
 * conditional block between two rows without the list having to understand it.
 *
 * The em-dash rule: a value of `null`, `undefined` or `''` renders `EM_DASH`
 * from `@/format`, never a blank cell and never a zero. Half of what this list
 * shows — an MBID, a barcode, a catalogue number — is legitimately absent, and
 * an empty `<dd>` reads as a rendering bug rather than as "nobody knows".
 */

import type { ReactNode } from 'react'

import { EM_DASH } from '@/format'
import { cx } from '@/design/cx'
import type { StyleableProps, Tone } from '@/design/types'
import styles from '@/design/KeyValueList/KeyValueList.module.css'

export interface KeyValueListProps extends StyleableProps {
  children: ReactNode
  /** Rule each row off with a hairline — the identify screen's evidence list. */
  ruled?: boolean
}

export function KeyValueList({ children, ruled, className }: KeyValueListProps) {
  return (
    <dl className={cx(styles.list, ruled === true && styles.ruled, className)}>
      {children}
    </dl>
  )
}

export interface KeyValueProps extends StyleableProps {
  label: ReactNode
  /** `null` / `undefined` / `''` all render the em dash. */
  value: ReactNode
  /** Colours the VALUE. Amber here is `--c-warn`, because this is text. */
  tone?: Tone
}

const TONE_CLASS: Record<Exclude<Tone, 'neutral'>, string | undefined> = {
  accent: styles.toneAccent,
  ok: styles.toneOk,
  warn: styles.toneWarn,
  bad: styles.toneBad,
}

export function KeyValue({
  label,
  value,
  tone = 'neutral',
  className,
}: KeyValueProps) {
  const known = value !== null && value !== undefined && value !== ''

  return (
    <div className={cx(styles.row, className)}>
      <dt className={styles.key}>{label}</dt>
      <dd className={cx(styles.value, tone !== 'neutral' && TONE_CLASS[tone])}>
        {known ? value : EM_DASH}
      </dd>
    </div>
  )
}
