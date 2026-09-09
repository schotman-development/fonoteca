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
  /**
   * The release's own billing line, when the catalogue holds one. Null for a
   * folder — a directory names no artist.
   */
  readonly albumArtist: string | null
  /**
   * Whether this artist is on that billing line — which is a different question
   * from whether they are billed on a *track*, and the one that separates a
   * record of theirs from a record they guest on.
   *
   * Null means the catalogue records no credit for the release at all, and it
   * has to stay distinguishable from false: absence of a credit row is the
   * catalogue not knowing, and demoting an album out of a discography on the
   * strength of it would be the same mistake as reading an unreadable directory
   * as an empty one.
   */
  readonly billedOnRelease: boolean | null
  /**
   * The group on the sleeve, where it is one this artist was a member of.
   *
   * Null both when it is somebody else's record and when the enrichment pass has
   * not yet been asked for membership, which is why nothing distinguishes the
   * two: both mean "no evidence", and the album stays where it would have been
   * before this existed.
   *
   * Carries the band's own id and name rather than the printed credit, because
   * the shelf groups on it: this library prints both "The Robert Cray Band" and
   * "Robert Cray Band", which is one band and would be two headings.
   */
  readonly band: { readonly id: string; readonly name: string } | null
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
        // Both are facts about the release, so every track of one album agrees
        // and the first to arrive settles it.
        albumArtist: track.album?.artist ?? null,
        billedOnRelease: track.album?.billed ?? null,
        band: track.album?.band ?? null,
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

/** The grids an artist's albums divide into. */
export type AlbumSectionKey = 'discography' | 'band' | 'collaborations' | 'composer'

/**
 * The same name reduced to bare words, for comparing one credit line to another.
 *
 * Lowercased, decomposed and stripped to alphanumerics, then padded with
 * spaces so a comparison lands on whole words. MusicBrainz punctuation is not
 * ASCII — "All\u2010Stars" and "Saint\u2010Sa\u00ebns" use U+2010 and a combining
 * diaeresis — and the padding is what stops the artist "Air" matching a line
 * reading "Airto Moreira".
 */
function words(name: string): string {
  return ` ${name
    .toLowerCase()
    .normalize('NFKD')
    .replaceAll(/[^a-z0-9]+/g, ' ')
    .trim()} `
}

/**
 * Whether a release's billing line names this artist, allowing for the bands
 * MusicBrainz files as artists of their own.
 *
 * **"Louis Armstrong" and "Louis Armstrong & His All-Stars" are two separate
 * artist entities**, so *Satch Plays Fats* — as much his record as anything he
 * made — carries a release credit his personal entity is not on. Demoting on
 * the credit id alone moves it off his discography and onto a shelf of other
 * people's albums, which is the same misfiling in the opposite direction.
 *
 * Either name containing the other, because the split runs both ways: the page
 * is sometimes the person and the credit the band ("Satch Plays Fats"),
 * sometimes the band and the credit the person (The Nat King Cole Trio's
 * albums are credited to Nat King Cole).
 *
 * Measured over the whole library: of the 124 albums a credit-id test alone
 * demotes, this keeps 9 and every one is an entity split of this kind — Louis
 * Armstrong, Nat King Cole and Molly Miller with their trios and orchestras —
 * while all 115 genuine guest appearances still move.
 *
 * **It is unsound and the library already contains the collisions.** Whole-word
 * containment holds between 60 pairs of this catalogue's own artist names, and
 * several are different people: "Rush" against "Bobby Rush", "Dion" against
 * "Céline Dion", "Hank Williams" against "Hank Williams, Jr.". None of them
 * fires today — measured over all 270 browsable artists, this rule is decisive
 * on 14 albums and all 14 are real entity splits — but one release credited to
 * "Bobby Rush" on Rush's page would be filed as Rush's own record.
 *
 * ponytail: naive name heuristic, kept because the thing that would replace it
 * cannot yet. `viaBand` now carries MusicBrainz's own "member of band" relation
 * and is the sound version of this question — but only for artists the
 * enrichment pass has re-asked, and it says nothing about an act like "Louis
 * Armstrong & His All-Stars" that MusicBrainz records no membership for. Narrow
 * this to a `viaBand` check once the backlog is described and the entity splits
 * are covered by it.
 */
export function sameArtist(album: ArtistAlbum, artistName: string): boolean {
  if (album.albumArtist === null) return false

  const line = words(album.albumArtist)
  const self = words(artistName)

  return self.trim().length > 0 && (line.includes(self) || self.includes(line))
}

/**
 * Which grid an album belongs on.
 *
 * Four questions, in the order of how much each answer is worth. One album gets
 * one grid: an album carries the union of its tracks' roles, so listing it under
 * every role it qualifies for would make a discography's total larger than the
 * discography. Every role is still printed on the card, which is where a person
 * reads why the record is theirs.
 */
