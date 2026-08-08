/**
 * What the Release radar says about one release — one rule, in one place.
 *
 * The radar's right-hand column answers two questions at a glance: *has this
 * come out yet*, and *do we have it*. Both are derived here rather than as a
 * ladder of ternaries inside the row, because the second one has a trap in it
 * that is worth stating once and pinning with tests.
 *
 * ── `queue_state` first, `status` second ──────────────────────────────────
 *
 * This is the repository's own invariant (`AlbumOut.queue_state` is not
 * `AlbumOut.status`), and the radar is exactly the surface it bites on.
 * `queue_album()` only promotes `skipped`/`failed`/`wanted` to `queued`, so a
 * release being **upgraded** keeps `status: 'downloaded'` for the whole
 * download. Reading `status` alone would draw a row that says *In library*
 * with a download running underneath it.
 *
 * ── "released" is a date, not a status ────────────────────────────────────
 *
 * `release_date` can be in the future — Qobuz dates announced records ahead,
 * and the radar's heading promises *upcoming* — so a future date is its own
 * timing verdict rather than something to hide. `fmtAgo` cannot be used for it:
 * it answers `"just now"` for anything ahead of the clock, which about a record
 * due in three weeks is simply false.
 *
 * The boundary is the calendar **day**, not the instant. A release date carries
 * no time of day, so `new Date('2026-08-05')` is midnight UTC and a record out
 * today reads as several hours in the past by the afternoon — "out today" is
 * the honest sentence, and comparing whole days is what produces it.
 */

import type { AlbumOut } from '@/api/types'

/** The ink a state word is set in. Mirrors the row's four `state*` classes. */
export type ReleaseTone = 'ok' | 'bad' | 'resting' | 'quiet'

export interface ReleaseState {
  /** The word for the right-hand column. */
  label: string
  tone: ReleaseTone
}

/**
 * Where a release sits relative to today, in whole calendar days.
 *
 * `null` for a release with no date — which the radar's own endpoint filters
 * out, so it is unreachable from that screen and answered anyway: this is a
 * pure function over an `AlbumOut`, and every other caller of one may pass a
 * dateless release.
 */
export interface ReleaseTiming {
  /** `future` · `today` · `past`. */
  when: 'future' | 'today' | 'past'
  /** Whole days ahead (`future`) or behind (`past`). `0` for `today`. */
  days: number
}

/** Midnight-local for a `YYYY-MM-DD`, so two dates compare as calendar days. */
function startOfDay(value: Date): number {
  return new Date(value.getFullYear(), value.getMonth(), value.getDate()).getTime()
}

const DAY_MS = 86_400_000

/**
 * How far off a release date is, in calendar days, or `null` when there is none.
 *
 * *now* is a parameter so a test does not have to freeze the clock — the same
 * convention `fmtAgo` uses.
 */
export function releaseTiming(
  releaseDate: string | null | undefined,
  now: number = Date.now(),
): ReleaseTiming | null {
  if (!releaseDate) return null
  // `YYYY-MM-DD` parsed by `new Date` is UTC midnight, which is the previous
  // day west of Greenwich. Split it instead, so the date the server sent is the
  // date that gets compared.
  const parts = releaseDate.slice(0, 10).split('-').map(Number)
  const [year, month, day] = parts
  if (
    parts.length !== 3 ||
    year === undefined ||
    month === undefined ||
    day === undefined ||
    !Number.isFinite(year) ||
    !Number.isFinite(month) ||
    !Number.isFinite(day)
  ) {
    return null
  }
  const released = new Date(year, month - 1, day)
  if (Number.isNaN(released.getTime())) return null

  const delta = Math.round((startOfDay(released) - startOfDay(new Date(now))) / DAY_MS)
  if (delta > 0) return { when: 'future', days: delta }
  if (delta < 0) return { when: 'past', days: -delta }
  return { when: 'today', days: 0 }
}

/**
 * The one sentence a radar row puts under its state word, or `null`.
 *
 * Only the two ends of the scale say anything. A record due in eleven days and
 * a record that came out yesterday are both news; one from 1973 is not, and a
 * note reading "19 years ago" on every row is a column of noise that makes the
 * two rows that matter harder to find, not easier.
 */
export function releaseNote(
  releaseDate: string | null | undefined,
  now: number = Date.now(),
): string | null {
  const timing = releaseTiming(releaseDate, now)
  if (!timing) return null
  if (timing.when === 'today') return 'out today'
  if (timing.when === 'future') {
    return timing.days === 1 ? 'out tomorrow' : `out in ${timing.days} days`
  }
  if (timing.days === 1) return 'out yesterday'
  return timing.days <= 30 ? `out ${timing.days} days ago` : null
}

/**
 * What Qobuzarr has done about a release, as a word and an ink.
 *
 * Total over both vocabularies: every `QueueState` and every `AlbumStatus` has
 * a branch, so a value added to either side surfaces as a type error here
 * rather than as a blank cell on the screen.
 */
export function releaseState(album: AlbumOut): ReleaseState {
  // In flight beats everything, and it is `queue_state` that knows — see the
  // note at the top of this file about an upgrade of an album that is already
  // `downloaded`.
  if (album.queue_state === 'active') return { label: 'Downloading', tone: 'ok' }
  if (album.queue_state === 'pending') return { label: 'Queued', tone: 'resting' }

  switch (album.status) {
    case 'downloaded':
      return { label: 'In library', tone: 'ok' }
    case 'downloading':
      return { label: 'Downloading', tone: 'ok' }
    case 'queued':
      return { label: 'Queued', tone: 'resting' }
    case 'failed':
      return { label: 'Failed', tone: 'bad' }
    case 'skipped':
      return { label: 'Ignored', tone: 'quiet' }
    case 'wanted':
      return { label: 'Wanted', tone: 'resting' }
  }
}
