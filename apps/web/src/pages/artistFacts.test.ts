import assert from 'node:assert/strict'
import { test } from 'node:test'
import { countryName, lifeSpan } from './artistFacts.ts'

const span = (beganYear: number | null, endedYear: number | null, ended: boolean) =>
  lifeSpan({ beganYear, endedYear, ended })

test('a closed life span prints both years', () => {
  assert.equal(span(1908, 1989, true), '1908–1989')
})

test('a living artist keeps a trailing dash rather than a bare year', () => {
  assert.equal(span(1957, null, false), '1957–')
})

// The one that reads as its opposite when `ended` is ignored: a group everybody
// knows split up, that nobody has dated.
test('ended with no year says so instead of reading as still active', () => {
  assert.equal(span(1962, null, true), '1962–?')
})

test('an end with no beginning still prints', () => {
  assert.equal(span(null, 1989, true), '–1989')
})

test('nothing known prints nothing', () => {
  assert.equal(span(null, null, false), null)
  assert.equal(span(null, null, true), null)
})

// Pinned to English. Unpinned this asserts the runtime's default locale, which
// is "Österreich" on a German machine — green here, red in CI, for a reason that
// has nothing to do with the code.
test('a country code becomes a country name', () => {
  assert.equal(countryName('AT', 'en'), 'Austria')
  assert.equal(countryName('gb', 'en'), 'United Kingdom')
})

// MusicBrainz's own non-ISO codes, and anything malformed, come back unchanged
// rather than throwing out of a render.
test('a code Intl does not know comes back as itself', () => {
  assert.equal(countryName('XW', 'en'), 'XW')
  assert.equal(countryName('Worldwide', 'en'), 'Worldwide')
  assert.equal(countryName(null), null)
})
