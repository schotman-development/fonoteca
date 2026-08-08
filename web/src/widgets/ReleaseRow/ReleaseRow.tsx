/**
 * One row of the Release radar's "New & upcoming" list (design 542–562).
 *
 * 62px of artwork, a title over a credit over a mono facts line, and a
 * right-hand stack of a coloured state word and a quiet note.
 *
 * ── what it draws, and what it used to draw ───────────────────────────────
 *
 * An **`AlbumOut`**, ordered by release date by `GET /api/releases/recent`.
 * This row was built against `QueueItemOut` and the column was therefore the
 * download queue wearing a heading that said *New & upcoming*: the queue is
 * ordered by the moment somebody pressed Download, so the top row was whatever
 * finished most recently — a 1975 remaster fetched this morning outranking a
 * record released last week — and a radar nobody had downloaded from was
 * empty. The queue is still drawn, properly, on the Download queue screen, where the
 * heading matches the ordering.
 *
 * The swap costs one thing and it is worth naming: **there is no progress
 * meter here any more.** `progress_tracks_done`/`_total` live on a queue item
 * and nowhere else, and fetching a queue payload per row to fill in a bar would
 * be the same mistake in a smaller costume. A row whose download is running
 * says `Downloading`; how far it has got is one click away on the queue screen.
 *
 * ── the state word reads `queue_state`, not `status` ──────────────────────
 *
 * Both, in that order, and `releaseState` is where that is written down: an
 * album being *upgraded* keeps `status: 'downloaded'` for the whole download,
 * so `status` alone would draw *In library* over a live transfer.
 *
 * ── the design's `Announced`, which now exists ────────────────────────────
 *
 * The queue version of this row recorded that the design's `Announced` state
 * (DCLogic 986, a release dated in the future) had no equivalent, because
 * `QueueState` has no such member and nothing models a release that is not yet
 * purchasable. Ordering on `release_date` is what makes it reachable: a future
 * date is simply a future date. It is drawn as the note line — `out in 11
 * days` — rather than as a state word, because *announced* is a fact about the
 * calendar and the state word's column is about what Qobuzarr has done.
 *
 * ── the facts line, and the clause that is still missing ──────────────────
 *
 * Release date · track count · quality. The quality clause is the same
 * `formatLabel` question `MissingRow` asks and is resolved through
 * `MetaOut.format_labels`, never re-derived — `app/core/quality.py` is the only
 * place that arithmetic lives. The design's byte total (`1.5 GB`, 550) has no
 * field on any payload and is still absent.
 */

import type { AlbumOut } from '@/api/types'
import { useMeta } from '@/api/queries'
import { Artwork, cx, initialsOf } from '@/design'
import { EM_DASH, fmtCount, fmtDate, formatLabel } from '@/format'
import { releaseNote, releaseState, type ReleaseTone } from '@/widgets/releaseState'

import styles from '@/widgets/ReleaseRow/ReleaseRow.module.css'

export interface ReleaseRowProps {
  album: AlbumOut
}

const TONE_CLASS: Readonly<Record<ReleaseTone, string | undefined>> = {
  ok: styles.stateOk,
  bad: styles.stateBad,
  resting: styles.stateResting,
  quiet: styles.stateQuiet,
}

export function ReleaseRow({ album }: ReleaseRowProps) {
  const { data: meta } = useMeta()

  const title = album.title || EM_DASH
  const state = releaseState(album)
  const note = releaseNote(album.release_date)

  const date = fmtDate(album.release_date)
  const quality = formatLabel(
    album.owned_format_id ?? album.upgrade_format_id,
    meta?.format_labels,
  )
  const facts = [
    date === EM_DASH ? null : date,
    album.tracks_count > 0 ? fmtCount(album.tracks_count, 'track') : null,
    quality === '' ? null : quality,
  ].filter((part): part is string => part !== null)

  return (
    <div className={styles.row}>
      <Artwork
        seed={album.id}
        initials={initialsOf(title, 'chars')}
        shape="square"
        size={62}
        src={album.cover_url ?? album.image_url}
      />

      <div className={styles.identity}>
        <div className={styles.title}>{title}</div>
        <div className={cx(styles.credit, 'truncate')}>
          {album.artist_name ?? EM_DASH}
        </div>
        {facts.length > 0 ? (
          <div className={cx(styles.facts, 'mono')}>{facts.join(' · ')}</div>
        ) : null}
      </div>

      <div className={styles.status}>
        <span className={cx(styles.state, TONE_CLASS[state.tone])}>{state.label}</span>
        {note === null ? null : (
          <span className={cx(styles.note, 'mono')}>{note}</span>
        )}
      </div>
    </div>
  )
}
