/**
 * What the two lists will accept out of the address bar.
 *
 * `node --test` with Node's own type stripping, matching `seating.ts` and
 * `files.ts` — nothing here imports React, a stylesheet or the API client.
 *
 * The case worth having is the third one: both lists spell their order `sort`,
 * and the two disagree about what it may hold. A URL carrying an album's order
 * onto the artists page has to arrive as "no order chosen" — narrowed to the
 * page's own options it does, and the `<select>` shows the default rather than
 * a blank control for a value it has no `<option>` for.
 */

import assert from 'node:assert/strict'
import { test } from 'node:test'

import { artistListSearch, releaseListSearch } from './listSearch.ts'

test('a chosen order and scope survive the round trip', () => {
  assert.deepEqual(artistListSearch({ query: 'karajan', sort: 'tracks', scope: 'following' }), {
    query: 'karajan',
    sort: 'tracks',
    scope: 'following',
  })

  assert.deepEqual(releaseListSearch({ query: 'off the wall', sort: 'year' }), {
    query: 'off the wall',
    sort: 'year',
  })
})

test('an empty filter is the absence of a filter', () => {
  assert.equal(artistListSearch({ query: '' }).query, undefined)
  assert.equal(releaseListSearch({ query: '' }).query, undefined)
  assert.equal(artistListSearch({}).query, undefined)
})

test("one list's order is not the other's", () => {
  // 'year' and 'added' are album orders; the artists page offers neither.
  assert.equal(artistListSearch({ sort: 'year' }).sort, undefined)
  assert.equal(artistListSearch({ sort: 'added' }).sort, undefined)

  // 'tracks' is an artist order, and 'scope' is not an album parameter at all.
  assert.equal(releaseListSearch({ sort: 'tracks' }).sort, undefined)
  assert.equal('scope' in releaseListSearch({ scope: 'following' }), false)
})

test('anything else is dropped rather than carried', () => {
  assert.deepEqual(artistListSearch({ sort: 'nonsense', scope: 42, query: null }), {
    query: undefined,
    sort: undefined,
    scope: undefined,
  })
})
