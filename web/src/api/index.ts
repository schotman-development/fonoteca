/**
 * The data layer's public surface: the fetch wrapper, the wire types and the
 * query hooks.
 *
 * `@/design` and `@/shell` each had a barrel and this did not, so screens were
 * about to reach into `@/api/queries` and `@/api/types` by file — which is how a
 * key factory ends up bypassed and two components stop sharing a cache entry.
 *
 * Wire types are re-exported with `export type *` rather than one name at a
 * time: `types.ts` is a hand-kept mirror of `app/schemas.py`, and a list here
 * would be a third copy to keep in step with both.
 */

export * from './client'
export type * from './types'
export * from './queries'
