/**
 * The pure half of the album-matching screen.
 *
 * `node --test` with Node's own type stripping, matching `@fonoteca/tokens` —
 * no runner dependency, and nothing here imports React, a stylesheet or the API
 * client at runtime.
 *
 * The first case is a regression: sending the worklist's own id where the API
 * takes a media file id failed to deserialise *before* the handler ran, so the
 * endpoint's validation never saw it and the screen showed a bare 400. The
 * integration tests could not have caught it — they POST media file ids
 * directly and never meet the worklist's id format.
 */

import assert from 'node:assert/strict'
import { test } from 'node:test'

import {
  albumFolderOf,
  defaultSeating,
  driftMs,
  mediaFileIdOf,
  pairsFrom,
  queryFor,
  seatKey,
} from './seating.ts'

const GUID = '019fffe5-4be4-7d4a-8a4a-d6459a551424'

function question(id: string, kind = 'recording'): never {
  return {
    id,
    kind,
    reason: 'Unknown',
    subject: '01 Track.flac',
    folders: ['André Previn/Swan Lake/CD1'],
    files: 1,
    length: null,
    size: null,
    format: 'FLAC',
    // The generated OpenQuestion type is structural and exact; these tests only
    // need the three fields the functions read.
  } as never
}

test('a worklist id is unwrapped to the media file id the API takes', () => {
  assert.equal(mediaFileIdOf(question(`recording:${GUID}`)), GUID)
})

test('a component question has no media file id', () => {
  assert.equal(mediaFileIdOf(question('release:638700000000000000', 'release')), null)
})

test('an id with nothing after the prefix is not a media file id', () => {
  assert.equal(mediaFileIdOf(question('recording:')), null)
})

test('pairs carry the bare guid, never the recording: prefix', () => {
  const files = [question(`recording:${GUID}`)]
  const pairs = pairsFrom(files, new Map([[`recording:${GUID}`, seatKey(2, 7)]]))

  assert.deepEqual(pairs, [{ file: GUID, disc: 2, position: 7 }])
})

test('an unseated file contributes no pair', () => {
  assert.deepEqual(pairsFrom([question(`recording:${GUID}`)], new Map()), [])
})

test('a seat whose file id cannot be unwrapped is dropped, not sent', () => {
  const files = [question('release:638700000000000000', 'release')]
  const pairs = pairsFrom(files, new Map([['release:638700000000000000', '1-1']]))

  assert.deepEqual(pairs, [])
})

function slot(position: number, heldBy: string | null = null): never {
  return { discNumber: 1, position, heldBy, title: `Track ${position}` } as never
}

test('with nothing filed, the default seating is plain track order', () => {
  const files = [question('recording:a'), question('recording:b')]
  const seats = defaultSeating(files, [slot(1), slot(2), slot(3)])

  assert.deepEqual(
    [...seats],
    [
      ['recording:a', '1-1'],
      ['recording:b', '1-2'],
    ],
  )
})

test('the leftover of an all-but-filed album is seated on the one gap, not on track 1', () => {
  const slots = [slot(1, '01.flac'), slot(2, '02.flac'), slot(3), slot(4, '04.flac')]
  const seats = defaultSeating([question('recording:c')], slots)

  assert.deepEqual([...seats], [['recording:c', '1-3']])
})

test('more open files than free positions leaves the extras unseated', () => {
  const files = [question('recording:a'), question('recording:b')]
  const seats = defaultSeating(files, [slot(1, '01.flac'), slot(2)])

  assert.deepEqual([...seats], [['recording:a', '1-2']])
})

test('drift is signed, in milliseconds, against the printed length', () => {
  assert.equal(driftMs('3:24', 202_000), 2_000)
  assert.equal(driftMs('3:24', 206_000), -2_000)
  assert.equal(driftMs('1:02:03', 3_723_000), 0)
})

test('drift is unknown rather than zero when either length is missing', () => {
  assert.equal(driftMs(null, 202_000), null)
  assert.equal(driftMs('3:24', null), null)
  assert.equal(driftMs('not a length', 202_000), null)
})

test('separate discs of one set collapse onto one album folder', () => {
  assert.equal(albumFolderOf('André Previn/Swan Lake/CD1'), 'André Previn/Swan Lake')
  assert.equal(albumFolderOf('André Previn/Swan Lake/CD2'), 'André Previn/Swan Lake')
  assert.equal(albumFolderOf('André Previn/Swan Lake'), 'André Previn/Swan Lake')
  assert.equal(albumFolderOf('Loose.flac'), 'Loose.flac')
  assert.equal(albumFolderOf(''), '')
})

test('the search seed drops what rippers put in brackets', () => {
  assert.equal(
    queryFor('Concertgebouworkest/Beethoven: Symphonies Nos. 1-9 (2020) [FLAC 24-44.1]'),
    'Concertgebouworkest Beethoven: Symphonies Nos. 1-9',
  )
})

test('the search seed of the library root is empty rather than a slash', () => {
  assert.equal(queryFor(''), '')
})
