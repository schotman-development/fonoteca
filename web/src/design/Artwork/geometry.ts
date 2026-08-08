/**
 * Artwork geometry — the two things the design decides per tile size.
 *
 * Pure, and in their own file rather than beside the component for two
 * reasons. The mechanical one is fast refresh: a module that exports both a
 * component and a function loses its hot-update boundary (oxlint's
 * `react/only-export-components`). The real one is that these are the *design's
 * table*, not the component's behaviour — the answer to "how big are the
 * initials at 108px?" is a fact about the drawing, it is checkable on its own,
 * and it should be readable without reading a render function.
 */

import type { Shape } from '@/design/types'

/** A fixed pixel size, or a tile that fills its grid cell at `aspect-ratio: 1`. */
export type ArtworkSize = number | 'fill'

/**
 * The mono size the design writes the initials at, per artwork size.
 *
 * Sourced one by one: 32 → 11px (design 571), 62 → 15px (545), 108 → 24px
 * (429), 126 → 21px (124). Note it is NOT monotonic — 21px inside the larger
 * tile and 24px inside the smaller one — which is why this is a table and not
 * a formula. Any formula that reproduced those four would be a coincidence
 * with a comment attached.
 *
 * A size that is absent draws no initials. That is the design's own answer and
 * not a fallback: 38px (146), 40px (620) and 84px (677) are bare gradients
 * with nothing written on them. A ninth size therefore arrives here or arrives
 * bare, and either is a decision somebody made on purpose.
 */
const ART_INITIALS_PX: Readonly<Record<number, number>> = {
  32: 11,
  62: 15,
  108: 24,
  126: 21,
}

/**
 * The grid cell is two answers, because the design's two grids differ: the
 * artist circle is 21px (257) and the album square 24px (281).
 */
const FILL_INITIALS_PX: Readonly<Record<Shape, number>> = {
  circle: 21,
  square: 24,
}

/** The initials size for a tile, or `null` where the design draws none. */
export function initialsFontSize(size: ArtworkSize, shape: Shape): number | null {
  if (size === 'fill') return FILL_INITIALS_PX[shape]
  return ART_INITIALS_PX[size] ?? null
}

/**
 * The square's corner, as a token reference.
 *
 * The design draws seven squares at 7/7/8/9/9/10/10px (146, 620, 544, 677,
 * 428, 124, 279). They snap onto three steps of the `--r-*` ladder — never
 * more than a pixel from any of them — the same way
 * `shared/surface.module.css` snaps its own three card radii onto one token.
 * Four radii for one shape is how a system ends up with a radius per call
 * site.
 *
 * A circle never asks: it is `--r-pill`, and that is the only thing telling an
 * artist grid from an album grid at a glance (257 against 279).
 */
export function squareRadiusVar(size: ArtworkSize): string {
  if (size === 'fill' || size >= 100) return 'var(--r-md)' // 126 → 10px, 108 → 9px
  if (size >= 48) return 'var(--r-sm)' //                     84 → 9px, 62 → 8px
  return 'var(--r-xs)' //                                     40 / 38 → 7px
}
