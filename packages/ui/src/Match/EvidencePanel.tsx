import type { HTMLAttributes, ReactNode, Ref } from 'react'

import { Card } from '../Card/Card.tsx'
import { Text } from '../Text/Text.tsx'
import styles from './EvidencePanel.module.css'

export type EvidenceTone = 'neutral' | 'success' | 'warning' | 'danger' | 'info'

export type EvidenceRow = {
  readonly label: string
  /**
   * **Absence is evidence.** This library is deliberately tag-stripped —
   * sampling it with ffprobe found `ACOUSTID_ID` on every file and nothing else
   * at all — so "ALBUM — not present" is a finding. Pass the absence as a value;
   * never drop the row.
   */
  readonly value: ReactNode
  readonly tone?: EvidenceTone
  readonly note?: ReactNode
  /** Tabular figures: paths, durations, byte counts, fingerprint lengths. */
  readonly mono?: boolean
  /** Takes the full width — for a path, or a long list of formats. */
  readonly wide?: boolean
}

export type EvidencePanelProps = Omit<HTMLAttributes<HTMLElement>, 'title'> & {
  /** What *kind* of thing is being identified. Becomes the card's heading. */
  readonly heading: ReactNode
  /** The thing itself, prominent: a folder, a file name, a recording title. */
  readonly subject: ReactNode
  readonly subjectNote?: ReactNode
  readonly subjectMono?: boolean
  /** The current outcome, as Badges. The tone is the caller's to choose. */
  readonly badges?: ReactNode
  readonly rows: readonly EvidenceRow[]
  /** The card's aside — rescan, re-fingerprint, open the folder. */
  readonly actions?: ReactNode
  /** Below the list — a file table, a fingerprint list, a row of play buttons. */
  readonly children?: ReactNode
  readonly ref?: Ref<HTMLElement>
}

/**
 * Everything known about the thing being identified, before any candidate is
 * chosen.
 *
 * It renders a `Card`, so it inherits the `<section>`, the real `<h2>`, the rule
 * and the aside slot rather than growing a second opinion about all four. Two
 * title-shaped props because they are two different things: `heading` says what
 * kind of subject this is, `subject` is the subject.
 *
 * The rows are a real `<dl>`, and the shape of it is not a style choice — a
 * `<dl>` may contain only `<dt>`, `<dd>`, `<div>` and script-supporting
 * elements, which is why each row is a `<div>` wrapper and why reaching for
 * `Stack` here would be fine (a div) while reaching for `Text` would not (a
 * span).
 *
 * Generic across all three subjects on purpose: a file set, a single file and a
 * recording differ in their rows, not in their shape.
 */
export function EvidencePanel({
  heading,
  subject,
  subjectNote,
  subjectMono = false,
  badges,
  rows,
  actions,
  children,
  className,
  ...rest
}: EvidencePanelProps) {
  return (
    <Card
      className={className ? `${styles.root} ${className}` : styles.root}
      title={heading}
      {...(actions != null ? { aside: actions } : {})}
      {...rest}
    >
      <div className={styles.subject}>
        <Text size="lg" weight="semibold" family={subjectMono ? 'mono' : 'sans'} block>
          {subject}
        </Text>
        {subjectNote != null ? (
          <Text size="xs" tone="tertiary" block>
            {subjectNote}
          </Text>
        ) : null}
        {badges != null ? <div className={styles.badges}>{badges}</div> : null}
      </div>

      <dl className={styles.rows}>
        {rows.map((row) => (
          <div
            key={row.label}
            className={styles.row}
            data-wide={row.wide || undefined}
            data-tone={row.tone ?? 'neutral'}
          >
            <dt className={styles.label}>{row.label}</dt>
            <dd className={styles.value}>
              <span className={styles.valueText} data-mono={row.mono || undefined}>
                {row.value}
              </span>
              {row.note != null ? <span className={styles.note}>{row.note}</span> : null}
            </dd>
          </div>
        ))}
      </dl>

      {children}
    </Card>
  )
}
