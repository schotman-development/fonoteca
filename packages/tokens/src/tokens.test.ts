import assert from 'node:assert/strict'
import { test } from 'node:test'

import { flatten, toVarRefs, varName } from './cssVars.ts'
import { scales } from './scales.ts'
import { dark, light } from './semantic.ts'

test('varName kebab-cases camelCase segments', () => {
  assert.equal(varName(['fontSize', 'sm']), '--font-size-sm')
  assert.equal(varName(['density', 'rowCompact']), '--density-row-compact')
  assert.equal(varName(['color', 'surface', 'base']), '--color-surface-base')
})

test('varName leaves numeric-leading segments intact', () => {
  assert.equal(varName(['fontSize', '2xs']), '--font-size-2xs')
  assert.equal(varName(['space', '12']), '--space-12')
})

/**
 * The types already guarantee this — both themes are declared as
 * `SemanticTokens`. The runtime check exists because a future `as const` or a
 * widened type would silently drop a key, and a token that only one theme
 * defines is exactly how an element becomes invisible in dark mode.
 */
test('light and dark define identical custom properties', () => {
  const lightKeys = Object.keys(flatten(light)).sort()
  const darkKeys = Object.keys(flatten(dark)).sort()
  assert.deepEqual(lightKeys, darkKeys)
  assert.ok(lightKeys.length > 0, 'expected the semantic layer to emit tokens')
})

test('no token resolves to an empty value', () => {
  for (const tree of [flatten(scales), flatten(light), flatten(dark)]) {
    for (const [name, value] of Object.entries(tree)) {
      assert.ok(value.trim().length > 0, `${name} is empty`)
    }
  }
})

test('toVarRefs mirrors the tree shape with var() leaves', () => {
  const refs = toVarRefs(light)
  assert.equal(refs.color.text.primary, 'var(--color-text-primary)')
  assert.equal(refs.shadow.md, 'var(--shadow-md)')
})

test('every var() reference names a property the stylesheet will define', () => {
  const defined = new Set(Object.keys(flatten(light)))
  const referenced = new Set<string>()

  const walk = (node: unknown): void => {
    if (typeof node === 'string') {
      const match = /^var\((--[a-z0-9-]+)\)$/.exec(node)
      assert.ok(match, `expected a var() reference, got ${node}`)
      referenced.add(match[1] as string)
      return
    }
    for (const child of Object.values(node as Record<string, unknown>)) walk(child)
  }
  walk(toVarRefs(light))

  for (const name of referenced) {
    assert.ok(defined.has(name), `${name} is referenced but never defined`)
  }
})
