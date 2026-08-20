/**
 * The questions the matching screens ask, and the candidates they offer.
 *
 * **This file is a stand-in for an API that does not exist yet, and it is the
 * only reason these screens render.** Nothing in the catalogue can answer any of
 * it today: `ReleaseFit` is computed during the attribution pass and thrown
 * away, no endpoint returns a candidate set, and no DTO carries a media file id.
 * When those exist this file is deleted and the types below become the generated
 * ones in `schema.d.ts` — so the shapes here are written as a proposal for that
 * contract rather than as whatever was convenient to render.
 *
 * The content is the documented hard cases rather than invented data, because a
 * matching screen is only worth looking at against the answers that are actually
 * difficult:
 *
 * - **Off the Wall.** Ten tracks, all ten held, a 2015 remaster matching to
 *   0.00 s — and in the live run it never reaches the candidate set, so all 31
 *   files land on whichever compilation did. This is the screen where a person
 *   fixes that, and the bootleg *Greatest Hits* sits in the list beside it
 *   explaining seventeen files on a coverage of 0.55.
 * - **Sloe Gin.** Two AcoustID clusters at 0.957 and 0.939 naming the same
 *   MusicBrainz recording — one answer arriving twice — and a third naming a
 *   live take.
 * - **Two Michael Jacksons**, one of whom wrote about beer.
 */

import type { EvidenceRow, ReleaseFitReadout, SlotRow } from '@fonoteca/ui'

import { type OpenReason, whyOpen } from './openQuestions.ts'

export type { OpenReason }

/**
 * One of the files being identified.
 *
 * `path` is library-relative, as the catalogue stores it, and it is also what
 * the play controls key on — there is no media file id to use, because no DTO
 * carries one yet. That is the same gap the audio endpoint sits in.
 */
export type SubjectFile = {
  readonly path: string
  readonly sizeBytes: number
  /** What fpcalc measured, stored to 10 ms. The only length these files admit to. */
  readonly fingerprint: string
}

export type ReleaseCandidate = {
  readonly id: string
  readonly title: string
  readonly artist: string
  readonly year?: number
  readonly country?: string
  readonly status: string
  readonly formats: string
  readonly label?: string
  readonly catalogNumber?: string
  readonly fit: ReleaseFitReadout
  readonly slots: readonly SlotRow[]
}

export type RecordingCandidate = {
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
  /** What separates this one from the others, in a sentence. */
  readonly note: string
}

export type ArtistCandidate = {
  readonly id: string
  readonly name: string
  readonly sortName: string
  readonly type: string
  readonly disambiguation?: string
  readonly trackCount: number
  readonly note: string
}

type QuestionBase = {
  readonly id: string
  /** The subject in one line, for the worklist. */
  readonly subjectLine: string
  /** How many files hang on the answer. The worklist sorts nothing; this ranks it for the eye. */
  readonly fileCount: number
  /**
   * The page's `<h1>`, which asks about the **subject**: "what are these ten
   * files?".
   */
  readonly title: string
  /**
   * The radio group's `<legend>`, which asks for the **answer**: "which release
   * explains these ten files?".
   *
   * Two sentences rather than one because the heading and the legend are not the
   * same question — and because a page whose only heading is repeated verbatim a
   * screen further down reads, to anyone navigating by heading, as two things.
   */
  readonly question: string
  readonly reason: OpenReason
  /** What kind of thing is being identified — the evidence card's heading. */
  readonly heading: string
  readonly subject: string
  readonly subjectNote?: string
  readonly subjectMono?: boolean
  readonly evidence: readonly EvidenceRow[]
  /** Playable, when there is anything to play. Empty for a question about a recording's credits. */
  readonly files: readonly SubjectFile[]
  readonly refusal: { readonly label: string; readonly description: string }
  /** What choosing would do, and what refusing would do. Rendered under the commit button. */
  readonly ifChosen: string
  readonly ifRefused: string
}

/**
 * The three subjects, as one union.
 *
 * They differ in their candidates and in nothing else, which is the claim the
 * component set makes — the screen below is one component tree with three
 * option renderers, not three screens.
 */
