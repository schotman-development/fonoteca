import type { components } from '@fonoteca/api-client'

type QobuzStatus = components['schemas']['QobuzStatusResponse']
type TrackOutcome = components['schemas']['TrackOutcome']

/**
 * The English half of the acquisition screen.
 *
 * Imports no React and no stylesheet, so it runs under `node --test` — the same
 * reason `seating.ts` and `openQuestions.ts` are separate files. What lives here
 * is everything a test can assert without a browser: which settings are
 * missing, what a format id means, what an outcome means.
 *
 * The readiness rule is the one worth the file on its own. Qobuz fails in three
 * places that look identical from a component — an unconfigured instance, a
 * configured one that cannot sign a download, and a configured one with nowhere
 * to put the bytes — and only the third is obvious from the symptom.
 */

/**
 * What asking for each format id gets you.
 *
 * Qobuz serve the best encoding a release actually has up to the one requested,
 * so these are ceilings rather than promises: 27 against a CD master returns
 * 16-bit / 44.1 kHz, and the response says so.
 */
export const FORMATS: Readonly<Record<number, string>> = {
  5: 'MP3 320 kbps',
  6: 'FLAC 16-bit / 44.1 kHz',
  7: 'FLAC up to 24-bit / 96 kHz',
  27: 'FLAC up to 24-bit / 192 kHz',
}

export function formatLabel(id: number): string {
  return FORMATS[id] ?? `Format ${id}`
}

/**
 * What actually came back, from the numbers Qobuz stated about the file.
 *
 * Separate from {@link formatLabel} because they answer different questions —
 * one is what was asked for, the other is what was served, and on a CD-quality
 * release under a hi-res request they differ. Reporting the request as though
 * it were the result is how a library ends up believing it holds hi-res.
 */
export function qualityLabel(
  bitDepth: number | null | undefined,
  samplingRate: number | null | undefined,
): string | null {
  if (bitDepth == null && samplingRate == null) return null

  const parts: string[] = []
  if (bitDepth != null) parts.push(`${bitDepth}-bit`)
  if (samplingRate != null) parts.push(`${samplingRate} kHz`)

  return parts.join(' / ')
}

export type Readiness =
  | { readonly kind: 'ready'; readonly detail: string }
  | { readonly kind: 'browse-only'; readonly detail: string; readonly settings: readonly string[] }
  | { readonly kind: 'unconfigured'; readonly detail: string; readonly settings: readonly string[] }

/**
 * What this instance can currently do, and what to set if the answer is "less
 * than you wanted".
 *
 * Three states rather than a boolean, because the middle one is real and is the
 * one nobody expects: search and album browsing need only the app id and the
 * token, so an instance with no app secret works perfectly until the moment
 * somebody presses Download. Reporting that as "configured" is what makes the
 * failure arrive at the worst possible time.
 */
export function readiness(status: QobuzStatus): Readiness {
  if (!status.configured) {
    return {
      kind: 'unconfigured',
      detail:
        'Nothing can be searched or downloaded until this instance knows which subscription to use.',
      settings: ['Fonoteca__Providers__Qobuz__AppId', 'Fonoteca__Providers__Qobuz__UserAuthToken'],
    }
  }

  if (!status.canDownload) {
    return {
      kind: 'browse-only',
      detail: 'Searching works. Downloads cannot be signed without the app secret.',
      settings: ['Fonoteca__Providers__Qobuz__AppSecret'],
    }
  }

  return {
    kind: 'ready',
    detail: `Asking for ${formatLabel(status.formatId)}.`,
  }
}

export const OUTCOMES: Readonly<
  Record<TrackOutcome, { readonly label: string; readonly tone: 'success' | 'neutral' | 'danger' }>
> = {
  Downloaded: { label: 'Downloaded', tone: 'success' },
  Skipped: { label: 'Skipped', tone: 'neutral' },
  Failed: { label: 'Failed', tone: 'danger' },
}

/** `3:42`, from whole seconds. Qobuz's stated length, which is a claim rather than a measurement. */
export function duration(seconds: number | null | undefined): string | null {
  if (seconds == null || !Number.isFinite(seconds) || seconds < 0) return null

  const minutes = Math.floor(seconds / 60)
  const remainder = Math.floor(seconds % 60)

  return `${minutes}:${remainder.toString().padStart(2, '0')}`
}

/**
 * Bytes, at one decimal place, in the units a person reads.
 *
 * Binary units against decimal labels is the usual fudge here and it is not
 * worth the argument: what this number is for is "did a whole album arrive",
 * and 1000 versus 1024 never changes that answer.
 */
export function fileSize(bytes: number | null | undefined): string | null {
  if (bytes == null || !Number.isFinite(bytes) || bytes < 0) return null
  if (bytes < 1000) return `${bytes} B`

  const units = ['kB', 'MB', 'GB']
  let value = bytes / 1000
  let unit = 0

  while (value >= 1000 && unit < units.length - 1) {
    value /= 1000
    unit += 1
  }

  return `${value.toFixed(1)} ${units[unit]}`
}

/**
 * One line summarising a finished download.
 *
 * Built here rather than in the component because it has to stay honest about
 * three different numbers — what was fetched, what was already there, and what
 * the album actually has. A download reporting "12 tracks" when eleven were
 * skipped and Qobuz lists fourteen is three lies in one sentence.
 */
export function downloadSummary(result: {
  readonly downloaded: number
  readonly trackCount: number
  readonly tracks: readonly { readonly outcome: TrackOutcome }[]
}): string {
  const failed = result.tracks.filter((track) => track.outcome === 'Failed').length
  const skipped = result.tracks.filter((track) => track.outcome === 'Skipped').length

  const parts = [`${result.downloaded} of ${result.trackCount} downloaded`]
  if (skipped > 0) parts.push(`${skipped} skipped`)
  if (failed > 0) parts.push(`${failed} failed`)

  return parts.join(', ')
}
