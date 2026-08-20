/**
 * The candidate filter.
 *
 * **It filters what has already been gathered. It does not search MusicBrainz.**
 * The mirror runs with no Solr on purpose and `IMusicBrainzCatalogue` has no
 * search method, so a box that appeared to query MusicBrainz would be promising
 * something the architecture will not deliver. `CandidateSearch` says as much on
 * screen.
 *
 * This is a function rather than a prop on a component, and that is the whole
 * design: the count announced in the live region and the rows actually on screen
 * must be the same number, and the only way to guarantee that while the
 * components stay presentation-only is for the caller's `filter` and the
 * caller's count to go through one predicate. `useDeferredValue` stays the
 * caller's too — a component that holds no state cannot own it.
 */

export type CandidateSearchFields = {
  readonly title: string
  readonly artist?: string
  readonly year?: number | string
  readonly country?: string
  readonly formats?: string
  /** Anything else worth matching — label, catalogue number, disambiguation. */
  readonly extra?: readonly string[]
}

/**
 * Case-folded and stripped of diacritics, so "bela fleck" finds *Béla Fleck* and
 * "dvorak" finds *Dvořák*. A library catalogued from MusicBrainz is full of
 * names nobody is going to type accurately.
 */
function fold(value: string): string {
  return value
    .normalize('NFD')
    .replace(/\p{Diacritic}/gu, '')
    .toLowerCase()
}

function haystack(fields: CandidateSearchFields): string {
  return fold(
    [
      fields.title,
      fields.artist,
      fields.year,
      fields.country,
      fields.formats,
      ...(fields.extra ?? []),
    ]
      .filter((part) => part != null && part !== '')
      .join(' '),
  )
}

/**
 * Every whitespace-separated token must appear somewhere, in any order.
 *
 * A plain substring match would not find *Ella in Berlin: Mack the Knife* from
 * "ella berlin", and that is the ordinary way a person types half a title they
 * half remember. An empty query matches everything.
 */
export function matchesQuery(fields: CandidateSearchFields, query: string): boolean {
  const tokens = fold(query).split(/\s+/).filter(Boolean)
  if (tokens.length === 0) return true

  const hay = haystack(fields)
  return tokens.every((token) => hay.includes(token))
}

/** `matchesQuery` over a list. Returns the same array when the query is empty. */
export function filterCandidates<T>(
  items: readonly T[],
  query: string,
  fields: (item: T) => CandidateSearchFields,
): readonly T[] {
  if (query.trim() === '') return items
  return items.filter((item) => matchesQuery(fields(item), query))
}
