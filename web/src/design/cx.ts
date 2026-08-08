/**
 * The class-name joiner.
 *
 * Every primitive in this directory builds its class list the same way: one
 * module class, then a variant class, then whatever the caller passed. Two of
 * those three are routinely absent, and the honest expressions for that
 * (`` `${a} ${b ?? ''}` ``, `[a, b].filter(Boolean).join(' ')`) either leave a
 * trailing space in the DOM or are rewritten slightly differently in each of
 * thirty-odd files.
 *
 * So: one function, no dependency, no `clsx`. It takes only strings and falsy
 * values on purpose — objects and arrays are the part of `clsx` that invites a
 * component to compute its classes out of a data structure, and a primitive
 * that needs that is a primitive whose variants are not enumerated.
 */

export type ClassPart = string | false | null | undefined

export function cx(...parts: ClassPart[]): string {
  let out = ''
  for (const part of parts) {
    // Skips `''` as well as false/null/undefined: an absent CSS-module class
    // is `undefined` at runtime, and a `styles.missing` typo is `undefined`
    // too, so neither may contribute a stray separator.
    if (!part) continue
    out = out === '' ? part : `${out} ${part}`
  }
  return out
}
