import type { Meta, StoryObj } from '@storybook/react-vite'
import { useId, useState } from 'react'
import { flushSync } from 'react-dom'
import { expect, userEvent } from 'storybook/test'

import { Artwork } from '../Artwork/Artwork.tsx'
import { Badge, type BadgeTone } from '../Badge/Badge.tsx'
import { Button } from '../Button/Button.tsx'
import { CatalogueCard } from '../CatalogueCard/CatalogueCard.tsx'
import { CatalogueGrid } from '../CatalogueCard/CatalogueGrid.tsx'
import { cover } from '../CatalogueCard/fixtures.ts'
import { Disclosure } from '../Disclosure/Disclosure.tsx'
import { Field } from '../Field/Field.tsx'
import { Input } from '../Input/Input.tsx'
import { Stack } from '../Stack/Stack.tsx'
import { Table, TableCell, TableHeaderCell } from '../Table/Table.tsx'
import { Text } from '../Text/Text.tsx'
import styles from './PageDesign.module.css'
import { countryName, Edited, Prose, stamp, Tabs, Value, type Written } from './parts.tsx'

/*
  Design candidate for the album page, the artist page's sibling. Nothing here
  is wired to the application. The shape is `ReleaseSummary` and
  `ReleaseTrackRow` plus the `Releases`, `ReleaseGroups`, `ReleaseCovers`,
  credit and `MediaFiles` columns the wire does not carry yet. Some of it has
  no source at all: the review, a person's edits, and most of the Credits tab — the
  catalogue stores billed credits and composer/conductor/ensemble relations
  on recordings and works, not instruments or production roles, and nothing
  rolls them up to an album with track ranges.
*/

type Integrity = 'Unchecked' | 'Intact' | 'Corrupt' | 'Truncated' | 'Unreadable'

type Quality = {
  readonly codec: string
  readonly lossless: boolean
  readonly bitDepth: number | null
  readonly sampleRateHz: number
  readonly bitrateKbps: number
}

type AudioFile = {
  readonly path: string
  readonly sizeMb: number
  /** Null until the probe pass has decoded it. */
  readonly quality: Quality | null
  readonly integrity: Integrity
}

type Track = {
  readonly disc: number
  readonly position: number
  /** As printed, which is not always the position: "A1", "12a". */
  readonly number: string | null
  /** With the work's run-in already taken off, as `workGroups` does. */
  readonly title: string
  readonly work: string | null
  readonly length: string | null
  /** Only where the track's credit differs from the album's. */
  readonly artist: string | null
  readonly files: readonly AudioFile[]
}

type Credit = {
  readonly name: string
  readonly role: string
  readonly group: 'Main' | 'Performers' | 'Composition' | 'Production'
  /** 1-based places in the album's running order; null is the whole album. */
  readonly tracks: readonly number[] | null
  readonly image?: string
}

/** Year always, month and day only when MusicBrainz knows them. Never widened. */
type PartialDate = {
  readonly year: number
  readonly month: number | null
  readonly day: number | null
}

type Album = {
  readonly title: string
  readonly disambiguation: string | null
  readonly mbid: string | null
  readonly groupMbid: string | null
  /** The billing line as printed, join phrases and all. */
  readonly credit: string
  readonly primaryType: string | null
  readonly secondaryTypes: readonly string[]
  readonly firstReleaseYear: number | null
  readonly released: PartialDate | null
  readonly country: string | null
  readonly status: string | null
  readonly label: string | null
  readonly catalogNumber: string | null
  readonly barcode: string | null
  readonly discCount: number | null
  readonly trackCount: number
  readonly formats: string | null
  readonly monitored: boolean
  readonly cover: string | null
  readonly coverSource: 'archive' | 'qobuz' | 'upload' | null
  readonly coverLookupUtc: string | null
  /** `certainty.ts`'s words and tone. `ok` is `Attributed`, which renders as nothing. */
  readonly certainty: {
    readonly label: string
    readonly note: string
    readonly tone: 'ok' | 'warning' | 'neutral'
  }
  readonly editionAlternatives: number
  readonly review: Written | null
  readonly reviewLookupUtc: string | null
  readonly releaseLookupUtc: string | null
  readonly probedUtc: string | null
  readonly contributable: number
}

type Profile = {
  readonly album: Album
  readonly tracks: readonly Track[]
  readonly credits: readonly Credit[]
  readonly moreBy: readonly {
    readonly title: string
    readonly year: number
    readonly image?: string
  }[]
}

const CD: Quality = {
  codec: 'FLAC',
  lossless: true,
  bitDepth: 16,
  sampleRateHz: 44_100,
  bitrateKbps: 912,
}
const HIRES: Quality = {
  codec: 'FLAC',
  lossless: true,
  bitDepth: 24,
  sampleRateHz: 96_000,
  bitrateKbps: 2_870,
}
const MP3: Quality = {
  codec: 'MP3',
  lossless: false,
  bitDepth: null,
  sampleRateHz: 44_100,
  bitrateKbps: 320,
}

const file = (path: string, quality: Quality | null, integrity: Integrity = 'Intact') => ({
  path,
  sizeMb: quality == null ? 38 : Math.round((quality.bitrateKbps * 300) / 8_000),
  quality,
  integrity: quality == null ? 'Unchecked' : integrity,
})

const SLOE_GIN_TITLES = [
  ['Seagull', '5:17'],
  ['Dirt in My Pocket', '4:32'],
  ['One of These Days', '7:44'],
  ['Ball Peen Hammer', '4:32'],
  ['Around the Bend', '5:04'],
  ['Sloe Gin', '8:12'],
  ['Black Night', '6:51'],
  ['Revenge of the 10 Gallon Hat', '3:42'],
  ['Another Kinda Love', '4:07'],
  ['Jelly Roll', '4:46'],
  ['India', '3:05'],
] as const

