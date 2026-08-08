/**
 * The prop vocabularies the primitive layer shares.
 *
 * Type declarations only — there is no runtime in this file and therefore no
 * test beside it. What is worth writing down is *why* these three unions are
 * shared rather than re-declared per primitive: a `tone` that means one set of
 * five words on `Meter` and a different set of four on `Badge` is how a screen
 * ends up asking for a colour that the component silently ignores.
 *
 * Nothing here knows what a release, an artist or a queue item is. If a union
 * would need one of those words it belongs in `@/widgets`, not here.
 */

/**
 * Status colouring, and the whole of it.
 *
 * `neutral` is the resting state and paints nothing — a screen where nothing is
 * wrong shows no colour at all. `accent` is identity rather than status (a link,
 * the proposed half of a diff, an on switch); it happens to be the same teal as
 * `ok` by design, because "correct" is the resting state and does not get a
 * second colour (see the note above `--c-ok` in tokens.css).
 *
 * The mapping to tokens is per primitive, because it depends on whether the
 * colour lands on TEXT or on a MARK: amber text is `--c-warn` (5.0:1) and an
 * amber bar or dot is `--c-warn-solid` (3.2:1), and the two are not
 * interchangeable. A primitive picking the wrong one is a contrast failure that
 * no type can catch, so each one states which it uses.
 */
export type Tone = 'neutral' | 'accent' | 'ok' | 'warn' | 'bad'

/**
 * Three control sizes. `sm` is a row action (6px/12px, 12px text — design lines
 * 154, 156, 336), `md` is the default (8px/14px, 13px — 39, 222, 535), `lg` is
 * the one-per-panel commitment (10px/18px, 13px — 663–665).
 *
 * `Figure` reads the same union for its two sizes (19px at `md`, 24px at `lg` —
 * design 230 and 684) rather than inventing `small`/`large`, so a screen never
 * has to remember which word a given primitive chose.
 */
export type Size = 'sm' | 'md' | 'lg'

/**
 * Artwork and avatar geometry. A square is a release (`--r-md`); a circle is a
 * person (`--r-pill`). The design never draws a release round or an artist
 * square, and the distinction is load-bearing rather than decorative — it is
 * the only thing telling the two grids apart at a glance (design 257 vs 279).
 */
export type Shape = 'square' | 'circle'

/**
 * A react-router address, or nothing.
 *
 * Several primitives are a `<button>` when they act and an `<a>` when they
 * navigate — the StatCard that opens `/activity`, the quiet section link, the
 * artwork cell that opens an artist. They take `to` and render `<Link>`; with
 * no `to` they render `<button>` and require `onClick`. The alias exists so
 * that "this primitive can navigate" is spelled the same way in each of them,
 * and so a component never accepts *both* a `to` and an `onClick` meaning two
 * different destinations.
 */
export type PolymorphicTo = string | undefined

/** The half of a navigating primitive's props that is about where it goes. */
export interface NavigableProps {
  /** In-app address. Present ⇒ the primitive renders a router `<Link>`. */
  to?: PolymorphicTo
  /** Absent `to` ⇒ the primitive renders a `<button>` and this is required. */
  onClick?: () => void
}

/** Every primitive accepts a class name so a screen can place it; none accepts a style object. */
export interface StyleableProps {
  className?: string
}
