/**
 * The one rule about a meter's number, on its own so it can be read without
 * reading a render function — and so `Meter.tsx` exports a component and
 * nothing else (oxlint's `react/only-export-components`, the same split
 * `Artwork/geometry.ts` makes).
 *
 * Two refusals, and both are the three-valued rule in arithmetic form:
 *
 *   - **out of range is clamped, not trusted.** `progress_percent` is a server
 *     figure computed from a track count that can move mid-download, and the
 *     unclamped rendering of `1.4` is a fill painting 40% outside its own
 *     track — over whatever sits beside it.
 *   - **not a number is `null`, not zero.** `NaN` and `Infinity` are what
 *     arithmetic on a three-valued field produces (`done / total` where
 *     `total` is `0`, or `null` coerced), and the honest reading of "0 of 0"
 *     is *nothing has counted this*, which is exactly what `null` means here.
 *     Painting it as `0` claims a measurement that was never taken.
 */

/** A fraction in 0..1, or `null` for anything that is not one. */
export function clampFraction(value: number | null): number | null {
  if (value === null || !Number.isFinite(value)) return null
  if (value <= 0) return 0
  if (value >= 1) return 1
  return value
}
