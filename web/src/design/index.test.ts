/**
 * The barrel's own test, and it exists for one failure.
 *
 * `@/design/index.ts` is the layer's whole public surface: screens import from
 * it and never from a file inside it. Nothing in TypeScript notices when a
 * primitive is added to this directory and left out of the barrel — the new
 * directory compiles, its own tests pass, and the omission surfaces as a screen
 * reaching past the barrel because the short import "does not work", which is
 * the one rule this layer has.
 *
 * So: enumerate the primitives from the directory itself and assert each is
 * exported under its own name. A hand-kept list is exactly the thing that goes
 * stale, which is what this is here to catch.
 *
 * `import.meta.glob` rather than `node:fs` because the suite runs in jsdom with
 * no `@types/node` — and it is the better answer anyway: Vite resolves the
 * pattern at build time, so a directory added without a sub-barrel fails here
 * rather than at whatever runtime first reached for it.
 */

import { describe, expect, it } from 'vitest'

import * as design from '@/design'

/**
 * Every `<Primitive>/index.ts` beside this file. `shared/` has none — it holds
 * CSS recipes, which are not values and are deliberately not exported.
 */
const SUB_BARRELS = import.meta.glob('./*/index.ts')

function primitiveDirs(): string[] {
  return Object.keys(SUB_BARRELS)
    .map((path) => path.split('/')[1])
    .filter((name): name is string => name !== undefined)
    .sort()
}

/**
 * The handful of directories whose exported component is named differently from
 * the folder, each for a reason that is about the API rather than the file
 * layout. Anything not listed here must export its own name.
 */
const NAMED_DIFFERENTLY: Readonly<Record<string, readonly string[]>> = {
  // The list and its row are two components in one file, and a screen needs both.
  KeyValueList: ['KeyValueList', 'KeyValue'],
  // The pill, the live region, the provider and the hook.
  Toast: ['Toast', 'ToastHost', 'ToastProvider', 'useToast'],
  // The label/hint wrapper and the input it wraps.
  Field: ['Field', 'TextInput'],
}

describe('the @/design barrel', () => {
  it('finds the primitives at all, so an empty glob cannot pass silently', () => {
    expect(primitiveDirs().length).toBeGreaterThan(25)
  })

  it('exports every primitive in the directory', () => {
    const missing = primitiveDirs().filter((dir) =>
      (NAMED_DIFFERENTLY[dir] ?? [dir]).some((name) => !Object.hasOwn(design, name)),
    )

    expect(missing).toEqual([])
  })

  it('exports the foundations a primitive is built out of', () => {
    for (const name of ['cx', 'artIndex', 'artGradientVar', 'initialsOf'] as const) {
      expect(design[name]).toBeTypeOf('function')
    }
  })

  it('does not export a CSS recipe — CSS is not a value', () => {
    // `shared/` is composed by stylesheets, never imported by a screen. If one
    // of them ever appears here, a screen can apply a recipe directly and the
    // "exactly one definition of each" rule stops being checkable.
    for (const key of Object.keys(design)) {
      expect(key).not.toMatch(/module|css/i)
    }
  })
})
