/**
 * Fixtures for the stories, and only for the stories.
 *
 * The content is the documented hard cases rather than invented data, because a
 * matching screen is only worth looking at against the answers that are actually
 * difficult. Three of them run through this file:
 *
 * - **Off the Wall.** Ten tracks, all ten held, a 2015 remaster matching to
 *   0.00 s — and in the live run it never reaches the candidate set, so all 31
 *   files land on whichever compilation did. The screen these fixtures dress is
 *   the one where a person fixes that.
 * - **The bootleg trap.** A 31-track *Greatest Hits* at coverage 0.55 and drift
 *   1.99 s explains seventeen files. Take the release explaining the most files
 *   and it wins; show coverage beside files-explained and it plainly loses.
 * - **The edition tie.** Three earlier pressings on identical track lists, every
 *   length 0.76 s away from what the audio measures. Duration is the only thing
 *   that separates them, which is why it is a column.
 *
 * Artwork comes from `CatalogueCard`'s `cover()` rather than a second SVG
 * generator, and nothing here touches the network — every story in this package
 * is also a test run in a real browser.
 *
 * Named individually as well as collected, because `noNonNullAssertion` is an
 * error in this repo and `RELEASE_CANDIDATES[0]!` is how a story that wants one
 * particular fixture would otherwise have to ask for it.
 */

import { cover } from '../CatalogueCard/fixtures.ts'
import type { ReleaseFitReadout } from './FitSummary.tsx'
import type { SlotRow } from './SlotTable.tsx'

/* ------------------------------------------------------------------ subject */

export type EvidenceFile = {
  readonly path: string
  readonly sizeBytes: number
  /** What fpcalc measured, stored to 10 ms. The only length the files admit to. */
  readonly fingerprint: string
}

export type FileSetSubject = {
  readonly folder: string
  readonly files: readonly EvidenceFile[]
  readonly formats: string
  readonly totalDuration: string
  /** Empty, and that is the finding. */
  readonly tags: readonly string[]
  readonly outcome: string
}

export const OFF_THE_WALL_FILES: readonly EvidenceFile[] = [
  { path: '01 Don’t Stop ’Til You Get Enough.flac', sizeBytes: 41_238_912, fingerprint: '6:04.97' },
  { path: '02 Rock with You.flac', sizeBytes: 24_903_168, fingerprint: '3:39.98' },
  { path: '03 Working Day and Night.flac', sizeBytes: 35_127_296, fingerprint: '5:13.99' },
  { path: '04 Get on the Floor.flac', sizeBytes: 30_408_704, fingerprint: '4:38.97' },
  { path: '05 Off the Wall.flac', sizeBytes: 27_262_976, fingerprint: '4:05.98' },
  { path: '06 Girlfriend.flac', sizeBytes: 20_447_232, fingerprint: '3:04.99' },
  { path: '07 She’s Out of My Life.flac', sizeBytes: 21_495_808, fingerprint: '3:37.98' },
  { path: '08 I Can’t Help It.flac', sizeBytes: 29_360_128, fingerprint: '4:28.98' },
  { path: '09 It’s the Falling in Love.flac', sizeBytes: 25_165_824, fingerprint: '3:47.97' },
  { path: '10 Burn This Disco Out.flac', sizeBytes: 25_690_112, fingerprint: '3:40.99' },
]

/**
 * The folder says 1979 and the audio measures as the 2015 remaster. Two claims,
 * and the panel prefers neither: the directory name is one of only two pieces of
 * evidence in existence, and it is the one not to believe.
 */
export const OFF_THE_WALL_SUBJECT: FileSetSubject = {
  folder: 'Michael Jackson/Off the Wall (1979)',
  files: OFF_THE_WALL_FILES,
  formats: 'FLAC 16/44.1',
  totalDuration: '42:44',
  tags: [],
  outcome: 'NoConfidentFit',
}

/* --------------------------------------------------------- release candidates */

export type ReleaseCandidateFixture = {
  readonly id: string
  readonly title: string
  readonly artist: string
  readonly year?: number
  readonly country?: string
  readonly status: string
  readonly formats: string
  readonly discCount: number
  readonly label?: string
  readonly catalogNumber?: string
  readonly image?: string
  readonly fit: ReleaseFitReadout
  readonly slots: readonly SlotRow[]
}