export type MatchingQuestion = QuestionBase &
  (
    | { readonly kind: 'release'; readonly candidates: readonly ReleaseCandidate[] }
    | { readonly kind: 'recording'; readonly candidates: readonly RecordingCandidate[] }
    | { readonly kind: 'artist'; readonly candidates: readonly ArtistCandidate[] }
  )

/* ------------------------------------------------------------ the file set */

const OFF_THE_WALL_FILES: readonly SubjectFile[] = [
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

const OFF_THE_WALL_FOLDER = 'Michael Jackson/Off the Wall (1979)'

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

/** What each release prints, which is not what the files measure. */
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
      path: `${OFF_THE_WALL_FOLDER}/${OFF_THE_WALL_FILES[index]?.path ?? ''}`,
      sizeBytes: OFF_THE_WALL_FILES[index]?.sizeBytes ?? 0,
    },
  }))
}

/** The right answer. Every slot filled, and the audio agrees to the millisecond. */
const OFF_THE_WALL_2015: ReleaseCandidate = {
  id: 'otw-2015',
  title: 'Off the Wall',
  artist: 'Michael Jackson',
  year: 2015,
  country: 'GB',
  status: 'Official',
  formats: 'CD',
  label: 'Epic',
  catalogNumber: '88875 12345 2',
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
 * *good* before asking whether it is *big* removes it entirely — and putting
 * coverage next to files-explained is what makes that visible to a person.
 */
const GREATEST_HITS_BOOTLEG: ReleaseCandidate = {
  id: 'greatest-hits-bootleg',
  title: 'Greatest Hits',
  artist: 'Michael Jackson',
  year: 1998,
  country: 'RU',
  status: 'Bootleg',
  formats: 'CD',
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
      path: `${OFF_THE_WALL_FOLDER}/${OFF_THE_WALL_FILES[index]?.path ?? ''}`,
      sizeBytes: OFF_THE_WALL_FILES[index]?.sizeBytes ?? 0,
    },
  })),
}

/**
 * The five-disc box set the files actually landed on in the live run. Twenty
 * files explained — twice the album — and 26% of itself covered.
 */
const THE_COLLECTION: ReleaseCandidate = {
  id: 'the-collection',
  title: 'The Collection',
  artist: 'Michael Jackson',
  year: 2009,
  country: 'GB',
  status: 'Official',
  formats: '5×CD',
  label: 'Epic',
  catalogNumber: '88697 44586 2',
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
        path: `${OFF_THE_WALL_FOLDER}/01 Don’t Stop ’Til You Get Enough.flac`,
        sizeBytes: 41_238_912,
      },
    },
    { discNumber: 1, position: 2, title: 'Rock with You', duration: '3:40' },
    { discNumber: 1, position: 3, title: 'Working Day and Night', duration: '5:14' },
    {
      discNumber: 2,
      position: 1,
      title: 'Off the Wall',
      duration: '4:06',
      measured: '4:05.98',
      driftMs: 20,
      file: { path: `${OFF_THE_WALL_FOLDER}/05 Off the Wall.flac`, sizeBytes: 27_262_976 },
    },
    { discNumber: 2, position: 2, title: 'Girlfriend', duration: '3:05' },
    { discNumber: 3, position: 1, title: 'Billie Jean', duration: '4:54' },
    { discNumber: 4, position: 1, title: 'Bad', duration: '4:07' },
    { discNumber: 5, position: 1, title: 'Black or White', duration: '4:16' },
  ],
}

