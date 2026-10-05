import assert from 'node:assert/strict'
import { test } from 'node:test'

import type { components } from '@fonoteca/api-client'

import { undoLabel, undoOutcome, undoQuestion } from './tagUndo.ts'

type Result = components['schemas']['TagUndoResult']

const result: Result = {
  status: 'Undone',
  folder: 'Miles Davis/kob rip',
  edit: { id: 'abc', kind: 'TagWrite', at: '2026-10-01T12:00:00Z', files: 3 },
  restored: 3,
  moved: 1,
  unlinked: 0,
  relinked: 0,
  covers: 0,
  reopened: 3,
  problems: [],
}

test('the label names which edit a press reverses', () => {
  assert.equal(
    undoLabel({ id: 'a', kind: 'TagWrite', at: '2026-10-01T12:00:00Z', files: 1 }),
    'Undo the last tag write',
  )
  assert.equal(
    undoLabel({ id: 'a', kind: 'Identification', at: '2026-10-01T12:00:00Z', files: 1 }),
    'Undo the AcoustID tag',
  )
})

test('undoing a tag write says the folder becomes a question', () => {
  const question = undoQuestion('Miles Davis/Kind of Blue', {
    id: 'a',
    kind: 'TagWrite',
    at: '2026-10-01T12:00:00Z',
    files: 1,
  })

  assert.match(question, /“Miles Davis\/Kind of Blue”/)
  assert.match(question, /1 file\b/)
  assert.match(question, /question on the Identify screen/)
})

test('undoing an identification says only the tag goes', () => {
  const question = undoQuestion('A/B', {
    id: 'a',
    kind: 'Identification',
    at: '2026-10-01T12:00:00Z',
    files: 2,
  })

  assert.match(question, /2 files/)
  assert.doesNotMatch(question, /Identify screen/)
})

test('an undo stopped by a file that would not change says it can be tried again', () => {
  const said = undoOutcome({ ...result, status: 'Incomplete', problems: ['A/B/01.flac: denied'] })

  assert.match(
    said,
    /^Not finished: A\/B\/01\.flac: denied\. 3 files already got their tags back; nothing was moved/,
  )
  assert.match(said, /still there to undo/)
})

test('the outcome counts what changed and names what was left', () => {
  assert.equal(
    undoOutcome(result),
    '3 files got their tags back, 1 move reversed, 3 files reopened as a question.',
  )

  assert.equal(
    undoOutcome({ ...result, restored: 0, moved: 0, reopened: 0, problems: ['A/B: taken'] }),
    'Nothing needed changing. Left as they were: A/B: taken.',
  )
})
