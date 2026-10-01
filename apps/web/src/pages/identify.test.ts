import assert from 'node:assert/strict'
import { test } from 'node:test'

import type { components } from '@fonoteca/api-client'

import {
  factsOf,
  gapsOf,
  interleave,
  isPlaceholderTrack,
  label,
  normalise,
  noticesOf,
  pairsOf,
  seat,
  songCount,
  titleMatch,
} from './identify.ts'

type FileRow = components['schemas']['IdentifyFileRow']
type Slot = components['schemas']['AlbumSlotRow']

function file(track: number, title: string, length: number, extra: Partial<FileRow> = {}): FileRow {
  return {
    mediaFileId: `file-${track}`,
    path: `Artist/Album/${track} - ${title}.flac`,
    name: `${String(track).padStart(2, '0')} - ${title}.flac`,
    subFolder: '',
    title,
    track,
    disc: null,
    length: null,
    lengthMs: length * 1000,
    reason: 'Unknown',
    size: '1 MiB',
    format: 'FLAC',
    tags: [],
    ...extra,
  }
}

/** A track of the lead edition unless `extra` says otherwise, keyed on a recording per position. */
function slot(position: number, title: string, length: number, extra: Partial<Slot> = {}): Slot {
  return {
    recording: `rec-${extra.discNumber ?? 1}-${position}`,
    release: 'lead',
    discNumber: 1,
    position,
    number: String(position),
    title,
    artist: null,
    duration: null,
    durationMs: length * 1000,
    editions: 1,
    onLead: true,
    heldBy: null,
    ...extra,
  }
}

test('a file missing from the release is placed where it sits in the folder, not at the end', () => {
  const files = [
    file(14, 'Money for Nothing', 627),
    file(15, 'Brothers in Arms', 558),
    file(16, 'Going Home', 407),
  ]
  const slots = [slot(14, 'Money for Nothing', 616), slot(15, 'Going Home', 418)]

  const lines = interleave(seat(files, slots), files)

  assert.deepEqual(
    lines.map((line) => (line.kind === 'unseated' ? `open:${line.file.title}` : line.slot.title)),
    ['Money for Nothing', 'open:Brothers in Arms', 'Going Home'],
  )
})

test('two files tagged with one track number are placed in folder order', () => {
  const files = [
    file(14, 'Money for Nothing', 627),
    file(15, 'Brothers in Arms', 558, { mediaFileId: 'brothers' }),
    file(15, 'Going Home', 407, { mediaFileId: 'home' }),
  ]
  const slots = [slot(14, 'Money for Nothing', 616), slot(15, 'Going Home', 418)]

  assert.deepEqual(
    interleave(seat(files, slots), files).map((line) =>
      line.kind === 'unseated' ? `open:${line.file.title}` : line.slot.title,
    ),
    ['Money for Nothing', 'open:Brothers in Arms', 'Going Home'],
  )
})

test('title beats track number, and the track tag that disagrees is reported', () => {
  const files = [file(16, 'Going Home', 407)]
  const seating = seat(files, [slot(15, 'Going Home', 418), slot(16, 'Other', 400)])

  const row = seating.rows[0]
  assert.equal(row?.kind, 'seated')
  assert.equal(row?.kind === 'seated' && row.numberDiffers, true)
  assert.equal(row?.kind === 'seated' && row.drift, -11)
  assert.equal(seating.rows[1]?.kind, 'empty')
})

test('a file whose title differs is seated by track number but never filed', () => {
  const files = [file(1, 'Why Aye Man', 548), file(2, 'Corned Beef City', 280)]
  const seating = seat(files, [slot(1, 'Trapper Man', 360), slot(2, 'Corned Beef City', 280)])

  assert.deepEqual(pairsOf(seating), [{ file: 'file-2', release: 'lead', disc: 1, position: 2 }])
  assert.ok(noticesOf(seating).some((notice) => notice.key === 'renamed'))
})

test('titles in another script still match only themselves', () => {
  assert.notEqual(normalise('Зимние грёзы'), '')

  const files = [file(1, 'Зимние грёзы', 600), file(2, 'Малороссийская', 700)]
  const seating = seat(files, [slot(1, 'Малороссийская', 700), slot(2, 'Зимние грёзы', 600)])

  assert.deepEqual(pairsOf(seating), [
    { file: 'file-2', release: 'lead', disc: 1, position: 1 },
    { file: 'file-1', release: 'lead', disc: 1, position: 2 },
  ])
})