/** Three pressings on identical track lists, every length 0.76 s away. */
function pressing(
  id: string,
  year: number,
  country: string,
  catalogNumber: string,
): ReleaseCandidate {
  return {
    id,
    title: 'Off the Wall',
    artist: 'Michael Jackson',
    year,
    country,
    status: 'Official',
    formats: 'Vinyl, LP',
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

const OFF_THE_WALL_QUESTION: MatchingQuestion = {
  kind: 'release',
  id: 'off-the-wall',
  subjectLine: OFF_THE_WALL_FOLDER,
  fileCount: 10,
  title: 'What are these ten files?',
  question: 'Which release explains these ten files?',
  reason: whyOpen('NoConfidentFit'),
  heading: 'These ten files',
  subject: OFF_THE_WALL_FOLDER,
  subjectMono: true,
  subjectNote:
    'The folder name and the audio are the only two claims in existence, and one of them is the ' +
    'one not to believe.',
  evidence: [
    { label: 'Files', value: '10' },
    { label: 'Format', value: 'FLAC 16/44.1', mono: true },
    { label: 'Total length', value: '42:44', mono: true },
    { label: 'Album tag', value: 'not present', tone: 'warning' },
    { label: 'Track number tag', value: 'not present', tone: 'warning' },
    { label: 'Barcode tag', value: 'not present', tone: 'warning' },
    { label: 'AcoustID', value: 'on all 10 files', tone: 'success', mono: true },
    {
      label: 'Folder says',
      value: '1979',
      note: 'The audio measures as the 2015 remaster.',
      wide: true,
    },
  ],
  files: OFF_THE_WALL_FILES.map((file) => ({
    ...file,
    path: `${OFF_THE_WALL_FOLDER}/${file.path}`,
  })),
  refusal: {
    label: 'None of these',
    description:
      'A wrong album is worse than a missing one, and far harder to notice. Refusing leaves these ' +
      'files unfiled and asks again later.',
  },
  ifChosen: 'All ten files would be attributed to this release.',
  ifRefused: 'These ten files would stay unfiled.',
  candidates: [
    OFF_THE_WALL_2015,
    GREATEST_HITS_BOOTLEG,
    THE_COLLECTION,
    pressing('otw-1979-us', 1979, 'US', 'FE 35745'),
    pressing('otw-1979-uk', 1979, 'GB', 'EPC 83468'),
    pressing('otw-1982-jp', 1982, 'JP', '25·3P-175'),
  ],
}

/* ----------------------------------------------------------- the one file */

const SLOE_GIN_QUESTION: MatchingQuestion = {
  kind: 'recording',
  id: 'sloe-gin',
  subjectLine: 'Joe Bonamassa/Sloe Gin/04 Sloe Gin.flac',
  fileCount: 1,
  title: 'What is this file?',
  question: 'Which recording is this?',
  reason: {
    label: 'Two clusters, one margin',
    tone: 'warning',
    note:
      'AcoustID returned three clusters and the top two are near-tied. A cluster is not an ' +
      'answer — one recording routinely sits under several of them — so the identification pass ' +
      'compared their dominant recordings rather than their scores, and stopped short of writing ' +
      'a tag.',
    next:
      'The top two name the same MusicBrainz recording, which is one answer arriving twice. ' +
      'Choosing it here is what the rule declined to do on its own.',
  },
  heading: 'This file',
  subject: 'Joe Bonamassa/Sloe Gin/04 Sloe Gin.flac',
  subjectMono: true,
  evidence: [
    { label: 'Size', value: '58.2 MB', mono: true },
    { label: 'Fingerprint length', value: '8:23.14', mono: true },
    { label: 'Clusters returned', value: '3' },
    { label: 'Top two scores', value: '0.957 · 0.939', mono: true },
    {
      label: 'Album tag',
      value: 'not present',
      tone: 'warning',
      note: 'The directory name is the only text evidence there is.',
      wide: true,
    },
  ],
  files: [
    {
      path: 'Joe Bonamassa/Sloe Gin/04 Sloe Gin.flac',
      sizeBytes: 61_027_942,
      fingerprint: '8:23.14',
    },
  ],
  refusal: {
    label: 'None of these',
    description:
      'AcoustID has never heard of plenty of bootlegs and field recordings, and saying so is what ' +
      'lets the worklist reach empty.',
  },
  ifChosen: 'This file would be tagged with the chosen recording and enriched from it.',
  ifRefused: 'The file would keep its fingerprint and no AcoustID.',
  candidates: [
    {
      id: 'sloe-gin-lossless',
      title: 'Sloe Gin',
      artist: 'Joe Bonamassa',
      release: 'Sloe Gin',
      length: '8:23',
      score: 0.957,
      sources: 41,
      mbid: 'c1f4a1de-2c4d-4a01-9b2f-3e4d5a6b7c8d',
      note: 'The same MusicBrainz recording as the other near-tied cluster.',
    },
    {
      id: 'sloe-gin-lossy',
      title: 'Sloe Gin',
      artist: 'Joe Bonamassa',
      release: 'Sloe Gin',
      length: '8:23',
      score: 0.939,
      sources: 6,
      mbid: 'c1f4a1de-2c4d-4a01-9b2f-3e4d5a6b7c8d',
      note: 'A 128 kbps rip of the same track, in a cluster nobody ever merged.',
    },
    {
      id: 'sloe-gin-live',
      title: 'Sloe Gin (live)',
      artist: 'Joe Bonamassa',
      release: 'Live from Nowhere in Particular',
      length: '9:47',
      score: 0.931,
      sources: 3,
      mbid: 'a7b8c9d0-1e2f-3a4b-5c6d-7e8f9a0b1c2d',
      note: 'A different performance, and 84 seconds longer.',
    },
  ],
}

/* ------------------------------------------------------------- the credit */

const ROCK_WITH_YOU_QUESTION: MatchingQuestion = {
  kind: 'artist',
  id: 'rock-with-you',
  subjectLine: 'Rock with You',
  fileCount: 3,
  title: 'Who is this?',
  question: 'Which artist is credited?',
  reason: {
    label: 'Two artists share this name',
    tone: 'warning',
    note:
      'MusicBrainz holds more than one artist under this name, and a credit line does not say ' +
      'which. Filing the recording under the wrong one puts it in a discography it does not ' +
      'belong to, where nothing will look for it.',
    next:
      'The disambiguation on each candidate is the whole answer here, and it is one a person ' +
      'reads in a second and a rule cannot read at all.',
  },
  heading: 'This recording',
  subject: 'Rock with You',
  subjectNote: 'Held as 3 files, in 2 folders.',
  evidence: [
    { label: 'Length', value: '3:40', mono: true },
    { label: 'Billed credit', value: 'Michael Jackson' },
    { label: 'Artist tag', value: 'not present', tone: 'warning' },
    { label: 'Folder says', value: 'Michael Jackson', mono: true },
    {
      label: 'Relationships',
      value: 'none',
      note: 'No conductor, orchestra or composer link to fall back on.',
      wide: true,
    },
  ],
  files: [],
  refusal: {
    label: 'None of these',
    description:
      'The recording stays browsable by title, and no artist page grows a track that is not ' +
      'theirs.',
  },
  ifChosen: 'This recording, and its three files, would be browsable under the chosen artist.',
  ifRefused: 'The recording would stay uncredited.',
  candidates: [
    {
      id: 'jackson-musician',
      name: 'Michael Jackson',
      sortName: 'Jackson, Michael',
      type: 'Person',
      trackCount: 31,
      note: 'Already browsable under this name.',
    },
    {
      id: 'jackson-beer',
      name: 'Michael Jackson',
      sortName: 'Jackson, Michael',
      type: 'Person',
      disambiguation: 'British beer and whisky writer',
      trackCount: 0,
      note: 'Nothing in this library is credited to them.',
    },
    {
      id: 'berliner',
      name: 'Berliner Philharmoniker',
      sortName: 'Berliner Philharmoniker',
      type: 'Orchestra',
      trackCount: 1_811,
      note: 'An ensemble, recognised by its type as well as by the relation.',
    },
    {
      id: 'karajan',
      name: 'Herbert von Karajan',
      sortName: 'Karajan, Herbert von',
      type: 'Person',
      disambiguation: 'conductor',
      trackCount: 1_204,
      note: 'A conductor reaches the artist list through a relationship, never the credit line.',
    },
  ],
}

/** In the order the worklist shows them: most files first, which is most cost first. */
export const MATCHING_QUESTIONS: readonly MatchingQuestion[] = [
  OFF_THE_WALL_QUESTION,
  ROCK_WITH_YOU_QUESTION,
  SLOE_GIN_QUESTION,
]