export function sectionOf(album: ArtistAlbum, artistName: string): AlbumSectionKey {
  const theirs = sameArtist(album, artistName)

  // **A group they played in is neither a cover nor a guest spot, and it settles
  // the shelf on its own because every other signal on these albums is
  // misleading.** MusicBrainz credits a Dire Straits recording to the group, so
  // Mark Knopfler is on neither billing line and arrives here only as the
  // composer of the work — and the composer shelf says "their music, recorded by
  // somebody else", which is wrong about the man who played and sang it.
  //
  // **Every membership, including an act named after them.** There was an
  // exception here for a while — an eponymous band kept its albums in the
  // discography — and it could not be defended: it made Dire Straits a shelf on
  // Mark Knopfler's page and The Robert Cray Band part of Robert Cray's
  // discography, and the only thing separating those two cases was whether the
  // artist's name happens to be a substring of the band's. That is a fact about
  // spelling, not about music.
  //
  // The exception's real job was stopping the fall-through below, where a member
  // credited only as writer landed on the "recorded by somebody else" shelf.
  // Returning here does that outright.
  if (album.band !== null) return 'band'

  // **Writing it is not making it, and the sleeve cannot be used to decide
  // otherwise — MusicBrainz bills a classical release to the composer.** The
  // billing line of a Perlman/Ashkenazy record reads "Beethoven; Perlman,
  // Ashkenazy", so both tests below say this is Beethoven's own record; on this
  // library that put 27 albums of other people playing dead composers into
  // their discographies and left the shelf built to hold them nearly empty.
  //
  // Composer *and* something else is a different claim and falls through: an
  // artist who wrote a record and played on it carries a billed, conductor or
  // ensemble role beside the composer one. So does an album by a group they
  // were in, which the test above has already taken — membership is evidence
  // the record is theirs, and this rule is for when there is none.
  //
  // `length > 0` is not decoration: `[].every(…)` is true, so an album whose
  // artist has no stated role at all would land here — which is what a track
  // reached only through a band membership looked like before the API named
  // that reason.
  if (album.roles.length > 0 && album.roles.every((role) => role === 'composer')) {
    return 'composer'
  }

  // Their own record, by the sleeve or by the name on it. Ahead of the roles
  // because a conductor or an orchestra printed on the billing line is looking
  // at their own album rather than a booking, and the recording relation says
  // "conductor" either way.
  if (album.billedOnRelease === true || theirs) return 'discography'

  // **"Billed" on a track is not "billed" on the album, and reading it as one
  // is what put a B.B. King tribute album into Marc Broussard's discography.**
  // He sings one song on it; the record is Joe Bonamassa's. MusicBrainz credits
  // the *recording* to a line that includes him and the *release* to a line that
  // does not, and only the second is a claim about whose album it is. Measured
  // on the target library, 649 artist/album pairs are billed on a recording and
  // absent from the release's own credit.
  //
  // Only a stated absence demotes. Null is the catalogue holding no credit for
  // the release, which is not evidence the artist is missing from it, and eight
  // of this library's 644 releases are in that state.
  if (album.roles.includes('billed')) {
    return album.billedOnRelease === false ? 'collaborations' : 'discography'
  }

  return 'collaborations'
}

/** One headed grid: a shelf, or one band's shelf within it. */
export type AlbumSection = {
  readonly key: AlbumSectionKey
  /**
   * The group these albums are by, for a `band` section — and null everywhere
   * else, including a band section whose release credit is missing.
   */
  readonly band: string | null
  readonly albums: ArtistAlbum[]
}

/**
 * The discography, split into its shelves, in that order, empties dropped.
 *
 * **The band shelf is split again, one section per group, because "With the
 * band" answers a question nobody asked with the band's name missing.** Mark
 * Knopfler was in Dire Straits, the Notting Hillbillies and two more, and one
 * heading over all of them says only that some group was involved.
 *
 * The group is the release's own billing line, which costs nothing: it is the
 * same field `viaBand` was decided from, it is already on the wire, and the
 * cards already print it. Taking the band's *name* from the artist row instead
 * would mean sending the membership set to the browser to answer a question the
 * sleeve has already answered.
 *
 * Ordered by how much of it the library holds, then by year, then by name — so
 * the group somebody actually thinks of them as being in leads, rather than
 * whichever name sorts first. Collaborations are deliberately *not* split the
 * same way: membership is a standing fact worth a heading, where a guest spot
 * is a one-off and splitting on it would give a page of one-card sections.
 */
export function sectionsOf(albums: readonly ArtistAlbum[], artistName: string): AlbumSection[] {
  const order: readonly AlbumSectionKey[] = ['discography', 'band', 'collaborations', 'composer']
  const shelf = new Map(albums.map((album) => [album.key, sectionOf(album, artistName)]))

  return order.flatMap((key): AlbumSection[] => {
    const mine = albums.filter((album) => shelf.get(album.key) === key)

    if (mine.length === 0) return []
    if (key !== 'band') return [{ key, band: null, albums: mine }]

    // Keyed on the band's id. Keyed on what the sleeve printed, "The Robert Cray
    // Band" and "Robert Cray Band" are two shelves for one band — both spellings
    // are in this library, on two of his releases.
    const byBand = new Map<string, { name: string; albums: ArtistAlbum[] }>()

    for (const album of mine) {
      // Every album on this shelf reached it through `album.band`, so the null
      // arm is unreachable; it is here to keep the fold total.
      const id = album.band?.id ?? ''
      const found = byBand.get(id) ?? { name: album.band?.name ?? '', albums: [] }

      found.albums.push(album)
      byBand.set(id, found)
    }

    return [...byBand.values()]
      .map((found) => ({
        key,
        band: found.name.length > 0 ? found.name : null,
        albums: found.albums,
      }))
      .sort(
        (a, b) =>
          b.albums.length - a.albums.length ||
          (a.albums[0]?.year ?? 9999) - (b.albums[0]?.year ?? 9999) ||
          (a.band ?? '').localeCompare(b.band ?? ''),
      )
  })
}
