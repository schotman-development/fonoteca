/**
 * Size, percentage and count formatters.
 *
 * The domain rule they all serve: **`null` is not zero.** `on_disk_ratio` is
 * `null` when an artist has no releases at all (a ratio of nothing is not 0%),
 * `tracks_on_disk` is `null` when nothing has counted a release, and rendering
 * either as `0` reports a healthy library as an empty one. Every function here
 * answers with an em dash instead, and none of them throws.
 */

import { EM_DASH } from '@/format/constants'

const BINARY_UNITS = ['B', 'KiB', 'MiB', 'GiB', 'TiB'] as const

/**
 * A byte count in binary units with one decimal (`1.4 GiB`).
 *
 * Zero is an em dash rather than `0.0 B`, matching `deps.fmt_size`: the API
 * sends `0` for a size it has not measured, and a library that has not been
 * measured is not a library of empty files.
 */
export function fmtSize(bytes: number | null | undefined): string {
  if (!bytes || Number.isNaN(bytes) || bytes < 0) return EM_DASH
  const index = Math.min(
    Math.floor(Math.log(bytes) / Math.log(1024)),
    BINARY_UNITS.length - 1,
  )
  const unit = BINARY_UNITS[index] ?? 'B'
  return `${(bytes / 1024 ** index).toFixed(1)} ${unit}`
}

/**
 * A fraction (`0`–`1`) as a whole-number percentage.
 *
 * Takes the fraction the API already computed (`on_disk_ratio`,
 * `library_scan_complete_ratio`) rather than a pair, so nothing re-derives a
 * denominator the server has already decided on.
 */
export function fmtPercent(fraction: number | null | undefined, digits = 0): string {
  if (fraction === null || fraction === undefined || Number.isNaN(fraction)) {
    return EM_DASH
  }
  return `${(fraction * 100).toFixed(digits)}%`
}

/**
 * `part / total` as a percentage, or an em dash when the total is zero or
 * unknown. Division by nothing is not 0% and not 100%.
 */
export function fmtRatioPercent(
  part: number | null | undefined,
  total: number | null | undefined,
  digits = 0,
): string {
  if (part === null || part === undefined) return EM_DASH
  if (!total || Number.isNaN(total)) return EM_DASH
  return fmtPercent(part / total, digits)
}

/**
 * `"9 of 12"` — the explicit fraction the release screen prints.
 *
 * Three-valued on purpose (§6.4): `part === null` means *nothing has counted
 * this release*, which is an em dash and **not** `0 of 12`. A part below the
 * total is reported honestly, including when that is bad news — nine files
 * where the catalogue says twelve is a real gap.
 */
export function fmtOfTotal(
  part: number | null | undefined,
  total: number | null | undefined,
): string {
  if (part === null || part === undefined) return EM_DASH
  if (total === null || total === undefined) return String(part)
  return `${part} of ${total}`
}

/**
 * `"3 tracks"` / `"1 track"`. Pluralisation lives here so no component writes
 * `${n} track${n === 1 ? '' : 's'}` a fourth time.
 */
export function fmtCount(
  value: number | null | undefined,
  singular: string,
  plural = `${singular}s`,
): string {
  if (value === null || value === undefined || Number.isNaN(value)) return EM_DASH
  return `${value} ${value === 1 ? singular : plural}`
}

/**
 * The accessible label for a completion meter: `"9 of 12 tracks on disk"`.
 * Required by `Meter`'s `role="img"` + `aria-label`, and it must state both
 * numbers — a bare percentage on a screen reader is not actionable.
 */
export function fmtTracksOnDisk(
  onDisk: number | null | undefined,
  total: number | null | undefined,
): string {
  if (onDisk === null || onDisk === undefined) {
    return 'No per-track record — nothing has counted this release per track yet'
  }
  if (!total) return `${onDisk} track${onDisk === 1 ? '' : 's'} on disk`
  return `${onDisk} of ${total} tracks on disk`
}
