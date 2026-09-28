import type { components } from '@fonoteca/api-client'

import { leaf } from './artistAlbums.ts'

type TrackRow = components['schemas']['TrackRow']

/** One recording of a piece: who, on which album, and how many of its tracks are held. */
export type Performance = {
  readonly performers: string | null
  readonly album: string
  readonly albumId: string | null
  readonly year: number | null
  readonly tracks: number
}

/** A piece, as its movements' titles name it, and every recording of it held. */
export type Composition = {
  readonly title: string
  readonly performances: readonly Performance[]
}

/**
 * The piece a movement belongs to: the work title before the first ": ".
 *
 * `workGroups.ts`'s cut, for its reason — the link points at the movement and
 * the parent work is not stored — but without its designator test: here a
 * work with no ": " is a piece in one part, an overture or a song, and still
 * one of the things a composer wrote.
 */
export function pieceOf(workTitle: string): string {
  const title = workTitle.trim()
  const cut = title.indexOf(': ')
  return cut > 0 ? title.slice(0, cut) : title
}

/**
 * A composer's pieces, each with the recordings of it the library holds, most
 * recorded first.
 *
 * Only tracks this artist is credited on as composer, and only those with a
 * work — a track with none cannot say which piece it is. A recording is one
 * album and one set of performers, so two orchestras on one compilation are
 * two recordings, and one orchestra's movements spread over a disc are one.
 */
export function worksOf(tracks: readonly TrackRow[]): Composition[] {
  type Building = { -readonly [K in keyof Performance]: Performance[K] }

  const pieces = new Map<string, Map<string, Building>>()

  for (const track of tracks) {
    if (!track.roles.includes('composer') || track.workTitle == null) continue

    const title = pieceOf(track.workTitle)
    if (title.length === 0) continue

    const key = JSON.stringify([track.album?.albumId ?? track.folder, track.performers])

    const recordings = pieces.get(title) ?? new Map<string, Building>()
    pieces.set(title, recordings)

    const recording = recordings.get(key)

    if (recording === undefined) {
      recordings.set(key, {
        performers: track.performers,
        album: track.album?.title ?? leaf(track.folder),
        albumId: track.album?.albumId ?? null,
        year: track.album?.year ?? null,
        tracks: 1,
      })
    } else {
      recording.tracks += 1
    }
  }

  return [...pieces]
    .map(([title, recordings]) => ({
      title,
      performances: [...recordings.values()].sort(
        (a, b) => (a.year ?? 9999) - (b.year ?? 9999) || a.album.localeCompare(b.album),
      ),
    }))
    .sort((a, b) => b.performances.length - a.performances.length || a.title.localeCompare(b.title))
}