const OFF_THE_WALL_TITLES: readonly string[] = [
  'Don’t Stop ’Til You Get Enough',
  'Rock with You',
  'Working Day and Night',
  'Get on the Floor',
  'Off the Wall',
  'Girlfriend',
  'She’s Out of My Life',
  'I Can’t Help It',
  'It’s the Falling in Love',
  'Burn This Disco Out',
]

const OFF_THE_WALL_PRINTED: readonly string[] = [
  '6:05',
  '3:40',
  '5:14',
  '4:39',
  '4:06',
  '3:05',
  '3:38',
  '4:29',
  '3:48',
  '3:41',
]

/** Ten filled slots, with a per-slot drift in milliseconds. */
function offTheWallSlots(driftMs: readonly number[]): readonly SlotRow[] {
  return OFF_THE_WALL_TITLES.map((title, index) => ({
    discNumber: 1,
    position: index + 1,
    title,
    duration: OFF_THE_WALL_PRINTED[index] ?? '—',
    measured: OFF_THE_WALL_FILES[index]?.fingerprint ?? '—',
    driftMs: driftMs[index] ?? 0,
    file: {
      path: `Michael Jackson/Off the Wall (1979)/${OFF_THE_WALL_FILES[index]?.path ?? ''}`,
      sizeBytes: OFF_THE_WALL_FILES[index]?.sizeBytes ?? 0,
    },
  }))
}

/** The right answer. Every slot filled, and the audio agrees to the millisecond. */
export const OFF_THE_WALL_2015: ReleaseCandidateFixture = {
  id: 'otw-2015',
  title: 'Off the Wall',
  artist: 'Michael Jackson',
  year: 2015,
  country: 'GB',
  status: 'Official',
  formats: 'CD',
  discCount: 1,
  label: 'Epic',
  catalogNumber: '88875 12345 2',
  image: cover('#f59f00', '#c92a2a'),
  fit: {
    coverage: 1,
    meanDriftMs: 0,
    filesExplained: 10,
    slotCount: 10,
    held: 10,
    trackCount: 10,
    official: true,
    editionAlternatives: 0,
  },
  slots: offTheWallSlots([0, 0, 10, 0, 20, 0, 10, 0, 30, 10]),
}

/**
 * What a naive greedy picks: it explains seventeen files, more than any album
 * does, on a coverage of 0.55 and a drift of 1.99 s. Requiring a fit to be
 * *good* before asking whether it is *big* removes it entirely.
 */
export const GREATEST_HITS_BOOTLEG: ReleaseCandidateFixture = {
  id: 'greatest-hits-bootleg',
  title: 'Greatest Hits',
  artist: 'Michael Jackson',
  year: 1998,
  country: 'RU',
  status: 'Bootleg',
  formats: 'CD',
  discCount: 1,
  image: cover('#495057', '#212529'),
  fit: {
    coverage: 0.55,
    meanDriftMs: 1_990,
    filesExplained: 17,
    slotCount: 31,
    held: 17,
    trackCount: 31,
    official: false,
    editionAlternatives: 0,
  },
  slots: OFF_THE_WALL_TITLES.slice(0, 6).map((title, index) => ({
    discNumber: 1,
    position: index + 3,
    title,
    duration: OFF_THE_WALL_PRINTED[index] ?? '—',
    measured: OFF_THE_WALL_FILES[index]?.fingerprint ?? '—',
    driftMs: 1_990,
    file: {
      path: `Michael Jackson/Off the Wall (1979)/${OFF_THE_WALL_FILES[index]?.path ?? ''}`,
      sizeBytes: OFF_THE_WALL_FILES[index]?.sizeBytes ?? 0,
    },
  })),
}

/**
 * The five-disc box set the files actually landed on in the live run. Twenty
 * files explained — twice the album — and 26% of itself covered.
 */
