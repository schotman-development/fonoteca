import assert from 'node:assert/strict'
import { test } from 'node:test'
import {
  complete,
  condition,
  FIELDS,
  group,
  OPERATORS,
  valuesFor,
  withAppended,
  withChild,
  withField,
  withOperator,
} from './smartRules.ts'

test('a new condition asks its kind first question', () => {
  assert.equal(condition('Genre').operator, 'Contains')
  assert.equal(condition('Year').operator, 'AtLeast')
  assert.equal(condition('Lossless').operator, 'IsTrue')
  assert.deepEqual([condition('Added').operator, condition('Added').value], ['InTheLast', '30'])
})

test('every operator offered for a kind is one its fields can be asked', () => {
  for (const [field, info] of Object.entries(FIELDS)) {
    assert.ok(OPERATORS[info.kind].length > 0, field)
  }
})

test('another field of the same kind keeps the question, another kind starts again', () => {
  const asked = { ...condition('Artist'), operator: 'Is' as const, value: 'Miles Davis' }

  assert.deepEqual(withField(asked, 'Composer'), { ...asked, field: 'Composer' })
  assert.deepEqual(withField(asked, 'Year'), condition('Year'))
})

test('an operator drops the values it does not take', () => {
  const range = { ...condition('Year'), operator: 'Between' as const, value: '1955', to: '1965' }

  assert.equal(withOperator(range, 'AtLeast').to, null)
  assert.equal(withOperator(range, 'AtLeast').value, '1955')
  assert.equal(withOperator(range, 'IsEmpty').value, null)
  assert.deepEqual([valuesFor('Between'), valuesFor('IsTrue'), valuesFor('Contains')], [2, 0, 1])
})

test('a group edits one child, removes one, or gains one at the end', () => {
  const tree = group('Any', [condition('Title'), condition('Album')])

  assert.deepEqual(withChild(tree, 1, condition('Year')).rules?.[1], condition('Year'))
  assert.deepEqual(withChild(tree, 0, null).rules, [condition('Album')])
  assert.equal(withAppended(tree, group()).rules?.length, 3)
  assert.equal(tree.rules?.length, 2)
})

test('rules are complete once every condition has what its question needs', () => {
  const text = { ...condition('Genre'), value: 'jazz' }
  const range = { ...condition('Year'), operator: 'Between' as const, value: '1955', to: '' }

  assert.equal(complete(group('All', [text, condition('Lossless')])), true)
  assert.equal(complete(group('All', [condition('Genre')])), false)
  assert.equal(complete(group('All', [range])), false)
  assert.equal(complete(group('All', [{ ...range, to: 'soon' }])), false)
  assert.equal(complete(group('All', [{ ...range, to: '1965' }])), true)
  assert.equal(complete(group('All', [])), true)
})
