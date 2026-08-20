/**
 * Seconds as `m:ss`, or `h:mm:ss` from an hour.
 *
 * **Do not use this to re-render a duration the API already formatted.** The
 * catalogue emits track lengths as `m:ss`/`h:mm:ss` strings server-side, so that
 * the same decision is not in a second place; those strings win. This exists
 * only because the player works in seconds and nothing else does.
 *
 * It **floors**. A 245.9-second track that rounded would print a total of `4:06`
 * and then stop at `4:05`, which reads as a bug in the player rather than as the
 * rounding it is.
 *
 * Anything that is not a real, non-negative number of seconds — `NaN` before
 * metadata arrives, `Infinity` for a stream of unknown length — is `--:--`.
 */
export function formatTime(seconds: number): string {
  if (!Number.isFinite(seconds) || seconds < 0) return '--:--'

  const total = Math.floor(seconds)
  const hours = Math.floor(total / 3600)
  const minutes = Math.floor((total % 3600) / 60)
  const remainder = total % 60

  const ss = String(remainder).padStart(2, '0')
  if (hours === 0) return `${minutes}:${ss}`

  return `${hours}:${String(minutes).padStart(2, '0')}:${ss}`
}
