import { describe, expect, it } from 'vitest'

import { toastTone } from '@/widgets/toastTone'

describe('toastTone', () => {
  it('maps every wire level onto a design tone', () => {
    expect(toastTone('info')).toBe('neutral')
    expect(toastTone('success')).toBe('ok')
    expect(toastTone('warning')).toBe('warn')
    expect(toastTone('error')).toBe('bad')
  })

  it('an absent level is neutral, never accent — colour marks an exception', () => {
    expect(toastTone(null)).toBe('neutral')
    expect(toastTone(undefined)).toBe('neutral')
  })
})