test('a track another file already holds is not offered', () => {
  const files = [file(1, 'One', 100)]
  const seating = seat(files, [slot(1, 'One', 100, { heldBy: 'already.flac' })])

  assert.equal(seating.rows[0]?.kind, 'held')
  assert.equal(seating.unseated.length, 1)
})

test('the disc the server gives a file decides which disc it seats on', () => {
  const files = [
    file(1, 'Intro', 60, { subFolder: 'CD 1', disc: 1, mediaFileId: 'a' }),
    file(1, 'Encore', 60, { subFolder: 'CD 2', disc: 2, mediaFileId: 'b' }),
  ]
  const seating = seat(files, [slot(1, 'Opening', 60), slot(1, 'Closing', 60, { discNumber: 2 })])

  assert.deepEqual(
    seating.rows.map((row) => (row.kind === 'seated' ? row.file.mediaFileId : null)),
    ['a', 'b'],
  )
})

test('a clean match says so, and facts compare regardless of case and punctuation', () => {
  const files = [file(1, 'So What', 329)]
  assert.deepEqual(
    noticesOf(seat(files, [slot(1, 'So what', 331)])).map((notice) => notice.tone),
    ['success'],
  )

  const facts = factsOf(
    {
      album: 'Heart Soul & Saxophone',
      artist: 'Vanessa Collier',
      year: 2014,
      label: null,
      discs: 1,
      release: null,
      agreeing: 0,
    },
    9,
    {
      title: 'Heart, Soul & Saxophone',
      artist: 'Vanessa Collier',
      year: 2014,
      tracks: 9,
      merged: 11,
    },
  )

  assert.deepEqual(
    facts.map((fact) => fact.state),
    ['same', 'same', 'same', 'same'],
  )
  assert.equal(facts[3]?.album, '9 tracks, 11 across editions')
})

test('a repeated title is seated by its track tag, never on the first track with that name', () => {
  const files = [file(4, 'Allegro', 300, { mediaFileId: 'allegro-4' })]
  const seating = seat(files, [
    slot(1, 'Allegro', 300),
    slot(2, 'Adagio', 400),
    slot(4, 'Allegro', 300),
  ])

  assert.deepEqual(pairsOf(seating), [{ file: 'allegro-4', release: 'lead', disc: 1, position: 4 }])
})

test('two files with one title keep the tracks their tags name, whatever the path order', () => {
  const files = [
    file(10, 'Intro', 60, { mediaFileId: 'x10' }),
    file(2, 'Intro', 60, { mediaFileId: 'x2' }),
  ]
  const seating = seat(files, [slot(2, 'Intro', 60), slot(10, 'Intro', 60)])

  assert.deepEqual(pairsOf(seating), [
    { file: 'x2', release: 'lead', disc: 1, position: 2 },
    { file: 'x10', release: 'lead', disc: 1, position: 10 },
  ])
})

test('a unique title filed against its track tag says so', () => {
  const files = [file(16, 'Going Home', 407)]
  const notices = noticesOf(seat(files, [slot(15, 'Going Home', 418)]))

  assert.ok(notices.some((notice) => notice.key === 'renumbered' && notice.text.includes('#15')))
})

test('a second copy of a filed track is not seated on another track with its title', () => {
  const files = [file(1, 'Intro', 60, { mediaFileId: 'intro-dup' })]
  const seating = seat(files, [
    slot(1, 'Intro', 60, { heldBy: '01 Intro.flac' }),
    slot(9, 'Intro', 60),
  ])

  assert.deepEqual(pairsOf(seating), [])
})

test('on a release with two discs, a file whose disc is unknown is not placed by number', () => {
  const files = [
    file(1, 'Intro', 60, { mediaFileId: 'intro' }),
    file(2, 'Song B', 200, { mediaFileId: 'b' }),
    file(3, 'Encore', 300, { mediaFileId: 'encore' }),
  ]
  const seating = seat(files, [
    slot(1, 'Intro', 60),
    slot(2, 'Song A', 200),
    slot(3, 'Encore', 300),
    slot(1, 'Intro', 60, { discNumber: 2 }),
    slot(2, 'Song B', 200, { discNumber: 2 }),
    slot(3, 'Encore', 300, { discNumber: 2 }),
  ])

  // Song B's title is unique, so it seats; the repeated titles stay open.
  assert.deepEqual(pairsOf(seating), [{ file: 'b', release: 'lead', disc: 2, position: 2 }])
  assert.deepEqual(
    seating.unseated.map((each) => each.mediaFileId),
    ['intro', 'encore'],
  )
})

