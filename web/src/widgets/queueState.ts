/**
 * What the download queue says about one entry — one rule, in one place.
 *
 * `releaseState.ts` is the sibling of this file and answers the *album's*
 * question ("what has Qobuzarr done about this release?"). This one answers the
 * *queue item's* ("what is this unit of work doing?"), and they are genuinely
 * different: a release being upgraded is `downloaded` while its queue item is
 * `active`, which is exactly the invariant `releaseState` exists to keep. A row
 * on the queue screen is about the work, so it reads this.
 *
 * Three rules live here, and each is the sort that goes wrong quietly when it
 * is spelled out at a call site instead.
 *
 * ── the switch is total, so a sixth state cannot render blank ──────────────
 *
 * `QueueState` is `pending · active · done · failed · cancelled`. Every
 * function below switches over all five with no `default`, so a member added to
 * the enum is a **type error here** rather than an empty cell on the screen or
 * a button that appears on a row it cannot act on.
 *
 * ── which buttons a row may offer is the server's rule, not the row's ──────
 *
 * `retry_queue_item()` answers **409** for an `active` item ("that item is
 * downloading right now"), and resets a `failed`/`cancelled` one to pending.
 * `cancel_queue_item()` will happily cancel anything, including an item that
 * finished ten minutes ago — which is a press that writes `cancelled` over a
 * `done` row and reverses nothing. So the honest offer is narrower than what
 * the endpoints accept, and `queueActions()` is where that is written down: a
 * button drawn on a row the press cannot help is worse than no button, because
 * somebody has to press it to find out.
 *
 * A `pending` item deliberately offers no Retry. It has not failed; "retry"
 * would reset the attempt counter of something that has not been attempted, and
 * the row already offers the press that means anything there — Cancel.
 *
 * ── progress is three-valued, and `0` is not "nothing yet" ─────────────────
 *
 * `Meter`'s rule, applied to this payload. `progress_tracks_total` is `0` until
 * the downloader has resolved the release's track list, and a bar drawn at 0%
 * next to an item that is genuinely working reads as stalled — which is what
 * makes somebody cancel it and press Download again. So `queueProgress()`
 * returns `null` for an item nothing has counted and a real fraction otherwise,
 * and the row pairs `null` with `indeterminate` while the state is `active`.
 */

import type { QueueItemOut, QueueState } from '@/api/types'

/** The ink a state word is set in. Mirrors `QueueRow`'s four `state*` classes. */
export type QueueTone = 'ok' | 'bad' | 'resting' | 'quiet'

export interface QueueStateWord {
  /** The word for the right-hand column. */
  label: string
  tone: QueueTone
}

/**
 * The word and the ink for a queue entry's state.
 *
 * `Waiting` rather than `Pending`: the column is read at a glance beside
 * `Downloading`, and what a pending item is doing is waiting its turn — the
 * queue is sequential by design, one album at a time, and that is the fact
 * somebody is looking for when they open this screen wondering why their
 * download has not started.
 */
export function queueStateWord(state: QueueState): QueueStateWord {
  switch (state) {
    case 'active':
      return { label: 'Downloading', tone: 'ok' }
    case 'pending':
      return { label: 'Waiting', tone: 'resting' }
    case 'done':
      // Finished successfully, and no longer news. Quiet rather than `ok`: a
      // column of green on a screen whose whole point is what is still
      // outstanding buries the two rows that need somebody.
      return { label: 'Done', tone: 'quiet' }
    case 'failed':
      return { label: 'Failed', tone: 'bad' }
    case 'cancelled':
      return { label: 'Cancelled', tone: 'quiet' }
  }
}

export interface QueueActions {
  /** Offer **Retry** — `POST /api/queue/{id}/retry`. */
  canRetry: boolean
  /** Offer **Cancel** — `DELETE /api/queue/{id}`. */
  canCancel: boolean
}

/**
 * Which of the two presses this row may honestly offer. See the note above.
 */
export function queueActions(state: QueueState): QueueActions {
  switch (state) {
    case 'pending':
      return { canRetry: false, canCancel: true }
    case 'active':
      // Retry is a 409 while the worker holds it. Cancel is real and does not
      // interrupt mid-track: `QueueWorker.cancel_item` raises a per-item stop
      // flag, so the album stops after the file currently in flight.
      return { canRetry: false, canCancel: true }
    case 'failed':
      return { canRetry: true, canCancel: false }
    case 'cancelled':
      return { canRetry: true, canCancel: false }
    case 'done':
      return { canRetry: false, canCancel: false }
  }
}

/**
 * How far through the tracks this item is, as a fraction in 0..1, or `null`
 * when nothing has counted them yet.
 *
 * Derived from `progress_tracks_done`/`progress_tracks_total` rather than from
 * `progress_percent`, because the pair is what the sentence beside the bar is
 * written from and one figure computed two ways is two figures. The server's
 * percent is the same arithmetic rounded to an integer.
 */
export function queueProgress(item: QueueItemOut): number | null {
  if (item.progress_tracks_total <= 0) return null
  return item.progress_tracks_done / item.progress_tracks_total
}

/**
 * The sentence beside the bar: `9 of 12 tracks`, or `null` when nothing has
 * counted them.
 *
 * `null` and not `'0 of 0 tracks'` — the row draws the state word alone in that
 * case, which is the true statement that this item has not got far enough to
 * have a count.
 */
export function queueProgressText(item: QueueItemOut): string | null {
  if (item.progress_tracks_total <= 0) return null
  return `${item.progress_tracks_done} of ${item.progress_tracks_total} tracks`
}
