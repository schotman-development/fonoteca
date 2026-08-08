/**
 * One line of the Activity feed (design 813–823).
 *
 * A four-column grid — time, kind, message, level — over the whole history,
 * newest first. Three of the four columns needed a decision that the design
 * could not make for them.
 *
 * ── the kind is derived, not stored ───────────────────────────────────────
 *
 * The design writes a kind per fixture row (`TAMPER`, `GRAB`, `SCAN`, …). The
 * API has `ActivityOut.event`, a dotted string, and `activityKind()` is the one
 * place that turns one into a word and a colour. It is a pure module rather
 * than a branch in here because the Activity screen's chips group over the same
 * segments (`eventGroup()`), and two files disagreeing about what
 * `library.refiled` is would be a row that filters into one chip and paints
 * itself as another.
 *
 * ── the detail line is omitted, not emptied ───────────────────────────────
 *
 * The design's second line always exists. Here it is built from `artist_name`
 * and `album_title`, both nullable — a `library.scan` row names neither — and
 * when both are absent the element is not rendered. An empty line reserves
 * space in a fixed grid and reads as a row that failed to load half of itself.
 *
 * ── the fourth column is the level, because there is no result ────────────
 *
 * The design's last column is `l.result` — "quarantined", "412 files",
 * "96 written" (DCLogic 1010–1020). No field corresponds to it: `ActivityOut`
 * is `{ id, level, event, message, artist_id, album_id, created_at,
 * artist_name, album_title }` and nothing summarises an outcome. Inventing one
 * would be inventing a *number*, which is the one thing this rebuild may never
 * do, so the column carries the `level` instead — as a `Badge`, the design
 * system's quiet pill, and **nothing at all for `info`**, which is every
 * ordinary row and would otherwise put a chip on all nine of them.
 *
 * ── the time is `HH:MM:SS`, formatted by the platform ─────────────────────
 *
 * `@/format` has no seconds-precision formatter and must not grow one for a
 * single column, so this uses `Intl.DateTimeFormat` with an explicit `h23`
 * cycle — a locale-independent 24-hour clock, which is what the design draws
 * (`14:02:19`) and what stays sortable by eye. The exact instant, in labelled
 * UTC, goes in the `title` the way it does everywhere else in this app.
 */

import type { ActivityOut } from '@/api/types'
import { Badge, cx } from '@/design'
import { ACTIVITY_LEVEL_LABELS, EM_DASH, fmtUtc } from '@/format'
import type { ActivityTone } from '@/widgets/activityKind'
import { activityKind } from '@/widgets/activityKind'

import styles from '@/widgets/ActivityRow/ActivityRow.module.css'

export interface ActivityRowProps {
  entry: ActivityOut
}

/**
 * Built once at module scope: constructing a `DateTimeFormat` is the expensive
 * part, and a hundred-row feed would otherwise build a hundred of them.
 * `hourCycle: 'h23'` rather than `hour12: false`, which some engines still
 * render as `24:00` at midnight.
 */
const CLOCK = new Intl.DateTimeFormat(undefined, {
  hour: '2-digit',
  minute: '2-digit',
  second: '2-digit',
  hourCycle: 'h23',
})

function clockOf(value: string | null): string {
  if (value === null || value === '') return EM_DASH
  const date = new Date(value)
  return Number.isNaN(date.getTime()) ? EM_DASH : CLOCK.format(date)
}

const KIND_CLASS: Readonly<Record<ActivityTone, string | undefined>> = {
  neutral: styles.kindNeutral,
  accent: styles.kindAccent,
  ok: styles.kindOk,
  warn: styles.kindWarn,
  bad: styles.kindBad,
  blue: styles.kindBlue,
  violet: styles.kindViolet,
  amber: styles.kindAmber,
}

/** `info` is every ordinary row, so it says nothing at all. */
const LEVEL_TONE = {
  debug: 'neutral',
  info: null,
  warning: 'warn',
  error: 'bad',
} as const

export function ActivityRow({ entry }: ActivityRowProps) {
  const kind = activityKind(entry.event, entry.level)
  const detail = [entry.artist_name, entry.album_title]
    .filter((part): part is string => part !== null && part !== '')
    .join(' · ')
  const tone = LEVEL_TONE[entry.level]

  return (
    <div className={styles.row}>
      <div
        className={cx(styles.time, 'mono')}
        title={entry.created_at === null ? undefined : fmtUtc(entry.created_at)}
      >
        {clockOf(entry.created_at)}
      </div>

      <div>
        <span
          className={cx(styles.kind, 'mono', KIND_CLASS[kind.tone])}
          // The event itself, for anyone who needs to know which one this is
          // — the label is only its family.
          title={entry.event}
        >
          {kind.label}
        </span>
      </div>

      <div className={styles.body}>
        <div className={styles.message}>{entry.message}</div>
        {detail === '' ? null : (
          <div className={cx(styles.detail, 'mono')}>{detail}</div>
        )}
      </div>

      <div className={styles.result}>
        {tone === null ? null : (
          <Badge tone={tone}>{ACTIVITY_LEVEL_LABELS[entry.level]}</Badge>
        )}
      </div>
    </div>
  )
}
