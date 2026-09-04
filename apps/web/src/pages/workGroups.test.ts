/**
 * The work grouping, which decides whether a track list gets headings at all.
 *
 * `node --test` with Node's own type stripping, matching `seating.ts` — nothing
 * here imports React, a stylesheet or the API client at runtime.
 *
 * Three of these are regressions against measurements on the real library. The
 * link points at the *movement*, so grouping on it left Brahms's complete
 * symphonies flat. A run of one drew a full-width band above a single row —
 * 47 of them on Shostakovich's cycle. And a mis-linked movement turned one
 * questionable subtitle into a heading contradicting the track beneath it.
 */

import assert from 'node:assert/strict'
import { test } from 'node:test'

import { workGroups } from './workGroups.ts'

const BRAHMS = 'Sinfonie Nr. 1 c-Moll, op. 68'
const BRAHMS_2 = 'Sinfonie Nr. 2 D-Dur, op. 73'

function track(title: string, workTitle: string | null = null) {
  return { title, workTitle }
}

/** A movement as the catalogue actually holds it: the leaf work, not the piece. */
function movement(work: string, part: string) {
  return track(`${work}: ${part}`, `${work}: ${part}`)
}

test('movements group under the piece, not under themselves', () => {
  // The measured shape. Every workTitle here is distinct, so grouping on the
  // link as stored produces four groups of one and no headings at all.
  const groups = workGroups([
    movement(BRAHMS, 'I. Un poco sostenuto'),
    movement(BRAHMS, 'II. Andante sostenuto'),
    movement(BRAHMS_2, 'I. Allegro non troppo'),
    movement(BRAHMS_2, 'II. Adagio non troppo'),
  ])

  assert.deepEqual(
    groups?.map((group) => [group.workTitle, group.tracks.length]),
    [
      [BRAHMS, 2],
      [BRAHMS_2, 2],
    ],
  )
})

test('a run of one gets no heading', () => {
  const groups = workGroups([
    movement(BRAHMS, 'I. Un poco sostenuto'),
    movement(BRAHMS, 'II. Andante sostenuto'),
    // MusicBrainz mis-links this one to a different symphony. As a heading it
    // would assert a piece the row underneath it contradicts.
    track(`${BRAHMS}: III. Un poco allegretto`, 'Symphony no. 4 in E minor, op. 98: IV. Allegro'),
  ])

  assert.deepEqual(
    groups?.map((group) => [group.workTitle, group.tracks.length]),
    [
      [BRAHMS, 2],
      [null, 1],
    ],
  )
})

test('adjacent unheaded runs merge into one stretch of plain rows', () => {
  const groups = workGroups([
    movement(BRAHMS, 'I. Un poco sostenuto'),
    movement(BRAHMS, 'II. Andante sostenuto'),
    track('Interview with the conductor'),
    movement(BRAHMS_2, 'I. Allegro non troppo'),
    track('Applause'),
  ])

  assert.deepEqual(
    groups?.map((group) => [group.workTitle, group.tracks.length]),
    [
      [BRAHMS, 2],
      [null, 3],
    ],
  )
})

test('an album with no works is left flat', () => {
  assert.equal(workGroups([track('Nutbush City Limits'), track('Close to My Fire')]), null)
})

test('a work per track is not a grouping', () => {
  // What MusicBrainz does to pop: every song linked to a work of its own name.
  assert.equal(
    workGroups([track('Sloe Gin', 'Sloe Gin'), track('Dislocated Boy', 'Dislocated Boy')]),
    null,
  )
})

test('one movement of one piece on a pop album groups nothing', () => {
  assert.equal(workGroups([movement(BRAHMS, 'I. Un poco sostenuto'), track('Sloe Gin')]), null)
})

test('the printed order survives a work being returned to', () => {
  const groups = workGroups([
    movement(BRAHMS, 'I. Un poco sostenuto'),
    movement(BRAHMS, 'II. Andante sostenuto'),
    movement(BRAHMS_2, 'I. Allegro non troppo'),
    movement(BRAHMS_2, 'II. Adagio non troppo'),
    movement(BRAHMS, 'III. Un poco allegretto'),
    movement(BRAHMS, 'IV. Adagio'),
  ])

  // Three runs, not two buckets: re-sorting would move the rows and renumber
  // the album on screen.
  assert.deepEqual(
    groups?.map((group) => [group.workTitle, group.tracks.length]),
    [
      [BRAHMS, 2],
      [BRAHMS_2, 2],
      [BRAHMS, 2],
    ],
  )

  // And the two runs of the same work are separate keys, or React reconciles
  // them as one.
  assert.equal(new Set(groups?.map((group) => group.key)).size, 3)
})

test('every track comes back exactly once, in order', () => {
  const input = [
    movement(BRAHMS, 'I. Un poco sostenuto'),
    track('Interview'),
    movement(BRAHMS_2, 'I. Allegro non troppo'),
    movement(BRAHMS_2, 'II. Adagio non troppo'),
    track('Applause'),
  ]

  assert.deepEqual(
    workGroups(input)?.flatMap((group) => group.tracks),
    input,
  )
})

