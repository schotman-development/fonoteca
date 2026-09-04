/**
 * A track list, folded into the works its tracks perform.
 *
 * Classical is the whole reason this exists. MusicBrainz titles a movement
 * "III. Rondo: Allegro", which on its own names nothing — the piece it belongs
 * to is a `Work`, one hop off the recording, and a symphony is four consecutive
 * rows that ought to point at it. Printing the work on every row says it four
 * times; printing it once above them is the album as anybody would write it
 * down.
 *
 * **The link points at the movement, not at the symphony, and that is the whole
 * problem this module solves.** `Recording.WorkId` reaches a *leaf* work titled
 * "Sinfonie Nr. 1 c-Moll, op. 68: II. Andante" — the parent piece lives in a
 * work-to-work "parts" relationship the catalogue does not store. Grouped on
 * the leaf, four movements are four distinct works and nothing groups at all:
 * measured against the real library, 6 of 564 albums grouped and Brahms's
 * complete symphonies rendered flat. Grouped on the part of the title before
 * the first ": ", which is how MusicBrainz composes "Parent: Part", it is 23
 * albums and Brahms is four headings of four movements each.
 *
 * **Consecutive runs, not a group-by.** A release prints its tracks in an
 * order somebody chose, and a work interleaved with another one on purpose — a
 * recital alternating two composers, an encore reprising the first piece — is a
 * fact about the album, not noise to be tidied away. Sorting the rows into
 * work buckets would renumber the album on screen, which is exactly the
 * invention the attribution pass refuses to make about folders.
 *
 * No React, no stylesheet, so `node --test` runs it — the same reason
 * `seating.ts` and `artistAlbums.ts` are their own modules.
 */

/** Anything with a title and, sometimes, the composition it performs. */
export type Performed = {
  readonly title: string
  readonly workTitle?: string | null | undefined
}

export type WorkGroup<T> = {
  /** Stable across renders, and unique even when a work is returned to later. */
  readonly key: string
  /** Null where the run gets no heading — see `workGroups`. */
  readonly workTitle: string | null
  readonly tracks: readonly T[]
  /**
   * What every title in this group repeats, for a screen that has just said it
   * once as the heading. Empty unless the whole group shares it — see
   * {@link sharedPrefix}. Slice it off a title to get the movement:
   * `track.title.slice(group.prefix.length)`.
   */
  readonly prefix: string
}

/** What a run is gathered under: what to compare, and what to print. */
type Heading = { readonly key: string; readonly title: string }

/**
 * Punctuation and case removed, so "Stop" and "Stop!" are one thing.
 *
 * MusicBrainz's work titles differ from a release's printed ones by an
 * exclamation mark or a curly apostrophe often enough that comparing them
 * literally lets most of the repeats through.
 */
function bare(value: string): string {
  return value.toLowerCase().replace(/[^\p{L}\p{N}]+/gu, '')
}

/**
 * The piece a track is a movement of, or null if it is not a movement of one.
 *
 * **The part designator is the whole test.** A leaf work titled "Sinfonie Nr. 1
 * c-Moll, op. 68: II. Andante" is a *movement of* something, and the something
 * — the text before the ": " — is what a heading should say and the only thing
 * its siblings share, since movements of one piece are separate works with
 * separate ids. A leaf with no designator IS the piece, and tracks that share
 * one are not parts of a structure: they are *different recordings of the same
 * song*. "Blue Train (false start)" and "Blue Train (alternate take 7)" are one
 * work twice over, and a "Blue Train" band above them announces nothing the two
 * rows do not already say.
 *
 * **Read off the work link, never off the printed title.** A release whose
 * recordings carry no work groups nothing here, and that is deliberate: the
 * titles do spell the structure out — "Symphony No. 1 in C major, Op. 21: I.
 * Adagio molto" — and reading them would be this screen inventing a catalogue
 * fact from a string, which is the thing the whole application refuses to do
 * with folder names. A release that does not group is a release the catalogue
 * has not enriched yet, and the fix is upstream in the enrichment pass.
 *
 * Requiring the designator is also what makes titles safe to key on. Titles are
 * not unique — this library holds four distinct works called "Main Theme",
 * Schindler's List and Superman among them — but a bare title like that never
 * reaches a heading, so the collision cannot be reached.
 *
 * ponytail: the cut takes the first ": ", so a work whose own title carries a
 * subtitle is shortened — "The Armed Man: A Mass for Peace: Benedictus" would
 * head its group as "The Armed Man". Measured across the library that is 24
 * titles out of 4,600, none of which reaches a heading. The upgrade, if it ever
 * matters, is the longest ": "-terminated prefix the run's titles actually
 * share, which recovers the subtitle without a parent link.
 */
