import type { Meta, StoryObj } from '@storybook/react-vite'
import { type ReactNode, useId, useRef, useState } from 'react'

import { Artwork } from '../Artwork/Artwork.tsx'
import { Badge } from '../Badge/Badge.tsx'
import { Button } from '../Button/Button.tsx'
import { Field } from '../Field/Field.tsx'
import { Input } from '../Input/Input.tsx'
import { Stack } from '../Stack/Stack.tsx'
import { Text } from '../Text/Text.tsx'
import { VisuallyHidden } from '../VisuallyHidden/VisuallyHidden.tsx'
import styles from './IdentifyDesigns.module.css'

/*
  Design candidates for the one-problem-at-a-time Identify screen. Nothing here
  is wired to the application. Track lists, lengths and file names are real
  (read from the library and MusicBrainz); tag agreement counts are illustrative.
*/

type FolderFile = {
  readonly name: string
  readonly title: string
  readonly track: number
  readonly length: string
}

type Folder = {
  readonly path: string
  readonly audio: string
  readonly filed: number
  /** Files with recording candidates of their own (Ambiguous, BelowThreshold). */
  readonly ambiguous: number
  readonly files: readonly FolderFile[]
  readonly tags: {
    readonly album: string
    readonly artist: string
    readonly year: number
    readonly label: string | null
    readonly discs: number
    readonly agreeing: number
    readonly releaseId: string
  }
}

type Slot = { readonly position: number; readonly title: string; readonly length: string }

type Option = {
  readonly mbid: string
  readonly title: string
  readonly artist: string
  readonly year: number
  readonly country: string | null
  readonly format: string
  readonly type: string
  readonly status: string
  readonly discs: number
  readonly label: string | null
  readonly catalogue: string | null
  readonly barcode: string | null
  readonly slots: readonly Slot[]
}

type Problem = { readonly folder: Folder; readonly releases: readonly Option[] }

const knopflerFiles: readonly [string, string][] = [
  ['Why Aye Man', '9:08'],
  ['Corned Beef City', '4:40'],
  ['Sailing To Philadelphia', '7:37'],
  ['Once Upon A Time In The West', '6:56'],
  ['Romeo And Juliet', '9:47'],
  ['My Bacon Roll', '8:55'],
  ['Matchstick Man', '7:48'],
  ['Done With Bonaparte', '12:38'],
  ['Heart Full Of Holes', '7:41'],
  ["She's Gone/Your Latest Trick", '6:47'],
  ['Postcards From Paraguay', '5:18'],
  ['On Every Street', '5:16'],
  ['Speedway At Nazareth', '9:48'],
  ['Money For Nothing', '10:27'],
  ['Brothers In Arms', '9:18'],
  ['Going Home', '6:47'],
]

const slots = (rows: readonly [string, string][]): readonly Slot[] =>
  rows.map(([title, length], index) => ({ position: index + 1, title, length }))

const KNOPFLER: Problem = {
  folder: {
    path: 'Mark Knopfler/Down The Road Whereever - Live Amsterdam 2019',
    audio: 'AAC · 128 kbps · 48 kHz · stereo',
    filed: 0,
    ambiguous: 0,
    files: knopflerFiles.map(([title, length], index) => ({
      name: `${String(index + 1).padStart(2, '0')} - ${title.replace('/', '_')}.m4a`,
      title,
      track: index + 1,
      length,
    })),
    tags: {
      album: 'Down The Road Wherever Tour 2019',
      artist: 'Mark Knopfler',
      year: 2019,
      label: null,
      discs: 1,
      agreeing: 16,
      releaseId: '7db049fd-3335-474b-a69e-97a8d14cfd79',
    },
  },
  releases: [
    {
      mbid: '7db049fd-3335-474b-a69e-97a8d14cfd79',
      title: 'Down the Road Wherever Tour 2019 (Live at the Ryman, Nashville 3/9/2019)',
      artist: 'Mark Knopfler',
      year: 2019,
      country: 'XW',
      format: 'Digital Media',
      type: 'Album · Live',
      status: 'Official',
      discs: 1,
      label: null,
      catalogue: null,
      barcode: null,
      slots: slots([
        ['Why Aye Man', '10:09'],
        ['Corned Beef City', '4:40'],
        ['Sailing to Philadelphia', '7:50'],
        ['Once Upon a Time in the West', '7:03'],
        ['Romeo and Juliet', '10:23'],
        ['My Bacon Roll', '9:25'],
        ['Matchstick Man', '8:47'],
        ['Done With Bonaparte', '17:23'],
        ['Heart Full of Holes', '7:41'],
        ['She’s Gone/Your Latest Trick', '6:46'],
        ['Postcards From Paraguay', '5:13'],
        ['On Every Street', '5:08'],
        ['Speedway at Nazareth', '9:49'],
        ['Money for Nothing', '10:16'],
        ['Going Home', '6:58'],
      ]),
    },
    {
      mbid: '3394d468-ac11-4ef4-813c-ca5dbef67221',
      title: 'Down the Road Wherever',
      artist: 'Mark Knopfler',
      year: 2018,
      country: 'XW',
      format: 'Digital Media',
      type: 'Album',
      status: 'Official',
      discs: 1,
      label: null,
      catalogue: null,
      barcode: null,
      slots: slots([
        ['Trapper Man', '6:00'],
        ['Back on the Dance Floor', '5:30'],
        ['Nobody’s Child', '4:16'],
        ['Just a Boy Away From Home', '5:12'],
        ['When You Leave', '4:12'],
        ['Good on You Son', '5:37'],
        ['My Bacon Roll', '5:35'],
        ['Nobody Does That', '5:15'],
        ['Drovers’ Road', '5:05'],
        ['One Song at a Time', '6:17'],
        ['Floating Away', '5:02'],
        ['Slow Learner', '4:34'],
        ['Heavy Up', '6:00'],
        ['Every Heart in the Room', '4:30'],
        ['Rear View Mirror', '2:29'],
        ['Matchstick Man', '2:52'],
      ]),
    },
  ],
}

