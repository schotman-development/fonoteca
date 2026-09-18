/**
 * The pure half of the Identify screen: which file goes on which track, and what
 * is wrong with that.
 *
 * Nothing here imports React, a stylesheet or the API client at runtime, so it
 * runs under `node --test` — the rule `seating.ts` and `files.ts` state.
 */

import type { components } from '@fonoteca/api-client'

import { queryFor } from './seating.ts'

type FileRow = components['schemas']['IdentifyFileRow']
type FolderTags = components['schemas']['IdentifyFolderTags']
type Slot = components['schemas']['ReleaseSlotRow']

/** More than this many seconds off the track and a seated file is worth a notice. */
export const DRIFT_WARNING_S = 8

export type Row =
  | {
      readonly kind: 'seated'
      readonly slot: Slot
      readonly file: FileRow
      /** Seconds, file minus track. Null when either length is unknown. */
      readonly drift: number | null
      /** `partial`: one title's words run unbroken inside the other's. */
      readonly title: TitleMatch
      /** Put there by the person, through the row's track picker. */
      readonly chosen: boolean
      /** The file's own disc and track tags name a different position. */
      readonly numberDiffers: boolean
    }
  | { readonly kind: 'empty'; readonly slot: Slot }
  | { readonly kind: 'held'; readonly slot: Slot }

export type TitleMatch = 'same' | 'partial' | 'differs'

/**
 * A person's choices, by media file id: the `slotKey` of the track a file goes
 * on, or null to leave it open. Applied before any rule.
 */
export type Picks = ReadonlyMap<string, string | null>

export function slotKey(slot: Slot): string {
  return `${slot.discNumber}-${slot.position}`
}

export type Seating = {
  readonly rows: readonly Row[]
  /** Open files the release has no track for. They stay open. */
  readonly unseated: readonly FileRow[]
}

/**
 * Case, accents and punctuation removed, letters of every script kept.
 *
 * `[^a-z0-9]` would reduce a Cyrillic title to nothing, and two empty strings
 * are equal — every Tchaikovsky movement would match every other.
 */
export function normalise(text: string): string {
  return text
    .normalize('NFKD')
    .replaceAll(/\p{M}/gu, '')
    .toLowerCase()
    .replaceAll(/[^\p{L}\p{N}]/gu, '')
}

function words(text: string): readonly string[] {
  return text
    .normalize('NFKD')
    .replaceAll(/\p{M}/gu, '')
    .toLowerCase()
    .split(/[^\p{L}\p{N}]+/u)
    .filter((word) => word !== '')
}

/**
 * `same` after normalising; `partial` when either title's words appear, whole
 * and in order, inside the other's — "Mars, the Bringer of War" in "The Planets,
 * op. 32: Mars, the Bringer of War. Allegro". Whole words, so "Intro" is not in
 * "Introduction". A title that normalises to nothing ("?", "...") matches nothing.
 */
export function titleMatch(file: string, track: string): TitleMatch {
  const mine = words(file)
  const theirs = words(track)
  if (mine.length === 0 || theirs.length === 0) return 'differs'
  if (mine.join('') === theirs.join('')) return 'same'

  const within = (short: readonly string[], long: readonly string[]) =>
    long.some((_, at) => short.every((word, offset) => long[at + offset] === word))

  return within(mine, theirs) || within(theirs, mine) ? 'partial' : 'differs'
}

/**
 * The disc a file claims, for ordering. The server fills `disc` from the DISC tag
 * or the number in the disc folder's name, and leaves it null when neither says.
 */
export function discOf(file: FileRow): number {
  return file.disc ?? 1
}

/**
 * Seats open files on a release's tracks, in three passes:
 *
 * 1. title, disc and track tag all agree;
 * 2. the title alone, but only where it is unambiguous — one open file and one
 *    free track carry it. "Intro", "Allegro" and a song played on two discs of a
 *    live set are not, and seating them by title alone swaps files silently;
 * 3. the disc and track the file's tags state.
 *
 * A person's `picks` come first and take the file out of every pass, so a file
 * left open stays open and a file put on a track bumps whatever a rule put there.
 *
 * A track another file in the folder already holds (`heldBy`) is never offered.
 * Titles that normalise to nothing never match.
 */
