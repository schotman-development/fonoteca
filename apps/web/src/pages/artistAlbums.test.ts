import assert from 'node:assert/strict'
import { test } from 'node:test'

import { albumsOf, fromFolder } from './artistAlbums.ts'

const track = (over: Partial<Parameters<typeof albumsOf>[0][number]>) => ({
  recordingId: crypto.randomUUID(),
  title: 'A track',
  workTitle: null,
  duration: '3:20',
  roles: ['billed'],
  album: null,
  folder: 'An Artist/An Album',
  files: [{ path: 'An Artist/An Album/01.flac', sizeBytes: 1 }],
  ...over,
})

const album = (over: Record<string, unknown> = {}) => ({
  releaseId: 'r-1',
  mbid: 'm-1',
  title: 'Rumours',
  year: 1977,
  ...over,
})

test('a year is read off the folder however it was written', () => {
  assert.deepEqual(fromFolder('X/Rumours (1977)'), { title: 'Rumours', year: 1977 })
  assert.deepEqual(fromFolder('X/[1977] Rumours'), { title: 'Rumours', year: 1977 })
  assert.deepEqual(fromFolder('X/Rumours - 1977'), { title: 'Rumours', year: 1977 })
  assert.deepEqual(fromFolder('X/1977 Rumours'), { title: 'Rumours', year: 1977 })
})

test('a number that is not a year stays in the title', () => {
  assert.deepEqual(fromFolder('X/Blink-182'), { title: 'Blink-182', year: null })
  assert.deepEqual(fromFolder('X/Live At Pompeii'), { title: 'Live At Pompeii', year: null })
})

test('tracks of one release become one album, files and roles summed', () => {
  const albums = albumsOf([
    track({ album: album(), roles: ['billed'] }),
    track({
      album: album(),
      roles: ['conductor'],
      files: [
        { path: 'a.flac', sizeBytes: 1 },
        { path: 'a.mp3', sizeBytes: 1 },
      ],
    }),
  ])

  assert.equal(albums.length, 1)
  assert.deepEqual(albums[0]?.roles, ['billed', 'conductor'])
  assert.equal(albums[0]?.trackCount, 2)
  assert.equal(albums[0]?.fileCount, 3)
  assert.equal(albums[0]?.releaseId, 'r-1')
})

test('an unattributed track groups by its folder and says the year came from there', () => {
  const albums = albumsOf([track({ album: null, folder: 'An Artist/Tusk (1979)' })])

  assert.equal(albums[0]?.releaseId, null)
  assert.equal(albums[0]?.title, 'Tusk')
  assert.equal(albums[0]?.year, 1979)
  assert.equal(albums[0]?.yearFromFolder, true)
})

test('the folder never overrules a year the catalogue has', () => {
  const albums = albumsOf([track({ album: album({ year: 2015 }), folder: 'X/Rumours (1977)' })])

  assert.equal(albums[0]?.year, 2015)
  assert.equal(albums[0]?.yearFromFolder, false)
})

test('the folder fills a year the catalogue has not', () => {
  const albums = albumsOf([track({ album: album({ year: null }), folder: 'X/Rumours (1977)' })])

  assert.equal(albums[0]?.year, 1977)
  assert.equal(albums[0]?.yearFromFolder, true)
})

test('the discs of one rip are one folder, not two', () => {
  const albums = albumsOf([
    track({ album: null, folder: 'X/Rumours (1977)/CD2' }),
    track({ album: null, folder: 'X/Rumours (1977)/CD1' }),
  ])

  assert.equal(albums.length, 1)
  assert.equal(albums[0]?.title, 'Rumours')
  assert.deepEqual(albums[0]?.folders, ['X/Rumours (1977)'])
})

test('genuinely different folders under one release are both kept', () => {
  const albums = albumsOf([
    track({ album: album(), folder: 'X/Rumours (Remaster)' }),
    track({ album: album(), folder: 'X/Rumours' }),
  ])

  assert.deepEqual(albums[0]?.folders, ['X/Rumours', 'X/Rumours (Remaster)'])
})

test('the discography is ordered by year, undated last', () => {
  const albums = albumsOf([
    track({ album: album({ releaseId: 'c', title: 'C', year: null }) }),
    track({ album: album({ releaseId: 'b', title: 'B', year: 1979 }) }),
    track({ album: album({ releaseId: 'a', title: 'A', year: 1977 }) }),
  ])

  assert.deepEqual(
    albums.map((a) => a.title),
    ['A', 'B', 'C'],
  )
})

