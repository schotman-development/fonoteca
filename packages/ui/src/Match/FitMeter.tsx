import type { CSSProperties, HTMLAttributes, ReactNode, Ref } from 'react'

import { Text } from '../Text/Text.tsx'
import styles from './FitMeter.module.css'
import { formatCoverage } from './format.ts'

export type FitMeterTone = 'neutral' | 'accent' | 'success' | 'warning' | 'danger'

export type FitMeterProps = Omit<HTMLAttributes<HTMLDivElement>, 'children'> & {
  /** What is being measured — "Coverage", "Tracks held". */
  readonly label: ReactNode
  /**
   * 0..1, or `null` for *not measurable*.
   *
   * Null is not zero and must never render as it. An unmeasured fit produced no
   * evidence; a zero one produced evidence that says no.
   */
  readonly value: number | null
  /** The same fact in its own units — "10 of 10 tracks". */
  readonly detail?: ReactNode
  /** What null means *here* — "MusicBrainz prints no track lengths for this one." */
  readonly unmeasurable?: ReactNode
  readonly tone?: FitMeterTone
  readonly size?: 'sm' | 'md'
  readonly ref?: Ref<HTMLDivElement>
}

/**
 * One measurement, as a number and a bar.
 *
 * **There is no `role="meter"` here, and no native `<meter>`.** Three reasons,
 * and the first is decisive:
 *
 * 1. `role="meter"` requires `aria-valuenow`, which is a number. Coverage and
 *    drift can be *unmeasurable*, which no numeric attribute can express, and
 *    `aria-valuenow="0"` would be exactly the lie the domain took care to avoid.
 * 2. A meter's contents are not presentational, so the visible "100% — 10 of 10"
 *    would be announced *in addition to* its value text. One fact, twice.
 * 3. Native `<meter>` styles only through vendor shadow pseudo-elements,
 *    disappears entirely under `forced-colors`, and is mapped to `progressbar`
 *    by several screen readers — which announces a *task in progress*, which a
 *    coverage figure is not.
 *
 * So the value is real text, the bar is `aria-hidden` decoration fed a custom
 * property, and the root carries no role at all. It is not colour-only: the
 * fill's *length* encodes the value and the number is written beside it.
 */
export function FitMeter({
  label,
  value,
  detail,
  unmeasurable,
  tone = 'accent',
  size = 'md',
  className,
  style,
  ...rest
}: FitMeterProps) {
  const measured = value != null && Number.isFinite(value)
  const fraction = measured ? Math.min(1, Math.max(0, value)) : 0

  const fillStyle = { '--fit-value': `${fraction}`, ...style } as CSSProperties

  return (
    <div
      className={className ? `${styles.root} ${className}` : styles.root}
      data-tone={tone}
      data-size={size}
      data-measured={measured || undefined}
      style={fillStyle}
      {...rest}
    >
      <div className={styles.heading}>
        <Text size={size === 'sm' ? '2xs' : 'xs'} tone="tertiary">
          {label}
        </Text>
        <Text size={size === 'sm' ? 'xs' : 'sm'} weight="medium" family="mono">
          {formatCoverage(value)}
        </Text>
      </div>

      {/* Decoration. The words above and below carry the whole fact, so a
          screen reader that never sees this loses nothing. */}
      <div className={styles.track} aria-hidden="true">
        <div className={styles.fill} />
      </div>

      {measured ? (
        detail != null ? (
          <Text size="2xs" tone="tertiary">
            {detail}
          </Text>
        ) : null
      ) : (
        <Text size="2xs" tone="tertiary">
          {unmeasurable ?? 'Not measurable.'}
        </Text>
      )}
    </div>
  )
}