test('a title that is also on a filed track is not unique, whatever the file knows', () => {
  const untagged = [file(1, 'Allegro', 300, { track: null, mediaFileId: 'f' })]
  assert.deepEqual(
    pairsOf(
      seat(untagged, [slot(1, 'Allegro', 300, { heldBy: 'held.flac' }), slot(3, 'Allegro', 300)]),
    ),
    [],
  )

  const unknownDisc = [file(1, 'Intro', 60, { mediaFileId: 'g' })]
  assert.deepEqual(
    pairsOf(
      seat(unknownDisc, [
        slot(1, 'Intro', 60, { heldBy: 'held.flac' }),
        slot(1, 'Intro', 60, { discNumber: 2 }),
      ]),
    ),
    [],
  )
})

test('a disc tag that names another disc keeps a unique title off it', () => {
  const files = [file(4, 'Unique', 200, { disc: 2, mediaFileId: 'u' })]
  assert.deepEqual(
    pairsOf(seat(files, [slot(4, 'Unique', 200), slot(1, 'Other', 100, { discNumber: 2 })])),
    [],
  )
})

test('titles of nothing but punctuation never count as matching', () => {
  const files = [file(5, '?', 100, { mediaFileId: 'q' })]
  assert.deepEqual(pairsOf(seat(files, [slot(5, '...', 100)])), [])
})

test('a title that is part of the track title is filed on the track its tags name', () => {
  const files = [file(1, 'Mars, The Bringer Of War', 441)]
  const slots = [
    slot(1, 'The Planets, op. 32: Mars, the Bringer of War. Allegro', 441),
    slot(2, 'The Planets, op. 32: Venus, the Bringer of Peace. Adagio', 517),
  ]
  const seating = seat(files, slots)

  assert.equal(seating.rows[0]?.kind === 'seated' && seating.rows[0].title, 'partial')
  assert.deepEqual(pairsOf(seating), [{ file: 'file-1', release: 'lead', disc: 1, position: 1 }])
  assert.deepEqual(
    noticesOf(seating).map((notice) => notice.key),
    ['empty'],
  )
})

test('partial means whole words, and never seats a file anywhere its tags do not', () => {
  assert.equal(titleMatch('Intro', 'Introduction'), 'differs')
  assert.equal(titleMatch('Allegro (Remastered 2015)', 'Allegro'), 'partial')

  const files = [file(3, 'Mars, the Bringer of War', 441)]
  const seating = seat(files, [slot(1, 'The Planets: Mars, the Bringer of War', 441)])
  assert.deepEqual(pairsOf(seating), [])
})

test('a picked track is filed whatever the title, bumps the rule, and open means open', () => {
  const files = [
    file(1, 'Why Aye Man', 548, { mediaFileId: 'why' }),
    file(2, 'Corned Beef City', 280, { mediaFileId: 'beef' }),
  ]
  const slots = [slot(1, 'Trapper Man', 360), slot(2, 'Corned Beef City', 280)]

  const moved = seat(files, slots, new Map([['why', 'rec-1-2']]))
  assert.deepEqual(pairsOf(moved), [{ file: 'why', release: 'lead', disc: 1, position: 2 }])
  assert.equal(moved.rows[1]?.kind === 'seated' && moved.rows[1].chosen, true)
  assert.deepEqual(
    moved.unseated.map((each) => each.mediaFileId),
    ['beef'],
  )

  const left = seat(files, slots, new Map([['beef', null]]))
  assert.deepEqual(pairsOf(left), [])
  assert.ok(
    !noticesOf(left, 1, new Map([['beef', null]])).some((notice) => notice.file !== undefined),
  )
})

test('a track only another edition carries is seated by its title, never by its number', () => {
  const bonus = { release: 'deluxe', onLead: false, recording: 'rec-bonus' }
  const slots = [slot(1, 'One', 100), slot(2, 'Two', 100), slot(3, 'Demo', 100, bonus)]

  // Tagged 3, and the lead has no track 3: another edition's number means nothing here.
  const numbered = seat([file(3, 'Something Else', 100)], slots)
  assert.deepEqual(
    numbered.unseated.map((each) => each.mediaFileId),
    ['file-3'],
  )

  assert.deepEqual(pairsOf(seat([file(3, 'Demo', 100)], slots)), [
    { file: 'file-3', release: 'deluxe', disc: 1, position: 3 },
  ])
})

test('only the lead edition’s tracks with no file are a notice, and the rest print as +', () => {
  const extra = slot(4, 'Bonus', 100, { release: 'deluxe', onLead: false, recording: 'rec-bonus' })
  const seating = seat([file(1, 'One', 100)], [slot(1, 'One', 100), slot(2, 'Two', 100), extra])

  const empty = noticesOf(seating).find((notice) => notice.key === 'empty')
  assert.equal(empty?.text, 'Track #2 of this album has no file.')
  assert.equal(label(extra, 1), '+')
})