export function seat(
  files: readonly FileRow[],
  slots: readonly Slot[],
  picks: Picks = new Map(),
): Seating {
  const used = new Set<FileRow>()
  const seated = new Map<Slot, FileRow>()
  const open = slots.filter((slot) => slot.heldBy == null)

  for (const file of files) {
    if (!picks.has(file.mediaFileId)) continue
    used.add(file)
    const slot = open.find((each) => slotKey(each) === picks.get(file.mediaFileId))
    if (slot !== undefined && !seated.has(slot)) seated.set(slot, file)
  }
  const chosen = new Set(seated.values())

  // A file whose disc nobody states can only be placed by number on a one-disc
  // release. On a two-disc live set "Encore #3" is track 3 of either disc.
  const discs = new Set(slots.map((slot) => slot.discNumber)).size
  const onDisc = (file: FileRow, slot: Slot) =>
    file.disc == null ? discs === 1 : file.disc === slot.discNumber

  const take = (slot: Slot, file: FileRow | undefined) => {
    if (file === undefined || seated.has(slot) || used.has(file)) return
    seated.set(slot, file)
    used.add(file)
  }

  const titled = (title: string) =>
    title === '' ? [] : files.filter((file) => normalise(file.title) === title)

  for (const slot of open) {
    take(
      slot,
      titled(normalise(slot.title)).find(
        (file) => !used.has(file) && file.track === slot.position && onDisc(file, slot),
      ),
    )
  }

  for (const slot of open) {
    const title = normalise(slot.title)
    const sameTitleFiles = titled(title)
    // Held tracks count: a title that is also on a track already filed is not
    // unique, and this file may be a second copy of that track.
    const sameTitleSlots = slots.filter((each) => normalise(each.title) === title)

    const only = sameTitleFiles[0]

    // Not a file whose own track tag names another track of the same title —
    // that is a second copy of a track, filed or not, rather than this one.
    const copy =
      only?.track != null &&
      slots.some(
        (other) =>
          other !== slot &&
          other.position === only.track &&
          onDisc(only, other) &&
          normalise(other.title) === title,
      )

    if (
      sameTitleFiles.length === 1 &&
      sameTitleSlots.length === 1 &&
      !copy &&
      only !== undefined &&
      (only.disc == null || only.disc === slot.discNumber)
    ) {
      take(slot, only)
    }
  }

  for (const slot of open) {
    take(
      slot,
      files.find((file) => !used.has(file) && file.track === slot.position && onDisc(file, slot)),
    )
  }

  const rows = slots.map((slot): Row => {
    if (slot.heldBy != null) return { kind: 'held', slot }

    const file = seated.get(slot)
    if (file === undefined) return { kind: 'empty', slot }

    return {
      kind: 'seated',
      slot,
      file,
      drift:
        file.lengthMs == null || slot.durationMs == null
          ? null
          : Math.round((file.lengthMs - slot.durationMs) / 1000),
      title: titleMatch(file.title, slot.title),
      chosen: chosen.has(file),
      numberDiffers:
        file.track != null &&
        (file.track !== slot.position || (file.disc != null && file.disc !== slot.discNumber)),
    }
  })

  const placed = new Set(seated.values())
  return { rows, unseated: files.filter((file) => !placed.has(file)) }
}

export type Line = Row | { readonly kind: 'unseated'; readonly file: FileRow }

/**
 * The release's rows in the release's order, with each file that has no track
 * placed where it sits in the folder: before the first row whose file — or, for
 * a track without one, whose own position — comes after it.
 *
 * Disc, then track tag, then path order. Two files can carry the same track
 * number — a live rip tagged against a release that dropped a song — and the
 * folder's own order is what says which came first.
 */
export function interleave(seating: Seating, files: readonly FileRow[]): readonly Line[] {
  const at = (disc: number, track: number, order: number) =>
    disc * 100_000_000 + track * 10_000 + order
  const where = (file: FileRow) => {
    const order = files.indexOf(file) + 1
    return at(discOf(file), file.track ?? order, order)
  }

  const pending = [...seating.unseated].sort((left, right) => where(left) - where(right))
  const lines: Line[] = []

  for (const row of seating.rows) {
    const key =
      row.kind === 'seated' ? where(row.file) : at(row.slot.discNumber, row.slot.position, 0)

    while (pending[0] !== undefined && where(pending[0]) < key) {
      lines.push({ kind: 'unseated', file: pending.shift() as FileRow })
    }

    lines.push(row)
  }

  return [...lines, ...pending.map((file) => ({ kind: 'unseated' as const, file }))]
}

/**
 * Filed: a file a person put on the track, or one whose title matches it at least
 * in part. A partial match only ever confirms a track the disc and track tags
 * chose — the title passes seat on the whole title alone.
 */
export function filed(row: Row): boolean {
  return row.kind === 'seated' && (row.chosen || row.title !== 'differs')
}

export function pairsOf(
  seating: Seating,
): readonly { readonly file: string; readonly disc: number; readonly position: number }[] {
  return seating.rows.flatMap((row) =>
    row.kind === 'seated' && filed(row)
      ? [{ file: row.file.mediaFileId, disc: row.slot.discNumber, position: row.slot.position }]
      : [],
  )
}

/** The largest absolute drift among seated files, in seconds. */
export function worstDrift(seating: Seating): number {
  return Math.max(
    0,
    ...seating.rows.flatMap((row) =>
      row.kind === 'seated' && row.drift !== null ? [Math.abs(row.drift)] : [],
    ),
  )
}

export type Notice = {
  readonly key: string
  readonly tone: 'danger' | 'warning' | 'success'
  /** Rendered in monospace before `text` when present. */
  readonly file?: string
  readonly text: string
}

