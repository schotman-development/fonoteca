import { describe, expect, it } from 'vitest'

import { cx } from '@/design/cx'

describe('cx', () => {
  it('joins the parts it is given with single spaces', () => {
    expect(cx('a', 'b', 'c')).toBe('a b c')
  })

  it('drops every falsy part rather than emitting a separator for it', () => {
    expect(cx('a', false, null, undefined, '', 'b')).toBe('a b')
  })

  it('never produces a leading or trailing space', () => {
    expect(cx(undefined, 'only', false)).toBe('only')
    expect(cx(false, undefined)).toBe('')
  })

  it('returns the empty string when there is nothing to join', () => {
    expect(cx()).toBe('')
  })

  it('preserves order, because the last class must be able to win', () => {
    expect(cx('base', 'variant', 'fromCaller')).toBe('base variant fromCaller')
  })

  it('does not de-duplicate — a repeated class is the caller saying so', () => {
    expect(cx('a', 'a')).toBe('a a')
  })
})
