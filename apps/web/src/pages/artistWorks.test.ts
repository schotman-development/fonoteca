/**
 * The Works tab, built from the movements a composer's tracks perform.
 *
 * `node --test` with Node's own type stripping, like the other page rules.
 */

import assert from 'node:assert/strict'
import { test } from 'node:test'

import { pieceOf, worksOf } from './artistWorks.ts'

const MRAVINSKY = 'Leningrad Philharmonic, Yevgeny Mravinsky'

function track(
  workTitle: string | null,
  performers: string | null,
  album: { albumId: string; title: string; year: number | null } | null,
  roles: string[] = ['composer'],
) {
  return {
    recordingId: crypto.randomUUID(),
    title: workTitle ?? 'Untitled',
    workTitle,
    duration: null,
    roles,
    album:
      album == null
        ? null
        : {
            ...album,
            mbid: null,
            editionId: null,
            coverReleaseId: null,
            artist: null,
            billed: null,
            band: null,
          },
    folder: 'Tchaikovsky/Loose',
    files: [],
    performers,
  }
}

const SYMPHONIES = { albumId: 'r1', title: 'Symphonies Nos. 4, 5 & 6', year: 1961 }
const ARGERICH = { albumId: 'r2', title: 'Piano Concerto No. 1', year: 1994 }
const HOROWITZ = { albumId: 'r3', title: 'Piano Concerto No. 1', year: 1941 }

test('movements of one piece on one album are one recording', () => {
  const works = worksOf([
    track('Symphony No. 4 in F minor, op. 36: I. Andante sostenuto', MRAVINSKY, SYMPHONIES),
    track('Symphony No. 4 in F minor, op. 36: II. Andantino', MRAVINSKY, SYMPHONIES),
  ])

  assert.equal(works.length, 1)
  assert.equal(works[0]?.title, 'Symphony No. 4 in F minor, op. 36')
  assert.deepEqual(works[0]?.performances, [
    { performers: MRAVINSKY, album: SYMPHONIES.title, albumId: 'r1', year: 1961, tracks: 2 },
  ])
})

test('the most recorded piece comes first, and its recordings run oldest first', () => {
  const concerto = 'Piano Concerto No. 1 in B-flat minor, op. 23'
  const works = worksOf([
    track('Symphony No. 4 in F minor, op. 36: I. Andante sostenuto', MRAVINSKY, SYMPHONIES),
    track(`${concerto}: I. Allegro non troppo`, 'Martha Argerich', ARGERICH),
    track(`${concerto}: I. Allegro non troppo`, 'Vladimir Horowitz', HOROWITZ),
  ])

  assert.deepEqual(
    works.map((work) => work.title),
    [concerto, 'Symphony No. 4 in F minor, op. 36'],
  )
  assert.deepEqual(
    works[0]?.performances.map((performance) => performance.year),
    [1941, 1994],
  )
})

test('a track they did not write, or with no work, is not a piece of theirs', () => {
  assert.deepEqual(
    worksOf([
      track('Symphony No. 4 in F minor, op. 36: I. Andante sostenuto', MRAVINSKY, SYMPHONIES, [
        'conductor',
      ]),
      track(null, MRAVINSKY, SYMPHONIES),
    ]),
    [],
  )
})

test('a work in one part is its own piece', () => {
  assert.equal(pieceOf('1812 Overture, op. 49'), '1812 Overture, op. 49')
  assert.equal(pieceOf('Swan Lake, op. 20: Act II: Scene'), 'Swan Lake, op. 20')
})