export const THE_COLLECTION: ReleaseCandidateFixture = {
  id: 'the-collection',
  title: 'The Collection',
  artist: 'Michael Jackson',
  year: 2009,
  country: 'GB',
  status: 'Official',
  formats: '5×CD',
  discCount: 5,
  label: 'Epic',
  catalogNumber: '88697 44586 2',
  image: cover('#5f3dc4', '#1864ab'),
  fit: {
    coverage: 0.26,
    meanDriftMs: 240,
    filesExplained: 20,
    slotCount: 76,
    held: 20,
    trackCount: 76,
    official: true,
    editionAlternatives: 0,
  },
  slots: [
    {
      discNumber: 1,
      position: 1,
      title: 'Don’t Stop ’Til You Get Enough',
      duration: '6:05',
      measured: '6:04.97',
      driftMs: 30,
      file: {
        path: 'Michael Jackson/Off the Wall (1979)/01 Don’t Stop ’Til You Get Enough.flac',
        sizeBytes: 41_238_912,
      },
    },
    { discNumber: 1, position: 2, title: 'Rock with You', duration: '3:40', driftMs: null },
    { discNumber: 1, position: 3, title: 'Working Day and Night', duration: '5:14', driftMs: null },
    {
      discNumber: 2,
      position: 1,
      title: 'Off the Wall',
      duration: '4:06',
      measured: '4:05.98',
      driftMs: 20,
      file: {
        path: 'Michael Jackson/Off the Wall (1979)/05 Off the Wall.flac',
        sizeBytes: 27_262_976,
      },
    },
    { discNumber: 2, position: 2, title: 'Girlfriend', duration: '3:05', driftMs: null },
    { discNumber: 3, position: 1, title: 'Billie Jean', duration: '4:54', driftMs: null },
    { discNumber: 4, position: 1, title: 'Bad', duration: '4:07', driftMs: null },
    { discNumber: 5, position: 1, title: 'Black or White', duration: '4:16', driftMs: null },
  ],
}

/** Three pressings on identical track lists, every length 0.76 s away. */
function pressing(
  id: string,
  year: number,
  country: string,
  catalogNumber: string,
): ReleaseCandidateFixture {
  return {
    id,
    title: 'Off the Wall',
    artist: 'Michael Jackson',
    year,
    country,
    status: 'Official',
    formats: 'Vinyl, LP',
    discCount: 1,
    label: 'Epic',
    catalogNumber,
    fit: {
      coverage: 1,
      meanDriftMs: 760,
      filesExplained: 10,
      slotCount: 10,
      held: 10,
      trackCount: 10,
      official: true,
      editionAlternatives: 2,
    },
    slots: offTheWallSlots([760, 760, 750, 770, 760, 760, 750, 770, 760, 760]).map(
      (slot, index) => ({
        ...slot,
        // The printed number on a vinyl pressing is not the position, and the
        // printed one wins: "B5" is what is on the label.
        number: index < 5 ? `A${index + 1}` : `B${index - 4}`,
      }),
    ),
  }
}

export const OFF_THE_WALL_1979_US = pressing('otw-1979-us', 1979, 'US', 'FE 35745')
export const OFF_THE_WALL_1979_UK = pressing('otw-1979-uk', 1979, 'GB', 'EPC 83468')
export const OFF_THE_WALL_1982_JP = pressing('otw-1982-jp', 1982, 'JP', '25·3P-175')

/**
 * The only way to exercise "not measurable" on a full screen: MusicBrainz prints
 * no track lengths for this release, so there is nothing to compare the audio
 * against and the fit passes the drift gate untested.
 */
export const ELLA_BERLIN_UNDATED: ReleaseCandidateFixture = {
  id: 'ella-berlin-undated',
  title: 'Ella in Berlin: Mack the Knife',
  artist: 'Ella Fitzgerald',
  country: 'US',
  status: 'Official',
  formats: 'CD',
  discCount: 1,
  image: cover('#495057', '#adb5bd'),
  fit: {
    coverage: 0.83,
    meanDriftMs: null,
    filesExplained: 10,
    slotCount: 12,
    held: 10,
    trackCount: 12,
    official: true,
    editionAlternatives: 0,
  },
  slots: [
    { discNumber: 1, position: 1, title: 'That Old Black Magic', driftMs: null },
    { discNumber: 1, position: 2, title: 'Our Love Is Here to Stay', driftMs: null },
    { discNumber: 1, position: 3, title: 'Gone with the Wind', driftMs: null },
  ],
}

/** Ranked as the domain ranks them: coverage × files explained, then coverage. */
export const RELEASE_CANDIDATES: readonly ReleaseCandidateFixture[] = [
  OFF_THE_WALL_2015,
  GREATEST_HITS_BOOTLEG,
  THE_COLLECTION,
  OFF_THE_WALL_1979_US,
  OFF_THE_WALL_1979_UK,
  OFF_THE_WALL_1982_JP,
]

