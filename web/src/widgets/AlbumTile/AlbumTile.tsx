/**
 * One cell of the album wall (design 277–292).
 *
 * A square of artwork with two corners of information, then a title, a credit
 * and a mono meta line. Unlike `ArtistTile` this is a **button**, not a link:
 * pressing it selects the release into the detail drawer beside the grid
 * rather than going anywhere, and a link that does not navigate is a lie the
 * middle mouse button finds immediately.
 *
 * ── the two corners say different kinds of thing ──────────────────────────
 *
 * Top-left is a verdict — `albumFlag()`, the one place that rule lives, so the
 * grid and the drawer's health banner cannot disagree about a release. Top
 * right is a figure: `QualityBadge` over **`owned_format_id`**, the copy on
 * disk. The design gates that corner on `a.hires` (284) and `hires` is the
 * *catalogue's* flag, so a 16/44.1 copy of a release Qobuz also sells in 24/96
 * would be badged hi-res. Passing `undefined` rather than a badge component
 * when there is nothing on disk matters: `Artwork` renders the positioned
 * wrapper for any `badgeEnd` it is given, so a component that returns `null`
 * still leaves an empty box in the corner.
 *
 * ── the clause that was dropped ───────────────────────────────────────────
 *
 * The design's meta line is `{year} · {spec} · {size}` (290). There is no
 * per-album byte count anywhere in `app/schemas.py` — not on `AlbumOut`, not on
 * `AlbumDetailOut` — and a size is exactly the kind of figure that must never
 * be invented, so the clause is gone rather than placeheld: a `Placeholder` is
 * for a block, a control or a figure that stands alone, and one inside a
 * three-part mono line would be louder than the line. It is in
 * `web/WIRING.md`. `spec` is the same `owned_format_id` label as the corner
 * badge, which is the design's own arrangement (`a.spec` appears in both) and
 * is what makes the line readable when the artwork scrolls out of view.
 *
 * Both remaining clauses are dropped individually when unknown, and the line
 * disappears entirely when neither is known — never `— · —`.
 */

import type { AlbumOut } from '@/api/types'
import { useMeta } from '@/api/queries'
import { Artwork, Badge, initialsOf } from '@/design'
import { formatLabel } from '@/format'
import { albumFlag } from '@/widgets/albumFlag'
import { QualityBadge } from '@/widgets/QualityBadge'

import styles from '@/widgets/AlbumTile/AlbumTile.module.css'

export interface AlbumTileProps {
  album: AlbumOut
  /** The release the drawer is currently showing. Draws `Artwork`'s ring. */
  selected: boolean
  onSelect: () => void
}

export function AlbumTile({ album, selected, onSelect }: AlbumTileProps) {
  const { data: meta } = useMeta()

  const flag = albumFlag(album)
  const spec = formatLabel(album.owned_format_id, meta?.format_labels)
  const line = [album.year === null ? '' : String(album.year), spec].filter(
    (part) => part !== '',
  )

  return (
    <button
      type="button"
      className={styles.tile}
      // Wrapped, not passed by reference: React hands a `MouseEvent` to the
      // first argument, and `onSelect` is declared `() => void` precisely so a
      // handler cannot be given a value it might pass on. Same rule as
      // `Toggle`'s in the design layer.
      onClick={() => {
        onSelect()
      }}
      // "The one being shown", not "pressed": the press opens a panel that
      // stays open, and `aria-current` is the word for the item it is about.
      aria-current={selected ? 'true' : undefined}
    >
      <Artwork
        className={styles.art}
        seed={album.id}
        initials={initialsOf(album.title, 'chars')}
        shape="square"
        size="fill"
        // `cover_url` is the server's coalesce of the Qobuz image and any
        // enriched one (deps `_apply_album_metadata`); `image_url` is the raw
        // field, kept as the fallback for a payload that carried no metadata.
        src={album.cover_url ?? album.image_url}
        selected={selected}
        badgeStart={
          flag === null ? undefined : <Badge tone={flag.tone}>{flag.label}</Badge>
        }
        badgeEnd={
          album.owned_format_id === null ? undefined : (
            <QualityBadge formatId={album.owned_format_id} />
          )
        }
      />
      {/* `title=` because the CSS clamps this to two lines. The clip is
          visual only — the whole string stays in the DOM and so stays the
          button's accessible name — but a pointer has no other way to read
          the tail of a long one. */}
      <span className={styles.title} title={album.title}>
        {album.title}
      </span>
      {album.artist_name === null ? null : (
        <span className={styles.credit}>{album.artist_name}</span>
      )}
      {line.length === 0 ? null : (
        <span className={styles.meta}>{line.join(' · ')}</span>
      )}
    </button>
  )
}
