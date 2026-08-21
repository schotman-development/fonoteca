/**
 * Turning what a person ticked on the worklist into what the API takes.
 *
 * **A module of its own because it broke in the browser and nothing could have
 * caught it.** The album screen sent `OpenQuestion.Id` — `recording:{guid}` —
 * where the request field is a bare `Guid`, so the body failed to deserialise
 * *before* the handler ran: none of the endpoint's own validation was reached,
 * the response was `text/plain` rather than a problem document, and the screen
 * showed "400 Bad Request" with nothing in it to read. The integration tests
 * could not have found it either, because they POST media file ids directly and
 * never see the worklist's id format at all.
 *
 * So the two vocabularies meet here, in a file with no React, no CSS module and
 * no client in it — which is what makes `node --test` able to run it.
 */

import type { components } from '@fonoteca/api-client'

type OpenQuestion = components['schemas']['OpenQuestion']
type ReleaseSlotRow = components['schemas']['ReleaseSlotRow']

/** One file seated on one position, as `POST matching/files/release` takes it. */
export type Seat = {
  readonly file: string
  readonly disc: number
  readonly position: number
}

/**
 * The media file id inside a `recording:{guid}` question id.
 *
 * Null for a `release:` question, whose id names a component stamp rather than
 * a file — see `componentStampOf`, which must never be collapsed into this one:
 * both are "the bit after the colon" and they identify different kinds of thing.
 */
export function mediaFileIdOf(question: OpenQuestion): string | null {
  if (question.kind !== 'recording') return null

  const id = question.id.startsWith('recording:') ? question.id.slice('recording:'.length) : ''
  return id.length > 0 ? id : null
}

/** A slot's identity on the wire and in a `<select>`: disc and position. */
export function seatKey(disc: number, position: number): string {
  return `${disc}-${position}`
}

/**
 * The seating, as the API takes it.
 *
 * Keyed on the *question* id throughout the screen — that is what the worklist,
 * the ticks and the row identity all use — and unwrapped exactly here.
 *
 * Anything that does not unwrap is dropped rather than sent. Unreachable, since
 * the folder list holds only recording questions, but the alternative is sending
 * a pair the server must reject, which fails the whole filing rather than the
 * one row.
 */
export function pairsFrom(
  files: readonly OpenQuestion[],
  seats: ReadonlyMap<string, string>,
): Seat[] {
  return files.flatMap((question) => {
    const held = seats.get(question.id)
    const file = mediaFileIdOf(question)

    if (held === undefined || file === null) return []

    const [disc, position] = held.split('-')
    const seat = { file, disc: Number(disc), position: Number(position) }

    return Number.isNaN(seat.disc) || Number.isNaN(seat.position) ? [] : [seat]
  })
}

/**
 * The pairing offered before anybody edits it: open files, in path order, onto
 * the positions nothing already holds.
 *
 * **The free-slot part is the whole of it, and it is why this is a function
 * rather than an index.** An album is almost never wholly unmatched — the passes
 * place most of a rip and leave behind the one or two files AcoustID could not
 * separate — but the worklist lists open questions, so such a folder arrives here
 * as a one-file album against a twenty-one slot release. Seating by raw index
 * then proposes track 1, which is wrong in a way that looks deliberate: the
 * number is plausible, the drift is the only thing contradicting it, and the
 * file it displaces is not on screen to notice.
 *
 * Seating onto the gaps instead gets the common case exactly right — one file,
 * one hole, and the default is already the answer — and collapses back to plain
 * index order on a folder where nothing is filed, since then every slot is free.
 *
 * It is still a guess and still fully editable. A rip missing its own track 7
 * with siblings already placed will still shift everything after the gap by one,
 * which is what the drift column on every row is for.
 */
export function defaultSeating(
  files: readonly OpenQuestion[],
  slots: readonly ReleaseSlotRow[],
): Map<string, string> {
  const free = slots.filter((slot) => slot.heldBy == null)

  return new Map(
    files.flatMap((file, index) => {
      const slot = free[index]

      return slot === undefined ? [] : [[file.id, seatKey(slot.discNumber, slot.position)] as const]
    }),
  )
}

/**
 * Signed distance between what the file measures and what the album prints.
 *
 * The file's length arrives pre-formatted as `m:ss`, so it is parsed back rather
 * than carried as a number — the worklist prints it and does no arithmetic on
 * it, and widening that endpoint for one screen would put a second length on
 * every one of seven hundred rows to serve the dozen a person opens.
 *
 * Which does mean this is accurate to the second where the slot is accurate to
 * the millisecond. Fine for what it is used for: the question on this screen is
 * "is this the right track", where the answer is tens of seconds out — not
 * "which pressing is this", where it is fractions of one and `ReleaseFit`
 * measures it properly.
 */
export function driftMs(
  length: string | null | undefined,
  printedMs: number | null | undefined,
): number | null {
  if (length == null || printedMs == null) return null

  const parts = length.split(':').map(Number)
  if (parts.length === 0 || parts.some(Number.isNaN)) return null

  const seconds = parts.reduce((total, part) => total * 60 + part, 0)
  return seconds * 1000 - printedMs
}

/**
 * What to search MusicBrainz for, guessed from the folder name.
 *
 * A starting point somebody edits, not an answer. The folder is the one claim
 * about these files that exists, and it is a claim this application otherwise
 * makes a point of not believing — the documented failure is a folder named for
 * a 1979 album holding the audio of the 2015 remaster. That caution is about
 * *deciding*; putting the name in a search box whose results a person then reads
 * costs nothing if it is wrong.
 *
 * The brackets go because rippers put the year, the codec and the bit depth in
 * them, and `[FLAC 24-192]` is three words of noise against an index of album
 * titles.
 */
export function queryFor(folder: string): string {
  return folder
    .split('/')
    .map((part) =>
      part
        .replaceAll(/\([^)]*\)/g, ' ')
        .replaceAll(/\[[^\]]*\]/g, ' ')
        .replaceAll(/\s+/g, ' ')
        .trim(),
    )
    .filter((part) => part !== '')
    .join(' ')
    .trim()
}

/**
 * The album folder a file sits in: `Artist/Album`.
 *
 * A two-disc rip arrives as two folders — `.../CD1` and `.../CD2` — which is two
 * headings for one album and one decision. Cutting at {@link ALBUM_FOLDER_DEPTH}
 * puts them back together.
 */
export function albumFolderOf(folder: string): string {
  const parts = folder.split('/')

  return parts.length <= ALBUM_FOLDER_DEPTH ? folder : parts.slice(0, ALBUM_FOLDER_DEPTH).join('/')
}

/**
 * How deep the album folder is.
 *
 * A fact about how *this* library is laid out, which nothing in the catalogue
 * records: measured, every open file sits at `Artist/Album` or one below it. A
 * genre-first tree would want three, and there is no way to tell which from a
 * path — guessing is worse than one edit here.
 */
export const ALBUM_FOLDER_DEPTH = 2