const SLOE_GIN: Profile = {
  album: {
    title: 'Sloe Gin',
    disambiguation: null,
    mbid: '5b1d6e3f-8e2a-4f0c-9a57-2a8c1f0b6d11',
    groupMbid: 'a3c4f1f2-6d0e-4a52-8f23-1c7e0d1b9e42',
    credit: 'Joe Bonamassa',
    primaryType: 'Album',
    secondaryTypes: [],
    firstReleaseYear: 2007,
    released: { year: 2007, month: 8, day: 21 },
    country: 'US',
    status: 'Official',
    label: 'J&R Adventures',
    catalogNumber: 'JRA 1003',
    barcode: '0804879071429',
    discCount: 1,
    trackCount: 11,
    formats: 'CD',
    monitored: false,
    cover: cover('#1864ab', '#121416'),
    coverSource: 'archive',
    coverLookupUtc: '2026-09-04T10:02:00Z',
    certainty: {
      label: 'Certain',
      note: 'One release fitted these files, and nothing else fitted as well.',
      tone: 'ok',
    },
    editionAlternatives: 0,
    releaseLookupUtc: '2026-09-02T21:30:00Z',
    probedUtc: '2026-09-05T02:11:00Z',
    reviewLookupUtc: '2026-09-04T10:03:00Z',
    review: {
      byPerson: false,
      source: 'Wikipedia',
      url: 'https://en.wikipedia.org/wiki/Sloe_Gin_(album)',
      text: [
        'Sloe Gin is the seventh studio album by Joe Bonamassa, released in 2007 on his own J&R Adventures label and produced by Kevin Shirley, the start of a partnership that shaped nearly everything he recorded afterwards.',
        'The record leans further from straight blues than its predecessors, mixing acoustic arrangements with heavy electric covers — Tim Curry’s “Sloe Gin”, Bad Company’s “Seagull” and Charles Brown’s “Black Night” among them.',
        'It reached number one on the Billboard Top Blues Albums chart, his second record to do so, and remains one of the most requested sources for his live set.',
      ].join('\n\n'),
    },
    contributable: 0,
  },
  tracks: SLOE_GIN_TITLES.map(([title, length], index) => ({
    disc: 1,
    position: index + 1,
    number: String(index + 1),
    title,
    work: null,
    length,
    artist: null,
    files:
      index === 6
        ? []
        : index === 0
          ? [
              file(`Joe Bonamassa/Sloe Gin/01 ${title}.flac`, CD),
              file(`Joe Bonamassa/Sloe Gin (mp3)/01 ${title}.mp3`, MP3),
            ]
          : [
              file(
                `Joe Bonamassa/Sloe Gin/${String(index + 1).padStart(2, '0')} ${title}.flac`,
                CD,
              ),
            ],
  })),
  credits: [
    {
      name: 'Joe Bonamassa',
      role: 'guitar, vocals',
      group: 'Main',
      tracks: null,
      image: cover('#1971c2', '#0b0d0e'),
    },
    { name: 'Anton Fig', role: 'drums', group: 'Performers', tracks: null },
    { name: 'Carmine Rojas', role: 'bass', group: 'Performers', tracks: null },
    {
      name: 'Rick Melick',
      role: 'keyboards',
      group: 'Performers',
      tracks: [1, 2, 3, 4, 5, 6, 8, 9, 10, 11],
    },
    { name: 'Bob Ezrin', role: 'writer', group: 'Composition', tracks: [6] },
    { name: 'Michael Kamen', role: 'writer', group: 'Composition', tracks: [6] },
    { name: 'Kevin Shirley', role: 'producer', group: 'Production', tracks: null },
  ],
  moreBy: [
    { title: 'Blues Deluxe', year: 2003, image: cover('#5f3dc4', '#1864ab') },
    { title: 'Royal Tea', year: 2020, image: cover('#c92a2a', '#343a40') },
    { title: 'Live at Carnegie Hall', year: 2021 },
  ],
}

const movements = (
  work: string,
  from: number,
  names: readonly (readonly [string, string])[],
): readonly Track[] =>
  names.map(([title, length], index) => ({
    disc: 1,
    position: from + index,
    number: String(from + index),
    title,
    work,
    length,
    artist: null,
    files: [
      file(
        `Berliner Philharmoniker/Beethoven 5 & 7 (Karajan 1963)/${String(from + index).padStart(2, '0')} ${title}.flac`,
        HIRES,
        from + index === 7 ? 'Corrupt' : 'Intact',
      ),
    ],
  }))

/** Works, three kinds of credit, a hi-res transfer with a damaged file, and several fitting pressings. */
const KARAJAN: Profile = {
  album: {
    ...SLOE_GIN.album,
    title: 'Symphonien Nr. 5 & 7',
    mbid: '0c9a8f6e-2b7d-4d3e-9d6f-8a1e5b3c7f20',
    groupMbid: '7e2b4c1a-9f8d-4e6b-a3c5-2d1f0e9b8a76',
    credit: 'Ludwig van Beethoven; Berliner Philharmoniker, Herbert von Karajan',
    firstReleaseYear: 1963,
    released: { year: 2014, month: 11, day: null },
    country: 'DE',
    label: 'Deutsche Grammophon',
    catalogNumber: '479 3440',
    barcode: '0028947934408',
    formats: 'Digital Media',
    trackCount: 8,
    cover: cover('#e67700', '#7d1a1a'),
    coverSource: 'qobuz',
    certainty: {
      label: 'One of several pressings',
      note:
        'Several editions fitted exactly as well and agreed about where every track sits, so one was ' +
        'chosen — official first, then earliest. Nothing the catalogue stores differs between them, ' +
        'but the choice was a coin flip.',
      tone: 'warning',
    },
    editionAlternatives: 2,
    review: null,
    reviewLookupUtc: '2026-09-04T10:05:00Z',
    contributable: 1,
  },
  tracks: [
    ...movements('Symphony No. 5 in C minor, Op. 67', 1, [
      ['I. Allegro con brio', '7:24'],
      ['II. Andante con moto', '10:02'],
      ['III. Allegro', '5:03'],
      ['IV. Allegro', '8:52'],
    ]),
    ...movements('Symphony No. 7 in A major, Op. 92', 5, [
      ['I. Poco sostenuto – Vivace', '13:04'],
      ['II. Allegretto', '8:24'],
      ['III. Presto', '8:05'],
      ['IV. Allegro con brio', '6:40'],
    ]),
  ],
  credits: [
    { name: 'Ludwig van Beethoven', role: 'composer', group: 'Main', tracks: null },
    {
      name: 'Berliner Philharmoniker',
      role: 'orchestra',
      group: 'Main',
      tracks: null,
      image: cover('#343a40', '#868e96'),
    },
    { name: 'Herbert von Karajan', role: 'conductor', group: 'Main', tracks: null },
    { name: 'Otto Gerdes', role: 'producer', group: 'Production', tracks: null },
    { name: 'Günter Hermanns', role: 'recording engineer', group: 'Production', tracks: null },
  ],
  moreBy: [],
}

