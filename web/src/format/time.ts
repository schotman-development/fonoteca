/**
 * Time formatters, ported from the Jinja filters in `app/api/deps.py`.
 *
 * Two domain rules are encoded here:
 *
 * 1. **Timestamps arrive as aware UTC ISO-8601.** `deps.as_utc` re-stamps
 *    SQLite's naive datetimes before serialisation, so `new Date(iso)` is
 *    correct and the browser's offset is applied once. Display is **local**
 *    and unlabelled; the exact UTC string goes in a `title=` attribute
 *    (`fmtUtc`), which is what the old UI did.
 * 2. **Negative elapsed renders "just now", never "in -3s".** The server clock
 *    and the browser clock disagree by a second or two routinely, and a
 *    relative time that runs backwards reads as a bug in the data rather than
 *    as skew. `fmtAgo(null)` is the word **"never"** — not an em dash, because
 *    "this artist has never been checked" is a sentence, not a missing value.
 *
 * Everything here is pure and takes `now` as an argument so it can be tested
 * without freezing the clock. Nothing throws: an unparseable string is treated
 * exactly like `null`.
 */

import { EM_DASH } from '@/format/constants'

/** Anything the API can hand us for a timestamp. */
export type TimeInput = string | number | Date | null | undefined

function toDate(value: TimeInput): Date | null {
  if (value === null || value === undefined || value === '') return null
  const date = value instanceof Date ? value : new Date(value)
  return Number.isNaN(date.getTime()) ? null : date
}

const pad = (n: number): string => String(n).padStart(2, '0')

/**
 * `YYYY-MM-DD HH:mm` in the **viewer's** timezone. Built by hand rather than
 * through `toLocaleString` so the shape is identical on every machine — the
 * value is read alongside sortable data, and a US-format date in one column and
 * an ISO one in the next is worse than either.
 */
export function fmtDateTime(value: TimeInput): string {
  const date = toDate(value)
  if (!date) return EM_DASH
  return (
    `${date.getFullYear()}-${pad(date.getMonth() + 1)}-${pad(date.getDate())}` +
    ` ${pad(date.getHours())}:${pad(date.getMinutes())}`
  )
}

/**
 * The exact instant, in UTC, for a `title=` attribute. Explicitly labelled:
 * this is the one place a timezone is stated, which is what makes the
 * unlabelled local rendering elsewhere safe to read.
 */
export function fmtUtc(value: TimeInput): string {
  const date = toDate(value)
  if (!date) return EM_DASH
  return (
    `${date.getUTCFullYear()}-${pad(date.getUTCMonth() + 1)}-${pad(date.getUTCDate())}` +
    ` ${pad(date.getUTCHours())}:${pad(date.getUTCMinutes())} UTC`
  )
}

/**
 * `YYYY-MM-DD`. A bare `release_date` arrives as a date with no time and no
 * zone, so it is echoed as-is rather than pushed through `Date` — parsing
 * `"1977-05-13"` and formatting it locally can move it a day west of Greenwich.
 */
export function fmtDate(value: TimeInput): string {
  if (value === null || value === undefined || value === '') return EM_DASH
  if (typeof value === 'string' && /^\d{4}-\d{2}-\d{2}$/.test(value)) return value
  const date = toDate(value)
  if (!date) return EM_DASH
  return `${date.getFullYear()}-${pad(date.getMonth() + 1)}-${pad(date.getDate())}`
}

/**
 * A duration in seconds as `45s` / `5m 03s` / `2h 05m` / `3d 04h`.
 *
 * Two significant units, never three: this is read at a glance in a table cell,
 * and the second unit is zero-padded so a column of them stays aligned.
 */
export function fmtSpan(seconds: number | null | undefined): string {
  if (seconds === null || seconds === undefined || Number.isNaN(seconds)) {
    return EM_DASH
  }
  const total = Math.floor(Math.max(0, seconds))
  if (total < 60) return `${total}s`
  if (total < 3600) return `${Math.floor(total / 60)}m ${pad(total % 60)}s`
  if (total < 86400) {
    return `${Math.floor(total / 3600)}h ${pad(Math.floor((total % 3600) / 60))}m`
  }
  return `${Math.floor(total / 86400)}d ${pad(Math.floor((total % 86400) / 3600))}h`
}

/**
 * `"3m 12s ago"`, `"just now"` for a future stamp, `"never"` for `null`.
 *
 * `now` is a parameter so `<RelativeTime>` can re-render on its 30s tick and so
 * tests do not have to freeze the clock.
 */
export function fmtAgo(value: TimeInput, now: number = Date.now()): string {
  const date = toDate(value)
  if (!date) return 'never'
  const elapsed = (now - date.getTime()) / 1000
  if (elapsed < 0) return 'just now'
  return `${fmtSpan(elapsed)} ago`
}

/**
 * Seconds elapsed since *value*, or `null` when it is unset — the numeric half
 * of `fmtAgo`, for anything that needs to compare rather than render.
 */
export function secondsSince(value: TimeInput, now: number = Date.now()): number | null {
  const date = toDate(value)
  if (!date) return null
  return (now - date.getTime()) / 1000
}

/**
 * A track or album length as `m:ss` or `h:mm:ss`.
 *
 * Zero is an em dash, deliberately: the API sends `0` for a duration it does
 * not know, and `0:00` would claim the track is silent.
 */
export function fmtDuration(seconds: number | null | undefined): string {
  if (!seconds || Number.isNaN(seconds) || seconds < 0) return EM_DASH
  const total = Math.floor(seconds)
  const hours = Math.floor(total / 3600)
  const minutes = Math.floor((total % 3600) / 60)
  const secs = total % 60
  if (hours) return `${hours}:${pad(minutes)}:${pad(secs)}`
  return `${minutes}:${pad(secs)}`
}