test('leftovers fold into the album they are missing from', () => {
  const albums = albumsOf([
    track({ album: album(), folder: 'X/Rumours (1977)', roles: ['billed'] }),
    track({ album: album(), folder: 'X/Rumours (1977)', roles: ['billed'] }),
    track({ album: null, folder: 'X/Rumours (1977)', roles: ['composer'] }),
  ])

  assert.equal(albums.length, 1)
  assert.equal(albums[0]?.releaseId, 'r-1')
  assert.equal(albums[0]?.trackCount, 3)
  assert.equal(albums[0]?.unplaced, 1)
  // The roles came with them.
  assert.deepEqual(albums[0]?.roles, ['billed', 'composer'])
})

test('a release that only reprints one of the folder does not swallow it', () => {
  const albums = albumsOf([
    // A compilation with one track in somebody else's album folder, and the
    // twenty the passes could not place. The compilation is the folder's only
    // claimant and is emphatically not its album.
    track({ album: album({ releaseId: 'comp', title: 'Free Album 2017' }), folder: 'X/Vienna' }),
    ...Array.from({ length: 20 }, () => track({ album: null, folder: 'X/Vienna' })),
  ])

  assert.equal(albums.length, 2)
  assert.equal(albums.find((a) => a.releaseId === 'comp')?.trackCount, 1)
  assert.equal(albums.find((a) => a.releaseId === null)?.trackCount, 20)
})

test('the bigger of two claimants takes the leftovers', () => {
  const albums = albumsOf([
    ...Array.from({ length: 9 }, () =>
      track({ album: album({ releaseId: 'lp', title: 'The LP' }), folder: 'X/The LP' }),
    ),
    track({ album: album({ releaseId: 'comp', title: 'Hits' }), folder: 'X/The LP' }),
    track({ album: null, folder: 'X/The LP' }),
  ])

  assert.equal(albums.find((a) => a.releaseId === 'lp')?.unplaced, 1)
  assert.equal(
    albums.find((a) => a.releaseId === null),
    undefined,
  )
})

test('a folder two releases claim equally is left standing on its own', () => {
  const albums = albumsOf([
    track({ album: album({ releaseId: 'a', title: 'A' }), folder: 'X/Deluxe (1977)' }),
    track({ album: album({ releaseId: 'a', title: 'A' }), folder: 'X/Deluxe (1977)' }),
    track({ album: album({ releaseId: 'b', title: 'B' }), folder: 'X/Deluxe (1977)' }),
    track({ album: album({ releaseId: 'b', title: 'B' }), folder: 'X/Deluxe (1977)' }),
    track({ album: null, folder: 'X/Deluxe (1977)' }),
  ])

  assert.equal(albums.length, 3)
  assert.equal(albums.filter((a) => a.releaseId === null).length, 1)
})

test('a folder nothing was placed from stays an album of its own', () => {
  const albums = albumsOf([track({ album: null, folder: 'X/Tusk (1979)' })])

  assert.equal(albums.length, 1)
  assert.equal(albums[0]?.unplaced, 1)
})

test('a year inside a longer parenthetical is not the album year', () => {
  // The bracket has to close on the year itself. These are ripper conventions,
  // and the earlier rule ate the opening bracket and left the closing one.
  assert.deepEqual(fromFolder('X/Off the Wall (2015 Remaster)'), {
    title: 'Off the Wall (2015 Remaster)',
    year: null,
  })
  assert.deepEqual(fromFolder('X/1917 (Original Score)'), {
    title: '1917 (Original Score)',
    year: null,
  })
  // The library's own convention, which the same rule has to keep: a leading
  // year before a dash is a year here, and `2001 - A Space Odyssey` losing its
  // name is the price. Measured — five real folders against nothing.
  assert.deepEqual(fromFolder('X/2019 - Texas Honey'), {
    title: 'Texas Honey',
    year: 2019,
  })
  assert.deepEqual(fromFolder('X/Ballads & Blues 1982-1994 (1994)'), {
    title: 'Ballads & Blues 1982-1994',
    year: 1994,
  })
})

test("a rip's note about itself is not part of the album title", () => {
  assert.deepEqual(fromFolder('X/Beethoven: Symphonies Nos. 1-9 (2020) [FLAC 24-44.1]'), {
    title: 'Beethoven: Symphonies Nos. 1-9',
    year: 2020,
  })
  assert.deepEqual(fromFolder('X/Live From Nowhere in Particular (Live)'), {
    title: 'Live From Nowhere in Particular (Live)',
    year: null,
  })
})
