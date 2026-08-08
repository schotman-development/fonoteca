/**
 * Quality labels — and **only** labels.
 *
 * Domain rule (§6.7): `app/core/quality.py` is the single place that decides
 * whether a better copy is obtainable, because the Upgrade button and the
 * download worker must not be able to disagree. Nothing here compares two
 * formats, ranks them, or infers an upgrade. `AlbumOut.upgrade_format_id` is
 * the server's answer; `null` on a downloaded release means **render nothing** —
 * not a disabled button, not "Download".
 *
 * `formatLabel` reads `MetaOut.format_labels`, which is fetched once and cached
 * forever, so a format id the server adds shows up without a client release.
 * It answers `''` rather than an em dash: it is used inline inside a sentence
 * ("→ FLAC 24bit 96kHz"), where a dash would read as a missing word.
 */

import { EM_DASH } from '@/format/constants'

/** The three fields a quality badge is drawn from. */
export interface QualitySource {
  max_bit_depth?: number | null
  max_sampling_rate?: number | null
  hires?: boolean
}

/** `96` not `96.0`, `44.1` not `44.10` — Python's `:g`, which the old UI used. */
function trimFloat(value: number): string {
  return String(Number(value.toFixed(4)))
}

/**
 * `24bit/96kHz`, falling back to `Hi-Res` when only the flag is known and to an
 * em dash when nothing is. Three rungs, and the middle one matters: Qobuz
 * reports `hires` for releases whose depth and rate it does not publish, and
 * inventing `24bit/96kHz` for those would put a figure on disk in a folder name
 * that nothing measured.
 */
export function fmtQuality(album: QualitySource | null | undefined): string {
  if (!album) return EM_DASH
  const depth = album.max_bit_depth
  const rate = album.max_sampling_rate
  if (depth && rate) return `${Math.round(depth)}bit/${trimFloat(rate)}kHz`
  if (album.hires) return 'Hi-Res'
  return EM_DASH
}

/**
 * The human label for a Qobuz `format_id`, from `GET /api/meta`.
 *
 * `''` for an unknown or absent id, never a dash — see the module note. The map
 * is keyed by *stringified* ints because JSON object keys are strings.
 */
export function formatLabel(
  formatId: number | null | undefined,
  labels: Record<string, string> | null | undefined,
): string {
  if (formatId === null || formatId === undefined) return ''
  return labels?.[String(formatId)] ?? ''
}

/**
 * The label a track's own file carries: `24bit/96kHz` from what was actually
 * downloaded, not from what the catalogue offers.
 *
 * Separate from `fmtQuality` because the two answer different questions and
 * conflating them is how a folder of 16/44.1 files ends up named `[FLAC 24-96]`
 * — `plan_refile()` refuses to render a tag it cannot measure for exactly this
 * reason.
 */
export function fmtTrackQuality(track: {
  bit_depth?: number | null
  sampling_rate?: number | null
}): string {
  const { bit_depth: depth, sampling_rate: rate } = track
  if (depth && rate) return `${Math.round(depth)}bit/${trimFloat(rate)}kHz`
  return EM_DASH
}