const collierTracks: readonly [string, string, string][] = [
  ['The Run Around', '4:08', '4:10'],
  ['Tongue Tied', '3:52', '3:54'],
  ['So What', '5:29', '5:31'],
  ["I Can't Stand The Rain", '5:06', '5:08'],
  ['Bad News Bears', '5:17', '5:20'],
  ['God Bless The Child', '4:24', '4:26'],
  ['Learn How To Love', '3:16', '3:19'],
  ['Keep It Saxy', '4:47', '4:49'],
  ['Right By Your Side', '5:12', '5:12'],
]

const COLLIER: Problem = {
  folder: {
    path: 'Vanessa Collier/Heart Soul & Saxophone (2014)',
    audio: 'FLAC · 16-bit · 44.1 kHz · stereo · lossless',
    filed: 0,
    ambiguous: 2,
    files: collierTracks.map(([title, length], index) => ({
      name: `Vanessa Collier - Heart Soul & Saxophone - ${String(index + 1).padStart(2, '0')} - ${title}.flac`,
      title,
      track: index + 1,
      length,
    })),
    tags: {
      album: 'Heart Soul & Saxophone',
      artist: 'Vanessa Collier',
      year: 2014,
      label: 'Phenix Fire Records',
      discs: 1,
      agreeing: 9,
      releaseId: '297ebf3a-b7ea-4ee7-8898-b2b00d477b8a',
    },
  },
  releases: [
    {
      mbid: '297ebf3a-b7ea-4ee7-8898-b2b00d477b8a',
      title: 'Heart Soul & Saxophone',
      artist: 'Vanessa Collier',
      year: 2014,
      country: null,
      format: 'CD',
      type: 'Album',
      status: 'Official',
      discs: 1,
      label: null,
      catalogue: null,
      barcode: null,
      slots: collierTracks.map(([title, , length], index) => ({
        position: index + 1,
        title,
        length,
      })),
    },
  ],
}

const PROBLEMS: readonly Problem[] = [KNOPFLER, COLLIER]

/* ------------------------------------------------------------------ diffing */

const seconds = (length: string) =>
  length.split(':').reduce((total, part) => total * 60 + Number(part), 0)

const normalise = (text: string) => text.toLowerCase().replaceAll(/[^a-z0-9]/g, '')

const clock = (total: number) =>
  total >= 3600
    ? `${Math.floor(total / 3600)}:${String(Math.floor((total % 3600) / 60)).padStart(2, '0')}:${String(total % 60).padStart(2, '0')}`
    : `${Math.floor(total / 60)}:${String(total % 60).padStart(2, '0')}`

type Row =
  | {
      readonly kind: 'seated'
      readonly slot: Slot
      readonly file: FolderFile
      readonly drift: number
      readonly titleDiffers: boolean
    }
  | { readonly kind: 'empty'; readonly slot: Slot }

type Diff = {
  readonly rows: readonly Row[]
  readonly unseated: readonly FolderFile[]
  readonly seated: number
  readonly worstDrift: number
}

/** Seat by title first, then by the tag's track number, and report what is left over. */
function diff(folder: Folder, option: Option): Diff {
  const seat = new Map<number, FolderFile>()
  const used = new Set<FolderFile>()

  for (const slot of option.slots) {
    const file = folder.files.find(
      (f) => !used.has(f) && normalise(f.title) === normalise(slot.title),
    )
    if (file !== undefined) {
      seat.set(slot.position, file)
      used.add(file)
    }
  }

  for (const slot of option.slots) {
    if (seat.has(slot.position)) continue
    const file = folder.files.find((f) => !used.has(f) && f.track === slot.position)
    if (file !== undefined) {
      seat.set(slot.position, file)
      used.add(file)
    }
  }

  const rows: Row[] = option.slots.map((slot) => {
    const file = seat.get(slot.position)
    if (file === undefined) return { kind: 'empty', slot }

    return {
      kind: 'seated',
      slot,
      file,
      drift: seconds(file.length) - seconds(slot.length),
      titleDiffers: normalise(file.title) !== normalise(slot.title),
    }
  })

  const seatedRows = rows.filter((row) => row.kind === 'seated')

  return {
    rows,
    unseated: folder.files.filter((file) => !used.has(file)),
    seated: seatedRows.filter((row) => !row.titleDiffers).length,
    worstDrift: Math.max(0, ...seatedRows.map((row) => Math.abs(row.drift))),
  }
}

const namedByTags = (folder: Folder, option: Option) => folder.tags.releaseId === option.mbid

