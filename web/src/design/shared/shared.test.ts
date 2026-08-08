/* The shared recipes are CSS, so there is no behaviour here to assert — what
 * there is, is a contract: five files, each defining a named recipe that
 * primitives `composes:` by name. Renaming or dropping one of those class names
 * breaks its composers silently, because `composes: gone from '../shared/x.module.css'`
 * resolves to nothing and the element simply loses the declarations rather than
 * failing to build.
 *
 * So this asserts the names exist and that `composes:` resolved — a composed
 * class carries BOTH its own name and the one it composed, which is the single
 * observable fact that tells "this recipe is defined once and reused" from
 * "somebody copied it".
 */

import { describe, expect, it } from 'vitest'

import chip from '@/design/shared/chip.module.css'
import label from '@/design/shared/label.module.css'
import meta from '@/design/shared/meta.module.css'
import surface from '@/design/shared/surface.module.css'
import toggle from '@/design/shared/switch.module.css'

describe('shared recipes', () => {
  it('exposes the eyebrow and its nav-spaced variant', () => {
    expect(label.label).toBeTruthy()
    expect(label.labelNav).toBeTruthy()
  })

  it('exposes both chip geometries and the categorical dot', () => {
    expect(chip.chip).toBeTruthy()
    expect(chip.chipSm).toBeTruthy()
    expect(chip.dot).toBeTruthy()
  })

  it('exposes the switch track, knob and their on-states', () => {
    expect(toggle.track).toBeTruthy()
    expect(toggle.trackOn).toBeTruthy()
    expect(toggle.knob).toBeTruthy()
    expect(toggle.knobOn).toBeTruthy()
  })

  it('exposes the card edge and its two variants', () => {
    expect(surface.surface).toBeTruthy()
    expect(surface.hoverEdge).toBeTruthy()
    expect(surface.surfaceInset).toBeTruthy()
  })

  it('exposes the mono micro-label', () => {
    expect(meta.meta).toBeTruthy()
    expect(meta.metaMuted).toBeTruthy()
  })

  it('composes rather than copies — a variant carries its base class too', () => {
    // If `composes:` were replaced by a duplicated block, these would each be a
    // single class name and the two definitions would be free to drift.
    for (const [base, variant] of [
      [label.label, label.labelNav],
      [chip.chip, chip.chipSm],
      [surface.surface, surface.hoverEdge],
      [surface.surface, surface.surfaceInset],
    ] as const) {
      expect(variant?.split(' ')).toContain(base)
    }
  })
})
