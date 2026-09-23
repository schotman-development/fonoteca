/**
 * Primitive colour scales. These are raw values with no meaning attached —
 * nothing outside `semantic.ts` should reference them, and no component should
 * ever reference them at all. If you find yourself reaching for `sand[800]` in
 * a component, the semantic layer is missing a token.
 *
 * The status hues are Open Color (https://yeun.github.io/open-color/), chosen
 * because the scales are already tuned for consistent perceived lightness
 * across hues — which matters when status colours sit next to each other in a
 * table row. The neutrals and the accent are this project's own; see below.
 */

/**
 * The neutrals, and the one scale that is not Open Color. A cool blue-grey on
 * pure white is what made every screen read as a spreadsheet; a grey with a
 * little yellow in it, on a page that is not quite white, is the single change
 * that most makes a long session easier on the eye. Warm enough to notice
 * side by side, never enough to read as beige.
 */
export const sand = {
  50: '#fbfaf8',
  100: '#f5f3ef',
  200: '#efece6',
  300: '#e8e4dd',
  400: '#d9d4cb',
  500: '#bdb6aa',
  600: '#a8a195',
  700: '#6b665d',
  800: '#58534b',
  900: '#2b2823',
  950: '#23211e',
  1000: '#1a1916',
  /** Below the dark base, so `sunken` has somewhere to sit beneath it. */
  1100: '#131210',
} as const

/**
 * The accent: Open Color's blue with most of the saturation taken out. A fully
 * saturated fill is right for a single call to action and wrong for the dozen
 * selected rows, links and focus rings that share it — it becomes the loudest
 * thing on every screen.
 */
export const denim = {
  50: '#eef3f8',
  100: '#dfe8f2',
  200: '#c2d4e8',
  300: '#94b8de',
  400: '#6d9acb',
  500: '#4f80b5',
  600: '#3d70a6',
  700: '#2f6399',
  800: '#285684',
  900: '#20476d',
  950: '#183651',
} as const

export const green = {
  50: '#ebfbee',
  100: '#d3f9d8',
  200: '#b2f2bb',
  300: '#8ce99a',
  400: '#69db7c',
  500: '#51cf66',
  600: '#40c057',
  700: '#37b24d',
  800: '#2f9e44',
  900: '#2b8a3e',
  950: '#1a5928',
} as const

export const amber = {
  50: '#fff9db',
  100: '#fff3bf',
  200: '#ffec99',
  300: '#ffe066',
  400: '#ffd43b',
  500: '#fcc419',
  600: '#fab005',
  700: '#f59f00',
  800: '#f08c00',
  900: '#e67700',
  950: '#8a4b00',
} as const

export const red = {
  50: '#fff5f5',
  100: '#ffe3e3',
  200: '#ffc9c9',
  300: '#ffa8a8',
  400: '#ff8787',
  500: '#ff6b6b',
  600: '#fa5252',
  700: '#f03e3e',
  800: '#e03131',
  900: '#c92a2a',
  950: '#7d1a1a',
} as const

export const violet = {
  50: '#f3f0ff',
  100: '#e5dbff',
  200: '#d0bfff',
  300: '#b197fc',
  400: '#9775fa',
  500: '#845ef7',
  600: '#7950f2',
  700: '#7048e8',
  800: '#6741d9',
  900: '#5f3dc4',
  950: '#341f76',
} as const

export const absolute = {
  white: '#ffffff',
  black: '#000000',
  transparent: 'transparent',
} as const

export const primitives = {
  sand,
  denim,
  green,
  amber,
  red,
  violet,
  absolute,
} as const
