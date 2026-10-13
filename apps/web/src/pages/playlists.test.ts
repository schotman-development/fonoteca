import assert from 'node:assert/strict'
import { test } from 'node:test'
import { coverAlbums, moved, songsLine } from './playlists.ts'

test('an entry moves up and down by one place', () => {
  assert.deepEqual(moved(['a', 'b', 'c'], 1, -1), ['b', 'a', 'c'])
  assert.deepEqual(moved(['a', 'b', 'c'], 1, 1), ['a', 'c', 'b'])
})

test('a move off either end is no move at all', () => {
  assert.equal(moved(['a', 'b'], 0, -1), null)
  assert.equal(moved(['a', 'b'], 1, 1), null)
  assert.equal(moved([], 0, 1), null)
})

test('the cover takes four different albums from the top and skips songs with none', () => {
  const album = (albumId: string) => ({ albumId })
  const picked = coverAlbums([
    album('a'),
    null,
    album('a'),
    album('b'),
    album('c'),
    album('d'),
    album('e'),
  ])

  assert.deepEqual(
    picked.map((entry) => entry.albumId),
    ['a', 'b', 'c', 'd'],
  )
})

test('the count line says how many songs have no copy, and only when some have none', () => {
  assert.equal(songsLine(12, 12, '48:12'), '12 songs · 48:12')
  assert.equal(songsLine(1, 0, null), '1 song · 1 not in the library')
})
