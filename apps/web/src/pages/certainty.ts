/**
 * How the attribution outcomes read on screen.
 *
 * Shared between the album list and the album page so that one answer never
 * carries two names. The three that can appear on a filed album are here; the
 * refusals never reach a release page, because a refused file has no release.
 *
 * `Attributed` deliberately renders as nothing at all. It is the ordinary case
 * and a badge on every album would be noise that trains the eye to skip the two
 * badges that matter.
 */
export const CERTAINTY: Readonly<
  Record<
    string,
    { readonly label: string; readonly tone: 'ok' | 'warning' | 'neutral'; readonly note: string }
  >
> = {
  Attributed: {
    label: 'Certain',
    tone: 'ok',
    note: 'One release fitted these files, and nothing else fitted as well.',
  },
  AttributedAmbiguously: {
    label: 'One of several pressings',
    tone: 'warning',
    note:
      'Several editions fitted exactly as well and agreed about where every track sits, so one was ' +
      'chosen — official first, then earliest. Nothing the catalogue stores differs between them, ' +
      'but the choice was a coin flip.',
  },
  GroupOnly: {
    label: 'Album known, pressing not',
    tone: 'warning',
    note:
      'The editions that fitted disagree about which disc and track this music sits on, so no ' +
      'pressing was named — a track number here would be an invention.',
  },
}