/**
 * A release row and nothing asked since: no picture looked for, no review,
 * nothing probed. Two discs, and the pressing is not known.
 */
const CARNEGIE: Profile = {
  album: {
    ...SLOE_GIN.album,
    title: 'Live at Carnegie Hall: An Acoustic Evening',
    mbid: '3f5e1c2d-7a8b-4c9d-8e0f-1a2b3c4d5e6f',
    groupMbid: '9d4e2a1b-5c6f-4e7d-8a9b-0c1d2e3f4a5b',
    primaryType: 'Album',
    secondaryTypes: ['Live'],
    firstReleaseYear: 2017,
    released: { year: 2017, month: null, day: null },
    label: null,
    catalogNumber: null,
    barcode: null,
    discCount: 2,
    trackCount: 6,
    formats: 'CD+CD',
    cover: null,
    coverSource: null,
    coverLookupUtc: null,
    certainty: {
      label: 'Matched by you',
      note:
        'No rule placed these files. Somebody found the album, checked the pairing track by track ' +
        'and filed them, which is the strongest evidence available for music AcoustID cannot place.',
      tone: 'neutral',
    },
    review: null,
    reviewLookupUtc: null,
    probedUtc: null,
  },
  tracks: [
    ['This Train', '5:42'],
    ['Drive', '4:28'],
    ['Black Lung Heartache', '4:47'],
    ['Song of Yesterday', '8:01'],
    ['Woke Up Dreaming', '4:47'],
    ['Hummingbird', '5:46'],
  ].map(([title = '', length = ''], index) => ({
    disc: index < 3 ? 1 : 2,
    position: (index % 3) + 1,
    number: null,
    title,
    work: null,
    length,
    artist: index === 5 ? 'Joe Bonamassa & Reese Wynans' : null,
    files: [file(`Joe Bonamassa/Live at Carnegie Hall/${index + 1} ${title}.flac`, null)],
  })),
  credits: [
    {
      name: 'Joe Bonamassa',
      role: 'guitar, vocals',
      group: 'Main',
      tracks: null,
      image: cover('#1971c2', '#0b0d0e'),
    },
    { name: 'Reese Wynans', role: 'piano', group: 'Performers', tracks: [6] },
  ],
  moreBy: SLOE_GIN.moreBy.slice(0, 2),
}

function qualityLabel(quality: Quality): string {
  return quality.lossless
    ? `${quality.codec} ${quality.bitDepth ?? '?'}/${quality.sampleRateHz / 1000}`
    : `${quality.codec} ${quality.bitrateKbps}`
}

/** Above CD in depth or rate is hi-res, as `AudioQuality.Tier` reads it. */
function qualityTone(quality: Quality): BadgeTone {
  if (!quality.lossless) return 'warning'
  return (quality.bitDepth ?? 0) > 16 || quality.sampleRateHz > 48_000 ? 'accent' : 'neutral'
}

function dateLabel(date: PartialDate | null): string | null {
  if (date == null) return null
  return new Date(Date.UTC(date.year, (date.month ?? 1) - 1, date.day ?? 1)).toLocaleDateString(
    'en-GB',
    {
      year: 'numeric',
      ...(date.month != null ? { month: 'long' } : {}),
      ...(date.month != null && date.day != null ? { day: 'numeric' } : {}),
      timeZone: 'UTC',
    },
  )
}

const COVER_SOURCE = {
  archive: 'Cover Art Archive, this pressing',
  qobuz: 'Qobuz, matched by barcode or title',
  upload: 'Uploaded by you',
} as const

/** Consecutive runs of one disc and one work, so the album keeps its own order. */
function runs(tracks: readonly Track[]) {
  const out: { disc: number; work: string | null; firstOfDisc: boolean; tracks: Track[] }[] = []
  for (const track of tracks) {
    const last = out.at(-1)
    if (last != null && last.disc === track.disc && last.work === track.work) {
      last.tracks.push(track)
    } else {
      out.push({
        disc: track.disc,
        work: track.work,
        firstOfDisc: last?.disc !== track.disc,
        tracks: [track],
      })
    }
  }
  // `workGroups`' bar: a work heads only a run of two or more.
  return out.map((run) => (run.tracks.length < 2 ? { ...run, work: null } : run))
}

function FileBadges({ files }: { readonly files: readonly AudioFile[] }) {
  if (files.length === 0) {
    return (
      <Text size="sm" tone="tertiary">
        missing
      </Text>
    )
  }
  return (
    <Stack gap={4} wrap>
      {files.map((held) =>
        held.quality == null ? (
          <Text key={held.path} size="sm" tone="tertiary">
            not probed
          </Text>
        ) : (
          <Badge key={held.path} tone={qualityTone(held.quality)} size="sm" mono>
            {qualityLabel(held.quality)}
          </Badge>
        ),
      )}
      {files
        .filter((held) => held.integrity !== 'Intact' && held.integrity !== 'Unchecked')
        .map((held) => (
          <Badge key={`${held.path}:integrity`} tone="danger" size="sm">
            {held.integrity.toLowerCase()}
          </Badge>
        ))}
    </Stack>
  )
}

