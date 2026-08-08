/**
 * Which of the Activity screen's four chips an event belongs to (design 807,
 * `logFilter` at 1366).
 *
 * ── why this is client-side, and must stay client-side ────────────────────
 *
 * `GET /api/activity` filters on `Activity.event == event` — an **exact**
 * match, not a prefix. The design's chips are `All`, `Integrity`, `Writes` and
 * `Grabs`, and each spans a dozen event names (`library.scan`,
 * `library.corrupt`, `library.refiled`, …), so no single request can produce
 * one. Four requests could, and that is the trap: it would run four identical
 * queries under four keys for a page of rows the screen already has, which is
 * the one-query-key rule broken for nothing.
 *
 * So the screen asks once, unfiltered, and the chip narrows what is already in
 * hand. The chip is URL state and is deliberately **not** part of the query
 * key. The honest cost — that a chip searches the loaded page rather than the
 * whole history — is the screen's to state, and `WIRING.md` records that a
 * server-side group filter would remove it.
 *
 * The grouping is the event's first dotted segment, and the mapping is the
 * addendum's §4 verbatim: `library.*` is every measurement and every move on
 * disk; `enrichment.*`/`artist.*`/`album.*` are the writes to metadata;
 * `download.*`/`queue.*` are the grabs. `indexer.*` — the catalogue sweep — is
 * none of the three, which is why `other` exists rather than being folded into
 * one of them: an `All` chip that hid rows would be lying about the word.
 *
 * Pure and total. An unknown or malformed event is `other`, never an
 * exception, and never silently dropped from a list.
 */

/** The four buckets behind the Activity screen's chips. */
export type EventGroup = 'integrity' | 'writes' | 'grabs' | 'other'

const GROUP_BY_SEGMENT: Readonly<Record<string, EventGroup>> = {
  library: 'integrity',
  enrichment: 'writes',
  artist: 'writes',
  album: 'writes',
  download: 'grabs',
  queue: 'grabs',
}

/** Which chip shows `event`. Anything unrecognised — including `indexer.*` — is `other`. */
export function eventGroup(event: string): EventGroup {
  const [head] = event.split('.')
  const segment = (head ?? '').trim().toLowerCase()
  return GROUP_BY_SEGMENT[segment] ?? 'other'
}
