/**
 * What the enrichment pass learned about an artist, as a person reads it.
 *
 * Pure, and in its own file for the reason `seating.ts` and `files.ts` state:
 * it imports no React and no stylesheet, so it runs under `node --test` with no
 * build step. The life span has five arms and one of them is a MusicBrainz
 * state that reads as its opposite if you get it wrong, which is more branches
 * than belongs inline in JSX.
 */

/**
 * "1908–1989", "1962–", "1962–?", "–1989", or nothing at all.
 *
 * **`ended` is not `endedYear != null`.** MusicBrainz records "this artist's
 * life span is over" separately from when it ended, and a great many groups
 * carry the flag with no date. Read from the year alone, every one of them
 * prints as still going — which is the wrong answer in the one direction a
 * reader would never think to check. That case gets `1962–?`: the dash says it
 * is over, the question mark says nobody has dated it.
 *
 * An open span keeps its trailing dash rather than printing the year bare,
 * because "1962" beside a name reads as a release year on a page that is mostly
 * about albums.
 */
export function lifeSpan(artist: {
  beganYear: number | null
  endedYear: number | null
  ended: boolean
}): string | null {
  const { beganYear, endedYear, ended } = artist

  if (beganYear == null && endedYear == null) return null
  if (beganYear == null) return `–${endedYear}`
  if (endedYear != null) return `${beganYear}–${endedYear}`

  return ended ? `${beganYear}–?` : `${beganYear}–`
}

/**
 * "AT" as "Austria", through the browser's own region names.
 *
 * `Intl.DisplayNames` is the whole implementation: a table of two hundred
 * countries is data the platform already ships, and shipping a second copy of
 * it would be a second copy to keep current when a country changes its name.
 *
 * MusicBrainz uses a handful of codes ISO does not — `XW` for worldwide, `XE`
 * for Europe, `SU` for the Soviet Union — and `fallback: 'code'` prints those
 * back as themselves, which is honest and short. The regex is what keeps a
 * malformed code from throwing: `of()` raises `RangeError` on anything that is
 * not two letters or three digits, and a `RangeError` out of a render is a
 * blank page rather than a blank field.
 *
 * `locale` defaults to the viewer's, which is what a browser should do and what
 * every caller wants. It is a parameter only so the test can pin one: asserting
 * "Austria" against the runtime default passes here and fails on any machine
 * whose ICU default is not English, and a test that depends on the developer's
 * locale is a test that fails in CI for a reason nobody can reproduce.
 */
export function countryName(code: string | null, locale?: string): string | null {
  if (code == null || !/^[A-Za-z]{2}$/.test(code)) return code

  return (
    new Intl.DisplayNames(locale, { type: 'region', fallback: 'code' }).of(code.toUpperCase()) ??
    code
  )
}