/** What one credited artist worked on here: their tracks, or the whole album. */
function Songs({ credit, tracks }: { readonly credit: Credit; readonly tracks: readonly Track[] }) {
  const id = useId()
  const theirs =
    credit.tracks == null ? tracks : credit.tracks.flatMap((place) => tracks[place - 1] ?? [])
  const discs = new Set(tracks.map((track) => track.disc)).size > 1
  return (
    <div className={styles.panel}>
      <h3 id={`${id}h`} className={styles.heading}>
        {credit.name} on this album
      </h3>
      <Text size="sm" tone="secondary">
        {credit.role} ·{' '}
        {credit.tracks == null ? 'every track' : `${theirs.length} of ${tracks.length} tracks`}
      </Text>
      <Table density="cozy" aria-labelledby={`${id}h`}>
        <thead>
          <tr>
            <TableHeaderCell numeric>No</TableHeaderCell>
            <TableHeaderCell>Track</TableHeaderCell>
            <TableHeaderCell numeric>Length</TableHeaderCell>
          </tr>
        </thead>
        <tbody>
          {theirs.map((track) => (
            <tr key={`${track.disc}-${track.position}`}>
              <TableCell numeric>
                <Text size="sm" family="mono" tone="tertiary">
                  {discs ? `${track.disc}·` : ''}
                  {track.number ?? track.position}
                </Text>
              </TableCell>
              <TableCell>
                <Text tone={track.files.length > 0 ? 'primary' : 'tertiary'}>
                  {track.work != null ? `${track.work}: ${track.title}` : track.title}
                </Text>
              </TableCell>
              <TableCell numeric>
                <Text size="sm" family="mono">
                  {track.length ?? '—'}
                </Text>
              </TableCell>
            </tr>
          ))}
        </tbody>
      </Table>
    </div>
  )
}

const TABS = [
  { key: 'overview', label: 'Overview' },
  { key: 'credits', label: 'Credits' },
] as const

type TabKey = (typeof TABS)[number]['key']

