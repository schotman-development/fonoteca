/**
 * The placeholder art — one tile, eight sizes and two shapes.
 *
 * The design draws this ten times and every one of them is the same object:
 * a two-stop gradient chosen by hashing a string, a pair of mono initials in
 * `--art-ink`, a contact shadow, and — on the album grid — two absolutely
 * positioned badges and a selection ring. Design lines 123–131 (the 126px
 * shelf card), 146 (38px), 257–259 (the circle grid cell), 279–291 (the square
 * grid cell, badges and ring), 306–309 (the dimmed variant), 428–430 (108px),
 * 544–546 (62px), 571 (32px circle), 620 (40px) and 677 (84px).
 *
 * ── it knows nothing about what it is a picture of ────────────────────────
 *
 * No `album`, no `artist`, no `cover`. The caller hands it a `seed` (whatever
 * string should decide the colour — a title, a name, an id) and the `initials`
 * it wants drawn. Deciding *which* of the two initials rules applies is the
 * caller's job and deliberately not this component's: `initialsOf(x, 'chars')`
 * is what a release gets and `initialsOf(x, 'words')` is what a person gets
 * (see design/artwork.ts), and a primitive that guessed between them would be
 * guessing whether it is looking at a person — which is exactly the domain
 * knowledge this layer must not hold.
 *
 * ── three things are derived from `size` rather than written per call site ─
 *
 * 1. **The initials' size**, and 2. **the corner radius** — both from
 *    `Artwork/geometry.ts`, which is the design's own table read out loud.
 *    The initials sizes are not monotonic (21px inside a 126px tile, 24px
 *    inside a 108px one — 124 against 429), so there is no formula to find;
 *    and a size the table does not name draws NO initials, which is not a
 *    fallback but the design's answer, since 38px (146), 40px (620) and 84px
 *    (677) are bare gradients with nothing written on them.
 * 3. **The padding the initials sit in**, as a percentage of the tile's own
 *    width — 11px at 126, 12px at a 132px grid cell, 9px at 108 (124, 279,
 *    428). A percentage is the one expression that works for the `fill`
 *    variant too, where the pixel width is whatever the grid column turns out
 *    to be and no px value could be right.
 *
 * ── a real image never costs the tile its identity ────────────────────────
 *
 * `src` renders an `<img>` *over* the gradient rather than instead of it, so a
 * cover that is slow, blocked or 404 leaves the release looking like itself
 * rather than like a hole. The image is `alt=""` — decorative — because the
 * thing that names this artwork is the card around it, and an `<img>` that
 * repeated the title would make every cell announce twice.
 */

import type { CSSProperties, ReactNode } from 'react'

import { artGradientVar } from '@/design/artwork'
import { cx } from '@/design/cx'
import type { ArtworkSize } from '@/design/Artwork/geometry'
import { initialsFontSize, squareRadiusVar } from '@/design/Artwork/geometry'
import type { Shape, StyleableProps } from '@/design/types'
import styles from '@/design/Artwork/Artwork.module.css'

export interface ArtworkProps extends StyleableProps {
  /** The string the gradient hashes on. Same string in, same colour forever. */
  seed: string
  /**
   * What to draw on the tile. The caller has already chosen between the
   * `chars` and `words` rules; `''` draws nothing, as does any size the design
   * leaves bare.
   */
  initials: string
  /** `square` is a release, `circle` is a person. */
  shape: Shape
  /** A pixel size the design draws, or `fill` for an `aspect-ratio: 1` cell. */
  size: ArtworkSize
  /**
   * A real cover. Drawn over the gradient, never in place of it, so a broken
   * or pending image still shows the release's own colour.
   */
  src?: string | null
  /** The design's selection ring and lifted shadow (design 279's `a.ring`). */
  selected?: boolean
  /** Absolutely positioned in the top-left corner (design 282). */
  badgeStart?: ReactNode
  /** Absolutely positioned in the top-right corner (design 285). */
  badgeEnd?: ReactNode
  /** The design's `opacity: .92` variant (306). */
  dim?: boolean
}

export function Artwork({
  seed,
  initials,
  shape,
  size,
  src = null,
  selected = false,
  badgeStart,
  badgeEnd,
  dim = false,
  className,
}: ArtworkProps) {
  const fontPx = initialsFontSize(size, shape)
  const showInitials = fontPx !== null && initials !== ''

  // The only inline style in this layer, and it is token references plus the
  // one measurement React genuinely owns: `--art` names a gradient from
  // tokens.css and `--art-size` is the caller's own number. Neither is a
  // colour and neither is a value the stylesheet could have known.
  const style = {
    '--art': artGradientVar(seed),
    ...(size === 'fill' ? {} : { '--art-size': `${size}px` }),
    '--art-radius': shape === 'circle' ? 'var(--r-pill)' : squareRadiusVar(size),
    ...(fontPx === null ? {} : { '--art-initials-fs': `${fontPx}px` }),
  } as CSSProperties

  return (
    <div
      className={cx(
        styles.art,
        shape === 'circle' ? styles.circle : styles.square,
        size === 'fill' && styles.fill,
        selected && styles.selected,
        dim && styles.dim,
        className,
      )}
      style={style}
      data-shape={shape}
      data-selected={selected ? '' : undefined}
    >
      {showInitials ? (
        // Hidden from assistive tech: it is a colour-and-two-letters stand-in
        // for a picture, and the card around it carries the real name.
        <span className={styles.initials} aria-hidden="true">
          {initials}
        </span>
      ) : null}
      {src === null || src === '' ? null : (
        <img className={styles.image} src={src} alt="" loading="lazy" />
      )}
      {badgeStart === undefined ? null : (
        <span className={styles.badgeStart}>{badgeStart}</span>
      )}
      {badgeEnd === undefined ? null : (
        <span className={styles.badgeEnd}>{badgeEnd}</span>
      )}
    </div>
  )
}
