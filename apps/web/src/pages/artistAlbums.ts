/**
 * An artist's tracks, folded into the albums they came from.
 *
 * The endpoint answers in recordings because that is what the browse rule is
 * about — the three ways of being responsible for a recording — and an artist
 * page is not a track list. Folding happens here rather than in SQL because
 * every fact it needs is already on the wire: the release each file was
 * attributed to, the folder it sits in, and why this artist has it.
 *
 * **Two kinds of album come out of it and they are not the same claim.** One
 * was decided by the attribution pass from the audio; the other is a directory
 * that nothing has worked out yet, and it is labelled as one all the way to the
 * screen. That distinction is the project's oldest rule and this is the first
 * place a folder gets to look like an album at all.
 *
 * No React, no stylesheet, so `node --test` runs it — the same reason
 * `seating.ts` is its own module.
 */

import type { components } from '@fonoteca/api-client'

import { albumFolderOf } from './seating.ts'

type TrackRow = components['schemas']['TrackRow']

export type ArtistAlbum = {
  /** Stable across renders: the release, or the folder standing in for one. */
  readonly key: string
  /** Null when no release was attributed — then this is a folder, and says so. */
  readonly releaseId: string | null
  /** The Cover Art Archive's key. Null for a folder, and for a minted release. */
  readonly mbid: string | null
  readonly title: string
  readonly year: number | null
  /** True when the year was read off the directory name rather than MusicBrainz. */
  readonly yearFromFolder: boolean
  /**
   * Every album folder its files sit in, cut at `Artist/Album` — so a two-disc
   * rip is one folder and not two. More than one after that cut is a real
   * disagreement and worth seeing: the same album ripped twice, or a directory
   * holding two.
   */
  readonly folders: readonly string[]
  /** Why this artist has it: billed, conductor, ensemble, composer. */
  readonly roles: readonly string[]
  readonly trackCount: number
  readonly fileCount: number
  /**
   * How many of those tracks no release was attributed to — the hole in a
   * part-placed album, counted rather than shown as a second card.
   */
  readonly unplaced: number
}

/** The album-shaped end of a library-relative directory. */
export function leaf(folder: string): string {
  const cut = folder.lastIndexOf('/')
  return cut < 0 ? folder : folder.slice(cut + 1)
}

/**
 * What a directory name claims, which is a title and — often — a year.
 *
 * `Rumours (1977)`, `[1977] Rumours` and `Rumours - 1977` are all in the wild,
 * so the year is taken from either end and the brackets around it are optional.
 * Anything outside 1900–2099 is left in the title, and so is a year that is
 * plainly part of one: `Blink-182`, `Greatest Hits (1971-1975)`, `The 1975`.
 *
 * What is left in square brackets afterwards is a rip's own note about itself —
 * `[FLAC 24-44.1]` — and it is dropped, for the reason `queryFor` already drops
 * it: it is three words of noise on a line that has to read as an album title.
 * Round brackets stay, because `(Live)`, `(Remaster)` and
 * `(20th anniversary edition)` are the title.
 */
