/**
 * What kind of thing happened, and what colour that is — the activity feed's
 * second column (design 816, `KIND_STYLE` at 1022).
 *
 * The design hand-writes eight kinds (`TAMPER`, `MOVE`, `REPAIR`, `IDENT`,
 * `GRAB`, `ENRICH`, `CLEAN`, `SCAN`) as a literal on each fixture row. The API
 * has no such field: `ActivityOut.event` is a dotted string — `queue.held`,
 * `download.complete`, `library.integrity` — and the kind is its **first
 * segment**, uppercased. That is a real vocabulary rather than an invented one
 * (the full list is in the addendum's §4: `library`, `download`, `queue`,
 * `enrichment`, `artist`, `album`, `indexer`), and reading it off the event
 * means an event the backend adds tomorrow gets a sensible label today.
 *
 * ── level beats segment, and only two levels say anything ─────────────────
 *
 * `ActivityOut.level` is separate from the event and is the only field that
 * claims something went wrong. So `error` is red and `warning` is amber
 * **whatever** the event was — a `library.scan` that failed must not be painted
 * the same amber-as-category as one that succeeded, and a `download.complete`
 * at `error` level is not blue. Everything else takes the categorical colour of
 * its segment, which is a way of telling groups apart and not a verdict.
 *
 * ── the tone union is wider than `Tone`, deliberately ─────────────────────
 *
 * `Tone` is the design system's *status* vocabulary — neutral, accent, ok,
 * warn, bad — and it has no word for "blue" or "violet" because in that system
 * colour means how bad something is. This column is the one place the design
 * uses colour **categorically** (`GRAB` blue, `ENRICH` violet, `SCAN` amber,
 * DCLogic 1022–1026), and tokens.css names those three exactly:
 * `--c-cat-blue`, `--c-cat-violet`, `--c-cat-amber`. Collapsing them into
 * `Tone` would either lose two of the three colours or paint every library scan
 * as a warning, so the return type is `ActivityTone` — a strict superset of
 * `Tone`, so nothing that already handles a `Tone` is invalidated, and
 * `ActivityRow` is the only thing that has to know the extra three.
 *
 * `--c-cat-amber` and `--c-warn` happen to be the same hex. They are still two
 * names, because "this was a scan" and "this needs attention" are two claims,
 * and only one of them survives a re-theme.
 *
 * Pure and total: it never throws, and an event this file has never heard of
 * gets its own name in neutral ink rather than a guess at what it might mean.
 */

import type { ActivityLevel } from '@/api/types'
import type { Tone } from '@/design'
import { EM_DASH } from '@/format'

/**
 * The design's status tones plus the three categorical hues the activity
 * column uses. A superset of `Tone`: every `Tone` is an `ActivityTone`.
 */
export type ActivityTone = Tone | 'blue' | 'violet' | 'amber'

/** One rendered kind cell: the word, and the ink it is set in. */
export interface ActivityKind {
  /** The event's first segment, uppercased — `QUEUE`, `LIBRARY`, `DOWNLOAD`. */
  label: string
  tone: ActivityTone
}

/**
 * The categorical colour per event family. Grabs are blue, enrichment violet,
 * library work amber; the three that are neither traffic nor measurement —
 * `artist`, `album`, `indexer` — are bookkeeping and get no colour at all.
 */
const SEGMENT_TONE: Readonly<Record<string, ActivityTone>> = {
  download: 'blue',
  queue: 'blue',
  enrichment: 'violet',
  library: 'amber',
  artist: 'neutral',
  album: 'neutral',
  indexer: 'neutral',
}

/** The part of `library.integrity` that names the family. */
function segmentOf(event: string): string {
  const [head] = event.split('.')
  return (head ?? '').trim().toLowerCase()
}

/**
 * The kind cell for one activity row.
 *
 * @param event a dotted event name, e.g. `download.completed`
 * @param level the row's own severity, which overrides the categorical colour
 */
export function activityKind(event: string, level: ActivityLevel): ActivityKind {
  const segment = segmentOf(event)
  // An event with no name at all renders the em dash rather than an empty
  // cell: a blank column reads as a rendering fault, the dash reads as "this
  // row did not say".
  const label = segment === '' ? EM_DASH : segment.toUpperCase()

  if (level === 'error') return { label, tone: 'bad' }
  if (level === 'warning') return { label, tone: 'warn' }

  return { label, tone: SEGMENT_TONE[segment] ?? 'neutral' }
}
