/**
 * The single source of truth for CSS custom property names.
 *
 * Both the emitted stylesheet and the typed `ref` object are derived from this
 * function, so a name can never drift between what CSS defines and what
 * TypeScript claims exists.
 */

export type TokenTree = { readonly [key: string]: string | TokenTree }

/** `{ fontSize: { '2xs': '11px' } }` → `--font-size-2xs` */
export type VarRefs<T> = {
  readonly [K in keyof T]: T[K] extends string ? string : VarRefs<T[K]>
}

const kebab = (segment: string): string =>
  segment.replace(/([a-z0-9])([A-Z])/g, '$1-$2').toLowerCase()

export const varName = (path: readonly string[]): string => `--${path.map(kebab).join('-')}`

/** Depth-first flatten to `{ '--color-text-primary': '#212529' }`. */
export function flatten(tree: TokenTree, prefix: readonly string[] = []): Record<string, string> {
  const out: Record<string, string> = {}
  for (const [key, value] of Object.entries(tree)) {
    const path = [...prefix, key]
    if (typeof value === 'string') {
      out[varName(path)] = value
    } else {
      Object.assign(out, flatten(value, path))
    }
  }
  return out
}

/** Mirrors the token tree's shape, with every leaf replaced by `var(--name)`. */
export function toVarRefs<T extends TokenTree>(
  tree: T,
  prefix: readonly string[] = [],
): VarRefs<T> {
  const out: Record<string, unknown> = {}
  for (const [key, value] of Object.entries(tree)) {
    const path = [...prefix, key]
    out[key] = typeof value === 'string' ? `var(${varName(path)})` : toVarRefs(value, path)
  }
  return out as VarRefs<T>
}