function TagsBadge({ folder, option }: { readonly folder: Folder; readonly option: Option }) {
  return namedByTags(folder, option) ? (
    <Badge tone="success" size="sm">
      Your files’ tags name this release
    </Badge>
  ) : null
}

/** Stands in for MusicBrainz: an MBID or release URL is a lookup, anything else matches words. */
function search(releases: readonly Option[], query: string): readonly Option[] {
  const byId = releases.find((release) => query.toLowerCase().includes(release.mbid))
  if (byId !== undefined) return [byId]

  const words = query
    .toLowerCase()
    .split(/[^a-z0-9]+/)
    .filter((word) => word.length > 1)

  return releases
    .map((release) => ({
      release,
      score: words.filter((word) => normalise(`${release.title} ${release.artist}`).includes(word))
        .length,
    }))
    .filter((hit) => hit.score > 0)
    .sort((left, right) => right.score - left.score)
    .map((hit) => hit.release)
}

const coverOf = (option: Option) => `https://coverartarchive.org/release/${option.mbid}/front-250`

const totalLength = (folder: Folder) =>
  clock(folder.files.reduce((total, file) => total + seconds(file.length), 0))

/* ------------------------------------------------------------ shared parts */

function FolderHeader({
  folder,
  compact = false,
}: {
  readonly folder: Folder
  readonly compact?: boolean
}) {
  return (
    <Stack gap={16} align="start" className={styles.folderHeader}>
      {compact ? null : <Artwork name={folder.tags.album} size="lg" />}
      <Stack direction="column" gap={4} align="start" className={styles.grow}>
        <h2 className={styles.heading}>
          <Text size="md" weight="semibold" family="mono" block>
            {folder.path}
          </Text>
        </h2>
        <Text size="sm" tone="secondary" block>
          {folder.files.length} files · {folder.files.length - folder.filed} open ·{' '}
          {totalLength(folder)} · {folder.audio}
        </Text>
        <Text size="sm" block>
          Tags say: <strong>{folder.tags.artist}</strong> — <strong>{folder.tags.album}</strong> (
          {folder.tags.year}){folder.tags.label !== null ? ` · ${folder.tags.label}` : ''}
        </Text>
        <Text size="xs" tone="tertiary" block>
          {folder.tags.agreeing} of {folder.files.length} files name the same MusicBrainz release
        </Text>
      </Stack>
    </Stack>
  )
}

function OptionFacts({ option }: { readonly option: Option }) {
  return (
    <Stack direction="column" gap={2} align="start">
      <Text size="xs" tone="secondary" family="mono" block>
        {[option.year, option.country, option.format, option.type, option.status]
          .filter((part) => part !== null)
          .join(' · ')}
      </Text>
      <Text size="xs" tone="secondary" family="mono" block>
        {option.slots.length} tracks · {option.discs} disc{option.discs === 1 ? '' : 's'} · label{' '}
        {option.label ?? 'not listed'} · cat. no. {option.catalogue ?? '—'} · barcode{' '}
        {option.barcode ?? '—'}
      </Text>
    </Stack>
  )
}

function FitBadges({ folder, option }: { readonly folder: Folder; readonly option: Option }) {
  const result = diff(folder, option)

  return (
    <Stack gap={6} wrap>
      <TagsBadge folder={folder} option={option} />
      <Badge tone={result.seated === folder.files.length ? 'success' : 'warning'} size="sm" mono>
        {result.seated} of {folder.files.length} files match a track
      </Badge>
      <Badge tone={result.worstDrift > 8 ? 'warning' : 'neutral'} size="sm" mono>
        worst drift {result.worstDrift}s
      </Badge>
    </Stack>
  )
}

type FactRow = {
  readonly label: string
  readonly tags: string
  readonly release: string
  readonly known: boolean
}

function factRows(folder: Folder, option: Option): readonly FactRow[] {
  return [
    { label: 'Album', tags: folder.tags.album, release: option.title, known: true },
    { label: 'Artist', tags: folder.tags.artist, release: option.artist, known: true },
    { label: 'Year', tags: String(folder.tags.year), release: String(option.year), known: true },
    {
      label: 'Tracks',
      tags: `${folder.files.length} files`,
      release: `${option.slots.length} tracks`,
      known: true,
    },
    { label: 'Discs', tags: String(folder.tags.discs), release: String(option.discs), known: true },
    {
      label: 'Label',
      tags: folder.tags.label ?? 'none',
      release: option.label ?? 'not listed',
      known: folder.tags.label !== null && option.label !== null,
    },
  ]
}

function differs(row: FactRow): boolean {
  if (!row.known) return false
  if (row.label === 'Tracks')
    return Number.parseInt(row.tags, 10) !== Number.parseInt(row.release, 10)
  return normalise(row.tags) !== normalise(row.release)
}

