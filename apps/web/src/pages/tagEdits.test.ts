import assert from 'node:assert/strict'
import { test } from 'node:test'

import {
  cellKey,
  changesOf,
  type Draft,
  firstProblem,
  nameProblem,
  problem,
  setAll,
  shown,
} from './tagEdits.ts'

test('only what a person touched is sent, album first', () => {
  const draft: Draft = new Map([
    [cellKey('b', 'TITLE'), { kind: 'set', value: 'So What' }],
    [cellKey(null, 'ALBUM'), { kind: 'set', value: 'Kind of Blue' }],
    [cellKey('a', 'GENRE'), { kind: 'none' }],
    [cellKey('a', 'TITLE'), { kind: 'reset' }],
  ])

  assert.deepEqual(changesOf(draft), [
    { file: null, field: 'ALBUM', value: 'Kind of Blue', reset: false },
    { file: 'a', field: 'GENRE', value: null, reset: false },
    { file: 'a', field: 'TITLE', value: null, reset: true },
    { file: 'b', field: 'TITLE', value: 'So What', reset: false },
  ])
})

test('a value is shown from the draft, else from what the write would put in the file', () => {
  assert.equal(shown({ value: 'Catalogue' }, undefined), 'Catalogue')
  assert.equal(shown({ value: 'Catalogue' }, { kind: 'set', value: 'Mine' }), 'Mine')
  assert.equal(shown({ value: 'Catalogue' }, { kind: 'none' }), '')
  assert.equal(shown({ value: null }, { kind: 'reset' }), '')
})

test('the all-files row sets every file', () => {
  const draft = setAll(new Map(), ['a', 'b'], 'GENRE', { kind: 'set', value: 'Jazz' })

  assert.equal(draft.size, 2)
  assert.deepEqual(draft.get(cellKey('b', 'GENRE')), { kind: 'set', value: 'Jazz' })
})

test('a field of one’s own needs a name every container carries as one', () => {
  const here = ['TITLE', 'Occasion']

  assert.equal(nameProblem('Mood note', here), null)
  assert.equal(nameProblem('REPLAYGAIN_TRACK_GAIN', here), null)
  assert.notEqual(nameProblem('occasion', here), null)
  assert.notEqual(nameProblem('Mood', here), null)
  assert.notEqual(nameProblem('TT2', here), null)
  assert.notEqual(nameProblem('MusicBrainz Album Id', here), null)
  assert.notEqual(nameProblem('Lyrics:eng', here), null)
  assert.notEqual(nameProblem('1st', here), null)
  assert.notEqual(nameProblem('Publisher', here), null)
  assert.notEqual(nameProblem('Bpm', here), null)
  assert.notEqual(nameProblem('info.IART', here), null)
})

test('numbers are whole and in range, and text is not blank', () => {
  assert.equal(problem('YEAR', '1959'), null)
  assert.notEqual(problem('YEAR', '1959-10-12'), null)
  assert.notEqual(problem('TRACKNUMBER', '0'), null)
  assert.notEqual(problem('TRACKNUMBER', '1000'), null)
  assert.notEqual(problem('TITLE', '  '), null)
  assert.notEqual(problem('TITLE', 'two\nlines'), null)
  assert.equal(problem('COMMENT', 'two\nlines'), null)

  assert.match(
    firstProblem(new Map([[cellKey('a', 'YEAR'), { kind: 'set', value: 'soon' }]])) ?? '',
    /^Year: /,
  )
  assert.equal(firstProblem(new Map([[cellKey('a', 'YEAR'), { kind: 'none' }]])), null)
})