/** What would go wrong if this release were filed, one notice each. */
export function noticesOf(
  seating: Seating,
  discs = 1,
  picks: Picks = new Map(),
): readonly Notice[] {
  const seated = seating.rows.filter((row) => row.kind === 'seated')
  const renamed = seated.filter((row) => !filed(row))
  const renumbered = seated.filter((row) => filed(row) && !row.chosen && row.numberDiffers)
  const drifting = seated.filter(
    (row) => filed(row) && row.drift !== null && Math.abs(row.drift) > DRIFT_WARNING_S,
  )
  const empty = seating.rows.filter((row) => row.kind === 'empty')

  // A file somebody chose to leave open needs no telling.
  const notices: Notice[] = seating.unseated
    .filter((file) => picks.get(file.mediaFileId) !== null)
    .map((file) => ({
      key: `unseated:${file.mediaFileId}`,
      tone: 'danger',
      file: file.name,
      text: 'is not on this release. It stays open.',
    }))

  if (renamed.length > 0) {
    notices.push({
      key: 'renamed',
      tone: 'danger',
      text: `${renamed.length} of your files ${renamed.length === 1 ? 'has' : 'have'} a different title from the track ${renamed.length === 1 ? 'it' : 'they'} would go on, and will not be filed unless you pick the track.`,
    })
  }

  if (renumbered.length > 0) {
    notices.push({
      key: 'renumbered',
      tone: 'warning',
      text: `${renumbered.length} file${renumbered.length === 1 ? '' : 's'} will be filed on a different track than ${renumbered.length === 1 ? 'its' : 'their'} own track tag says: ${renumbered.map((row) => label(row.slot, discs)).join(', ')}.`,
    })
  }

  if (drifting.length > 0) {
    notices.push({
      key: 'drifting',
      tone: 'warning',
      text: `${drifting.length} file${drifting.length === 1 ? ' is' : 's are'} more than ${DRIFT_WARNING_S} seconds off the track length: ${drifting.map((row) => label(row.slot, discs)).join(', ')}.`,
    })
  }

  if (empty.length > 0) {
    notices.push({
      key: 'empty',
      tone: 'warning',
      text: `Track${empty.length === 1 ? '' : 's'} ${empty.map((row) => label(row.slot, discs)).join(', ')} on this release ${empty.length === 1 ? 'has' : 'have'} no file.`,
    })
  }

  if (notices.length === 0) {
    notices.push({
      key: 'clean',
      tone: 'success',
      text: 'Every file has a track on this release.',
    })
  }

  return notices
}

/** `#7`, or `2-7` on a release with more than one disc. */
export function label(slot: Slot, discs: number): string {
  return discs > 1 ? `${slot.discNumber}-${slot.position}` : `#${slot.position}`
}

export type Fact = {
  readonly label: string
  readonly tags: string
  readonly release: string
  readonly state: 'same' | 'differs' | 'unknown'
}

/** The folder's tags beside the release's own facts. */
export function factsOf(
  tags: FolderTags,
  files: number,
  release: {
    readonly title: string
    readonly artist?: string | null
    readonly year?: number | null
    readonly discCount: number
    readonly label?: string | null
    readonly tracks: number
  },
): readonly Fact[] {
  const text = (
    name: string,
    mine: string | null | undefined,
    theirs: string | null | undefined,
  ): Fact => ({
    label: name,
    tags: mine ?? 'none',
    release: theirs ?? 'not listed',
    state:
      mine == null || theirs == null
        ? 'unknown'
        : normalise(mine) === normalise(theirs)
          ? 'same'
          : 'differs',
  })

  const number = (
    name: string,
    mine: number | null | undefined,
    theirs: number,
    unit = '',
  ): Fact => ({
    label: name,
    tags: mine == null ? 'none' : `${mine}${unit}`,
    release: `${theirs}`,
    state: mine == null ? 'unknown' : mine === theirs ? 'same' : 'differs',
  })

  return [
    text('Album', tags.album, release.title),
    text('Artist', tags.artist, release.artist),
    text('Year', tags.year?.toString(), release.year?.toString()),
    {
      ...number('Tracks', files, release.tracks),
      tags: `${files} file${files === 1 ? '' : 's'}`,
      release: `${release.tracks} track${release.tracks === 1 ? '' : 's'}`,
    },
    number('Discs', tags.discs, release.discCount),
    text('Label', tags.label, release.label),
  ]
}

/** What to search MusicBrainz for: the tags' artist and album, else the folder name. */
export function searchFor(tags: FolderTags, folder: string): string {
  const fromTags = [tags.artist, tags.album].filter((part) => part != null && part !== '').join(' ')
  return fromTags !== '' ? fromTags : queryFor(folder)
}

/** `m:ss`, or `h:mm:ss` past the hour, from milliseconds. */
export function clock(ms: number): string {
  const total = Math.round(ms / 1000)
  const hours = Math.floor(total / 3600)
  const minutes = Math.floor((total % 3600) / 60)
  const seconds = String(total % 60).padStart(2, '0')

  return hours > 0
    ? `${hours}:${String(minutes).padStart(2, '0')}:${seconds}`
    : `${minutes}:${seconds}`
}
