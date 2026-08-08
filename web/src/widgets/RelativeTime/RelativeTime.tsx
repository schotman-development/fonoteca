/**
 * A timestamp, said the way a person reads one — `3m 12s ago` — with the exact
 * instant one hover away.
 *
 * The design writes relative times as flat strings (`2 h ago`, `yesterday`,
 * DCLogic 1211) and never says when that was. Two things are added here and
 * both are the same idea: a `title` carrying `fmtUtc`, so "when exactly?" is
 * answerable without leaving the row, and a real `<time dateTime>` element, so
 * the machine-readable instant is in the markup rather than only in the prose.
 * That is what makes the shortened form safe to show at all.
 *
 * ── it does not tick, and that is a rule rather than an omission ───────────
 *
 * A self-updating clock is a `setInterval`, and this application has exactly
 * one scheduler: `refetchInterval` on the query, from the `REFETCH` table in
 * `@/api/queries`. Every list that shows an age is polled (rows every 15s, the
 * queue every 5s), so the re-render arrives with fresh *data* rather than with
 * a fresher subtraction against stale data — and a screen that is idle issues
 * no requests and runs no timers, which a per-row interval would silently undo
 * forty times over on a long page.
 *
 * ── `null` is the em dash, not "never" ────────────────────────────────────
 *
 * `fmtAgo(null)` answers the word **"never"**, which is right where the field
 * is a *history* ("this artist has never been checked") and wrong where it is a
 * *value* that has not arrived yet — a queue item that has not finished has not
 * "never finished". This component is the second kind, so it branches before
 * `fmtAgo` and renders `EM_DASH`. A caller that wants the sentence renders
 * `fmtAgo` itself; that is one line and it means the two claims stay
 * distinguishable.
 */

import { EM_DASH, fmtAgo, fmtUtc } from '@/format'

import styles from '@/widgets/RelativeTime/RelativeTime.module.css'

export interface RelativeTimeProps {
  /** An ISO-8601 UTC instant, or `null` when nothing has stamped this yet. */
  value: string | null
}

export function RelativeTime({ value }: RelativeTimeProps) {
  // `''` is treated as absent for the same reason `@/format` does: an empty
  // string from a serialiser is not a moment in time.
  if (value === null || value === '') {
    return <span className={styles.time}>{EM_DASH}</span>
  }

  return (
    <time className={styles.time} dateTime={value} title={fmtUtc(value)}>
      {fmtAgo(value)}
    </time>
  )
}
