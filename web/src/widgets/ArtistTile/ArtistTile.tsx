/**
 * One cell of the artist wall (design 255–267).
 *
 * A circle of artwork, a name, and a mono meta line — and the whole cell is
 * one link, because that is what it is: an address, not an action. A
 * `<button onClick={navigate}>` would cost middle-click, copy-link and the
 * browser's own history, all of which somebody browsing a wall of 1,300
 * artists will reach for.
 *
 * ── the circle is load-bearing ────────────────────────────────────────────
 *
 * A person is round and a release is square (design 257 against 279), and that
 * shape is the only thing telling the two grids apart at a glance. The
 * initials follow the same split: the `words` rule, so `Mark Knopfler` is `MK`
 * and never `MA` — which would read as somebody else entirely (design 1114).
 *
 * ── `album_count` is three-valued and the third value is silence ──────────
 *
 * `null` on the roll-ups means *the router did not populate them*, which is a
 * different claim from "this artist has no albums". Rendering `0 albums` there
 * states a fact about the library that nothing measured, on a screen whose
 * entire job is to report the library. So the meta line is omitted — not
 * dashed, not zeroed — and the cell is a name over a picture, which is
 * legible and true.
 *
 * ── the flag the design draws and this does not ───────────────────────────
 *
 * Design 263–265 puts an amber `⚑ {n}` beside the meta line, counting the
 * artist's flagged releases (DCLogic 1112: `p.own.filter(a => a.health !==
 * 'ok').length`). `ArtistOut` carries no such roll-up: `wanted_count` is a
 * backlog, not an integrity verdict, and reading one as the other would put an
 * amber flag on a healthy artist who simply has releases left to download.
 * Integrity is measured per file and rolled up per *album*
 * (`AlbumOut.corrupt_tracks` / `integrity_state`), and there is no artist-level
 * endpoint that aggregates it. The mark is therefore absent rather than
 * invented; it is in `web/WIRING.md`.
 */

import { Link } from 'react-router-dom'

import type { ArtistOut } from '@/api/types'
import { Artwork, initialsOf } from '@/design'
import { fmtCount } from '@/format'

import styles from '@/widgets/ArtistTile/ArtistTile.module.css'

export interface ArtistTileProps {
  artist: ArtistOut
  /**
   * Where the cell goes — the caller owns the address. **`null` renders the
   * same cell with no anchor in it at all**, which is what a screen selecting
   * artists needs: while a selection is being made the cell is a target rather
   * than an address, and the only way to *guarantee* a click cannot navigate is
   * for there to be nothing to navigate. Covering the link with an overlay is
   * the version that looks right and is not — it holds only for as long as
   * nothing inside the tile out-stacks the overlay, and the artwork's own image
   * layer already did.
   */
  to: string | null
  /**
   * Picked in a selection. Draws `Artwork`'s own ring — the same mark the
   * album wall puts on the release its drawer is showing — rather than a
   * second selection treatment invented for this grid.
   */
  selected?: boolean
}

export function ArtistTile({ artist, to, selected = false }: ArtistTileProps) {
  // `null` is silence, so the check is explicit rather than a `fmtCount` call
  // whose em dash would put a dash where the design puts a count.
  const meta = artist.album_count === null ? null : fmtCount(artist.album_count, 'album')

  const inner = (
    <>
      <Artwork
        className={styles.art}
        seed={artist.name}
        initials={initialsOf(artist.name, 'words')}
        shape="circle"
        size="fill"
        selected={selected}
        // `portrait_url` is the server's coalesce — Qobuz's picture when it has
        // one, the enriched portrait otherwise (deps `_apply_artist_metadata`).
        src={artist.portrait_url}
      />
      {/* `title=` because the CSS clamps this to two lines. The clip is
          visual only — the whole string stays in the DOM and so stays the
          link's accessible name — but a pointer has no other way to read the
          tail of an orchestra's. */}
      <span className={styles.name} title={artist.name}>
        {artist.name}
      </span>
      {meta === null ? null : <span className={styles.meta}>{meta}</span>}
    </>
  )

  // Same cell, same styling, no anchor: `data-plain` is what turns the link's
  // underline off, so it belongs to the branch that has a link to un-style.
  if (to === null) return <div className={styles.tile}>{inner}</div>

  return (
    <Link to={to} data-plain="" className={styles.tile}>
      {inner}
    </Link>
  )
}