test('several recordings of one song are not a work grouping', () => {
  // A complete-masters set: one work, recorded five times. They are different
  // *recordings*, not movements of a structure, so a "Blue Train" band above
  // them announces nothing the rows do not already say.
  assert.equal(
    workGroups([
      track('Blue Train (false start)', 'Blue Train'),
      track('Blue Train (alternate take 7)', 'Blue Train'),
      track('Blue Train (alternate take 8)', 'Blue Train'),
    ]),
    null,
  )
})

test('an empty track list is flat rather than an empty group', () => {
  assert.equal(workGroups([]), null)
})

test('the run-in every title in a group repeats is reported as the prefix', () => {
  // The album this is for. MusicBrainz's work is titled "Concerto in E major,
  // op. 8 no. 1" where the release prints "Concerto no. 1", so matching the
  // heading against the titles would find nothing.
  const printed = 'Le quattro stagioni, op. 8 (The Four Seasons): Concerto no. 1, RV 269: '
  const groups = workGroups([
    track(`${printed}I. Allegro`, 'Concerto in E major, op. 8 no. 1, RV 269: I. Allegro'),
    track(`${printed}II. Largo`, 'Concerto in E major, op. 8 no. 1, RV 269: II. Largo'),
  ])

  const group = groups?.[0]
  assert.equal(group?.prefix, printed)
  assert.deepEqual(
    group?.tracks.map((t) => t.title.slice(group.prefix.length)),
    ['I. Allegro', 'II. Largo'],
  )
})

test('the prefix is cut at a delimiter, never mid-word', () => {
  // The raw common prefix here runs into "I. A", and slicing there would leave
  // "llegro" and "ndante".
  const groups = workGroups([movement(BRAHMS, 'I. Allegro'), movement(BRAHMS, 'I. Andante')])

  assert.equal(groups?.[0]?.prefix, `${BRAHMS}: `)
})

test('titles sharing nothing up to a delimiter keep every word', () => {
  // A release that prints its movements bare. The common prefix is "A", which
  // reaches no delimiter, so nothing is taken off.
  const groups = workGroups([
    track('Allegro', 'Sonata in B minor: I. Allegro'),
    track('Adagio', 'Sonata in B minor: II. Adagio'),
  ])

  assert.equal(groups?.[0]?.workTitle, 'Sonata in B minor')
  assert.equal(groups?.[0]?.prefix, '')
})

test('a prefix that would blank a row is not taken', () => {
  // The second row's whole printed title is the run-in. Removing it leaves an
  // empty cell, so the run-in stays on both rows instead.
  const printed = 'Le quattro stagioni: Concerto no. 1: '
  const groups = workGroups([
    track(`${printed}I. Allegro`, 'Concerto in E major, RV 269: I. Allegro'),
    track(printed, 'Concerto in E major, RV 269: II. Largo'),
  ])

  assert.equal(groups?.[0]?.workTitle, 'Concerto in E major, RV 269')
  assert.equal(groups?.[0]?.prefix, '')
})

test('two different works of the same name are not one group', () => {
  // Live on a real artist page: a film-music orchestra whose Schindler's List,
  // Superman and Lord of the Rings tracks each link to a work called "Main
  // Theme". Four distinct works share that title in this library, and the API's
  // work ordering is exactly what brings them next to each other. None of them
  // names a part, so none of them is a heading — which is what makes keying on
  // a title safe.
  assert.equal(
    workGroups([
      track("Schindler's List", 'Main Theme'),
      track('Superman', 'Main Theme'),
      track('The Fellowship of the Ring', 'Main Theme'),
    ]),
    null,
  )
})

test('a heading differing from its rows only by punctuation is not worth a band', () => {
  assert.equal(
    workGroups([
      track('Rhapsody in Blue!', 'Rhapsody in Blue: I.'),
      track('Rhapsody in Blue!', 'Rhapsody in Blue: II.'),
    ]),
    null,
  )
})

test('an inconsistently spaced title cannot be renumbered by the prefix', () => {
  // The common prefix of these two runs to "Suite:", and a delimiter search
  // bounded one character too far claims "Suite: " — which the second title
  // does not carry. Slicing seven characters off it would print movement II as
  // "I. Adagio".
  const groups = workGroups([
    track('Suite: I. Allegro', 'Sonata: I. Allegro'),
    track('Suite:II. Adagio', 'Sonata: II. Adagio'),
  ])

  const group = groups?.[0]
  assert.equal(group?.prefix, '')
  assert.deepEqual(
    group?.tracks.map((t) => t.title.slice(group.prefix.length)),
    ['Suite: I. Allegro', 'Suite:II. Adagio'],
  )
})

test('an unheaded run keeps its titles whole', () => {
  const groups = workGroups([
    movement(BRAHMS, 'I. Un poco sostenuto'),
    movement(BRAHMS, 'II. Andante sostenuto'),
    track('Applause'),
  ])

  // Nothing above a demoted run has said the run-in, so nothing may be removed.
  assert.equal(groups?.at(-1)?.workTitle, null)
  assert.equal(groups?.at(-1)?.prefix, '')
})