function AlbumPage({
  profile,
  editing: startEditing = false,
}: {
  readonly profile: Profile
  readonly editing?: boolean
}) {
  const source = profile.album
  const [album, setAlbum] = useState(source)
  const [draft, setDraft] = useState(source)
  const [editing, setEditing] = useState(startEditing)
  const [monitored, setMonitored] = useState(source.monitored)
  const [tab, setTab] = useState<TabKey>('overview')
  const [reviewOpen, setReviewOpen] = useState(false)
  const [picked, setPicked] = useState<string | null>(null)

  const id = useId()
  const asked = album.releaseLookupUtc != null
  const discs = album.discCount ?? 1
  const files = profile.tracks.flatMap((track) => track.files)
  const held = profile.tracks.filter((track) => track.files.length > 0).length
  const folders = [...new Set(files.map((held) => held.path.slice(0, held.path.lastIndexOf('/'))))]
  const qualities = [
    ...new Map(
      files.flatMap((held) =>
        held.quality == null ? [] : [[qualityLabel(held.quality), held.quality] as const],
      ),
    ).entries(),
  ]
  const facts = [
    dateLabel(album.released) ?? album.firstReleaseYear?.toString(),
    album.label,
    countryName(album.country),
    album.formats,
    album.disambiguation,
  ].filter((fact): fact is string => fact != null)
  const changed = (key: keyof Album) => JSON.stringify(album[key]) !== JSON.stringify(source[key])

  const set = (key: keyof Album) => (event: { target: { value: string } }) =>
    setDraft({ ...draft, [key]: event.target.value === '' ? null : event.target.value })
  const setNumber =
    (apply: (value: number | null) => Partial<Album>) => (event: { target: { value: string } }) =>
      setDraft({
        ...draft,
        ...apply(event.target.value === '' ? null : Number(event.target.value)),
      })
  const setDate = (part: keyof PartialDate) =>
    setNumber((value) => {
      const date = draft.released ?? { year: 0, month: null, day: null }
      const next = {
        ...date,
        [part]: value,
        ...(part === 'month' && value == null ? { day: null } : {}),
      }
      return { released: next.year == null || next.year === 0 ? null : (next as PartialDate) }
    })

  return (
    <article className={styles.page} aria-labelledby={`${id}title`}>
      <header>
        {/* The sleeve itself, blurred, is the backdrop: Roon's album hero. */}
        <div className={styles.banner} data-fallback="">
          {album.cover != null ? <img src={album.cover} alt="" /> : null}
        </div>

        <div className={styles.identity}>
          <div className={styles.cover}>
            <Artwork
              name={album.title}
              size="fill"
              {...(album.cover != null ? { src: album.cover } : {})}
            />
          </div>

          <div className={styles.names}>
            <h1 id={`${id}title`} className={styles.title}>
              {album.title}
            </h1>
            <Text size="lg" tone="secondary">
              {album.credit}
            </Text>
            <Text size="sm" tone="secondary">
              {facts.join(' · ')}
            </Text>

            <Stack gap={8} align="center" wrap>
              {[album.primaryType, ...album.secondaryTypes]
                .filter((type): type is string => type != null)
                .map((type) => (
                  <Badge key={type} tone="neutral" size="sm">
                    {type}
                  </Badge>
                ))}
              {/* Only when it is not the ordinary one, or the eye learns to skip the bootleg. */}
              {album.status != null && album.status !== 'Official' ? (
                <Badge tone="warning" size="sm">
                  {album.status}
                </Badge>
              ) : null}
              {qualities.map(([label, quality]) => (
                <Badge key={label} tone={qualityTone(quality)} size="sm" mono>
                  {label}
                </Badge>
              ))}
              {album.certainty.tone === 'ok' ? null : (
                <Badge tone={album.certainty.tone} size="sm">
                  {album.certainty.label}
                </Badge>
              )}
            </Stack>
          </div>

          <Stack gap={8} wrap>
            <Button variant="primary">Play</Button>
            <Button
              variant="secondary"
              aria-pressed={monitored}
              onClick={() => setMonitored(!monitored)}
            >
              {monitored ? 'Wanted' : 'Want'}
            </Button>
            <Button
              variant="secondary"
              aria-pressed={editing}
              onClick={() => {
                setDraft(album)
                setEditing(!editing)
                setTab('overview')
              }}
            >
              Edit
            </Button>
            <Button variant="ghost">Write tags</Button>
          </Stack>
        </div>
      </header>

      <Tabs
        tabs={TABS}
        label={album.title}
        selected={tab}
        onSelect={setTab}
        panel={(key) =>
          key === 'overview' ? (
            <div className={styles.columns}>
              <div className={styles.main}>
                <section aria-labelledby={`${id}review`}>
                  <Stack direction="column" gap={8}>
                    <h2 id={`${id}review`} className={styles.heading} tabIndex={-1}>
                      Review
                    </h2>
                    <Prose
                      written={album.review}
                      asked={album.reviewLookupUtc != null}
                      none="No review found for this album."
                      {...(reviewOpen
                        ? {}
                        : {
                            onMore: () => {
                              // The button goes with the excerpt; focus follows the reader.
                              flushSync(() => setReviewOpen(true))
                              document.getElementById(`${id}review`)?.focus()
                            },
                          })}
                    />
                  </Stack>
                </section>

                <section aria-labelledby={`${id}tracks`}>
                  <Stack direction="column" gap={8}>
                    <h2 id={`${id}tracks`} className={styles.heading}>
                      Tracks
                    </h2>
                    <div className={styles.tracklist}>
                      <Table density="cozy">
                        <caption>
                          <Text size="xs" tone="tertiary">
                            Every track on this edition, held or not
                          </Text>
                        </caption>
                        <thead>
                          <tr>
                            <TableHeaderCell numeric>No</TableHeaderCell>
                            <TableHeaderCell>Track</TableHeaderCell>
                            <TableHeaderCell numeric>Length</TableHeaderCell>
                            <TableHeaderCell>In your library</TableHeaderCell>
                          </tr>
                        </thead>
                        {runs(profile.tracks).map((run) => (
                          <tbody key={`${run.disc}:${run.tracks[0]?.position}`}>
                            {discs > 1 && run.firstOfDisc ? (
                              <tr className={styles.group}>
                                <th colSpan={4} scope="rowgroup">
                                  Disc {run.disc}
                                </th>
                              </tr>
                            ) : null}
                            {run.work != null ? (
                              <tr className={styles.group}>
                                <td />
                                <th colSpan={3} scope="rowgroup">
                                  {run.work}
                                </th>
                              </tr>
                            ) : null}
                            {run.tracks.map((track) => {
                              const tone = track.files.length > 0 ? 'primary' : 'tertiary'
                              return (
                                <tr key={`${track.disc}-${track.position}`}>
                                  <TableCell numeric>
                                    <Text size="sm" family="mono" tone="tertiary">
                                      {track.number ?? track.position}
                                    </Text>
                                  </TableCell>
                                  <TableCell>
                                    <Stack direction="column" gap={2}>
                                      <Text tone={tone}>{track.title}</Text>
                                      {track.artist != null ? (
                                        <Text size="xs" tone="secondary">
                                          {track.artist}
                                        </Text>
                                      ) : null}
                                    </Stack>
                                  </TableCell>
                                  <TableCell numeric>
                                    <Text size="sm" family="mono" tone={tone}>
                                      {track.length ?? '—'}
                                    </Text>
                                  </TableCell>
                                  <TableCell>
                                    <FileBadges files={track.files} />
                                  </TableCell>
                                </tr>
                              )
                            })}
                          </tbody>
                        ))}
                      </Table>
                    </div>
                  </Stack>
                </section>

                <Disclosure
                  size="sm"
                  summary={
                    <Text size="sm" weight="medium">
                      Files on disk
                    </Text>
                  }
                >
                  <div className={styles.tracklist}>
                    <Table density="cozy">
                      <caption>
                        <Text size="xs" tone="tertiary">
                          Every file filed under this album
                        </Text>
                      </caption>
                      <thead>
                        <tr>
                          <TableHeaderCell>Path</TableHeaderCell>
                          <TableHeaderCell>Format</TableHeaderCell>
                          <TableHeaderCell>Integrity</TableHeaderCell>
                          <TableHeaderCell numeric>Size</TableHeaderCell>
                        </tr>
                      </thead>
                      <tbody>
                        {files.map((held) => (
                          <tr key={held.path}>
                            <TableCell>
                              <Text size="xs" family="mono" className={styles.path}>
                                {held.path}
                              </Text>
                            </TableCell>
                            <TableCell>
                              <Text size="sm" family="mono">
                                {held.quality == null
                                  ? 'not probed'
                                  : `${qualityLabel(held.quality)} · ${held.quality.bitrateKbps} kbps`}
                              </Text>
                            </TableCell>
                            <TableCell>
                              <Text
                                size="sm"
                                tone={
                                  held.integrity === 'Intact'
                                    ? 'secondary'
                                    : held.integrity === 'Unchecked'
                                      ? 'tertiary'
                                      : 'danger'
                                }
                              >
                                {held.integrity === 'Unchecked'
                                  ? 'not checked'
                                  : held.integrity.toLowerCase()}
                              </Text>
                            </TableCell>
                            <TableCell numeric>
                              <Text size="sm" family="mono">
                                {held.sizeMb} MB
                              </Text>
                            </TableCell>
                          </tr>
                        ))}
                      </tbody>
                    </Table>
                  </div>
                </Disclosure>

                {profile.moreBy.length > 0 ? (
                  <section aria-labelledby={`${id}more`}>
                    <Stack direction="column" gap={8}>
                      <h2 id={`${id}more`} className={styles.heading}>
                        More by {album.credit}
                      </h2>
                      <CatalogueGrid aria-label={`More by ${album.credit}`}>
                        {profile.moreBy.map((other) => (
                          <CatalogueCard
                            key={other.title}
                            variant="album"
                            title={other.title}
                            subtitle={String(other.year)}
                            {...(other.image != null ? { image: other.image } : {})}
                          />
                        ))}
                      </CatalogueGrid>
                    </Stack>
                  </section>
                ) : null}
              </div>

              <aside className={styles.panel} aria-labelledby={`${id}about`}>
                <h2 id={`${id}about`} className={styles.heading}>
                  {editing ? 'Edit album' : 'About'}
                </h2>

                {editing ? (
                  <form
                    className={styles.form}
                    onSubmit={(event) => {
                      event.preventDefault()
                      setAlbum(draft)
                      setEditing(false)
                    }}
                  >
                    <Field className={styles.wide} label="Title">
                      {(control) => (
                        <Input
                          {...control}
                          value={draft.title}
                          onChange={set('title')}
                          required
                          fullWidth
                        />
                      )}
                    </Field>
                    <Field
                      className={styles.wide}
                      label="Credited to"
                      hint="As the sleeve prints it."
                    >
                      {(control) => (
                        <Input
                          {...control}
                          value={draft.credit}
                          onChange={set('credit')}
                          required
                          fullWidth
                        />
                      )}
                    </Field>
                    <Field
                      className={styles.wide}
                      label="Edition note"
                      hint="“remastered”, “deluxe”"
                    >
                      {(control) => (
                        <Input
                          {...control}
                          value={draft.disambiguation ?? ''}
                          onChange={set('disambiguation')}
                          fullWidth
                        />
                      )}
                    </Field>
                    <Field label="Type" hint="Album, EP, Single">
                      {(control) => (
                        <Input
                          {...control}
                          value={draft.primaryType ?? ''}
                          onChange={set('primaryType')}
                          fullWidth
                        />
                      )}
                    </Field>
                    <Field label="Also" hint="Live, Compilation — comma-separated">
                      {(control) => (
                        <Input
                          {...control}
                          value={draft.secondaryTypes.join(', ')}
                          onChange={(event) =>
                            setDraft({
                              ...draft,
                              secondaryTypes: event.target.value
                                .split(',')
                                .map((type) => type.trim())
                                .filter(Boolean),
                            })
                          }
                          fullWidth
                        />
                      )}
                    </Field>
                    <Field label="First released" hint="The album, any edition">
                      {(control) => (
                        <Input
                          {...control}
                          type="number"
                          value={draft.firstReleaseYear ?? ''}
                          onChange={setNumber((firstReleaseYear) => ({ firstReleaseYear }))}
                          mono
                          fullWidth
                        />
                      )}
                    </Field>
                    <Field label="This edition: year">
                      {(control) => (
                        <Input
                          {...control}
                          type="number"
                          value={draft.released?.year ?? ''}
                          onChange={setDate('year')}
                          mono
                          fullWidth
                        />
                      )}
                    </Field>
                    <Field label="Month" hint="Blank if unknown, never January">
                      {(control) => (
                        <Input
                          {...control}
                          type="number"
                          min={1}
                          max={12}
                          value={draft.released?.month ?? ''}
                          onChange={setDate('month')}
                          disabled={draft.released == null}
                          mono
                          fullWidth
                        />
                      )}
                    </Field>
                    <Field label="Day">
                      {(control) => (
                        <Input
                          {...control}
                          type="number"
                          min={1}
                          max={31}
                          value={draft.released?.day ?? ''}
                          onChange={setDate('day')}
                          disabled={draft.released?.month == null}
                          mono
                          fullWidth
                        />
                      )}
                    </Field>
                    <Field label="Country" hint="Two-letter code">
                      {(control) => (
                        <Input
                          {...control}
                          value={draft.country ?? ''}
                          onChange={set('country')}
                          maxLength={2}
                          mono
                          fullWidth
                        />
                      )}
                    </Field>
                    <Field label="Status" hint="Official, Promotion, Bootleg">
                      {(control) => (
                        <Input
                          {...control}
                          value={draft.status ?? ''}
                          onChange={set('status')}
                          fullWidth
                        />
                      )}
                    </Field>
                    <Field label="Label">
                      {(control) => (
                        <Input
                          {...control}
                          value={draft.label ?? ''}
                          onChange={set('label')}
                          fullWidth
                        />
                      )}
                    </Field>
                    <Field label="Catalogue number">
                      {(control) => (
                        <Input
                          {...control}
                          value={draft.catalogNumber ?? ''}
                          onChange={set('catalogNumber')}
                          mono
                          fullWidth
                        />
                      )}
                    </Field>
                    <Field label="Barcode">
                      {(control) => (
                        <Input
                          {...control}
                          value={draft.barcode ?? ''}
                          onChange={set('barcode')}
                          inputMode="numeric"
                          mono
                          fullWidth
                        />
                      )}
                    </Field>
                    <Field label="Format" hint="CD, Digital Media, CD+DVD-Video">
                      {(control) => (
                        <Input
                          {...control}
                          value={draft.formats ?? ''}
                          onChange={set('formats')}
                          fullWidth
                        />
                      )}
                    </Field>
                    <Field
                      className={styles.wide}
                      label="Review"
                      hint="Blank paragraphs split it. Written here, it is yours and no pass replaces it."
                    >
                      {(control) => (
                        <textarea
                          {...control}
                          className={styles.textarea}
                          rows={8}
                          value={draft.review?.text ?? ''}
                          onChange={(event) =>
                            setDraft({
                              ...draft,
                              review:
                                event.target.value === ''
                                  ? null
                                  : {
                                      text: event.target.value,
                                      source: 'You',
                                      url: null,
                                      byPerson: true,
                                    },
                            })
                          }
                        />
                      )}
                    </Field>
                    <Stack className={styles.wide} gap={8} justify="end">
                      <Button type="button" variant="ghost" onClick={() => setEditing(false)}>
                        Cancel
                      </Button>
                      <Button type="submit">Save</Button>
                    </Stack>
                  </form>
                ) : (
                  <>
                    <dl className={styles.facts}>
                      <dt>Title</dt>
                      <dd>
                        {album.title}
                        <Edited by={changed('title')} />
                      </dd>
                      <dt>Credited to</dt>
                      <dd>
                        {album.credit}
                        <Edited by={changed('credit')} />
                      </dd>
                      <dt>Edition note</dt>
                      <dd>
                        <Value value={album.disambiguation} asked={asked} />
                        <Edited by={changed('disambiguation')} />
                      </dd>
                      <dt>Type</dt>
                      <dd>
                        <Value
                          value={[album.primaryType, ...album.secondaryTypes]
                            .filter(Boolean)
                            .join(' · ')}
                          asked={asked}
                        />
                        <Edited by={changed('primaryType') || changed('secondaryTypes')} />
                      </dd>
                      <dt>First released</dt>
                      <dd>
                        <Value value={album.firstReleaseYear?.toString() ?? null} asked={asked} />
                        <Edited by={changed('firstReleaseYear')} />
                      </dd>
                      <dt>This edition</dt>
                      <dd>
                        <Value value={dateLabel(album.released)} asked={asked} />
                        <Edited by={changed('released')} />
                      </dd>
                      <dt>Country</dt>
                      <dd>
                        <Value value={countryName(album.country)} asked={asked} />
                        <Edited by={changed('country')} />
                      </dd>
                      <dt>Status</dt>
                      <dd>
                        <Value value={album.status} asked={asked} />
                        <Edited by={changed('status')} />
                      </dd>
                      <dt>Label</dt>
                      <dd>
                        <Value value={album.label} asked={asked} />
                        <Edited by={changed('label')} />
                      </dd>
                      <dt>Catalogue no.</dt>
                      <dd>
                        <Value value={album.catalogNumber} asked={asked} mono />
                        <Edited by={changed('catalogNumber')} />
                      </dd>
                      <dt>Barcode</dt>
                      <dd>
                        <Value value={album.barcode} asked={asked} mono />
                        <Edited by={changed('barcode')} />
                      </dd>
                      <dt>Format</dt>
                      <dd>
                        <Value value={album.formats} asked={asked} />
                        <Edited by={changed('formats')} />
                      </dd>
                      <dt>Discs</dt>
                      <dd>
                        <Value value={album.discCount?.toString() ?? null} asked={asked} />
                      </dd>
                      <dt>Tracks</dt>
                      <dd>
                        {held === album.trackCount
                          ? `All ${album.trackCount} in your library`
                          : `${held} of ${album.trackCount} in your library`}
                      </dd>
                      <dt>Identified</dt>
                      <dd>
                        <Stack direction="column" gap={4} align="start">
                          <span>
                            {album.certainty.label}
                            {album.editionAlternatives > 0
                              ? ` · ${album.editionAlternatives} others fitted`
                              : ''}
                          </span>
                          <Text size="xs" tone="tertiary">
                            {album.certainty.note}
                          </Text>
                          <Button size="sm" variant="ghost">
                            Identified wrong — ask again
                          </Button>
                        </Stack>
                      </dd>
                      <dt>Cover</dt>
                      <dd>
                        <Stack direction="column" gap={4} align="start">
                          {album.coverSource != null ? (
                            COVER_SOURCE[album.coverSource]
                          ) : (
                            <Text size="sm" tone="tertiary">
                              {album.coverLookupUtc == null
                                ? 'Not looked for yet'
                                : 'Neither source has one'}
                            </Text>
                          )}
                          <Button size="sm" variant="ghost">
                            Change cover
                          </Button>
                        </Stack>
                      </dd>
                      <dt>Review</dt>
                      <dd>
                        <Value
                          value={
                            album.review == null
                              ? null
                              : album.review.byPerson
                                ? 'Written by you'
                                : album.review.source
                          }
                          asked={album.reviewLookupUtc != null}
                        />
                        <Edited by={changed('review')} />
                      </dd>
                      <dt>MusicBrainz</dt>
                      <dd>
                        <Stack direction="column" gap={4} align="start">
                          {(
                            [
                              ['release', album.mbid, 'This edition'],
                              ['release-group', album.groupMbid, 'The album'],
                            ] as const
                          ).map(([kind, mbid, label]) =>
                            mbid == null ? (
                              <Text key={kind} size="sm" tone="tertiary">
                                {label}: not linked
                              </Text>
                            ) : (
                              <a
                                key={kind}
                                className={styles.link}
                                href={`https://musicbrainz.org/${kind}/${mbid}`}
                              >
                                {label}
                              </a>
                            ),
                          )}
                        </Stack>
                      </dd>
                      <dt>Wanted</dt>
                      <dd>{monitored ? 'Yes' : 'No'}</dd>
                      {album.contributable > 0 ? (
                        <>
                          <dt>AcoustID</dt>
                          <dd>
                            <Stack direction="column" gap={4} align="start">
                              {album.contributable === 1
                                ? 'One recording you chose by hand is not yet sent'
                                : `${album.contributable} recordings you chose by hand are not yet sent`}
                              <Button size="sm" variant="ghost">
                                Contribute fingerprints
                              </Button>
                            </Stack>
                          </dd>
                        </>
                      ) : null}
                      <dt>On disk</dt>
                      <dd>
                        <Stack direction="column" gap={4}>
                          {folders.map((folder) => (
                            <Text key={folder} size="xs" family="mono" className={styles.path}>
                              {folder}
                            </Text>
                          ))}
                        </Stack>
                      </dd>
                    </dl>

                    <h3 className={styles.heading}>
                      <Text size="sm" weight="semibold">
                        Last asked
                      </Text>
                    </h3>
                    <dl className={styles.facts}>
                      <dt>Release looked up</dt>
                      <dd>{stamp(album.releaseLookupUtc)}</dd>
                      <dt>Cover looked for</dt>
                      <dd>{stamp(album.coverLookupUtc)}</dd>
                      <dt>Review looked for</dt>
                      <dd>{stamp(album.reviewLookupUtc)}</dd>
                      <dt>Files probed</dt>
                      <dd>{stamp(album.probedUtc)}</dd>
                    </dl>
                  </>
                )}
              </aside>
            </div>
          ) : (
            <div className={styles.main}>
              {(['Main', 'Performers', 'Composition', 'Production'] as const).map(
                (group, index) => {
                  const people = profile.credits.filter((credit) => credit.group === group)
                  const open = people.find((person) => `${group}/${person.name}` === picked)
                  return people.length === 0 ? null : (
                    <section key={group} aria-labelledby={`${id}credits${index}`}>
                      <Stack direction="column" gap={8}>
                        <h2 id={`${id}credits${index}`} className={styles.heading}>
                          {group}
                        </h2>
                        <CatalogueGrid aria-label={`${group}: ${album.title}`}>
                          {people.map((person) => (
                            <CatalogueCard
                              key={person.name}
                              variant="artist"
                              title={person.name}
                              subtitle={person.role}
                              {...(person.image != null ? { image: person.image } : {})}
                              render={(props) => (
                                <button
                                  {...props}
                                  type="button"
                                  className={`${props.className} ${styles.credit}`}
                                  aria-expanded={`${group}/${person.name}` === picked}
                                  aria-controls={`${id}songs${index}`}
                                  onClick={() =>
                                    setPicked((current) =>
                                      current === `${group}/${person.name}`
                                        ? null
                                        : `${group}/${person.name}`,
                                    )
                                  }
                                />
                              )}
                            />
                          ))}
                        </CatalogueGrid>
                        <div id={`${id}songs${index}`}>
                          {open != null ? <Songs credit={open} tracks={profile.tracks} /> : null}
                        </div>
                      </Stack>
                    </section>
                  )
                },
              )}
            </div>
          )
        }
      />
    </article>
  )
}

