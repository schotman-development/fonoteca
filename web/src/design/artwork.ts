/**
 * Placeholder artwork: a stable gradient and a pair of initials for anything
 * that has no cover image.
 *
 * The point of hashing rather than cycling is *stability*. A release that has
 * no cover still has to look like the same release in the shelf, in the grid
 * and in the drawer, across a refetch that reordered the list — an index-based
 * colour re-paints the whole wall the moment one row is filtered out, which
 * reads as the data having changed. The seed is therefore the entity's own
 * text, and the function is pure: same string in, same swatch out, forever.
 *
 * The algorithm is the design's own (`HASH`, design line 902) rather than
 * something better. It is not a hash anybody should trust for anything else —
 * `% 997` on a 31-multiplier walk — but reproducing it exactly is what keeps a
 * screenshot of the design and a screenshot of the app the same colours.
 */

import { EM_DASH } from '@/format'

/** The number of gradients in the ramp: `--art-0` … `--art-11` in tokens.css. */
export const ART_COUNT = 12

/**
 * The design's hash, verbatim, folded into the ramp — always 0…11 inclusive.
 *
 * `for…of` walks a string by CODE POINT, so an astral character arrives here as
 * a two-unit string and `charCodeAt(0)` reads its high surrogate. That is what
 * the design does, and it is kept: the value is arbitrary either way, and the
 * only property that matters is that it is the same arbitrary value every time.
 * Nothing throws — an empty seed hashes to 0, and a lone surrogate is a number
 * like any other.
 */
export function artIndex(seed: string): number {
  let h = 0
  for (const ch of seed) {
    h = (h * 31 + ch.charCodeAt(0)) % 997
  }
  return h % ART_COUNT
}

/**
 * The same answer as a CSS value: `var(--art-7)`.
 *
 * Returned as a `var()` reference rather than as a gradient string so the ramp
 * lives in exactly one place. A component writes this into a custom property
 * (`style={{ '--art': artGradientVar(seed) }}`) and its stylesheet reads it —
 * which keeps the one inline style in the layer down to a token reference, and
 * leaves re-skinning the ramp an edit of tokens.css alone.
 */
export function artGradientVar(seed: string): string {
  return `var(--art-${artIndex(seed)})`
}

/**
 * Which of the two initial rules to apply. The design has both and they are not
 * interchangeable:
 *
 * - `chars` — the first two characters of the text (design line 1101). This is
 *   what a RELEASE gets: `Kind of Blue` → `KI`. Taking word initials there
 *   would give `KOB`, which reads as an acronym for something.
 * - `words` — the first character of each word (design line 1114). This is what
 *   a PERSON gets: `Mark Knopfler` → `MK`. Taking the first two characters
 *   there would give `MA`, which reads as a different person.
 */
export type InitialsMode = 'words' | 'chars'

/**
 * Up to two initials for a placeholder, upper-cased.
 *
 * Both rules are code-point safe, which the design's `slice(0, 2)` is not: an
 * emoji or an astral glyph in a title would be cut in half and rendered as the
 * replacement character. Non-ASCII is otherwise untouched — `Björk` → `B`,
 * `Ólafur Arnalds` → `ÓA`, `D'Angelo` → `D` by word and `D'` by character,
 * exactly as the design renders them.
 *
 * An empty or blank string returns the empty string, NOT a placeholder glyph:
 * the caller decides whether an unnamed thing shows nothing or shows
 * `EM_DASH`, because in a 126px artwork tile an em dash is a legible answer and
 * in a 32px avatar it is a smudge. `initialsOrDash` below is the second of
 * those, for the callers that want it.
 *
 * Never throws, for any input.
 */
export function initialsOf(text: string, mode: InitialsMode): string {
  const trimmed = text.trim()
  if (trimmed === '') return ''

  const picked =
    mode === 'chars'
      ? [...trimmed].slice(0, 2)
      : trimmed
          .split(/\s+/u)
          .map((word) => [...word][0])
          .filter((ch): ch is string => ch !== undefined)
          .slice(0, 2)

  // `toUpperCase`, not `toLocaleUpperCase`: the locale-aware form maps a Turkish
  // `i` to `İ`, so the same library would draw different initials on two
  // machines. A placeholder must be the same everywhere.
  return picked.join('').toUpperCase()
}

/**
 * `initialsOf` with the unknown-value rule the rest of the app follows: an
 * unnameable entity gets the em dash from `@/format`, never an empty tile and
 * never an invented letter.
 */
export function initialsOrDash(text: string, mode: InitialsMode): string {
  return initialsOf(text, mode) || EM_DASH
}
