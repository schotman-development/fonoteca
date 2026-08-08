/**
 * Pure formatters — the Jinja filters `app/api/deps.py` used to register, with
 * no React in them so they can be tested and reused from anywhere.
 *
 * The rule they share: **every one returns the em dash for an unknown value and
 * none of them throws.** `null` means "nothing has measured this", which is a
 * different claim from `0` and from `false`, and it must survive all the way to
 * the screen.
 */

export { EM_DASH } from '@/format/constants'
export {
  fmtAgo,
  fmtDate,
  fmtDateTime,
  fmtDuration,
  fmtSpan,
  fmtUtc,
  secondsSince,
} from '@/format/time'
export type { TimeInput } from '@/format/time'
export {
  fmtCount,
  fmtOfTotal,
  fmtPercent,
  fmtRatioPercent,
  fmtSize,
  fmtTracksOnDisk,
} from '@/format/number'
export { fmtQuality, fmtTrackQuality, formatLabel } from '@/format/quality'
export type { QualitySource } from '@/format/quality'
export {
  ACTIVITY_LEVEL_LABELS,
  ALBUM_STATUS_LABELS,
  ENRICHMENT_STATE_LABELS,
  INTEGRITY_STATE_LABELS,
  MONITOR_MODE_HINTS,
  MONITOR_MODE_LABELS,
  QUEUE_STATE_LABELS,
  RELEASE_TYPE_LABELS,
  TRACK_ORIGIN_LABELS,
  TRACK_STATUS_LABELS,
  enumLabel,
  humanise,
} from '@/format/labels'