/** The tie on its own, which is what makes drift the only column that matters. */
export const EDITION_TIE: readonly ReleaseCandidateFixture[] = [
  OFF_THE_WALL_2015,
  OFF_THE_WALL_1979_US,
  OFF_THE_WALL_1979_UK,
  OFF_THE_WALL_1982_JP,
]

/* ------------------------------------------------------- recording candidates */

export type RecordingCandidateFixture = {
  readonly id: string
  readonly title: string
  readonly artist: string
  readonly release: string
  readonly length: string
  /** The AcoustID cluster's score, 0..1. */
  readonly score: number
  /** Summed sources across the cluster. Popularity, and not a tie-breaker. */
  readonly sources: number
  readonly mbid: string
}

/**
 * The *Sloe Gin* case, straight from the identification notes: two clusters at
 * 0.957 and 0.939 that are one answer arriving twice — a lossless rip and a
 * 128kbps rip that were never merged — both naming the same recording.
 *
 * The third is why `Sources` must not outvote a near-tied rival: letting
 * popularity decide would tag a track from an album called *Live* with the
 * studio recording.
 */
export const SLOE_GIN_LOSSLESS: RecordingCandidateFixture = {
  id: 'sloe-gin-lossless',
  title: 'Sloe Gin',
  artist: 'Joe Bonamassa',
  release: 'Sloe Gin',
  length: '8:23',
  score: 0.957,
  sources: 41,
  mbid: 'c1f4a1de-2c4d-4a01-9b2f-3e4d5a6b7c8d',
}

export const SLOE_GIN_LOSSY: RecordingCandidateFixture = {
  id: 'sloe-gin-lossy',
  title: 'Sloe Gin',
  artist: 'Joe Bonamassa',
  release: 'Sloe Gin',
  length: '8:23',
  score: 0.939,
  sources: 6,
  mbid: 'c1f4a1de-2c4d-4a01-9b2f-3e4d5a6b7c8d',
}

export const SLOE_GIN_LIVE: RecordingCandidateFixture = {
  id: 'sloe-gin-live',
  title: 'Sloe Gin (live)',
  artist: 'Joe Bonamassa',
  release: 'Live from Nowhere in Particular',
  length: '9:47',
  score: 0.931,
  sources: 3,
  mbid: 'a7b8c9d0-1e2f-3a4b-5c6d-7e8f9a0b1c2d',
}

export const RECORDING_CANDIDATES: readonly RecordingCandidateFixture[] = [
  SLOE_GIN_LOSSLESS,
  SLOE_GIN_LOSSY,
  SLOE_GIN_LIVE,
]

/* ---------------------------------------------------------- artist candidates */

export type ArtistCandidateFixture = {
  readonly id: string
  readonly name: string
  readonly sortName: string
  readonly type: string
  readonly disambiguation?: string
  readonly trackCount: number
  readonly image?: string
}

/** The canonical MusicBrainz disambiguation case, and the classical one under it. */
export const JACKSON_MUSICIAN: ArtistCandidateFixture = {
  id: 'jackson-musician',
  name: 'Michael Jackson',
  sortName: 'Jackson, Michael',
  type: 'Person',
  trackCount: 31,
  image: cover('#f59f00', '#c92a2a'),
}

export const JACKSON_BEER_WRITER: ArtistCandidateFixture = {
  id: 'jackson-beer',
  name: 'Michael Jackson',
  sortName: 'Jackson, Michael',
  type: 'Person',
  disambiguation: 'British beer and whisky writer',
  trackCount: 0,
}

export const BERLINER_PHILHARMONIKER: ArtistCandidateFixture = {
  id: 'berliner',
  name: 'Berliner Philharmoniker',
  sortName: 'Berliner Philharmoniker',
  type: 'Orchestra',
  trackCount: 1_811,
}

export const KARAJAN: ArtistCandidateFixture = {
  id: 'karajan',
  name: 'Herbert von Karajan',
  sortName: 'Karajan, Herbert von',
  type: 'Person',
  disambiguation: 'conductor',
  trackCount: 1_204,
}

export const ARTIST_CANDIDATES: readonly ArtistCandidateFixture[] = [
  JACKSON_MUSICIAN,
  JACKSON_BEER_WRITER,
  BERLINER_PHILHARMONIKER,
  KARAJAN,
]
