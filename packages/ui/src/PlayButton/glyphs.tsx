import type { SVGProps } from 'react'

/**
 * The four states a play control can be in, drawn rather than coloured.
 *
 * Inline SVG on a 24-unit grid, following `ThemeSwitch/glyphs.tsx`: this package
 * has no icon library and is not going to grow one for four shapes. Every glyph
 * is `aria-hidden` and `focusable="false"` — the button's `aria-label` is the
 * name, and an SVG in the accessibility tree beside it would be a second one.
 */
export type GlyphProps = Omit<SVGProps<SVGSVGElement>, 'viewBox' | 'children'>

function Svg({ children, ...props }: SVGProps<SVGSVGElement>) {
  return (
    <svg viewBox="0 0 24 24" aria-hidden="true" focusable="false" {...props}>
      {children}
    </svg>
  )
}

/**
 * Filled, and shifted right by about a unit.
 *
 * A triangle centred on its bounding box reads as sitting to the left of centre,
 * because its visual mass is not where its box is — the same optical correction
 * every play button in the world has and none of them mentions.
 */
export function PlayGlyph(props: GlyphProps) {
  return (
    <Svg {...props}>
      <path d="M9 5.5 19 12 9 18.5z" fill="currentColor" />
    </Svg>
  )
}

export function PauseGlyph(props: GlyphProps) {
  return (
    <Svg {...props}>
      <path d="M8 5h3v14H8zM13 5h3v14h-3z" fill="currentColor" />
    </Svg>
  )
}

/**
 * A warning triangle. **Shape, not colour**, is what carries the error state —
 * the button's name says so too, and neither depends on the other.
 */
export function AlertGlyph(props: GlyphProps) {
  return (
    <Svg {...props}>
      <path
        d="M12 4 22 20H2zM11 10h2v5h-2zM11 16.5h2V18h-2z"
        fill="currentColor"
        fillRule="evenodd"
      />
    </Svg>
  )
}

/**
 * A three-quarter ring. It rotates through a keyframe in the stylesheet; under
 * reduced motion it simply stops, which still reads as "not a play triangle".
 * A ring is rotationally ambiguous by design, so a frozen one at any angle says
 * the same thing.
 */
export function SpinnerGlyph(props: GlyphProps) {
  return (
    <Svg {...props}>
      <path
        d="M12 3a9 9 0 1 1-6.36 2.64"
        fill="none"
        stroke="currentColor"
        strokeWidth="2.5"
        strokeLinecap="round"
      />
    </Svg>
  )
}
