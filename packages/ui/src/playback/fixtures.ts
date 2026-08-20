/**
 * Audio for the stories, and only for the stories.
 *
 * Synthesized rather than fetched, for the reason `CatalogueCard`'s `cover()`
 * generates artwork: every story in this package is also a test run in a real
 * browser, and a story that loads a file over the network is a test that fails
 * when the network does.
 *
 * `cover()` percent-encodes because SVG is text and that keeps it readable in
 * dev tools. This cannot: PCM is binary and is not valid UTF-8, and
 * percent-encoding it would roughly triple the payload. Base64 costs 33%.
 */

import type { PlaybackTrack } from './PlaybackContext.ts'

const SAMPLE_RATE = 8_000
const BITS = 8
const CHANNELS = 1

export type ToneOptions = {
  readonly hz: number
  /** Keep it short. 8 kHz 8-bit mono is 8 KB a second, and no story listens. */
  readonly seconds?: number
}

/**
 * A decaying sine as a WAV data URI. Mono, 8-bit, 8 kHz — a test tone, not
 * music, and small enough that four of them are a rounding error in the bundle.
 *
 * Two details are the whole difficulty:
 *
 * - **8-bit PCM is unsigned**, with silence at 128; 16-bit is signed, with
 *   silence at 0. Getting that backwards produces a file that decodes perfectly
 *   and sounds like a buzz saw.
 * - The envelope's 5 ms attack and 20 ms release are not polish. Without them
 *   the waveform starts and ends on a discontinuity, which is an audible click
 *   and a DC step at `ended`.
 */
export function tone({ hz, seconds = 1.5 }: ToneOptions): string {
  const frames = Math.floor(SAMPLE_RATE * seconds)
  const bytesPerFrame = (CHANNELS * BITS) / 8
  const dataLength = frames * bytesPerFrame

  const buffer = new ArrayBuffer(44 + dataLength)
  const view = new DataView(buffer)

  // The canonical 44-byte RIFF header. Multi-byte fields are little-endian —
  // that is what the `true` argument means and it is not optional.
  writeAscii(view, 0, 'RIFF')
  view.setUint32(4, 36 + dataLength, true)
  writeAscii(view, 8, 'WAVE')
  writeAscii(view, 12, 'fmt ') // four bytes: the trailing space is part of it
  view.setUint32(16, 16, true) // PCM fmt chunk size
  view.setUint16(20, 1, true) // format tag: integer PCM
  view.setUint16(22, CHANNELS, true)
  view.setUint32(24, SAMPLE_RATE, true)
  view.setUint32(28, SAMPLE_RATE * bytesPerFrame, true) // byte rate
  view.setUint16(32, bytesPerFrame, true) // block align
  view.setUint16(34, BITS, true)
  writeAscii(view, 36, 'data')
  view.setUint32(40, dataLength, true)

  const attack = 0.005
  const release = 0.02

  for (let frame = 0; frame < frames; frame += 1) {
    const t = frame / SAMPLE_RATE
    const remaining = seconds - t
    const ramp = Math.min(1, t / attack, Math.max(0, remaining / release))
    const amplitude = ramp * Math.exp(-3 * t)
    const sample = 128 + Math.round(127 * amplitude * Math.sin(2 * Math.PI * hz * t))
    view.setUint8(44 + frame, Math.min(255, Math.max(0, sample)))
  }

  return `data:audio/wav;base64,${base64(new Uint8Array(buffer))}`
}

function writeAscii(view: DataView, offset: number, text: string): void {
  for (let index = 0; index < text.length; index += 1) {
    view.setUint8(offset + index, text.charCodeAt(index))
  }
}

/**
 * In 8 KB chunks. Spreading sixteen thousand elements into one call is within
 * V8's argument limit today, and is exactly the kind of thing that stops being
 * within it.
 */
function base64(bytes: Uint8Array): string {
  let latin1 = ''
  for (let start = 0; start < bytes.length; start += 8_192) {
    latin1 += String.fromCharCode(...bytes.subarray(start, start + 8_192))
  }
  return globalThis.btoa(latin1)
}

/* Pitched apart, so a person reviewing the stories can hear which row is live. */

export const OFF_THE_WALL_TRACK: PlaybackTrack = {
  id: 'otw-1',
  src: tone({ hz: 220 }),
  title: 'Don’t Stop ’Til You Get Enough',
  subtitle: 'Michael Jackson — Off the Wall',
  duration: 1.5,
}

export const ROCK_WITH_YOU_TRACK: PlaybackTrack = {
  id: 'otw-2',
  src: tone({ hz: 277 }),
  title: 'Rock with You',
  subtitle: 'Michael Jackson — Off the Wall',
  duration: 1.5,
}

export const WORKING_DAY_TRACK: PlaybackTrack = {
  id: 'otw-3',
  src: tone({ hz: 330 }),
  title: 'Working Day and Night',
  subtitle: 'Michael Jackson — Off the Wall',
  duration: 1.5,
}

export const GET_ON_THE_FLOOR_TRACK: PlaybackTrack = {
  id: 'otw-4',
  src: tone({ hz: 440 }),
  title: 'Get on the Floor',
  subtitle: 'Michael Jackson — Off the Wall',
  duration: 1.5,
}

export const OFF_THE_WALL_TITLE_TRACK: PlaybackTrack = {
  id: 'otw-5',
  src: tone({ hz: 494 }),
  title: 'Off the Wall',
  subtitle: 'Michael Jackson — Off the Wall',
  duration: 1.5,
}

export const TONE_TRACKS: readonly PlaybackTrack[] = [
  OFF_THE_WALL_TRACK,
  ROCK_WITH_YOU_TRACK,
  WORKING_DAY_TRACK,
  GET_ON_THE_FLOOR_TRACK,
  OFF_THE_WALL_TITLE_TRACK,
]

/**
 * A truncated WAV under a real MIME type. It fails deterministically, with no
 * network, and produces the same `MediaError` code a 404 would — which is the
 * point, and is why the error copy claims neither cause.
 */
export const BROKEN_TRACK: PlaybackTrack = {
  id: 'broken',
  src: 'data:audio/wav;base64,AAAAAA==',
  title: 'A file that is not there',
  subtitle: 'Michael Jackson — Off the Wall',
}

/** Long enough on paper to exercise `h:mm:ss`, without being long in fact. */
export const LONG_TRACK: PlaybackTrack = {
  id: 'long',
  src: tone({ hz: 165 }),
  title: 'Symphony No. 5 in C minor, Op. 67',
  subtitle: 'Berliner Philharmoniker, Herbert von Karajan',
  duration: 3_847,
}
