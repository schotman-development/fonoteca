/**
 * The pure half of the acquisition screen.
 *
 * `node --test` with Node's own type stripping, matching `seating.ts` — no
 * runner dependency, and nothing here imports React, a stylesheet or the API
 * client at runtime.
 *
 * Most of it is the readiness rule. Qobuz has three settings that fail in three
 * different places, and the middle failure is the one that costs somebody an
 * evening: an instance with an app id and a token searches perfectly and cannot
 * download a thing, because signing needs a secret nothing else touches.
 */

import assert from 'node:assert/strict'
import { test } from 'node:test'

import {
  downloadSummary,
  duration,
  fileSize,
  formatLabel,
  qualityLabel,
  readiness,
} from './qobuz.ts'

const READY = {
  configured: true,
  canDownload: true,
  formatId: 27,
  libraryPath: '/mnt/music',
  busy: false,
}

test('a fully configured instance names the format it will ask for', () => {
  const state = readiness(READY)

  assert.equal(state.kind, 'ready')
  assert.match(state.detail, /24-bit \/ 192 kHz/)
})

test('an unconfigured instance names both settings it needs before anything works', () => {
  const state = readiness({ ...READY, configured: false, canDownload: false })

  assert.equal(state.kind, 'unconfigured')
  assert.deepEqual(state.settings, [
    'Fonoteca__Providers__Qobuz__AppId',
    'Fonoteca__Providers__Qobuz__UserAuthToken',
  ])
})

test('a missing app secret leaves search working and says so', () => {
  // The trap. `configured` is true, the screen works, and the failure waits
  // until somebody presses Download — where it arrives as a signature error
  // naming none of the four things that could have caused it.
  const state = readiness({ ...READY, canDownload: false })

  assert.equal(state.kind, 'browse-only')
  assert.deepEqual(state.settings, ['Fonoteca__Providers__Qobuz__AppSecret'])
  assert.match(state.detail, /Searching works/)
})

test('an unknown format id degrades to its number rather than lying', () => {
  assert.equal(formatLabel(27), 'FLAC up to 24-bit / 192 kHz')
  assert.equal(formatLabel(99), 'Format 99')
})

test('quality is reported from what came back, and is absent when nothing was stated', () => {
  assert.equal(qualityLabel(24, 96), '24-bit / 96 kHz')
  assert.equal(qualityLabel(16, null), '16-bit')
  assert.equal(qualityLabel(null, null), null)
})

test('durations are minutes and padded seconds', () => {
  assert.equal(duration(222), '3:42')
  assert.equal(duration(5), '0:05')
  assert.equal(duration(3600), '60:00')
  assert.equal(duration(null), null)
  assert.equal(duration(-1), null)
})

test('sizes climb units and stay at one decimal', () => {
  assert.equal(fileSize(512), '512 B')
  assert.equal(fileSize(1_500), '1.5 kB')
  assert.equal(fileSize(48_300_000), '48.3 MB')
  assert.equal(fileSize(null), null)
})

test('the summary counts skips and failures rather than hiding them in a total', () => {
  // Twelve tracks where eleven were already on disk is not "12 downloaded",
  // and an album Qobuz lists as fourteen is not complete at twelve.
  const summary = downloadSummary({
    downloaded: 1,
    trackCount: 14,
    tracks: [
      { outcome: 'Downloaded' },
      ...Array.from({ length: 11 }, () => ({ outcome: 'Skipped' as const })),
      { outcome: 'Failed' },
      { outcome: 'Failed' },
    ],
  })

  assert.equal(summary, '1 of 14 downloaded, 11 skipped, 2 failed')
})

test('a clean album says only what it did', () => {
  assert.equal(
    downloadSummary({
      downloaded: 2,
      trackCount: 2,
      tracks: [{ outcome: 'Downloaded' }, { outcome: 'Downloaded' }],
    }),
    '2 of 2 downloaded',
  )
})
