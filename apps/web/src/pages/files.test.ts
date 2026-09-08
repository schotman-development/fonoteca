/**
 * The path arithmetic behind the file manager.
 *
 * The rename cases are the point. Every other screen in this application
 * refuses to do damage by having nothing destructive on it; this one has a text
 * box next to a folder holding an album, and the failure mode is silent — the
 * entry moves, the row disappears, and the only trace is a scan that has since
 * discarded everything derived from those files.
 */

import assert from 'node:assert/strict'
import { test } from 'node:test'

import {
  artUrl,
  breadcrumbs,
  contentUrl,
  destinationFor,
  joinPath,
  matchSummary,
  parentOf,
  uploadName,
} from './files.ts'

test('breadcrumbs always start at the root', () => {
  assert.deepEqual(breadcrumbs(''), [{ name: 'Library', path: '' }])

  assert.deepEqual(breadcrumbs('Brahms/Symphony No. 1'), [
    { name: 'Library', path: '' },
    { name: 'Brahms', path: 'Brahms' },
    { name: 'Symphony No. 1', path: 'Brahms/Symphony No. 1' },
  ])
})

test('a rename changes the last segment and nothing else', () => {
  const path = 'Brahms/Symphony 1 (mess)'

  assert.equal(destinationFor(path, parentOf(path), 'Symphony No. 1'), 'Brahms/Symphony No. 1')
})

test('a rename at the root stays at the root', () => {
  assert.equal(destinationFor('stray.flac', parentOf('stray.flac'), 'Allegro.flac'), 'Allegro.flac')
})

test('a move changes the folder and keeps the name', () => {
  assert.equal(
    destinationFor('Brahms/Symphony 1', 'Classical/Brahms', 'Symphony 1'),
    'Classical/Brahms/Symphony 1',
  )
})

test('a move to the root is expressible', () => {
  assert.equal(destinationFor('Brahms/Symphony 1', '', 'Symphony 1'), 'Symphony 1')
})

test('a name carrying separators does not become a move', () => {
  // The whole reason the two halves are separate. Honoured literally,
  // "../Mahler" walks out of the folder — and the API's containment check would
  // refuse it, which means a confusing 400 rather than the rename that was
  // asked for.
  assert.equal(destinationFor('Brahms/Symphony 1', 'Brahms', '../Mahler'), 'Brahms/.. Mahler')
  assert.equal(destinationFor('Brahms/Symphony 1', 'Brahms', 'CD1/01'), 'Brahms/CD1 01')
})

test('a folder climbing out of the library is flattened, not honoured', () => {
  // Silently escaping is the failure worth guarding against; the API refuses it
  // either way, but only after the person has been told a plausible path.
  assert.equal(destinationFor('Brahms/Symphony 1', '../../etc', 'Symphony 1'), 'etc/Symphony 1')
})

test('a rename to nothing, to a dot, or to no change at all is refused', () => {
  assert.equal(destinationFor('Brahms/Symphony 1', 'Brahms', '   '), null)
  assert.equal(destinationFor('Brahms/Symphony 1', 'Brahms', '..'), null)
  assert.equal(destinationFor('Brahms/Symphony 1', 'Brahms', 'Symphony 1'), null)
})

test('parentOf is the folder an entry sits in', () => {
  assert.equal(parentOf('Brahms/Symphony 1/CD1'), 'Brahms/Symphony 1')
  assert.equal(parentOf('stray.flac'), '')
})

test('an uploaded folder keeps its shape, a picked file does not invent one', () => {
  const album = { name: '01.flac', webkitRelativePath: 'Album/CD1/01.flac' } as File
  const loose = { name: '01.flac', webkitRelativePath: '' } as File

  // Relative to the destination, never joined to it — the API sanitises this
  // half and believes the other, and one string cannot be both.
  assert.equal(uploadName(album), 'Album/CD1/01.flac')
  assert.equal(uploadName(loose), '01.flac')
})

test('joinPath does not put a leading slash on a root path', () => {
  assert.equal(joinPath('', 'Brahms'), 'Brahms')
  assert.equal(joinPath('Brahms/', 'Sym 1'), 'Brahms/Sym 1')
})

test('a folder nothing has scanned reads differently from one nothing matched', () => {
  assert.equal(matchSummary({ cataloguedFiles: 0, identified: 0, attributed: 0 }), null)

  assert.deepEqual(matchSummary({ cataloguedFiles: 12, identified: 12, attributed: 12 }), {
    label: '12 filed',
    tone: 'positive',
  })

  // One file is not "1 filed": the count only says anything when there is more
  // than one thing counted, and every row inside an album folder is one file.
  assert.deepEqual(matchSummary({ cataloguedFiles: 1, identified: 1, attributed: 1 }), {
    label: 'filed',
    tone: 'positive',
  })

  assert.deepEqual(matchSummary({ cataloguedFiles: 1, identified: 0, attributed: 0 }), {
    label: 'unmatched',
    tone: 'warning',
  })

  assert.deepEqual(matchSummary({ cataloguedFiles: 31, identified: 0, attributed: 0 }), {
    label: '31 unmatched',
    tone: 'warning',
  })

  assert.deepEqual(matchSummary({ cataloguedFiles: 31, identified: 8, attributed: 3 }), {
    label: '3/31 filed',
    tone: 'neutral',
  })
})

test('content and art urls escape a path rather than pasting it in', () => {
  // Album folders in this library contain '&', '#' and ' ', and every one of
  // them ends the query string early if it is not escaped.
  assert.equal(
    contentUrl('http://x', 'AC+DC/Back in Black/01 Hells Bells.flac'),
    'http://x/api/files/content?path=AC%2BDC%2FBack%20in%20Black%2F01%20Hells%20Bells.flac',
  )

  assert.equal(
    artUrl('http://x', 'Nat King Cole/Unforgettable'),
    'http://x/api/files/art?path=Nat%20King%20Cole%2FUnforgettable',
  )
})
