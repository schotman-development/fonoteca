/**
 * One entry in the download queue — the row the Download queue screen is a
 * list of.
 *
 * It is the third row in this directory that shows a release and it is
 * deliberately not either of the other two. `MissingRow` draws an `AlbumOut`
 * and offers the presses that *start* work; `ReleaseRow` draws an `AlbumOut`
 * ordered by release date and offers none. This draws a **`QueueItemOut`** —
 * the unit of work — and offers the two presses that change work already
 * accepted: Retry and Cancel.
 *
 * ── the bar is here, and it is the reason the screen exists ───────────────
 *
 * `ReleaseRow` gave its progress meter up when the radar stopped drawing the
 * queue, and the note there says why: `progress_tracks_done`/`_total` live on a
 * queue payload and a row must not fetch a second payload to fill in a
 * decoration. On this screen the queue payload *is* the list, so the bar costs
 * nothing and is the one thing on any screen that moves while you watch it.
 *
 * It is drawn for an `active` row only. A waiting row has made no progress and
 * a finished one has made all of it; a full-width track under every row would
 * be five bars saying nothing so that one bar could say something. And when the
 * downloader has not resolved the track list yet, the bar is `indeterminate`
 * rather than 0% — `queueProgress()` answers `null`, and `Meter` draws a moving
 * sliver. A 0% bar under a live download is what makes somebody cancel it.
 *
 * ── the artwork is bare, like every 38px tile in this application ─────────
 *
 * Same statement `MissingRow` makes: `Artwork`'s size table draws 38px without
 * initials, and passing `initials=""` says so at the call site rather than
 * leaving a reader to open a second file to find out whether letters were
 * forgotten.
 *
 * ── the error string is shown, once, and never as the row's identity ──────
 *
 * `last_error` is an upstream message — sometimes a sentence, sometimes a
 * traceback's last line. It sits on its own line under the identity, clamped to
 * one line with the full text in `title`, and only on a row whose state is one
 * the error explains. A `done` row can carry a stale `last_error` from an
 * earlier attempt that then succeeded, and printing it there would report a
 * successful download as broken.
 *
 * ── which buttons appear is `queueActions`, not a ternary here ────────────
 *
 * See `queueState.ts`. The short version: Retry is a 409 on an `active` item
 * and a no-op on a `pending` one, and Cancel writes `cancelled` over a `done`
 * row while reversing nothing. Both handlers are `() => void` and wrapped at
 * the call site for the same reason `MissingRow`'s are — a handler that is
 * never handed a value cannot pass React's `MouseEvent` on to a mutation.
 */

import type { QueueItemOut } from '@/api/types'
import { Artwork, Button, ListRow, Meter, cx } from '@/design'
import { EM_DASH, fmtAgo } from '@/format'
import {
  queueActions,
  queueProgress,
  queueProgressText,
  queueStateWord,
  type QueueTone,
} from '@/widgets/queueState'

import styles from '@/widgets/QueueRow/QueueRow.module.css'

export interface QueueRowProps {
  item: QueueItemOut
  /** Reset a failed or cancelled entry to pending. The caller owns the toast. */
  onRetry: () => void
  /** Cancel a waiting or running entry. The caller owns the toast. */
  onCancel: () => void
  /** A press is in flight for this row. Blocks a second one. */
  busy?: boolean
}

const TONE_CLASS: Readonly<Record<QueueTone, string | undefined>> = {
  ok: styles.stateOk,
  bad: styles.stateBad,
  resting: styles.stateResting,
  quiet: styles.stateQuiet,
}

/**
 * The one timestamp worth showing, named. A row is asked a different question
 * at each end of its life — *when did this start* while it runs, *when did it
 * finish* once it has — and showing both is two mono strings competing for the
 * same two seconds of attention.
 */
function stamp(item: QueueItemOut): string | null {
  if (item.finished_at !== null) return `finished ${fmtAgo(item.finished_at)}`
  if (item.started_at !== null) return `started ${fmtAgo(item.started_at)}`
  if (item.created_at !== null) return `queued ${fmtAgo(item.created_at)}`
  return null
}

export function QueueRow({ item, onRetry, onCancel, busy = false }: QueueRowProps) {
  const state = queueStateWord(item.state)
  const actions = queueActions(item.state)

  const title = item.album_title ?? EM_DASH
  const running = item.state === 'active'
  const fraction = queueProgress(item)
  const progressText = queueProgressText(item)

  // An attempt count is only news above one: every item that ever ran has made
  // one attempt, and a column of "attempt 1" is noise with a number in it.
  const attempts = item.attempts > 1 ? `attempt ${item.attempts}` : null
  const facts = [item.artist_name, attempts, stamp(item)].filter(
    (part): part is string => part !== null && part !== '',
  )

  // Only where the state is one the error explains — see the note above.
  const error =
    item.last_error !== null && (item.state === 'failed' || item.state === 'pending')
      ? item.last_error
      : null

  return (
    <ListRow>
      <Artwork
        seed={item.album_id}
        // Bare at 38px, as everywhere else in this application.
        initials=""
        shape="square"
        size={38}
        src={item.album_image_url}
      />

      <div className={styles.identity}>
        <div className={cx(styles.title, 'truncate')}>{title}</div>
        {facts.length === 0 ? null : (
          <div className={styles.sub}>{facts.join(' · ')}</div>
        )}
        {error === null ? null : (
          <div className={cx(styles.error, 'truncate')} title={error}>
            {error}
          </div>
        )}
      </div>

      <div className={styles.status}>
        <span className={cx(styles.state, TONE_CLASS[state.tone])}>{state.label}</span>
        {progressText === null || !running ? null : (
          <span className={cx(styles.count, 'mono')}>{progressText}</span>
        )}
      </div>

      <div className={styles.actions}>
        {actions.canRetry ? (
          <Button
            variant="secondary"
            size="sm"
            loading={busy}
            onClick={() => {
              onRetry()
            }}
          >
            Retry
          </Button>
        ) : null}
        {actions.canCancel ? (
          <Button
            variant="secondary"
            size="sm"
            loading={busy}
            onClick={() => {
              onCancel()
            }}
          >
            Cancel
          </Button>
        ) : null}
      </div>

      {/* Wraps onto its own line under everything else — `ListRow` is a
          wrapping flex row and this child is 100% wide. Drawn only while the
          item is running: see the note at the top of this file. */}
      {running ? (
        <div className={styles.progress}>
          <Meter
            value={fraction}
            indeterminate={fraction === null}
            thickness={4}
            tone="accent"
            label={`Downloading ${title}`}
            valueText={progressText ?? undefined}
          />
        </div>
      ) : null}
    </ListRow>
  )
}
