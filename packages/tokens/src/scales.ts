/**
 * Theme-independent scales: the same in light and dark.
 *
 * Everything here is tuned for a DENSE DATA APPLICATION. Fonoteca's primary
 * screen is a table with six figures of rows, so the defaults that suit a
 * marketing page — 16px body text, 24px rhythm, generous line height — actively
 * work against it. The scales below start small and step tightly, and the
 * spacing scale is named in literal pixels so there is never any ambiguity
 * about what `space-6` means when you are trying to fit a row into 28px.
 */

/** Literal pixel values. Named by size so a dense layout can be reasoned about exactly. */
export const space = {
  0: '0px',
  2: '2px',
  4: '4px',
  6: '6px',
  8: '8px',
  12: '12px',
  16: '16px',
  20: '20px',
  24: '24px',
  32: '32px',
  40: '40px',
  48: '48px',
  64: '64px',
  96: '96px',
} as const

/**
 * Type scale. `md` (14px) is body text; `sm` (13px) is the table-cell default.
 * The steps below `lg` are deliberately 1px apart — at dense sizes a 1.25 ratio
 * jumps straight past the size you actually wanted.
 */
export const fontSize = {
  '2xs': '11px',
  xs: '12px',
  sm: '13px',
  md: '14px',
  lg: '16px',
  xl: '18px',
  '2xl': '20px',
  '3xl': '24px',
  '4xl': '30px',
  '5xl': '36px',
} as const

export const fontWeight = {
  regular: '400',
  medium: '500',
  semibold: '600',
  bold: '700',
} as const

/** Unitless, so they scale with font-size. `tight` is for table rows. */
export const lineHeight = {
  tight: '1.2',
  snug: '1.35',
  normal: '1.5',
  relaxed: '1.7',
} as const

export const letterSpacing = {
  tight: '-0.01em',
  normal: '0',
  wide: '0.02em',
  wider: '0.06em',
} as const

/**
 * `mono` is not decorative here: content hashes, AcoustID fingerprints,
 * MusicBrainz IDs and sample rates all need tabular alignment to be scannable.
 */
export const fontFamily = {
  sans: `system-ui, -apple-system, 'Segoe UI', Roboto, 'Helvetica Neue', Arial, sans-serif`,
  mono: `ui-monospace, 'SF Mono', 'JetBrains Mono', 'Fira Code', Menlo, Consolas, monospace`,
} as const

export const radius = {
  none: '0px',
  xs: '2px',
  sm: '4px',
  md: '6px',
  lg: '8px',
  xl: '12px',
  full: '9999px',
} as const

export const borderWidth = {
  none: '0px',
  thin: '1px',
  thick: '2px',
} as const

/**
 * Row heights are first-class tokens, not incidental padding outcomes. A
 * virtualized table needs to know its row height as a number before it renders
 * anything, so the density modes are fixed values rather than computed ones.
 */
export const density = {
  rowCompact: '28px',
  rowCozy: '36px',
  rowComfortable: '44px',
  headerHeight: '32px',
  toolbarHeight: '44px',
  /** Reserved for the future playback transport bar. See the plan's playback section. */
  transportHeight: '72px',
} as const

export const duration = {
  instant: '0ms',
  fast: '120ms',
  normal: '200ms',
  slow: '320ms',
} as const

export const easing = {
  standard: 'cubic-bezier(0.2, 0, 0.2, 1)',
  decelerate: 'cubic-bezier(0, 0, 0.2, 1)',
  accelerate: 'cubic-bezier(0.4, 0, 1, 1)',
} as const

/**
 * A single ordered stack. Components must use these rather than ad-hoc numbers,
 * because with hand-built layering (no portal library) there is nothing else
 * keeping a popover above a sticky table header.
 */
export const zIndex = {
  base: '0',
  raised: '10',
  sticky: '100',
  dropdown: '200',
  overlay: '300',
  modal: '400',
  popover: '500',
  toast: '600',
  tooltip: '700',
} as const

export const scales = {
  space,
  fontSize,
  fontWeight,
  lineHeight,
  letterSpacing,
  fontFamily,
  radius,
  borderWidth,
  density,
  duration,
  easing,
  zIndex,
} as const
