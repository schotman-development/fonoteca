/**
 * The bulk edit's wire body, built from what the form is holding.
 *
 * This is the tri-state half of the pair `web/src/screens/library/artistPatch.ts`
 * completes, and the two are **opposites** on purpose — do not build one on the
 * other:
 *
 *  - The single-artist form writes one artist a person is looking at. An
 *    unchecked box there genuinely means `false`, and an empty release-type
 *    list is a real value.
 *  - This one writes a selection. A control nobody touched must produce an
 *    **omitted key**, never `false` — `ArtistBulkUpdateIn`'s `None` means
 *    *leave alone*, so one serialiser coercing an untouched switch to `false`
 *    silently unmonitors every artist in the selection. That is not a
 *    hypothetical: it is the bug the old HTML layer shipped, from a helper that
 *    turned `""` into `False`.
 *
 * So `KEEP` is a real state a draft can hold, and it is the one state that
 * reaches the wire as nothing at all. The interface leans on that twice over:
 * `BulkBar` applies one control per press, which builds a draft that is `KEEP`
 * in every field but one, and `BulkTypesDrawer` opens on `KEEP` and stays there
 * until a verb is chosen. Either way the body carries only what somebody
 * actually asked for — a property this function guarantees rather than merely
 * one the callers happen to have.
 *
 * Three further rules, each of which is a refusal rather than a guess:
 *
 *  - **An unrecognised `monitor_mode` is dropped, not raised.** The value comes
 *    from `MetaOut.monitor_modes` when that has loaded and a local list before
 *    it, so a vocabulary the server has since changed must degrade into "that
 *    control did nothing", never into a 422 that loses the two settings beside
 *    it.
 *  - **Ticked release types do nothing without a verb.** `set`, `add` and
 *    `remove` mean three different things to the same list, and applying the
 *    server's default (`set`) to a list somebody ticked while meaning `add`
 *    replaces the accepted types of the whole selection.
 *  - **A verb with an empty list is dropped too.** `add`/`remove` of nothing is
 *    a no-op, and `set` of nothing empties the accepted types of every selected
 *    artist — legal, silent, and almost never meant. Those artists would simply
 *    stop marking anything wanted. Emptying one artist stays a single-artist
 *    decision.
 *
 * `null` is the return for *nothing would change*, so a caller can disable the
 * button rather than posting a request whose honest answer is "nothing to
 * change".
 */

import type { ArtistBulkUpdateIn, MonitorMode, ReleaseTypesAction } from '@/api/types'

/** "Leave alone" — a state a control holds, and never a value on the wire. */
export const KEEP = 'keep'

export type BulkMonitored = typeof KEEP | 'on' | 'off'
export type BulkTypesAction = typeof KEEP | ReleaseTypesAction

export interface BulkDraft {
  /** Whether to write `monitored`, and what. */
  monitored: BulkMonitored
  /** A `MonitorMode`, `KEEP`, or anything else — anything else is dropped. */
  monitorMode: string
  /** What the ticked list means. `KEEP` sends neither it nor the list. */
  typesAction: BulkTypesAction
  releaseTypes: readonly string[]
}

/** Every field on "leave alone" — the base a one-press change is spread over,
 *  and the state the release-types drawer opens in. */
export const EMPTY_BULK_DRAFT: BulkDraft = {
  monitored: KEEP,
  monitorMode: KEEP,
  typesAction: KEEP,
  releaseTypes: [],
}

/**
 * The fallback vocabulary for the back-catalogue control, and the list
 * `buildBulkPayload` validates against. `MetaOut.monitor_modes` is the
 * authority once it has loaded; this is the first frame, and the guard that
 * keeps a value the server no longer knows off the wire.
 */
export const MONITOR_MODES: readonly MonitorMode[] = ['all', 'future', 'none']

const TYPES_ACTIONS: readonly ReleaseTypesAction[] = ['set', 'add', 'remove']

function isMonitorMode(value: string): value is MonitorMode {
  return (MONITOR_MODES as readonly string[]).includes(value)
}

function isTypesAction(value: string): value is ReleaseTypesAction {
  return (TYPES_ACTIONS as readonly string[]).includes(value)
}

/** Trim, drop the blanks, keep the first of any duplicate, keep the order. */
function clean(values: readonly string[]): string[] {
  const out: string[] = []
  for (const value of values) {
    const trimmed = value.trim()
    if (trimmed !== '' && !out.includes(trimmed)) out.push(trimmed)
  }
  return out
}

/**
 * The body for `POST /api/artists/bulk`, or `null` when the selection and the
 * draft together would change nothing.
 */
export function buildBulkPayload(
  artistIds: readonly string[],
  draft: BulkDraft,
): ArtistBulkUpdateIn | null {
  const ids = clean(artistIds)
  if (ids.length === 0) return null

  const payload: ArtistBulkUpdateIn = { artist_ids: ids }
  let verbs = 0

  if (draft.monitored === 'on' || draft.monitored === 'off') {
    payload.monitored = draft.monitored === 'on'
    verbs += 1
  }

  if (isMonitorMode(draft.monitorMode)) {
    payload.monitor_mode = draft.monitorMode
    verbs += 1
  }

  if (isTypesAction(draft.typesAction)) {
    const types = clean(draft.releaseTypes)
    if (types.length > 0) {
      payload.release_types = types
      payload.release_types_action = draft.typesAction
      verbs += 1
    }
  }

  return verbs === 0 ? null : payload
}