export function fromFolder(folder: string): { title: string; year: number | null } {
  const name = leaf(folder)

  // Three forms, tried in order of how much they assert. A bracketed year has
  // to be *closed* — the earlier one-regex version accepted a lone opening
  // bracket and turned `Off the Wall (2015 Remaster)` into
  // `Off the Wall Remaster)`, which is the title with a bracket knocked off it.
  // A leading bare year must be followed by a word rather than by a bracket, or
  // `1917 (Original Score)` becomes `(Original Score)`.
  //
  // It is deliberately *not* also guarded against a following dash, and that
  // was measured rather than reasoned: guarding it protects
  // `2001 - A Space Odyssey`, which this library does not contain, and costs
  // the five folders that do use `2019 - Texas Honey`. The two are
  // structurally identical and no rule can tell them apart, so the one the
  // evidence shows wins.
  const year =
    /[([]((?:19|20)\d{2})[)\]]/.exec(name) ??
    /^((?:19|20)\d{2})(?=\s+[^\s([])/.exec(name) ??
    /[-–] ((?:19|20)\d{2})(?=\s|$)/.exec(name)

  if (year === null) return { title: tidy(name, name), year: null }

  return {
    title: tidy(name.slice(0, year.index) + name.slice(year.index + year[0].length), name),
    year: Number(year[1]),
  }
}

/** The leftovers of a folder name, as a title. Falls back to what came in. */
function tidy(title: string, fallback: string): string {
  const clean = title
    .replaceAll(/\[[^\]]*\]/g, ' ')
    .replaceAll(/\s+/g, ' ')
    .replace(/^[\s\-–_]+|[\s\-–_]+$/g, '')
    .trim()

  return clean.length > 0 ? clean : fallback
}

/**
 * The artist's discography, oldest first.
 *
 * Ordered by year with the undated last, so a page reads as a discography
 * rather than as an alphabet. A track with no attributed release groups by its
 * folder, which is why the two kinds sort together: a folder that names its
 * year sits where it belongs among the albums rather than in a heap at the end.
 */
export function albumsOf(tracks: readonly TrackRow[]): ArtistAlbum[] {
  // The same shape with the readonly lifted, rather than a second declaration
  // that could drift from it. The two arrays need saying again: `-readonly`
  // lifts the property modifier and not the element type's.
  type Building = { -readonly [K in keyof ArtistAlbum]: ArtistAlbum[K] } & {
    folders: string[]
    roles: string[]
  }

  const albums = new Map<string, Building>()

  // How many of each folder's tracks each album accounts for. The merge below
  // cannot be decided without it: an album's own `folders` list says it has a
  // track somewhere in there, not that it is what that directory holds.
  const claims = new Map<string, Map<string, number>>()

  for (const track of tracks) {
    // `albumFolderOf` rather than the raw path, and it is load-bearing on both
    // sides: an unplaced two-disc rip would otherwise be two albums titled
    // "CD 01" and "CD 02", and a placed one would report a disagreement between
    // two folders that are the same album.
    const folder = albumFolderOf(track.folder)
    const key = track.album == null ? `folder:${folder}` : `release:${track.album.releaseId}`
    const guess = fromFolder(folder)

    let album = albums.get(key)

    if (album === undefined) {
      album = {
        key,
        releaseId: track.album?.releaseId ?? null,
        mbid: track.album?.mbid ?? null,
        title: track.album?.title ?? guess.title,
        // The folder fills the gap and never overrules: MusicBrainz knows no
        // year for about half a real library, and a directory that states one
        // is the only other claim in existence.
        year: track.album?.year ?? guess.year,
        yearFromFolder: track.album?.year == null && guess.year !== null,
        folders: [],
        roles: [],
        trackCount: 0,
        fileCount: 0,
        unplaced: 0,
      }
      albums.set(key, album)
    }

    if (!album.folders.includes(folder)) album.folders.push(folder)
    for (const role of track.roles) if (!album.roles.includes(role)) album.roles.push(role)

    album.trackCount += 1
    album.fileCount += track.files.length
    if (track.album == null) album.unplaced += 1

    const inFolder = claims.get(folder) ?? new Map<string, number>()
    inFolder.set(key, (inFolder.get(key) ?? 0) + 1)
    claims.set(folder, inFolder)
  }

  // A folder whose album *was* placed, apart from a track or two, is that album
  // with a hole in it — not a second record. Folding those in is what turns
  // "two Blues Deluxes" into "Blues Deluxe, one track unplaced".
  //
  // **The home has to be the folder's album, not merely a release with a track
  // in it, and the difference wrecked ten real artist pages.** A compilation
  // that reprints one song sits in that song's folder and was, by "the only
  // release claiming this folder", its sole claimant — so `Bonamassa Free Album
  // 2017`, which holds one track, swallowed the whole twenty-one-track Vienna
  // Opera House folder, and the Vienna album vanished from the page. So the
  // home must hold *more* of the folder than is left over, and hold strictly
  // more of it than any other release: one track against twenty is a reprint,
  // eleven against one is an album with a hole in it, and a tie is the
  // split-folder disagreement, which is not ours to settle.
  //
  // **It weighs this artist's slice of the folder, not the folder.** The
  // endpoint sends one artist's tracks, so a shared album can merge on the
  // singer's page and stay two cards on the composer's. Closing that needs the
  // API to answer about folders, which nothing asks it for yet.
  for (const [key, orphan] of albums) {
    if (orphan.releaseId !== null) continue

    const folder = orphan.folders[0]
    if (folder === undefined) continue

    const [best, runnerUp] = [...(claims.get(folder) ?? [])]
      .filter(([held]) => held !== key && albums.get(held)?.releaseId != null)
      .sort(([, a], [, b]) => b - a)

    if (best === undefined || best[1] <= orphan.trackCount) continue
    if (runnerUp !== undefined && runnerUp[1] === best[1]) continue

    const home = albums.get(best[0])
    if (home === undefined) continue

    home.trackCount += orphan.trackCount
    home.fileCount += orphan.fileCount
    home.unplaced += orphan.unplaced
    // The roles too. They are what makes the page legible on a classical
    // library, and leaving them behind files a billed track under "composer".
    for (const role of orphan.roles) if (!home.roles.includes(role)) home.roles.push(role)
    albums.delete(key)
  }

  for (const album of albums.values()) album.folders.sort()

  return [...albums.values()].sort(
    (a, b) => (a.year ?? 9999) - (b.year ?? 9999) || a.title.localeCompare(b.title),
  )
}
