/**
 * The tag editor's rules: what a cell shows, what a person changed, and what
 * the save sends.
 *
 * A change is one of three things, and the difference is what the server
 * keeps: a value (set it), none (take the field out of the file), or reset
 * (forget the person's value, so the catalogue's — or the file's own — is
 * written again). Only what a person touched is sent, so a cell they never
 * changed can never overwrite anything.
 *
 * Nothing here imports React, a stylesheet or the API client at runtime, so it
 * runs under `node --test`. The numeric limits mirror `PersonTags.Problem` on
 * the server, which has the last word.
 */

import type { components } from '@fonoteca/api-client'

type TagEdit = components['schemas']['TagEdit']

/** What a person did to one cell. */
export type DraftCell =
  | { readonly kind: 'set'; readonly value: string }
  | { readonly kind: 'none' }
  | { readonly kind: 'reset' }

/** Every touched cell, keyed by {@link cellKey}. */
export type Draft = ReadonlyMap<string, DraftCell>

/** The album's own cells have no file. */
export const ALBUM = 'album'

export const FIELD_LABELS: Readonly<Record<string, string>> = {
  TITLE: 'Title',
  ARTIST: 'Artist',
  ALBUM: 'Album',
  ALBUMARTIST: 'Album artist',
  YEAR: 'Year',
  TRACKNUMBER: 'Track',
  TRACKTOTAL: 'Tracks',
  DISCNUMBER: 'Disc',
  DISCTOTAL: 'Discs',
  GENRE: 'Genre',
  COMPOSER: 'Composer',
  COMMENT: 'Comment',
}

/** The fields that are corrected for the whole album where the folder is one. */
export const ALBUM_FIELDS: readonly string[] = ['ALBUM', 'ALBUMARTIST', 'YEAR']

const NUMBERS: Readonly<Record<string, number>> = {
  YEAR: 9999,
  TRACKNUMBER: 999,
  TRACKTOTAL: 999,
  DISCNUMBER: 999,
  DISCTOTAL: 999,
}

export function cellKey(file: string | null, field: string): string {
  return `${file ?? ALBUM}:${field}`
}

export function label(field: string): string {
  return FIELD_LABELS[field] ?? field
}

/** What is wrong with a value a person typed, or null. */
export function problem(field: string, value: string): string | null {
  if (value.trim() === '') return 'Empty — use “None” to take it out of the file.'

  // Line breaks and tabs only in a comment, as the server allows.
  const allowed = field === 'COMMENT' ? [9, 10, 13] : []
  if ([...value].some((c) => c.charCodeAt(0) < 32 && !allowed.includes(c.charCodeAt(0)))) {
    return 'Holds a control character.'
  }

  const most = NUMBERS[field]
  if (most !== undefined) {
    const number = Number(value)
    if (!/^\d+$/.test(value) || number < 1 || number > most)
      return `A whole number from 1 to ${most}.`
  }

  return null
}

/**
 * What is wrong with naming a field of one's own this, or null. Mirrors
 * `PersonTags.NameProblem`: a name every container carries as a field of its
 * own, never an identity, never four characters (ATL writes those as an ID3
 * frame or an MP4 atom), never one ATL maps to a field of its own.
 */
export function nameProblem(name: string, fields: readonly string[]): string | null {
  if (name.length < 1 || name.length > 64) return 'A name is 1 to 64 characters.'
  if (!/^[A-Za-z][A-Za-z0-9 _.()-]*$/.test(name) || name.endsWith(' ')) {
    return 'Letters, digits, spaces and _ - . ( ), from a letter.'
  }
  if (fields.some((field) => field.toLowerCase() === name.toLowerCase())) {
    return 'That field is already here.'
  }

  const flat = name.toUpperCase().replaceAll(' ', '').replaceAll('_', '')
  if (flat.startsWith('MUSICBRAINZ') || flat.startsWith('ACOUSTID') || flat === 'UFID') {
    return 'Identities are answered on the Identify screen.'
  }
  if (name.length === 4 || (name.length === 3 && /^[A-Z0-9]+$/.test(name))) {
    return 'Not four characters: those are written as an ID3 frame or an MP4 atom.'
  }
  if (TAG_LIBRARY_OWN.has(name.toUpperCase()) || /^(info|bext)\./i.test(name)) {
    return 'The tag library writes that as a field of its own in some files.'
  }

  return null
}

/** Mirrors `PersonTags.TagLibraryOwn`: the names ATL maps to a field of its own. */
const TAG_LIBRARY_OWN: ReadonlySet<string> = new Set([
  'ALBUM ARTIST',
  'ALBUMARTISTSORT',
  'ARTISTSORT',
  'BPM',
  'CATALOGNUMBER',
  'CONDUCTOR',
  'COPYRIGHT',
  'DESCRIPTION',
  'ENCODED-BY',
  'ENCODEDBY',
  'ENCODER',
  'LABELNO',
  'LANGUAGE',
  'LYRICIST',
  'LYRICS',
  'ORIGINALDATE',
  'PREFERENCE',
  'PRODUCTNUMBER',
  'PUBLISHER',
  'RATING',
  'TOTALDISCS',
  'TOTALTRACKS',
  'TRACK',
  'VORBIS-VENDOR',
])

/** What the input shows: the person's draft where there is one, else the value the write would put in the file. */
export function shown(
  cell: { readonly value: string | null },
  draft: DraftCell | undefined,
): string {
  if (draft === undefined || draft.kind === 'reset') return cell.value ?? ''
  return draft.kind === 'set' ? draft.value : ''
}

/** Every file of the folder set to one value for one field, as the "all files" row does. */
export function setAll(
  draft: Draft,
  files: readonly string[],
  field: string,
  cell: DraftCell,
): Draft {
  const next = new Map(draft)
  for (const file of files) next.set(cellKey(file, field), cell)
  return next
}

/** What the save sends, in a stable order: album first, then by file and field. */
export function changesOf(draft: Draft): TagEdit[] {
  return [...draft.entries()]
    .sort(([a], [b]) => (a < b ? -1 : a > b ? 1 : 0))
    .sort(([a], [b]) => Number(b.startsWith(`${ALBUM}:`)) - Number(a.startsWith(`${ALBUM}:`)))
    .map(([key, cell]) => {
      const at = key.lastIndexOf(':')
      const owner = key.slice(0, at)
      const field = key.slice(at + 1)

      return {
        file: owner === ALBUM ? null : owner,
        field,
        value: cell.kind === 'set' ? cell.value : null,
        reset: cell.kind === 'reset',
      }
    })
}

/** The draft's first problem, for the save button to refuse with. */
export function firstProblem(draft: Draft): string | null {
  for (const [key, cell] of draft) {
    if (cell.kind !== 'set') continue
    const field = key.slice(key.lastIndexOf(':') + 1)
    const found = problem(field, cell.value)
    if (found !== null) return `${label(field)}: ${found}`
  }
  return null
}
