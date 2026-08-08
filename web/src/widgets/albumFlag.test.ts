import { describe, expect, it } from 'vitest'

import { albumFlag } from '@/widgets/albumFlag'
import { makeAlbum } from '@/test/factories'

describe('albumFlag', () => {
  it('flags a release with a corrupt file, whatever else is true of it', () => {
    // Corrupt AND replaced: the unplayable file is the one worth saying.
    const album = makeAlbum({ corrupt_tracks: 2, integrity_state: 'replaced' })

    expect(albumFlag(album)).toEqual({ label: 'CORRUPT', tone: 'bad' })
  })

  it('flags a release whose audio was replaced', () => {
    const album = makeAlbum({ corrupt_tracks: 0, integrity_state: 'replaced' })

    expect(albumFlag(album)).toEqual({ label: 'REPLACED', tone: 'warn' })
  })

  it('reads a null integrity_state as UNVERIFIED, not as clean', () => {
    // The trap this whole module exists for: `null` means nothing has ever
    // baselined a file here, which is the opposite of a clean bill of health.
    const album = makeAlbum({ corrupt_tracks: 0, integrity_state: null })

    expect(albumFlag(album)).toEqual({ label: 'UNVERIFIED', tone: 'warn' })
  })

  it('gives a verified release no badge at all', () => {
    const album = makeAlbum({ corrupt_tracks: 0, integrity_state: 'verified' })

    expect(albumFlag(album)).toBeNull()
  })

  it('gives a retagged release no badge — the audio is provably unchanged', () => {
    const album = makeAlbum({ corrupt_tracks: 0, integrity_state: 'retagged' })

    expect(albumFlag(album)).toBeNull()
  })

  it('says nothing about missing or mixed beyond what was measured', () => {
    for (const state of ['missing', 'mixed'] as const) {
      expect(albumFlag(makeAlbum({ corrupt_tracks: 0, integrity_state: state }))).toBeNull()
    }
  })

  it('never produces the word TAMPERED — this app performs no tamper check', () => {
    const states = [null, 'verified', 'retagged', 'replaced', 'missing', 'mixed'] as const
    const labels = states.flatMap((state) =>
      [0, 3].map(
        (corrupt) =>
          albumFlag(makeAlbum({ integrity_state: state, corrupt_tracks: corrupt }))?.label ??
          '',
      ),
    )

    expect(labels.some((label) => label.includes('TAMPER'))).toBe(false)
  })
})
