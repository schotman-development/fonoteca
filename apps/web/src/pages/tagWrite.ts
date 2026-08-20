/**
 * What the file itself ended up saying, in English.
 *
 * `POST /api/catalogue/matching/recordings/{id}/decision` answers with a `tag`
 * field, and its values are a `TagWriteStatus` name plus two the enum
 * deliberately does not carry — `NotAttempted`, where there was no cluster to
 * write, and `Unreadable`, where the file could not be parsed safely enough to
 * change. This is the one place any of them become a sentence.
 *
 * **Only one of these is a failure, and it is not the loud-looking one.**
 * `Refused` is the ordinary answer while `Fonoteca:AllowFileMutation` is off,
 * which is the default: the decision is committed, the catalogue is right, and
 * the bytes on disk were not touched on purpose. Reading that as an error would
 * teach somebody to ignore the one message that is.
 *
 * The tone follows the worklist's rule — refusing is a first-class answer — so
 * nothing here is `danger` except a write that actually went wrong.
 */

import type { BadgeTone } from '@fonoteca/ui'

export type TagWriteReading = {
  readonly label: string
  readonly tone: BadgeTone
  /** Said only where the bare label would leave somebody guessing. */
  readonly note?: string
}

const TAG_WRITE: Readonly<Record<string, TagWriteReading>> = {
  Written: {
    label: 'Written into the file',
    tone: 'success',
    note: 'Verified by both tag libraries and by a length check before the swap, and reversible from the undo journal.',
  },
  NothingToDo: {
    label: 'The file already said so',
    tone: 'success',
    note: 'The AcoustID was already in the tags, so nothing was opened for writing.',
  },
  Refused: {
    label: 'Not written — mutation is off',
    tone: 'info',
    note: 'Fonoteca:AllowFileMutation is false, so everything except the write happened. The catalogue is decided; flip the flag and the identification pass will tag it without spending a lookup.',
  },
  NotAttempted: {
    label: 'Nothing to write',
    tone: 'neutral',
  },
  Unreadable: {
    label: 'The file could not be read safely',
    tone: 'warning',
    note: 'Two tag libraries have to agree about what is in the file before it is changed, and one of them could not parse it. The decision stands; the file was left alone.',
  },
  VerificationFailed: {
    label: 'Abandoned after checking',
    tone: 'warning',
    note: 'The staged copy did not verify, so it was discarded and the original is byte-for-byte what it was.',
  },
  Unsupported: {
    label: 'This container cannot carry an AcoustID',
    tone: 'neutral',
  },
  Failed: {
    label: 'The write failed',
    tone: 'danger',
  },
}

/** The fallback, so a status added to the API renders as itself rather than as nothing. */
export function readTagWrite(tag: string): TagWriteReading {
  return TAG_WRITE[tag] ?? { label: tag, tone: 'neutral' }
}
