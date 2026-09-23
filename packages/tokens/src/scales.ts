/**
 * Theme-independent scales: the same in light and dark.
 *
 * Tuned for COMFORT over density. Most screens here are read rather than
 * scanned, and sessions are long and spent deciding things, so type starts a
 * step above the usual data-application sizes and everything breathes; the
 * library table still fits a screen of rows at `compact`. The spacing scale is
 * named in literal pixels so there is never any ambiguity about what `space-6`
 * means.
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
 * Type scale. `md` (15px) is body text; `sm` (14px) is the table-cell default,
 * and nothing anybody reads goes below `2xs` (12px). The steps below `lg` are
 * deliberately 1px apart — at these sizes a 1.25 ratio jumps straight past the
 * size you actually wanted.
 */
export const fontSize = {
  '2xs': '12px',
  xs: '13px',
  sm: '14px',
  md: '15px',
  lg: '17px',
  xl: '19px',
  '2xl': '22px',
  '3xl': '26px',
  '4xl': '32px',
  '5xl': '40px',
} as const

export const fontWeight = {
  regular: '400',
  medium: '500',
  semibold: '600',
  bold: '700',
} as const

/** Unitless, so they scale with font-size. `tight` is for table rows. */
export const lineHeight = {
  tight: '1.25',
  snug: '1.4',
  normal: '1.6',
  relaxed: '1.75',
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
  xs: '4px',
  sm: '6px',
  md: '10px',
  lg: '12px',
  xl: '16px',
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
  rowCompact: '32px',
  rowCozy: '40px',
  rowComfortable: '48px',
  headerHeight: '40px',
  /**
   * The one height every control shares — button, text input, native select,
   * switch, command-bar trigger, upload label — so a row of mixed controls
   * lines up without anybody arranging it. `controlSm` is the same rule one
   * step down, for controls that sit inside a table row or a dense toolbar.
   * Deliberately separate from the row heights: a table getting roomier is no
   * reason for every button in the application to grow with it.
   */
  control: '40px',
  controlSm: '32px',
  toolbarHeight: '52px',
  /** Reserved for the future playback transport bar. See the plan's playback section. */
  transportHeight: '72px',
  /**
   * Shell metrics, here for the same reason the row heights are: the sidebar's
   * width is a number the layout has to agree on in more than one place — the
   * grid track and the narrow-viewport breakpoint below which that track stops
   * being affordable.
   */
  sidebarWidth: '240px',
  /**
   * The reading width of the centred content column. Wide enough for the
   * catalogue grids to fit several tiles per row, narrow enough that a line of
   * prose on a 2560px monitor is still a line and not a paragraph-long ruler.
   */
  contentMaxWidth: '1120px',
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