function FactsDiff({ folder, option }: { readonly folder: Folder; readonly option: Option }) {
  return (
    <table className={styles.table}>
      <caption>
        <VisuallyHidden>Your tags against {option.title}</VisuallyHidden>
      </caption>
      <thead>
        <tr>
          <th scope="col">
            <VisuallyHidden>Fact</VisuallyHidden>
          </th>
          <th scope="col">Your tags</th>
          <th scope="col">This release</th>
          <th scope="col">
            <VisuallyHidden>Difference</VisuallyHidden>
          </th>
        </tr>
      </thead>
      <tbody>
        {factRows(folder, option).map((row) => (
          <tr key={row.label} data-differs={differs(row) || undefined}>
            <th scope="row">{row.label}</th>
            <td>{row.tags}</td>
            <td>{row.release}</td>
            <td>
              {differs(row) ? (
                <Badge tone="warning" size="sm">
                  differs
                </Badge>
              ) : !row.known ? (
                <Badge tone="neutral" size="sm">
                  unknown
                </Badge>
              ) : (
                <Badge tone="success" size="sm">
                  same
                </Badge>
              )}
            </td>
          </tr>
        ))}
      </tbody>
    </table>
  )
}

function DriftBadge({ drift }: { readonly drift: number }) {
  const size = Math.abs(drift)
  const text = size === 0 ? 'same length' : `${drift > 0 ? '+' : '−'}${size}s`

  return (
    <Badge tone={size > 8 ? 'danger' : size > 3 ? 'warning' : 'success'} size="sm" mono>
      {text}
    </Badge>
  )
}

function TrackDiff({ folder, option }: { readonly folder: Folder; readonly option: Option }) {
  const result = diff(folder, option)

  return (
    <Stack direction="column" gap={8} align="stretch">
      <table className={styles.table}>
        <caption>
          <VisuallyHidden>Your files seated on {option.title}</VisuallyHidden>
        </caption>
        <thead>
          <tr>
            <th scope="col">#</th>
            <th scope="col">On the release</th>
            <th scope="col">Your file</th>
            <th scope="col">Length</th>
            <th scope="col">Difference</th>
          </tr>
        </thead>
        <tbody>
          {interleave(result).map((row) =>
            row.kind === 'unseated' ? (
              <UnseatedRow key={row.file.name} file={row.file} />
            ) : (
              <tr
                key={row.slot.position}
                data-kind={row.kind}
                data-differs={(row.kind === 'seated' && row.titleDiffers) || undefined}
              >
                <td className={styles.mono}>{row.slot.position}</td>
                <td>{row.slot.title}</td>
                <td className={styles.mono}>{row.kind === 'seated' ? row.file.name : '—'}</td>
                <td className={styles.mono}>
                  {row.kind === 'seated'
                    ? `${row.file.length} / ${row.slot.length}`
                    : row.slot.length}
                </td>
                <td>
                  <Stack gap={4} wrap>
                    {row.kind === 'empty' ? (
                      <Badge tone="neutral" size="sm">
                        no file
                      </Badge>
                    ) : (
                      <>
                        {row.titleDiffers ? (
                          <Badge tone="danger" size="sm">
                            title differs
                          </Badge>
                        ) : null}
                        {row.file.track !== row.slot.position ? (
                          <Badge tone="warning" size="sm" mono>
                            tag says #{row.file.track}
                          </Badge>
                        ) : null}
                        <DriftBadge drift={row.drift} />
                      </>
                    )}
                  </Stack>
                </td>
              </tr>
            ),
          )}
        </tbody>
      </table>
    </Stack>
  )
}

function UnseatedRow({ file }: { readonly file: FolderFile }) {
  return (
    <tr data-kind="unseated">
      <td className={styles.mono}>—</td>
      <td>
        <strong>Not on this release</strong>
      </td>
      <td className={styles.mono}>{file.name}</td>
      <td className={styles.mono}>{file.length}</td>
      <td>
        <Badge tone="danger" size="sm">
          stays open
        </Badge>
      </td>
    </tr>
  )
}

/**
 * The release's rows in the release's order, with each file that has no track
 * slipped in where it sits in the folder: before the first row whose file (or,
 * for an empty track, whose position) comes after it.
 */
function interleave(
  result: Diff,
): readonly (Row | { readonly kind: 'unseated'; readonly file: FolderFile })[] {
  const pending = [...result.unseated].sort((left, right) => left.track - right.track)
  const out: (Row | { readonly kind: 'unseated'; readonly file: FolderFile })[] = []

  for (const row of result.rows) {
    const at = row.kind === 'seated' ? row.file.track : row.slot.position
    while (pending[0] !== undefined && pending[0].track < at) {
      out.push({ kind: 'unseated', file: pending.shift() as FolderFile })
    }
    out.push(row)
  }

  return [...out, ...pending.map((file) => ({ kind: 'unseated' as const, file }))]
}

/**
 * What would go wrong if this album were filed, one snackbar each, pinned where
 * the File button is. Key it on the album so dismissals reset when it changes.
 */
