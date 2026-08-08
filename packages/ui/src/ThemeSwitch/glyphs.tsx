import type { SVGProps } from 'react'

/**
 * The two glyphs, drawn here rather than taken from an icon package. The design
 * system carries no third-party runtime dependencies, and a sun and a crescent
 * are a dozen path commands.
 *
 * Both are `aria-hidden` and `focusable="false"`: the switch itself carries the
 * accessible name, so a glyph that announced itself would give the control two
 * names and put a stray tab stop inside it in older engines.
 *
 * They are drawn on a 24 unit grid but render at 14–17px, which drives two
 * choices. The stroke is 2.2 rather than the usual 2, because a hairline
 * disappears at this size. And the moon is FILLED while the sun is stroked: a
 * stroked crescent's inner arc closes to a sliver at the tips and turns muddy
 * once it is 14 pixels wide, whereas the sun needs stroke for its rays.
 */
export type GlyphProps = Omit<SVGProps<SVGSVGElement>, 'viewBox' | 'children'>

export function SunGlyph(props: GlyphProps) {
  return (
    <svg
      viewBox="0 0 24 24"
      aria-hidden="true"
      focusable="false"
      fill="none"
      stroke="currentColor"
      strokeWidth={2.2}
      strokeLinecap="round"
      strokeLinejoin="round"
      {...props}
    >
      <circle cx="12" cy="12" r="4.4" />
      <path d="M12 2.3v2M12 19.7v2M2.3 12h2M19.7 12h2" />
      <path d="M5.25 5.25l1.45 1.45M17.3 17.3l1.45 1.45M18.75 5.25L17.3 6.7M6.7 17.3l-1.45 1.45" />
    </svg>
  )
}

export function MoonGlyph(props: GlyphProps) {
  return (
    <svg viewBox="0 0 24 24" aria-hidden="true" focusable="false" fill="none" {...props}>
      {/*
        Two arcs: the moon's own circle, then a smaller one cutting the bite out
        of it. Both radii are large enough that the chord between the endpoints
        fits, so the renderer never has to scale them up to make the arc close.
      */}
      <path d="M20.6 14.4A9 9 0 1 1 11.2 3.05 7.6 7.6 0 0 0 20.6 14.4Z" fill="currentColor" />
    </svg>
  )
}
