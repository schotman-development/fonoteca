import type { SVGProps } from 'react'

/** Two shapes the transport needs and no other component does. */
export type GlyphProps = Omit<SVGProps<SVGSVGElement>, 'viewBox' | 'children'>

function Svg({ children, ...props }: SVGProps<SVGSVGElement>) {
  return (
    <svg viewBox="0 0 24 24" aria-hidden="true" focusable="false" {...props}>
      {children}
    </svg>
  )
}

export function SpeakerGlyph(props: GlyphProps) {
  return (
    <Svg {...props}>
      <path d="M4 9h4l5-4v14l-5-4H4z" fill="currentColor" />
      <path
        d="M16.5 9.5a3.5 3.5 0 0 1 0 5M19 7a7 7 0 0 1 0 10"
        fill="none"
        stroke="currentColor"
        strokeWidth="1.75"
        strokeLinecap="round"
      />
    </Svg>
  )
}

/**
 * The same speaker with a cross through it. Muted is a *different shape*, not
 * the same shape in a different colour — which is also why the button's name
 * flips between "Mute" and "Unmute".
 */
export function SpeakerMutedGlyph(props: GlyphProps) {
  return (
    <Svg {...props}>
      <path d="M4 9h4l5-4v14l-5-4H4z" fill="currentColor" />
      <path
        d="M16.5 9.5 21 14M21 9.5 16.5 14"
        fill="none"
        stroke="currentColor"
        strokeWidth="1.75"
        strokeLinecap="round"
      />
    </Svg>
  )
}

export function CloseGlyph(props: GlyphProps) {
  return (
    <Svg {...props}>
      <path
        d="M6 6 18 18M18 6 6 18"
        fill="none"
        stroke="currentColor"
        strokeWidth="1.75"
        strokeLinecap="round"
      />
    </Svg>
  )
}