function Snackbars({ folder, option }: { readonly folder: Folder; readonly option: Option }) {
  const [dismissed, setDismissed] = useState<ReadonlySet<string>>(() => new Set())
  const result = diff(folder, option)
  const seated = result.rows.filter((row) => row.kind === 'seated')
  const renamed = seated.filter((row) => row.titleDiffers)
  const drifting = seated.filter((row) => !row.titleDiffers && Math.abs(row.drift) > 8)
  const empty = result.rows.filter((row) => row.kind === 'empty')

  const problems: {
    readonly key: string
    readonly tone: 'danger' | 'warning' | 'success'
    readonly text: ReactNode
  }[] = [
    ...result.unseated.map((file) => ({
      key: file.name,
      tone: 'danger' as const,
      text: (
        <>
          <strong className={styles.mono}>{file.name}</strong> is not on this release. It stays
          open.
        </>
      ),
    })),
    ...(renamed.length > 0
      ? [
          {
            key: 'renamed',
            tone: 'danger' as const,
            text: `${renamed.length} of your files have a different title from the track they would go on.`,
          },
        ]
      : []),
    ...(drifting.length > 0
      ? [
          {
            key: 'drifting',
            tone: 'warning' as const,
            text: `${drifting.length} file${drifting.length === 1 ? ' is' : 's are'} more than 8 seconds off the track length: ${drifting.map((row) => `#${row.slot.position}`).join(', ')}.`,
          },
        ]
      : []),
    ...(empty.length > 0
      ? [
          {
            key: 'empty',
            tone: 'warning' as const,
            text: `Track${empty.length === 1 ? '' : 's'} ${empty.map((row) => row.slot.position).join(', ')} on this release ${empty.length === 1 ? 'has' : 'have'} no file.`,
          },
        ]
      : []),
  ]

  if (problems.length === 0) {
    problems.push({
      key: 'clean',
      tone: 'success',
      text: 'Every file matches a track on this release.',
    })
  }

  const shown = problems.filter((problem) => !dismissed.has(problem.key))

  return (
    <ul className={styles.snackbars} aria-live="polite" aria-label="Notices about this album">
      {shown.map((problem) => (
        <li key={problem.key} className={styles.snackbar} data-tone={problem.tone}>
          <Text size="sm" className={styles.grow}>
            {problem.text}
          </Text>
          <Button
            variant="ghost"
            size="sm"
            aria-label="Dismiss"
            onClick={() => {
              setDismissed(new Set([...dismissed, problem.key]))
            }}
          >
            ✕
          </Button>
        </li>
      ))}
    </ul>
  )
}

function SearchRow({ queue }: { readonly queue: Queue }) {
  return (
    <Stack direction="column" gap={4} align="stretch">
      <form
        className={styles.search}
        onSubmit={(event) => {
          event.preventDefault()
          queue.submit()
        }}
      >
        <Field
          label="Search MusicBrainz for the album"
          hint="Filled in from your files’ tags. A release MBID or URL pasted here is looked up directly."
        >
          {(control) => (
            <Input
              {...control}
              fullWidth
              value={queue.query}
              onChange={(event) => {
                queue.setQuery(event.currentTarget.value)
              }}
            />
          )}
        </Field>
        <Button type="submit" variant="secondary" disabled={queue.query.trim() === ''}>
          Search
        </Button>
      </form>
      <div role="status">
        <Text size="xs" tone="tertiary">
          {queue.options.length === 0
            ? `Nothing matched “${queue.asked}”. Try just the artist and the album title.`
            : `${queue.options.length} album${queue.options.length === 1 ? '' : 's'} for “${queue.asked}”`}
        </Text>
      </div>
    </Stack>
  )
}

/* ----------------------------------------------------------- the queue mock */

type Outcome = { readonly kind: 'filed'; readonly option: Option } | { readonly kind: 'unreleased' }

const queryFor = (problem: Problem) => `${problem.folder.tags.artist} ${problem.folder.tags.album}`

function useQueue() {
  const [index, setIndex] = useState(0)
  const problem = PROBLEMS[index % PROBLEMS.length] as Problem

  const [query, setQuery] = useState(() => queryFor(problem))
  const [asked, setAsked] = useState(query)
  const [chosen, setChosen] = useState<string | null>(null)
  const [outcome, setOutcome] = useState<Outcome | null>(null)
  const [notice, setNotice] = useState<string | null>(null)

  const options = search(problem.releases, asked)
  const selected = options.find((option) => option.mbid === chosen) ?? options[0] ?? null

  const next = () => {
    const upcoming = PROBLEMS[(index + 1) % PROBLEMS.length] as Problem
    setIndex((current) => (current + 1) % PROBLEMS.length)
    setQuery(queryFor(upcoming))
    setAsked(queryFor(upcoming))
    setChosen(null)
    setOutcome(null)
    setNotice(null)
  }

  const submit = () => {
    setAsked(query.trim())
    setChosen(null)
  }

  return {
    index,
    problem,
    query,
    setQuery,
    asked,
    submit,
    options,
    selected,
    choose: setChosen,
    outcome,
    setOutcome,
    notice,
    setNotice,
    next,
  }
}

type MenuItem = {
  readonly label: string
  readonly description: string
  /** Why it cannot be used on this folder, or null when it can. */
  readonly unavailable: string | null
}

function menuFor(
  folder: Folder,
): readonly { readonly group: string; readonly items: readonly MenuItem[] }[] {
  return [
    {
      group: 'Look closer',
      items: [
        {
          label: 'Play the folder',
          description: 'Listen before deciding — live and studio takes sound nothing alike.',
          unavailable: null,
        },
        {
          label: 'Open in Files',
          description: 'Everything in the folder, including covers, cue sheets and stray files.',
          unavailable: null,
        },
        {
          label: 'Show every tag',
          description: 'The raw tags of each file, as the file carries them.',
          unavailable: null,
        },
      ],
    },
    {
      group: 'Answer another way',
      items: [
        {
          label: 'Match files one at a time…',
          description:
            folder.ambiguous > 0
              ? `${folder.ambiguous} file${folder.ambiguous === 1 ? ' has' : 's have'} recordings of ${folder.ambiguous === 1 ? 'its' : 'their'} own to choose from.`
              : 'For files whose audio matched several recordings.',
          unavailable:
            folder.ambiguous > 0 ? null : 'No file in this folder has recordings to choose from.',
        },
        {
          label: 'Add this album to MusicBrainz…',
          description:
            'Opens the MusicBrainz release editor filled in from these files’ tags and lengths.',
          unavailable: null,
        },
        {
          label: 'Wrong match — ask again',
          description:
            'Takes back what the passes filed here and puts the whole folder back as one question.',
          unavailable: folder.filed > 0 ? null : 'Nothing in this folder is filed yet.',
        },
      ],
    },
  ]
}

