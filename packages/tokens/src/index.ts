/**
 * @fonoteca/tokens — the design system's vocabulary.
 *
 * Two consumers, one source:
 *
 * - `dist/tokens.css` declares every token as a CSS custom property. This is
 *   what components actually use, from their `.module.css` files.
 * - `ref` is the same tree with `var(--…)` strings at the leaves, for the cases
 *   that genuinely need a token in JavaScript — a virtualizer that must know its
 *   row height before render, or an inline style computed from data.
 *
 * Reach for `ref` sparingly. Styling belongs in CSS; this exists for the
 * measurements that cannot wait for the cascade.
 */

export type { TokenTree, VarRefs } from './cssVars.ts'
export { flatten, toVarRefs, varName } from './cssVars.ts'
export {
  absolute,
  amber,
  blue,
  gray,
  green,
  primitives,
  red,
  violet,
} from './primitives.ts'
export * from './scales.ts'
export { scales } from './scales.ts'
export type { SemanticTokens, StatusColors, ThemeName } from './semantic.ts'
export { dark, light, themes } from './semantic.ts'

import { toVarRefs } from './cssVars.ts'
import { scales } from './scales.ts'
import { light } from './semantic.ts'

/**
 * Typed `var(--…)` references.
 *
 * Colour and shadow refs are derived from the `light` theme purely for their
 * SHAPE — the emitted variable is theme-aware, so `ref.color.text.primary`
 * resolves to whatever the active theme defines. `light` and `dark` are typed
 * identically, so either would produce the same names.
 */
export const ref = {
  ...toVarRefs(scales),
  ...toVarRefs(light),
} as const

/** Numeric row heights, for virtualization maths that cannot read the cascade. */
export const rowHeightPx = {
  compact: 28,
  cozy: 36,
  comfortable: 44,
} as const

export type Density = keyof typeof rowHeightPx
