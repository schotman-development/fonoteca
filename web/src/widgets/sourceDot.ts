/**
 * The categorical colour of a metadata source, as a token reference.
 *
 * `--c-src-*` exists because the hue here means *which one*, never *how bad* —
 * a source chip is identity, not status. Anything unrecognised gets the quiet
 * ink rather than a colour invented on the spot; a sixth source appearing in a
 * payload must not silently borrow MusicBrainz's red.
 */

const DOTS: Readonly<Record<string, string>> = {
  musicbrainz: 'var(--c-src-musicbrainz)',
  qobuz: 'var(--c-src-qobuz)',
  acoustid: 'var(--c-src-acoustid)',
  deezer: 'var(--c-src-deezer)',
  coverartarchive: 'var(--c-src-coverartarchive)',
  wikidata: 'var(--c-src-wikidata)',
}

export function sourceDot(source: string): string {
  return DOTS[source.trim().toLowerCase()] ?? 'var(--c-ink-mark)'
}