/**
 * The native popover: top layer, light dismiss and Escape come from the browser.
 * Anchored with CSS anchor positioning, so nothing here measures the button.
 */
function MoreMenu({ queue }: { readonly queue: Queue }) {
  const id = useId()
  const menu = useRef<HTMLDivElement>(null)

  return (
    <span className={styles.moreAnchor}>
      <Button variant="ghost" popoverTarget={id} aria-haspopup="true">
        More ▾
      </Button>
      <div ref={menu} id={id} popover="auto" className={styles.menu}>
        {menuFor(queue.problem.folder).map(({ group, items }) => (
          <fieldset key={group} className={styles.menuGroup}>
            <legend>
              <Text size="2xs" tone="tertiary" weight="medium">
                {group}
              </Text>
            </legend>
            {items.map((item) => (
              <button
                key={item.label}
                type="button"
                className={styles.menuItem}
                disabled={item.unavailable !== null}
                onClick={() => {
                  menu.current?.hidePopover()
                  queue.setNotice(`${item.label} — not wired up in this design.`)
                }}
              >
                <Text size="sm" weight="medium" block>
                  {item.label}
                </Text>
                <Text size="xs" tone="secondary" block>
                  {item.unavailable ?? item.description}
                </Text>
              </button>
            ))}
          </fieldset>
        ))}
      </div>
    </span>
  )
}

type Queue = ReturnType<typeof useQueue>

function Counter({ queue }: { readonly queue: Queue }) {
  return (
    <Stack direction="column" gap={2} align="start">
      <Text size="sm" tone="secondary">
        Folder {queue.index + 1} of {PROBLEMS.length}
      </Text>
      <div role="status">
        {queue.notice !== null ? (
          <Text size="xs" tone="tertiary">
            {queue.notice}
          </Text>
        ) : null}
      </div>
    </Stack>
  )
}

function Actions({
  queue,
  align = 'end',
}: {
  readonly queue: Queue
  readonly align?: 'start' | 'end'
}) {
  const { selected, problem } = queue
  const filing = selected === null ? 0 : diff(problem.folder, selected).seated

  return (
    <Stack gap={8} align="center" justify={align} wrap>
      <Button variant="ghost" onClick={queue.next}>
        Skip
      </Button>
      <Button
        variant="secondary"
        onClick={() => {
          queue.setOutcome({ kind: 'unreleased' })
        }}
      >
        Not a release
      </Button>
      <MoreMenu queue={queue} />
      <Button
        variant="primary"
        disabled={selected === null}
        onClick={() => {
          if (selected !== null) queue.setOutcome({ kind: 'filed', option: selected })
        }}
      >
        File {filing} files under this album
      </Button>
    </Stack>
  )
}

function Result({ queue }: { readonly queue: Queue }) {
  const { outcome, problem } = queue
  if (outcome === null) return null

  const summary = outcome.kind === 'unreleased' ? null : diff(problem.folder, outcome.option)

  return (
    <div role="status" className={styles.result}>
      <Stack direction="column" gap={8} align="start">
        {outcome.kind === 'unreleased' ? (
          <Text size="md" weight="semibold" block>
            Marked as not a release
          </Text>
        ) : (
          <>
            <Stack gap={12} align="center">
              <Artwork name={outcome.option.title} src={coverOf(outcome.option)} size="md" />
              <Text size="md" weight="semibold" block>
                Filed {summary?.seated} files under “{outcome.option.title}”
              </Text>
            </Stack>
            {summary !== null && summary.unseated.length > 0 ? (
              <Text size="sm" tone="secondary" block>
                Still open: {summary.unseated.map((file) => file.name).join(', ')}
              </Text>
            ) : null}
            {summary?.rows.some((row) => row.kind === 'empty') ? (
              <Text size="sm" tone="secondary" block>
                Positions with no file:{' '}
                {summary.rows
                  .filter((row) => row.kind === 'empty')
                  .map((row) => row.slot.position)
                  .join(', ')}
              </Text>
            ) : null}
          </>
        )}
        <Button variant="primary" onClick={queue.next}>
          Next folder
        </Button>
      </Stack>
    </div>
  )
}

function OptionRadio({
  queue,
  option,
  children,
}: {
  readonly queue: Queue
  readonly option: Option
  readonly children: ReactNode
}) {
  return (
    <label className={styles.radio}>
      <input
        type="radio"
        name={`option-${queue.index}`}
        checked={queue.selected?.mbid === option.mbid}
        onChange={() => {
          queue.choose(option.mbid)
        }}
      />
      {children}
    </label>
  )
}

