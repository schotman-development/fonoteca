/**
 * What the two catalogue lists keep in the address bar, and why it is not
 * component state.
 *
 * Sorting the artists by holdings, opening one and pressing Back landed on an
 * alphabetical list again — the order lived in a `useState`, and navigating to
 * the artist unmounted the component holding it. There was never anything to
 * restore. Held in the URL it is restored by the history entry itself, survives
 * a reload, and can be sent to somebody. It is the reason `filesRoute` already
 * gives for keeping the browsed folder there rather than in the page.
 *
 * **Pure — no React, no router, no `api.ts`** — so it runs under `node --test`,
 * the rule `seating.ts` and `files.ts` both state. That is also what lets the
 * option lists have one home: the pages render them into a `<select>` and
 * `routes.tsx` validates arriving URLs against them, so a third sort cannot
 * reach the control and miss the validator. A sort that is offered but not
 * validated is coerced away on the next navigation, which looks exactly like
 * the bug this file exists to fix.
 */

/**
 * The orders worth having for artists, and the reason there is not a fourth.
 *
 * Alphabetical is how you find somebody you already have in mind; by holdings
 * is how you find out who this library is actually *about*, which on a
 * collection assembled over years is rarely who you would guess. Holdings two
 * ways, because songs alone let one large box set outrank a shelf of records.
 * Everything else — by type, by year of first release — is a filter wearing a
 * sort's clothes, and neither is a column here.
 *
 * `tracks` keeps its name in the address bar and the API; it counts songs.
 */
export const ARTIST_SORTS = [
  ['name', 'Name'],
  ['tracks', 'Most songs'],
  ['albums', 'Most albums'],
] as const

export type ArtistSort = (typeof ARTIST_SORTS)[number][0]

/**
 * Which names count as artists here.
 *
 * The union the catalogue can reach a track through — credit lines, conductors,
 * ensembles and the composer of the work — is 2,860 names on the author's
 * library, of which 2,157 are songwriters and lyricists with one track each.
 * That is the right answer to "whose page can I reach this recording from" and a
 * useless front page. The default is the sleeve: who the album is *by*.
 */
export const ARTIST_SCOPES = [
  ['album', 'Album artists'],
  ['all', 'Everyone credited'],
  ['following', 'Following'],
] as const

export type ArtistScope = (typeof ARTIST_SCOPES)[number][0]

/**
 * The four orders worth having for albums.
 *
 * Title is how you find an album you already have in mind. Artist is what turns
 * an alphabetical soup of titles back into a shelf, since a person's records
 * sit together nowhere else on this screen. Year is the one that answers a
 * question nothing else here does — what this library is made of, decade by
 * decade — and it is worth remembering the year came from MusicBrainz's dating
 * of *this pressing*, so a remaster sorts as the remaster it is.
 *
 * Recently added is the year's opposite and the reason both are spelled out
 * rather than sharing a "Newest first": a 1979 record ripped this morning is
 * the oldest album on one and the newest on the other, and which of those
 * somebody meant is not guessable from the label.
 */
export const RELEASE_SORTS = [
  ['title', 'Title'],
  ['artist', 'Artist'],
  ['year', 'Released, newest first'],
  ['added', 'Recently added'],
] as const

export type ReleaseSort = (typeof RELEASE_SORTS)[number][0]

/** The album list's orders, by track; within an album, the files' order in its folder. */
export const TRACK_SORTS = [
  ['title', 'Title'],
  ['artist', 'Artist'],
  ['album', 'Album'],
  ['added', 'Recently added'],
] as const

export type TrackSort = (typeof TRACK_SORTS)[number][0]

/** By compositions rather than songs: a composer is what they wrote. */
export const COMPOSER_SORTS = [
  ['name', 'Name'],
  ['compositions', 'Most compositions'],
] as const

export type ComposerSort = (typeof COMPOSER_SORTS)[number][0]

/** Most recorded is the one that finds the standards: the pieces held in several performances. */
export const COMPOSITION_SORTS = [
  ['title', 'Title'],
  ['recorded', 'Most recorded'],
  ['composer', 'Composer'],
] as const

export type CompositionSort = (typeof COMPOSITION_SORTS)[number][0]

/**
 * The default of each, named once.
 *
 * They are left *out* of the URL rather than written into it, for the reason
 * both lists already leave them out of the request: the absence of a sort is
 * what the endpoint documents as sort-by-name, so spelling it out would put a
 * parameter in the address bar meaning "do what you were going to do anyway".
 */
export const ARTIST_DEFAULTS = { sort: 'name', scope: 'album' } as const
export const RELEASE_DEFAULT_SORT = 'title'

export type ArtistListSearch = {
  readonly query?: string | undefined
  readonly sort?: ArtistSort | undefined
  readonly scope?: ArtistScope | undefined
}

export type ReleaseListSearch = {
  readonly query?: string | undefined
  readonly sort?: ReleaseSort | undefined
}

/** The paged lists also keep their page, so Back from an album lands where it left. */
export type PagedListSearch<T extends string> = {
  readonly query?: string | undefined
  readonly sort?: T | undefined
  readonly page?: number | undefined
}

export type ComposerListSearch = {
  readonly query?: string | undefined
  readonly sort?: ComposerSort | undefined
}

/**
 * The value if the list offers it, and nothing at all if it does not.
 *
 * A search parameter is whatever was typed into the address bar, and the two
 * lists share the key `sort` while disagreeing about what it may hold — so
 * `?sort=year` is a real album order and meaningless on the artists page.
 * Narrowing to the page's own options is what makes that arrive as "no sort
 * chosen" rather than as a value the `<select>` cannot show and the API would
 * reject.
 */
function oneOf<T extends string>(
  options: ReadonlyArray<readonly [T, string]>,
  value: unknown,
): T | undefined {
  return options.find(([option]) => option === value)?.[0]
}

/**
 * An empty filter is the absence of a filter rather than a filter for nothing,
 * so it is dropped instead of being carried as `?query=`.
 */
function text(value: unknown): string | undefined {
  return typeof value === 'string' && value !== '' ? value : undefined
}

export function artistListSearch(search: Record<string, unknown>): ArtistListSearch {
  return {
    query: text(search.query),
    sort: oneOf(ARTIST_SORTS, search.sort),
    scope: oneOf(ARTIST_SCOPES, search.scope),
  }
}

export function releaseListSearch(search: Record<string, unknown>): ReleaseListSearch {
  return {
    query: text(search.query),
    sort: oneOf(RELEASE_SORTS, search.sort),
  }
}

/** A whole number from 2 up; the first page is the absence of one. */
function page(value: unknown): number | undefined {
  const number = typeof value === 'string' ? Number(value) : value
  return typeof number === 'number' && Number.isInteger(number) && number > 1 ? number : undefined
}

export function trackListSearch(search: Record<string, unknown>): PagedListSearch<TrackSort> {
  return {
    query: text(search.query),
    sort: oneOf(TRACK_SORTS, search.sort),
    page: page(search.page),
  }
}

export function composerListSearch(search: Record<string, unknown>): ComposerListSearch {
  return {
    query: text(search.query),
    sort: oneOf(COMPOSER_SORTS, search.sort),
  }
}

export function compositionListSearch(
  search: Record<string, unknown>,
): PagedListSearch<CompositionSort> {
  return {
    query: text(search.query),
    sort: oneOf(COMPOSITION_SORTS, search.sort),
    page: page(search.page),
  }
}
