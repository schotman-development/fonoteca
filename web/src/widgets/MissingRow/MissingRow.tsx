/**
 * A gap in a discography, with the two presses that close it (design 145–158,
 * and the identical row at 327–340).
 *
 * `ListRow` is the shape; what this adds is the four things a release needs
 * saying about it — a picture, an identity, a reason, and two buttons — plus
 * the two sentences the design writes that this application cannot honestly
 * write.
 *
 * ── the artwork carries no initials, on purpose ───────────────────────────
 *
 * Design 146 is a bare 38px gradient: no `{{ m.initials }}`, unlike every
 * other tile in the file. `Artwork` already answers that from its size table
 * (38 is one of the three sizes the design draws bare), so the empty
 * `initials` here is the same statement said twice, deliberately — a reader
 * checking this row against the design should not have to open a second file
 * to find out whether the letters were forgotten.
 *
 * ── two sentences the design tells and this does not ──────────────────────
 *
 * 1. **"download failed twice"** (DCLogic 1124). `AlbumOut` carries no attempt
 *    count — `attempts` lives on `QueueItemOut`, which is a queue row and not
 *    this row — so the number is unknowable here. The row says **"last
 *    download failed"**: same claim, same colour, same length, and true.
 *    Rewritten rather than placeheld, because a caption asserting a false fact
 *    is prose to correct, not a datum to mark missing.
 * 2. **"not on Qobuz"** (1121, 1123). There is no such value in `AlbumStatus`
 *    — the vocabulary is `skipped · wanted · queued · downloading · downloaded
 *    · failed` and none of them means "the catalogue dropped it". So the state
 *    does not exist, and neither does the design's `canGrab: false` branch
 *    that hides the Download button for it: **every** row here is grabbable,
 *    which is what makes the block actionable rather than a list of regrets.
 *
 * ── the second button is named after what the press will do ───────────────
 *
 * The endpoint toggles on an empty body, so the label is read off
 * `album.monitored` rather than fixed: **Ignore** for a release in the backlog,
 * **Monitor** for one on the artist page's "Not wanted" list. Same handler,
 * same request — a row that is already ignored must not offer to ignore it
 * again, which is a press that appears to do nothing and in fact undoes the
 * decision. `status` is not what decides it: a `skipped` release can still be
 * monitored (its type is outside the artist's accepted set), and clearing that
 * flag is a real change even though the status does not move.
 *
 * ── the quality clause is what a download would land ──────────────────────
 *
 * The design's sub line ends in `m.avail` — what Qobuz has (1127). The truthful
 * version is `upgrade_format_id`, the server's own answer to *what a fresh
 * download would land, when that is strictly better*, falling back to
 * `owned_format_id` for a row that already holds something. Both are format
 * ids resolved through `MetaOut.format_labels`; neither is re-derived here,
 * because `app/core/quality.py` is the single place that arithmetic is allowed
 * to live and the button and the worker must not be able to disagree. Unknown
 * drops the clause — it never guesses, and it never prints a dash mid-sentence.
 */

import type { AlbumOut } from '@/api/types'
import { useMeta } from '@/api/queries'
import { Artwork, Button, ListRow, cx } from '@/design'
import { formatLabel } from '@/format'

import styles from '@/widgets/MissingRow/MissingRow.module.css'

export interface MissingRowProps {
  album: AlbumOut
  /** Queue this release. The caller owns the mutation and the toast. */
  onDownload: () => void
  /** Toggle the release's monitor flag — an EMPTY body, so it toggles. */
  onIgnore: () => void
  /** A press is in flight for this row. Blocks a second one. */
  busy?: boolean
}

export function MissingRow({
  album,
  onDownload,
  onIgnore,
  busy = false,
}: MissingRowProps) {
  const { data: meta } = useMeta()

  const quality = formatLabel(
    album.upgrade_format_id ?? album.owned_format_id,
    meta?.format_labels,
  )
  const sub = [
    album.artist_name ?? '',
    album.year === null ? '' : String(album.year),
    quality,
  ].filter((part) => part !== '')

  return (
    <ListRow>
      <Artwork
        seed={album.id}
        // Bare by design (146). See the note at the top of this file.
        initials=""
        shape="square"
        size={38}
        src={album.cover_url ?? album.image_url}
      />

      <div className={styles.identity}>
        <div className={cx(styles.title, 'truncate')}>{album.title}</div>
        {sub.length === 0 ? null : (
          <div className={styles.sub}>{sub.join(' · ')}</div>
        )}
      </div>

      {album.status === 'failed' ? (
        <span className={styles.reason}>last download failed</span>
      ) : null}

      {/* Both handlers are WRAPPED rather than passed by reference. React hands
          a `MouseEvent` to the first argument, and these are declared
          `() => void` for the same reason `ToggleProps.onToggle` is: the
          monitor endpoint toggles on an EMPTY body, and a handler that is
          never given a value cannot pass one on. */}
      <div className={styles.actions}>
        <Button
          variant="primary"
          size="sm"
          loading={busy}
          onClick={() => {
            onDownload()
          }}
        >
          Download
        </Button>
        <Button
          variant="secondary"
          size="sm"
          disabled={busy}
          onClick={() => {
            onIgnore()
          }}
        >
          {album.monitored ? 'Ignore' : 'Monitor'}
        </Button>
      </div>
    </ListRow>
  )
}