/* ------------------------------------------------------------ design A */

function Carousel() {
  const queue = useQueue()
  const { folder } = queue.problem
  const { options, selected } = queue

  const current = selected === null ? -1 : options.indexOf(selected)

  const go = (step: number) => {
    const target = options[(current + step + options.length) % options.length]
    if (target !== undefined) queue.choose(target.mbid)
  }

  return (
    <div className={styles.page}>
      <Stack justify="between" align="center" wrap>
        <Counter queue={queue} />
      </Stack>

      <section className={styles.panel} aria-label="Folder">
        <FolderHeader folder={folder} />
      </section>

      {queue.outcome !== null ? (
        <Result queue={queue} />
      ) : (
        <>
          <SearchRow queue={queue} />

          {selected === null ? null : (
            <section
              className={styles.carousel}
              aria-roledescription="carousel"
              aria-label="Albums found"
            >
              <Stack justify="between" align="center" gap={12} wrap>
                <Text size="sm" weight="medium">
                  Which album is this?
                </Text>
                <Stack gap={8} align="center">
                  <Button
                    variant="secondary"
                    size="sm"
                    disabled={options.length < 2}
                    onClick={() => {
                      go(-1)
                    }}
                  >
                    ← Previous
                  </Button>
                  <Button
                    variant="secondary"
                    size="sm"
                    disabled={options.length < 2}
                    onClick={() => {
                      go(1)
                    }}
                  >
                    Next →
                  </Button>
                </Stack>
              </Stack>

              <article
                key={selected.mbid}
                className={styles.option}
                aria-roledescription="slide"
                aria-label={`${current + 1} of ${options.length}: ${selected.title}`}
                data-selected
              >
                <Stack gap={16} align="start">
                  <Artwork name={selected.title} src={coverOf(selected)} size="lg" />
                  <Stack direction="column" gap={6} align="start" className={styles.grow}>
                    <h3 className={styles.heading}>
                      <Text size="md" weight="semibold">
                        {selected.title}
                      </Text>
                      <Text size="sm" tone="secondary">
                        {' '}
                        — {selected.artist}
                      </Text>
                    </h3>
                    <OptionFacts option={selected} />
                    <FitBadges folder={folder} option={selected} />
                  </Stack>
                </Stack>
                <Snackbars key={selected.mbid} folder={folder} option={selected} />
                <FactsDiff folder={folder} option={selected} />
                <TrackDiff folder={folder} option={selected} />
              </article>
            </section>
          )}

          <div className={styles.stickyBar}>
            <Actions queue={queue} />
          </div>
        </>
      )}
    </div>
  )
}

/* ------------------------------------------------------------ design B */

function SideBySide() {
  const queue = useQueue()
  const { folder } = queue.problem
  const { options } = queue

  return (
    <div className={styles.page}>
      <Stack justify="between" align="center" wrap className={styles.topBar}>
        <Counter queue={queue} />
        {queue.outcome === null ? <Actions queue={queue} /> : null}
      </Stack>

      {queue.outcome !== null ? (
        <Result queue={queue} />
      ) : (
        <div className={styles.split}>
          <aside className={`${styles.panel} ${styles.stickyAside}`} aria-label="Folder">
            <Stack direction="column" gap={12} align="stretch">
              <Artwork name={folder.tags.album} size="fill" className={styles.bigCover} />
              <FolderHeader folder={folder} compact />
              <ol className={styles.fileList} aria-label="Files in this folder">
                {folder.files.map((file) => (
                  <li key={file.name}>
                    <Text size="xs" family="mono" truncate>
                      {file.name}
                    </Text>
                    <Text size="xs" family="mono" tone="tertiary">
                      {file.length}
                    </Text>
                  </li>
                ))}
              </ol>
            </Stack>
          </aside>

          <Stack direction="column" gap={16} align="stretch">
            <SearchRow queue={queue} />
            <fieldset className={styles.options}>
              <legend>
                <Text size="sm" weight="medium">
                  Which album is this?
                </Text>
              </legend>
              {options.map((option) => (
                <article
                  key={option.mbid}
                  className={styles.option}
                  data-selected={queue.selected?.mbid === option.mbid || undefined}
                >
                  <Stack gap={12} align="start">
                    <Artwork name={option.title} src={coverOf(option)} size="md" />
                    <Stack direction="column" gap={4} align="start" className={styles.grow}>
                      <OptionRadio queue={queue} option={option}>
                        <Text size="sm" weight="semibold">
                          {option.title}
                        </Text>
                      </OptionRadio>
                      <Text size="sm" tone="secondary">
                        {option.artist}
                      </Text>
                      <OptionFacts option={option} />
                      <FitBadges folder={folder} option={option} />
                    </Stack>
                  </Stack>
                  <Snackbars key={option.mbid} folder={folder} option={option} />
                  <FactsDiff folder={folder} option={option} />
                  <TrackDiff folder={folder} option={option} />
                </article>
              ))}
            </fieldset>
          </Stack>
        </div>
      )}
    </div>
  )
}

/* ------------------------------------------------------------ design C */

