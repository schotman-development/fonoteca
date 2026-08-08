import { describe, expect, it } from 'vitest'

import { EM_DASH } from '@/format'
import {
  ART_COUNT,
  artGradientVar,
  artIndex,
  initialsOf,
  initialsOrDash,
} from '@/design/artwork'

describe('artIndex', () => {
  it('reproduces the design’s own hash', () => {
    // Golden values, computed by running the design's HASH (line 902) against
    // the ramp length. These pin the algorithm: change the multiplier, the
    // modulus or the code-unit reading and one of these moves.
    expect(artIndex('')).toBe(0)
    expect(artIndex('A')).toBe(5)
    expect(artIndex('Mark Knopfler')).toBe(1)
    expect(artIndex('Kind of Blue')).toBe(8)
    expect(artIndex('Miles Davis')).toBe(7)
    expect(artIndex('Björk')).toBe(8)
    expect(artIndex("D'Angelo")).toBe(5)
  })

  it('always lands inside the ramp', () => {
    const seeds = [
      '',
      ' ',
      'a',
      'Björk',
      "D'Angelo",
      '\u{1D11E}',
      '\u{1D11E}\u{1D11E}\u{1D11E}',
      'Selected Ambient Works 85–92',
      'x'.repeat(5000),
      '0884977859300',
    ]
    for (const seed of seeds) {
      const i = artIndex(seed)
      expect(Number.isInteger(i)).toBe(true)
      expect(i).toBeGreaterThanOrEqual(0)
      expect(i).toBeLessThan(ART_COUNT)
    }
  })

  it('is stable — the same seed is the same swatch every time', () => {
    expect(artIndex('The Köln Concert')).toBe(artIndex('The Köln Concert'))
  })

  it('is case- and content-sensitive, so two neighbours rarely collide', () => {
    expect(artIndex('Blue')).not.toBe(artIndex('blue'))
  })
})

describe('artGradientVar', () => {
  it('names the ramp entry its index chose', () => {
    expect(artGradientVar('Mark Knopfler')).toBe('var(--art-1)')
    expect(artGradientVar('')).toBe('var(--art-0)')
  })

  it('agrees with artIndex for any seed', () => {
    for (const seed of ['Björk', 'Voodoo', '\u{1D11E}x', '']) {
      expect(artGradientVar(seed)).toBe(`var(--art-${artIndex(seed)})`)
    }
  })
})

describe('initialsOf', () => {
  it('takes the first two characters of a release title', () => {
    expect(initialsOf('Kind of Blue', 'chars')).toBe('KI')
    expect(initialsOf('Voodoo', 'chars')).toBe('VO')
    expect(initialsOf("D'Angelo", 'chars')).toBe("D'")
  })

  it('takes the first letter of each word for a person', () => {
    expect(initialsOf('Mark Knopfler', 'words')).toBe('MK')
    expect(initialsOf('LCD Soundsystem', 'words')).toBe('LS')
    expect(initialsOf("D'Angelo", 'words')).toBe('D')
  })

  it('stops at two initials however many words there are', () => {
    expect(initialsOf('Emerson Lake and Palmer', 'words')).toBe('EL')
  })

  it('handles a single character without throwing', () => {
    expect(initialsOf('A', 'chars')).toBe('A')
    expect(initialsOf('A', 'words')).toBe('A')
  })

  it('returns the empty string for an empty or blank name', () => {
    expect(initialsOf('', 'chars')).toBe('')
    expect(initialsOf('', 'words')).toBe('')
    expect(initialsOf('   ', 'words')).toBe('')
    expect(initialsOf('\n\t ', 'chars')).toBe('')
  })

  it('keeps non-ASCII letters intact', () => {
    expect(initialsOf('Björk', 'words')).toBe('B')
    expect(initialsOf('Ólafur Arnalds', 'words')).toBe('ÓA')
    expect(initialsOf('Björk', 'chars')).toBe('BJ')
    expect(initialsOf('Étoile', 'chars')).toBe('ÉT')
  })

  it('never splits a surrogate pair', () => {
    // 'chars' on a two-code-unit glyph must yield the whole glyph plus the next
    // character, not half of it followed by the replacement character.
    expect(initialsOf('\u{1D11E}x', 'chars')).toBe('\u{1D11E}X')
    expect(initialsOf('\u{1D11E}', 'chars')).toBe('\u{1D11E}')
    expect(initialsOf('\u{1D11E} Ensemble', 'words')).toBe('\u{1D11E}E')
  })

  it('collapses runs of whitespace rather than counting them as words', () => {
    expect(initialsOf('  Miles   Davis  ', 'words')).toBe('MD')
  })

  it('upper-cases the same way on every machine', () => {
    // Not toLocaleUpperCase: a Turkish locale would answer 'İ' here.
    expect(initialsOf('istanbul', 'chars')).toBe('IS')
  })
})

describe('initialsOrDash', () => {
  it('falls back to the em dash rather than an empty tile', () => {
    expect(initialsOrDash('', 'words')).toBe(EM_DASH)
    expect(initialsOrDash('   ', 'chars')).toBe(EM_DASH)
  })

  it('is initialsOf when there is anything to render', () => {
    expect(initialsOrDash('Radiohead', 'chars')).toBe('RA')
  })
})
