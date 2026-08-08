/**
 * Label maps for the closed vocabularies, plus the humaniser everything else
 * falls back to.
 *
 * Two rules:
 *
 * 1. **`GET /api/meta` is the vocabulary; this file is only the wording.** The
 *    server publishes every list (`album_statuses`, `monitor_modes`,
 *    `enrichment_states`, …) precisely so a `<select>` is never a hand-typed
 *    copy that goes one option short when somebody adds a status. Iterate
 *    `MetaOut`, then pass each value through `enumLabel` for its text.
 * 2. **`enumLabel` never throws and never returns empty.** A value this file has
 *    not heard of is humanised (`no_key` → `No key`) rather than dropped —
 *    a chip with no text is indistinguishable from a rendering bug.
 *
 * Status chips deliberately render the *raw* lowercase value (`wanted`,
 * `downloading`) the way the old UI did; these maps are for prose, `<select>`
 * options and tooltips, where a sentence needs a word rather than a token.
 */

import type {
  ActivityLevel,
  AlbumStatus,
  EnrichmentStateValue,
  IntegrityState,
  MonitorMode,
  QueueState,
  ReleaseType,
  TrackOrigin,
  TrackStatus,
} from '@/api/types'

/** Turn `not_found` into `Not found`. The last-resort branch of `enumLabel`. */
export function humanise(value: string): string {
  const text = value.replace(/[_-]+/g, ' ').trim()
  if (!text) return ''
  return text.charAt(0).toUpperCase() + text.slice(1)
}

export const ALBUM_STATUS_LABELS: Record<AlbumStatus, string> = {
  skipped: 'Ignored',
  wanted: 'Wanted',
  queued: 'Queued',
  downloading: 'Downloading',
  downloaded: 'Downloaded',
  failed: 'Failed',
}

/**
 * `skipped` reads as **Ignored** because that is what the button does — the
 * monitor toggle collapses a `wanted` release to `skipped` on the way off, and
 * "skipped" would suggest the download loop passed it over.
 */
export const MONITOR_MODE_LABELS: Record<MonitorMode, string> = {
  all: 'All releases',
  future: 'Future releases only',
  none: 'Nothing',
}

/** The one-line hint under the monitor-mode select. */
export const MONITOR_MODE_HINTS: Record<MonitorMode, string> = {
  all: 'Everything in the back catalogue is marked wanted.',
  future: 'Only releases discovered after this artist was followed.',
  none: 'The artist is tracked, but nothing is ever marked wanted.',
}

export const QUEUE_STATE_LABELS: Record<QueueState, string> = {
  pending: 'Pending',
  active: 'Downloading',
  done: 'Done',
  failed: 'Failed',
  cancelled: 'Cancelled',
}

export const ACTIVITY_LEVEL_LABELS: Record<ActivityLevel, string> = {
  debug: 'Debug',
  info: 'Info',
  warning: 'Warning',
  error: 'Error',
}

export const TRACK_STATUS_LABELS: Record<TrackStatus, string> = {
  pending: 'Pending',
  downloading: 'Downloading',
  downloaded: 'Downloaded',
  failed: 'Failed',
  skipped: 'Skipped',
}

/** Provenance is *stated*, never inferred from a track row's presence. */
export const TRACK_ORIGIN_LABELS: Record<TrackOrigin, string> = {
  download: 'Downloaded by Qobuzarr',
  scan: 'Found on disk',
}

export const RELEASE_TYPE_LABELS: Record<ReleaseType, string> = {
  album: 'Album',
  ep: 'EP',
  single: 'Single',
  live: 'Live',
  compilation: 'Compilation',
  download: 'Download',
  other: 'Other',
}

export const ENRICHMENT_STATE_LABELS: Record<EnrichmentStateValue, string> = {
  pending: 'Waiting',
  ok: 'Identified',
  not_found: 'Not found',
  ambiguous: 'Ambiguous',
  no_key: 'Nothing to match on',
  gated: 'Gated',
  failed: 'Failed',
  rejected: 'Dismissed',
}

/**
 * `unknown` is **never baselined**, not tampered with. Getting this wording
 * wrong is what would make the integrity feature cry wolf on the day it ships,
 * since every file in an unmeasured library is `unknown`.
 */
export const INTEGRITY_STATE_LABELS: Record<IntegrityState, string> = {
  unknown: 'Not measured',
  verified: 'Verified',
  retagged: 'Retagged',
  replaced: 'Replaced',
  missing: 'Missing',
}

const ALL_LABELS: Record<string, string> = {
  ...ALBUM_STATUS_LABELS,
  ...QUEUE_STATE_LABELS,
  ...ACTIVITY_LEVEL_LABELS,
  ...RELEASE_TYPE_LABELS,
  ...ENRICHMENT_STATE_LABELS,
}

/**
 * The display text for one enum value.
 *
 * `map` wins when supplied — several vocabularies share words (`failed` is a
 * status, a queue state and an enrichment state; `downloading` is a status and
 * a queue state), so a caller that knows which vocabulary it is in should say
 * so. Without one, a merged table is consulted and then `humanise` catches
 * everything else. Returns the em-dash-free empty string for `null`, because
 * this is used inside sentences and inside `<option>` text.
 */
export function enumLabel(
  value: string | null | undefined,
  map?: Record<string, string>,
): string {
  if (!value) return ''
  return map?.[value] ?? ALL_LABELS[value] ?? humanise(value)
}