test('a hidden track is never seated on the silence in front of it, nor is the silence a gap', () => {
  // Carencro, cut down: the rip dropped the silence, so the hidden track is
  // its third file and tagged 3, while the album prints it fourth.
  const slots = [
    slot(1, 'One', 100),
    slot(2, 'Two', 100),
    slot(3, '[silence]', 4),
    slot(4, '', 240),
  ]
  const seating = seat([file(1, 'One', 100), file(2, 'Two', 100), file(3, '', 240)], slots)

  assert.equal(
    seating.rows.some((row) => row.kind === 'seated' && row.slot.title === '[silence]'),
    false,
  )
  assert.equal(
    noticesOf(seating).some((notice) => notice.key === 'empty' && notice.text.includes('#3')),
    false,
  )
})

test('a rip that kept its silence has the silent file seated where title and number agree', () => {
  const slots = [slot(1, 'One', 100), slot(2, '[silence]', 4), slot(3, 'Hidden', 240)]
  const seating = seat(
    [file(1, 'One', 100), file(2, '[silence]', 4), file(3, 'Hidden', 240)],
    slots,
  )

  assert.deepEqual(seating.unseated, [])
  assert.equal(seating.rows.find((row) => row.slot.title === '[silence]')?.kind, 'seated')
})

test('a rip that kept a run of silences has each silent file seated on its own', () => {
  // Real placeholder runs are long — Carencro prints eleven — so the title
  // alone never picks one out: it is the title and the number together.
  const slots = [
    slot(1, 'One', 100),
    slot(2, '[silence]', 4),
    slot(3, '[silence]', 4),
    slot(4, '[silence]', 4),
    slot(5, 'Hidden', 240),
  ]
  const files = [
    file(1, 'One', 100),
    file(2, '[silence]', 4),
    file(3, '[silence]', 4),
    file(4, '[silence]', 4),
    file(5, 'Hidden', 240),
  ]

  const seating = seat(files, slots)

  assert.deepEqual(seating.unseated, [])
  assert.deepEqual(
    seating.rows.map((row) => (row.kind === 'seated' ? row.file.track : null)),
    [1, 2, 3, 4, 5],
  )
})

test('a person can still put a file on a placeholder', () => {
  const silence = slot(3, '[silence]', 4)
  const seating = seat(
    [file(3, '', 4)],
    [slot(1, 'One', 100), silence],
    new Map([['file-3', silence.recording]]),
  )

  assert.equal(seating.rows.find((row) => row.slot === silence)?.kind, 'seated')
})

test('a placeholder counts as a track only where a file already holds it', () => {
  const kept = slot(2, '[silence]', 4, { heldBy: '02.flac' })
  const dropped = slot(3, '[silence]', 4, { recording: 'rec-silence' })

  assert.equal(songCount([slot(1, 'One', 100), kept, dropped, slot(4, 'Hidden', 240)]), 3)
})

test('the tracks left without a file after filing never include a placeholder', () => {
  const slots = [slot(1, 'One', 100), slot(2, '[silence]', 4), slot(3, 'Three', 100)]
  const seating = seat([file(1, 'One', 100)], slots)

  assert.deepEqual(
    gapsOf(seating).map((row) => row.slot.title),
    ['Three'],
  )
})

test('only the exact placeholder titles are placeholders', () => {
  assert.equal(isPlaceholderTrack('[silence]'), true)
  assert.equal(isPlaceholderTrack('[data track]'), true)
  assert.equal(isPlaceholderTrack('Silence'), false)
  assert.equal(isPlaceholderTrack('[untitled]'), false)
  assert.equal(isPlaceholderTrack(null), false)
})

test('a title and number that agree only on another edition are not a match on their own', () => {
  // "Demo" is #2 on the lead and #3 on the deluxe, and the file is tagged 3: the
  // title is not unique, so the lead's #3 is what its number means.
  const slots = [
    slot(2, 'Demo', 100),
    slot(3, 'Other', 100),
    slot(3, 'Demo', 100, { release: 'deluxe', onLead: false, recording: 'rec-deluxe-demo' }),
  ]
  const seating = seat([file(3, 'Demo', 100)], slots)

  assert.deepEqual(
    seating.rows.map((row) => row.kind),
    ['empty', 'seated', 'empty'],
  )
  assert.deepEqual(pairsOf(seating), [])
})
