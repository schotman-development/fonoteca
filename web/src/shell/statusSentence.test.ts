import { describe, expect, it } from 'vitest'

import { statusSentence } from '@/shell/statusSentence'

const idle = {
  scanning: false,
  downloading: false,
  hashing: false,
  currentAlbum: null,
  lastScanAt: null,
}

describe('statusSentence', () => {
  it('puts the scan first — it is the press the top bar offers', () => {
    expect(
      statusSentence({ ...idle, scanning: true, downloading: true, hashing: true }),
    ).toBe('Scanning the library…')
  })

  it('names the album the worker is on', () => {
    expect(
      statusSentence({ ...idle, downloading: true, currentAlbum: 'Blues Of Desperation' }),
    ).toBe('Downloading Blues Of Desperation…')
  })

  it('says only “Downloading” when the worker has not published a title', () => {
    expect(statusSentence({ ...idle, downloading: true })).toBe('Downloading…')
  })

  it('reports an integrity pass when nothing louder is happening', () => {
    expect(statusSentence({ ...idle, hashing: true })).toBe('Verifying file integrity…')
  })

  it('says “never scanned” rather than inventing the design’s 08:00', () => {
    expect(statusSentence(idle)).toBe('Idle · never scanned')
  })

  it('reports the last scan when there has been one', () => {
    const sentence = statusSentence({ ...idle, lastScanAt: new Date().toISOString() })
    expect(sentence.startsWith('Idle · last scan ')).toBe(true)
  })
})