function headingFor(track: Performed): Heading | null {
  const work = track.workTitle?.trim()
  if (work == null || work.length === 0) return null

  const cut = work.indexOf(': ')
  if (cut <= 0) return null

  const title = work.slice(0, cut)

  // A heading repeating the row beneath it is a band of colour buying nothing.
  if (bare(title) === bare(track.title)) return null

  return { key: title, title }
}

/**
 * The run-in every title in a group repeats, cut at a delimiter.
 *
 * A release prints its movements in full — "Le quattro stagioni, op. 8 (The
 * Four Seasons): Concerto no. 1, RV 269 “La primavera”: I. Allegro" — so under
 * a heading that has already named the concerto, the part a reader actually
 * needs is the eleven characters at the end, and it is the part that truncates
 * away first.
 *
 * **Not the heading's own text.** MusicBrainz's work is titled "Concerto in E
 * major, op. 8 no. 1, RV 269" where the release prints "Concerto no. 1, RV
 * 269", so matching the heading against the titles finds nothing on the very
 * album this is for. What the titles genuinely share is the answer, and they
 * are only in a group together because they share a work.
 *
 * **Cut back to the last ": ", or not at all.** A raw common prefix of "I.
 * Allegro" and "I. Andante" is "I. A", and slicing there leaves "llegro". The
 * delimiter is the one MusicBrainz composes these titles with, which is the
 * same one {@link parentWork} reads, so the cut lands where a human would put
 * it. Nothing shared up to a delimiter means nothing is removed.
 */
function sharedPrefix(titles: readonly string[]): string {
  const first = titles[0]
  if (first === undefined || titles.length < 2) return ''

  let end = first.length
  for (const title of titles) {
    let i = 0
    while (i < end && i < title.length && title[i] === first[i]) i += 1
    end = i
  }

  const cut = first.lastIndexOf(': ', end - 1)
  if (cut < 0) return ''

  const prefix = first.slice(0, cut + 2)

  // `startsWith` because `lastIndexOf`'s second argument bounds where a match
  // may *begin*: one starting at `end - 1` runs one character past the shared
  // part, so "Suite: I. Allegro" and "Suite:II. Adagio" yield a "Suite: "
  // that the second title does not carry — and slicing its length off it prints
  // movement II as "I. Adagio". Plausible, wrong, and nothing on the row
  // contradicts it.
  //
  // And a title that is nothing but the prefix would be blanked. Better to
  // repeat the run-in on every row than to print an empty one.
  return titles.every(
    (title) => title.startsWith(prefix) && title.slice(prefix.length).trim().length > 0,
  )
    ? prefix
    : ''
}

/**
 * Consecutive tracks sharing a work, or null when grouping would be noise.
 *
 * **A run of one gets no heading**, and that is the rule the first version of
 * this was missing. A band above a single row says what the row says, and
 * because these links are per-movement a real album is full of them: measured,
 * Shostakovich's cycle drew 47 such bands and a soul album drew 15. Worse, a
 * work MusicBrainz has mis-linked — and there are some — became a full-width
 * heading asserting a symphony the track underneath it disagrees with. Demoted
 * to an unheaded row it is back to being one questionable subtitle.
 *
 * Null rather than a group per row: an album where nothing survives that
 * demotion has no grouping in it, and headings above every line would lose the
 * scan-down-the-numbers reading a track list is for.
 */
export function workGroups<T extends Performed>(
  tracks: readonly T[],
): readonly WorkGroup<T>[] | null {
  const runs: { head: Heading | null; tracks: T[] }[] = []

  for (const track of tracks) {
    const head = headingFor(track)
    const open = runs.at(-1)

    // On the key, never on the printed title — see `headingFor`.
    if (open !== undefined && (open.head?.key ?? null) === (head?.key ?? null)) {
      open.tracks.push(track)
      continue
    }

    runs.push({ head, tracks: [track] })
  }

  // Demote the runs of one, then merge them into whatever unheaded run they now
  // sit against — two adjacent headingless runs are one stretch of plain rows.
  const folded: { head: Heading | null; tracks: T[] }[] = []

  for (const run of runs) {
    const head = run.tracks.length > 1 ? run.head : null
    const open = folded.at(-1)

    if (head === null && open !== undefined && open.head === null) {
      open.tracks.push(...run.tracks)
      continue
    }

    folded.push({ head, tracks: run.tracks })
  }

  if (!folded.some((group) => group.head !== null)) return null

  return folded.map((group, index) => ({
    key: `${index}:${group.head?.key ?? ''}`,
    workTitle: group.head?.title ?? null,
    tracks: group.tracks,
    // Only under a heading. An unheaded run has nothing above it that said the
    // run-in already, so taking it off would lose the words outright.
    prefix: group.head === null ? '' : sharedPrefix(group.tracks.map((track) => track.title)),
  }))
}