function Columns() {
  const queue = useQueue()
  const { folder } = queue.problem
  const { options } = queue
  const diffs = options.map((option) => diff(folder, option))
  const longest = Math.max(folder.files.length, ...options.map((option) => option.slots.length))

  return (
    <div className={styles.page}>
      <Stack justify="between" align="center" wrap className={styles.topBar}>
        <Counter queue={queue} />
        {queue.outcome === null ? <Actions queue={queue} /> : null}
      </Stack>

      {queue.outcome !== null ? (
        <Result queue={queue} />
      ) : (
        <>
          <SearchRow queue={queue} />
          {options.length === 0 ? null : (
            <fieldset className={styles.bare}>
              <legend>
                <VisuallyHidden>Which album is this?</VisuallyHidden>
              </legend>
              <section
                className={styles.scroller}
                aria-label="Your folder compared with each album"
              >
                <table className={`${styles.table} ${styles.matrix}`}>
                  <caption>
                    <VisuallyHidden>Your folder compared with each album</VisuallyHidden>
                  </caption>
                  <thead>
                    <tr>
                      <th scope="col" className={styles.matrixYours}>
                        <Stack direction="column" gap={8} align="start">
                          <Artwork name={folder.tags.album} size="lg" />
                          <Text size="xs" tone="tertiary">
                            Your folder
                          </Text>
                          <Text size="sm" weight="semibold" family="mono">
                            {folder.path}
                          </Text>
                          <Text size="xs" tone="secondary">
                            {folder.files.length} files · {totalLength(folder)} · {folder.audio}
                          </Text>
                        </Stack>
                      </th>
                      {options.map((option, index) => (
                        <th
                          scope="col"
                          key={option.mbid}
                          data-selected={queue.selected?.mbid === option.mbid || undefined}
                        >
                          <Stack direction="column" gap={8} align="start">
                            <Artwork name={option.title} src={coverOf(option)} size="lg" />
                            <OptionRadio queue={queue} option={option}>
                              <Text size="sm" weight="semibold">
                                {option.title}
                              </Text>
                            </OptionRadio>
                            <OptionFacts option={option} />
                            <TagsBadge folder={folder} option={option} />
                            <Text size="xs" tone="secondary">
                              {diffs[index]?.seated} of {folder.files.length} match · worst drift{' '}
                              {diffs[index]?.worstDrift}s
                            </Text>
                          </Stack>
                        </th>
                      ))}
                    </tr>
                  </thead>
                  <tbody>
                    {factRows(folder, options[0] as Option).map((fact) => (
                      <tr key={fact.label} className={styles.factRow}>
                        <th scope="row">
                          <Text size="xs" tone="tertiary" block>
                            {fact.label}
                          </Text>
                          {fact.tags}
                        </th>
                        {options.map((option) => {
                          const row = factRows(folder, option).find(
                            (r) => r.label === fact.label,
                          ) as FactRow
                          return (
                            <td key={option.mbid} data-differs={differs(row) || undefined}>
                              <Stack gap={6} align="baseline" wrap>
                                {row.release}
                                {differs(row) ? (
                                  <Badge tone="warning" size="sm">
                                    differs
                                  </Badge>
                                ) : null}
                              </Stack>
                            </td>
                          )
                        })}
                      </tr>
                    ))}
                    {Array.from({ length: longest }, (_, index) => {
                      const file = folder.files[index]
                      return (
                        <tr key={file?.name ?? `slot-${index}`}>
                          <th scope="row" className={styles.mono}>
                            {file === undefined ? '—' : `${file.name} · ${file.length}`}
                          </th>
                          {options.map((option, optionIndex) => {
                            const seated = diffs[optionIndex]?.rows.find(
                              (row) => row.kind === 'seated' && row.file === file,
                            )
                            return (
                              <td
                                key={option.mbid}
                                data-differs={
                                  (seated?.kind === 'seated' && seated.titleDiffers) || undefined
                                }
                              >
                                {file === undefined ? null : seated?.kind === 'seated' ? (
                                  <Stack gap={4} align="baseline" wrap>
                                    <Text size="xs" family="mono" tone="tertiary">
                                      #{seated.slot.position}
                                    </Text>
                                    <Text size="sm">{seated.slot.title}</Text>
                                    {seated.titleDiffers ? (
                                      <Badge tone="danger" size="sm">
                                        title differs
                                      </Badge>
                                    ) : null}
                                    <DriftBadge drift={seated.drift} />
                                  </Stack>
                                ) : (
                                  <Badge tone="neutral" size="sm">
                                    not on this album
                                  </Badge>
                                )}
                              </td>
                            )
                          })}
                        </tr>
                      )
                    })}
                  </tbody>
                </table>
              </section>
            </fieldset>
          )}
        </>
      )}
    </div>
  )
}

/* ------------------------------------------------------------------ stories */

const meta = {
  title: 'Matching/Identify designs',
  parameters: { layout: 'fullscreen' },
} satisfies Meta

export default meta
type Story = StoryObj<typeof meta>

/** A: the folder, then one album at a time — flip through the search results, file the one showing. */
export const A_Carousel: Story = { name: 'A — Carousel', render: () => <Carousel /> }

/** B: the folder stays pinned on the left with its file list; albums scroll on the right. */
export const B_SideBySide: Story = { name: 'B — Side by side', render: () => <SideBySide /> }

/** C: one comparison table — your folder in the first column, one column per album. */
export const C_Columns: Story = { name: 'C — Columns', render: () => <Columns /> }
