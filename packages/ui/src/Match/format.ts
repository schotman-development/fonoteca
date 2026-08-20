/**
 * How the attribution numbers read on screen.
 *
 * One rule runs through all of it: **a missing measurement is not a measurement
 * of zero.** `ReleaseFit.MeanDrift` is nullable because a release MusicBrainz
 * prints no track lengths for produces no evidence at all — it passes the gate
 * untested and ranks below anything measured — and rendering that as `0.00 s`
 * would show the strongest possible reading of the weakest possible evidence.
 * So null gets a dash for the eye and words for the ear, never a number.
 */

/** An em dash. The eye's rendering of "we did not measure this". */
const NOT_MEASURED = '—'

/**
 * Coverage, 0..1, as a percentage.
 *
 * Below one percent it reads `<1%` rather than `0%`: a 76-track box set that
 * explains one file covers 1.3% of itself, and rounding a real fit down to zero
 * says "explains nothing" about something that explains something. The
 * distinction is what makes a box set legible next to the album it reprints.
 */
export function formatCoverage(value: number | null): string {
  if (value == null || !Number.isFinite(value)) return NOT_MEASURED

  const percent = value * 100
  if (percent > 0 && percent < 1) return '<1%'
  return `${Math.round(percent)}%`
}

/**
 * `held` of `total`.
 *
 * Always **tracks**, never files. Five encodings of one song are one track of
 * the album, and counting files here makes a half-ripped album read complete —
 * a mistake the catalogue already pays to avoid.
 */
export function formatRatio(held: number, total: number): string {
  return `${held.toLocaleString()} of ${total.toLocaleString()}`
}

/** Mean drift, in seconds to two places. `—` when there was nothing to measure. */
export function formatDrift(milliseconds: number | null): string {
  if (milliseconds == null || !Number.isFinite(milliseconds)) return NOT_MEASURED
  return `${(milliseconds / 1000).toFixed(2)} s`
}

/**
 * The same fact for a screen reader.
 *
 * A column of dashes is what makes drift scannable, and "not measured" written
 * forty times down it is what would destroy that — so the words live here and
 * reach the accessibility tree through `VisuallyHidden` instead.
 */
export function describeDrift(milliseconds: number | null): string {
  if (milliseconds == null || !Number.isFinite(milliseconds)) return 'not measurable'
  return `${(milliseconds / 1000).toFixed(2)} seconds`
}