const meta = {
  title: 'Pages/Album design',
  component: AlbumPage,
  parameters: { layout: 'fullscreen' },
  args: { profile: SLOE_GIN },
} satisfies Meta<typeof AlbumPage>

export default meta
type Story = StoryObj<typeof meta>

/** A settled album: cover from the archive, a review, one track missing and one held twice. */
export const Settled: Story = {
  play: async ({ canvas }) => {
    await userEvent.click(canvas.getByRole('tab', { name: 'Overview' }))
    await userEvent.keyboard('{ArrowRight}')
    await expect(canvas.getByRole('tab', { name: 'Credits' })).toHaveFocus()
    await expect(canvas.getByRole('tabpanel', { name: 'Credits' })).toBeVisible()
    const ezrin = canvas.getByRole('button', { name: /Bob Ezrin/ })
    await userEvent.click(ezrin)
    await expect(ezrin).toHaveAttribute('aria-expanded', 'true')
    await expect(canvas.getByRole('table', { name: 'Bob Ezrin on this album' })).toBeVisible()
    await expect(canvas.getByRole('cell', { name: 'Sloe Gin' })).toBeVisible()
    await expect(canvas.queryByRole('cell', { name: 'Seagull' })).toBeNull()
    await userEvent.click(ezrin)
    await expect(canvas.queryByRole('table', { name: 'Bob Ezrin on this album' })).toBeNull()
    await userEvent.click(canvas.getByRole('tab', { name: 'Credits' }))
    await userEvent.keyboard('{Home}')
    await userEvent.click(canvas.getByRole('button', { name: 'Read more' }))
    await expect(canvas.getByRole('heading', { name: 'Review' })).toHaveFocus()
    await expect(canvas.queryByRole('button', { name: 'Read more' })).toBeNull()
    const want = canvas.getByRole('button', { name: 'Want' })
    await userEvent.click(want)
    await expect(want).toHaveAttribute('aria-pressed', 'true')
  },
}

