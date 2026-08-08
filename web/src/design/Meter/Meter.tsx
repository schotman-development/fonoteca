/**
 * The progress bar — design 192–194 (the dashboard's job bars, 3px), 555–557
 * (the radar's per-release bar, 118×4) and 837–839 (the footer's, 4px).
 *
 * ── `null` is not zero, and that is the whole component ────────────────────
 *
 * `value` is `number | null`, where the number is a fraction in 0..1 and
 * `null` means **nothing has measured this**. It draws an empty track, no fill
 * at all, and reports itself to the accessibility tree as an *indeterminate*
 * progressbar — `aria-valuenow` is OMITTED rather than sent as `0`, because
 * `aria-valuenow="0"` is a measurement and says "none of it is done".
 *
 * The distinction is not academic here. `AlbumOut.complete`, `tracks_on_disk`
 * and `on_disk_ratio` are all three-valued, and the API publishes **no**
 * percentage for a library scan at all. A meter that defaulted `null` to zero
 * would draw an empty bar next to a scan that is halfway through, and the user
 * would read it as stalled and press the button again.
 *
 * ── `indeterminate` is the other half of that ──────────────────────────────
 *
 * A job the API reports as `running` with no percentage is not "0% done" and
 * it is not "unmeasured" either — it is *working*, and the honest drawing of
 * that is a moving sliver rather than a number. Fabricating a percent from an
 * elapsed timer is the specific thing this exists to prevent: it is a figure
 * somebody acts on, and it is invented.
 *
 * `indeterminate` wins over `value`, so a caller can pass both without having
 * to null one out — `<Meter value={pct} indeterminate={pct === null && running} />`
 * is the shape every screen wants.
 *
 * Under `prefers-reduced-motion` the sliver stops moving and becomes a static
 * partial track (see the module CSS). It must not simply freeze: the global
 * reduce block in tokens.css zeroes every animation duration, which would park
 * the sliver at whatever position 0ms lands on — off the left edge — and an
 * empty track is exactly the reading this component exists to avoid.
 *
 * ── it is named, and it clamps ─────────────────────────────────────────────
 *
 * `label` is required. A progressbar with no accessible name is announced as
 * "progress bar, 64%" with nothing saying 64% of what, and every one of the
 * design's meters sits in a list of three where that is the only question.
 *
 * Out-of-range values are clamped rather than trusted. `progress_percent` is a
 * server figure computed from a track count that can move mid-download, and the
 * unclamped rendering of 1.4 is a fill painting 40% outside its own track.
 * A non-finite value is treated as `null`: `NaN` comes out of arithmetic on a
 * three-valued field often enough to be worth refusing rather than painting.
 */

import { cx } from '@/design/cx'
import type { StyleableProps, Tone } from '@/design/types'

import { clampFraction } from '@/design/Meter/fraction'
import styles from '@/design/Meter/Meter.module.css'

/** The design draws two track heights and no others: 3px (192) and 4px (556, 838). */
export type MeterThickness = 3 | 4

export interface MeterProps extends StyleableProps {
  /**
   * A fraction in 0..1, or `null` for "nothing has measured this" — which
   * draws an empty track and omits `aria-valuenow`. Never pass `0` for unknown.
   */
  value: number | null
  /** The accessible name. Required — see the note above. */
  label: string
  /** `3` is the dashboard's job bar (design 192); `4` the radar and footer (556, 838). */
  thickness?: MeterThickness
  /**
   * Colours the fill. These are MARKS rather than text, so the contrast that
   * matters is against the track — see the measurements in the module CSS.
   *
   * It defaults to `accent` rather than to `neutral`, which is the one place
   * this primitive departs from "no colour is the resting state": every meter
   * the design draws is teal (192, 556, 838), and a grey fill inside a grey
   * track is a bar you cannot read. Progress is not a status, and `--c-accent`
   * is identity rather than a verdict, so nothing is being claimed by it.
   */
  tone?: Tone
  /**
   * Running, with no percentage to report. Draws a moving sliver, keeps
   * `aria-valuenow` omitted, and overrides `value`.
   */
  indeterminate?: boolean
  /**
   * What the reader hears instead of the bare number — `"64%"`, `"9 of 12
   * tracks"`. Absent, the platform reads the percentage. Ignored when there is
   * nothing to read, because a value text on an indeterminate bar is a figure
   * with no bar behind it.
   */
  valueText?: string
}

const TONE: Readonly<Record<Tone, string | undefined>> = {
  neutral: styles.toneNeutral,
  accent: styles.toneAccent,
  ok: styles.toneOk,
  warn: styles.toneWarn,
  bad: styles.toneBad,
}

const THICKNESS: Readonly<Record<MeterThickness, string | undefined>> = {
  3: styles.thin,
  4: styles.thick,
}

export function Meter({
  value,
  label,
  thickness = 4,
  tone = 'accent',
  indeterminate = false,
  valueText,
  className,
}: MeterProps) {
  const fraction = indeterminate ? null : clampFraction(value)
  const percent = fraction === null ? null : Math.round(fraction * 1000) / 10

  return (
    <div
      className={cx(styles.track, THICKNESS[thickness], className)}
      role="progressbar"
      aria-label={label}
      aria-valuemin={0}
      aria-valuemax={100}
      // Omitted — not zeroed — when nothing has been measured. That absence is
      // what an assistive technology reads as "indeterminate"; a `0` reads as
      // "none of it is done", which is a different and usually false claim.
      aria-valuenow={percent ?? undefined}
      aria-valuetext={percent === null ? undefined : (valueText ?? `${percent}%`)}
    >
      {indeterminate ? (
        <div className={cx(styles.sliver, TONE[tone])} />
      ) : percent === null ? null : (
        <div
          className={cx(styles.fill, TONE[tone])}
          // The one inline style this primitive has, and it is a datum rather
          // than a design value: the width IS the measurement, so it cannot live
          // in a stylesheet. Everything about how it looks is in the module.
          style={{ width: `${percent}%` }}
        />
      )}
    </div>
  )
}
