/**
 * One row of the Release radar's "Followed artists" list (design 569–580).
 *
 * A 32px circle, a name over a mono meta line, and a switch — `ListRow`'s
 * fourth site, so the row shape itself comes from the primitive rather than
 * being written a second time here.
 *
 * ── the switch says *monitoring*, and that is not a download button ───────
 *
 * The design's footnote calls this toggle "auto-download" (582). In Qobuzarr
 * it writes `ArtistOut.monitored`, and following or monitoring an artist
 * **queues nothing**: `Settings.auto_download` defaults to false and only an
 * explicit press downloads anything. The screen's footnote is where that is
 * said in words; what this component contributes is the half a type can hold —
 * `onToggle: () => void`, receiving nothing, so a caller cannot pass a value
 * on to an endpoint that toggles on an empty body.
 *
 * ── `album_count` of `null` renders NOTHING, not a zero ───────────────────
 *
 * `ArtistOut.album_count` is a roll-up the list router may not have populated,
 * and `null` there means *nobody counted*, not *no albums*. A row reading
 * "0 albums" about a discography of fourteen is the exact failure the
 * three-valued rule exists to prevent, so the clause is omitted entirely — and
 * so is the "followed …" clause when `added_at` is unset, because
 * `fmtAgo(null)` is the word "never" and "followed never" is not a sentence
 * about a followed artist.
 *
 * When both are absent the meta line is not rendered at all. An empty second
 * line is a gap in a row, not information.
 */

import type { ArtistOut } from '@/api/types'
import { Artwork, cx, initialsOf, ListRow, Toggle } from '@/design'
import { fmtCount } from '@/format'
import { RelativeTime } from '@/widgets/RelativeTime'

import styles from '@/widgets/FollowRow/FollowRow.module.css'

export interface FollowRowProps {
  artist: ArtistOut
  /** Flips `monitored`. Receives nothing — see the note above. */
  onToggle: () => void
  /** A write is in flight: the switch is held until the server answers. */
  busy?: boolean
}

export function FollowRow({ artist, onToggle, busy = false }: FollowRowProps) {
  const albums =
    artist.album_count === null ? null : fmtCount(artist.album_count, 'album')
  const followed = artist.added_at !== null && artist.added_at !== ''

  return (
    <ListRow>
      <Artwork
        seed={artist.id}
        // The WORDS rule: an artist is a person, so `Mark Knopfler` is `MK`.
        initials={initialsOf(artist.name, 'words')}
        shape="circle"
        size={32}
        src={artist.image_url}
      />

      <div className={styles.identity}>
        <div className={cx(styles.name, 'truncate')}>{artist.name}</div>
        {albums !== null || followed ? (
          <div className={cx(styles.meta, 'mono')}>
            {albums}
            {albums !== null && followed ? ' · ' : null}
            {followed ? (
              <>
                followed <RelativeTime value={artist.added_at} />
              </>
            ) : null}
          </div>
        ) : null}
      </div>

      <Toggle
        checked={artist.monitored}
        onToggle={onToggle}
        disabled={busy}
        label={`Monitor ${artist.name}`}
      />
    </ListRow>
  )
}