/** Works above their movements, three kinds of credit, hi-res with one damaged file, several fitting pressings. */
export const Classical: Story = { args: { profile: KARAJAN } }

/** A person's match, two discs, and nothing asked since: no cover, review or probe. */
export const NotYetAsked: Story = {
  args: { profile: CARNEGIE },
  play: async ({ canvas }) => {
    await userEvent.click(canvas.getByRole('tab', { name: 'Credits' }))
    await userEvent.click(canvas.getByRole('button', { name: /Reese Wynans/ }))
    await expect(canvas.getByRole('cell', { name: '2·3' })).toBeVisible()
    await userEvent.click(canvas.getByRole('button', { name: /Joe Bonamassa/ }))
    await expect(canvas.getByText(/every track/)).toBeVisible()
    await expect(canvas.getByRole('cell', { name: '1·1' })).toBeVisible()
  },
}

/** Editing is the About panel turned into a form; a saved change is marked as set by a person. */
export const Editing: Story = {
  play: async ({ canvas }) => {
    await userEvent.click(canvas.getByRole('button', { name: 'Edit' }))
    const label = canvas.getByLabelText('Label')
    await userEvent.clear(label)
    await userEvent.type(label, 'Provogue')
    await userEvent.click(canvas.getByRole('button', { name: 'Save' }))
    await expect(canvas.getAllByText('Provogue')[0]).toBeVisible()
    await expect(canvas.getByText('set by you')).toBeVisible()
  },
}
