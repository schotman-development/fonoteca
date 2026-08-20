import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect } from 'storybook/test'

import { Badge } from '../Badge/Badge.tsx'
import { Button } from '../Button/Button.tsx'
import { EvidencePanel } from './EvidencePanel.tsx'
import { OFF_THE_WALL_SUBJECT } from './fixtures.ts'

const FILE_SET_ROWS = [
  { label: 'Files', value: '10' },
  { label: 'Format', value: OFF_THE_WALL_SUBJECT.formats, mono: true },
  { label: 'Total length', value: OFF_THE_WALL_SUBJECT.totalDuration, mono: true },
  { label: 'Fingerprint lengths', value: '6:04.97 — 3:04.99', mono: true },
  { label: 'Album tag', value: 'not present', tone: 'warning' as const },
  { label: 'Track number tag', value: 'not present', tone: 'warning' as const },
  { label: 'AcoustID', value: 'on all 10 files', tone: 'success' as const, mono: true },
  {
    label: 'Outcome',
    value: 'NoConfidentFit',
    tone: 'danger' as const,
    note: 'No release explained these files well enough to name one.',
  },
]

const meta = {
  title: 'Matching/EvidencePanel',
  component: EvidencePanel,
  parameters: { layout: 'padded' },
  args: {
    heading: 'These ten files',
    subject: OFF_THE_WALL_SUBJECT.folder,
    subjectMono: true,
    subjectNote: 'The only two claims in existence: this folder name, and the audio.',
    badges: <Badge tone="danger">Refused</Badge>,
    rows: FILE_SET_ROWS,
  },
  render: (args) => (
    <div style={{ maxWidth: '760px' }}>
      <EvidencePanel {...args} />
    </div>
  ),
} satisfies Meta<typeof EvidencePanel>

export default meta
type Story = StoryObj<typeof meta>

/** The primary subject: a component of files the attribution pass refused. */
export const AFileSet: Story = {
  args: {
    actions: (
      <Button variant="secondary" size="sm">
        Re-fingerprint
      </Button>
    ),
  },
  play: async ({ canvas }) => {
    // A Card, so the heading is a real one.
    await expect(canvas.getByRole('heading', { name: 'These ten files' })).toBeVisible()
  },
}

/**
 * **Absence is a row with a value.**
 *
 * Sampling this library with ffprobe found `ACOUSTID_ID` on every file and
 * nothing else at all — no ALBUM, no TRACKNUMBER, no barcode. "not present" is
 * therefore a finding, and dropping the row would lose it. It is also what makes
 * the folder name and the audio the only two claims there are.
 */
export const NoTagsAtAll: Story = {
  play: async ({ canvas }) => {
    await expect(canvas.getAllByText('not present')).toHaveLength(2)
    await expect(canvas.getByText('Album tag')).toBeVisible()
  },
}

/**
 * The folder says 1979 and the audio measures as the 2015 remaster. The panel
 * shows both claims and prefers neither — the directory name is evidence, and it
 * is the piece of evidence not to believe.
 */
export const TheFolderLies: Story = {
  args: {
    rows: [
      { label: 'Folder says', value: 'Off the Wall (1979)', mono: true },
      {
        label: 'Audio measures as',
        value: 'the 2015 remaster',
        tone: 'info' as const,
        note: 'Ten track lengths matching to 0.00 s; the 1979 pressings sit at 0.76 s.',
      },
      { label: 'Album tag', value: 'not present', tone: 'warning' as const },
    ],
  },
}

/** The identification subject — one file, and the rows that describe one file. */
export const ASingleFile: Story = {
  args: {
    heading: 'This file',
    subject: 'Joe Bonamassa/Sloe Gin/04 Sloe Gin.flac',
    subjectNote: undefined,
    badges: <Badge tone="warning">Ambiguous</Badge>,
    rows: [
      { label: 'Size', value: '58.2 MB', mono: true },
      { label: 'Fingerprint length', value: '8:23.14', mono: true },
      { label: 'AcoustID', value: '9d8a7b6c-…-1f2e3d4c', mono: true },
      { label: 'Clusters returned', value: '3', note: 'Two of them name the same recording.' },
      { label: 'Album tag', value: 'not present', tone: 'warning' as const },
    ],
  },
}

/** The artist subject. Same component again — genericity via `rows` is the design. */
export const ARecording: Story = {
  args: {
    heading: 'This recording',
    subject: 'Symphony No. 5 in C minor, Op. 67: I. Allegro con brio',
    subjectMono: false,
    subjectNote: 'Held as 4 files, in 2 folders.',
    badges: <Badge tone="neutral">Enriched</Badge>,
    rows: [
      { label: 'Work', value: 'Symphony no. 5 in C minor, op. 67' },
      { label: 'Billed credit', value: 'Ludwig van Beethoven' },
      { label: 'Conductor', value: 'Herbert von Karajan', tone: 'info' as const },
      { label: 'Orchestra', value: 'Berliner Philharmoniker', tone: 'info' as const },
      {
        label: 'Why this matters',
        value: 'The credit line alone would file this under a man who died in 1827.',
        wide: true,
      },
    ],
  },
}

/**
 * A path is up to 4096 characters and a box set broke a live run at file 76, so
 * the length is real. The row takes the full width and breaks anywhere rather
 * than pushing the grid wider than the card.
 */
export const LongPath: Story = {
  args: {
    rows: [
      {
        label: 'Path',
        wide: true,
        mono: true,
        value:
          'Various Artists/The Complete Motown Singles, Vol. 9 1969 (2008 Remastered Box Set ' +
          'Limited Edition)/Disc 5 of 6 — Rare and Unissued Sides/17 Someday We’ll Be Together ' +
          '(Alternate Mono Single Mix).flac',
      },
      { label: 'Files', value: '76' },
      { label: 'Album tag', value: 'not present', tone: 'warning' as const },
    ],
  },
}
