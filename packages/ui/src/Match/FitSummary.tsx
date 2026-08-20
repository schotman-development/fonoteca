import type { HTMLAttributes, Ref } from 'react'

import { Badge } from '../Badge/Badge.tsx'
import { Text } from '../Text/Text.tsx'
import { VisuallyHidden } from '../VisuallyHidden/VisuallyHidden.tsx'
import { FitMeter } from './FitMeter.tsx'
import styles from './FitSummary.module.css'
import { describeDrift, formatDrift, formatRatio } from './format.ts'

/**
 * How well one release explains a set of files.
 *
 * A mirror of `Fonoteca.Domain/Identification/ReleaseFit.cs`, restated in the
 * shapes a screen needs, and deliberately **not** imported from the API client:
 * this package has no dependency on the contract and must not grow one.
 */
export type ReleaseFitReadout = {
  /** `filesExplained / slotCount`, 0..1 — is the *release* accounted for. */
  readonly coverage: number
  /**
   * Mean |measured − printed| track length, in milliseconds.
   *
   * **`null` is not measurable, not zero.** A release MusicBrainz prints no
   * lengths for produces no evidence: it passes the gate untested and ranks
   * below anything measured.
   */
  readonly meanDriftMs: number | null
  readonly filesExplained: number
  /** Tracks on the release, filled or not. */
  readonly slotCount: number
  /** Distinct **tracks** held, never files. */
  readonly held: number
  readonly trackCount: number
  readonly official: boolean
  /** How many other editions fitted exactly as well. Zero for a clean answer. */
  readonly editionAlternatives: number
}

export type FitSummaryProps = Omit<HTMLAttributes<HTMLDivElement>, 'children'> & {
  readonly fit: ReleaseFitReadout
  /** Drift at or under this reads as clean. Defaults to the strictest rung. */
  readonly driftToleranceMs?: number
  readonly layout?: 'row' | 'column'
  readonly ref?: Ref<HTMLDivElement>
}

/**
 * The evidence for one candidate, laid out so that a column of them can be
 * compared down the page.
 *
 * `filesExplained` is never rendered without `coverage` beside it, and that
 * pairing is the whole lesson of the failure this screen exists for: a
 * five-disc box set explaining twenty files looks like the best answer until
 * you see that twenty files is 26% of it, while the album beside it is ten
 * files and 100%. A component able to show one without the other would let a
 * caller reproduce the bug in the interface.
 */
export function FitSummary({
  fit,
  driftToleranceMs = 750,
  layout = 'row',
  className,
  ...rest
}: FitSummaryProps) {
  const measured = fit.meanDriftMs != null
  const drifted = measured && fit.meanDriftMs != null && fit.meanDriftMs > driftToleranceMs

  return (
    <div
      className={className ? `${styles.root} ${className}` : styles.root}
      data-layout={layout}
      {...rest}
    >
      <FitMeter
        label="Coverage"
        value={fit.coverage}
        tone={fit.coverage >= 0.75 ? 'success' : fit.coverage >= 0.5 ? 'warning' : 'danger'}
        detail={`${fit.filesExplained.toLocaleString()} of ${fit.slotCount.toLocaleString()} slots filled`}
        className={styles.meter}
      />

      <FitMeter
        label="Tracks held"
        value={fit.trackCount === 0 ? null : fit.held / fit.trackCount}
        tone="accent"
        detail={`${formatRatio(fit.held, fit.trackCount)} tracks`}
        unmeasurable="This release lists no tracks."
        className={styles.meter}
      />

      <Stat
        label="Mean drift"
        value={formatDrift(fit.meanDriftMs)}
        tone={drifted ? 'warning' : 'primary'}
        // The dash is what makes the column scannable; the words still reach a
        // screen reader, which is the only reader that cannot see the dash.
        note={measured ? null : 'Not measurable.'}
        spoken={measured ? null : describeDrift(fit.meanDriftMs)}
      />

      <div className={styles.badges}>
        {/*
         * Shown in **both** states, unlike `ReleasePage`, which shows a status
         * badge only when it is not Official. That rule is right for a page
         * about one album — a badge on every one trains the eye to skip it —
         * and wrong here, where the entire job is comparison down a column and
         * a cell that is sometimes blank is harder to compare than one that is
         * always filled.
         */}
        <Badge tone={fit.official ? 'neutral' : 'warning'} variant="outline" size="sm">
          {fit.official ? 'Official' : 'Unofficial'}
        </Badge>
        {fit.editionAlternatives > 0 ? (
          <Badge tone="warning" size="sm">
            {fit.editionAlternatives === 1
              ? '1 other edition fits as well'
              : `${fit.editionAlternatives} other editions fit as well`}
          </Badge>
        ) : null}
      </div>
    </div>
  )
}

type StatProps = {
  readonly label: string
  readonly value: string
  readonly tone: 'primary' | 'warning'
  readonly note: string | null
  readonly spoken: string | null
}

function Stat({ label, value, tone, note, spoken }: StatProps) {
  return (
    <div className={styles.stat}>
      <Text size="xs" tone="tertiary">
        {label}
      </Text>
      <Text size="sm" weight="medium" family="mono" tone={tone}>
        {value}
        {spoken != null ? <VisuallyHidden> {spoken}</VisuallyHidden> : null}
      </Text>
      {note != null ? (
        <Text size="2xs" tone="tertiary">
          {note}
        </Text>
      ) : null}
    </div>
  )
}
