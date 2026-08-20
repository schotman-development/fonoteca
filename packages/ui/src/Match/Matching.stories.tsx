import type { Meta, StoryObj } from '@storybook/react-vite'
import { type ReactNode, useDeferredValue, useState } from 'react'
import { expect, userEvent, waitFor } from 'storybook/test'

import { AudioTransport } from '../AudioTransport/AudioTransport.tsx'
import { Badge } from '../Badge/Badge.tsx'
import { Button } from '../Button/Button.tsx'
import { PlayButton } from '../PlayButton/PlayButton.tsx'
import { TONE_TRACKS } from '../playback/fixtures.ts'
import type { PlaybackTrack } from '../playback/PlaybackContext.ts'
import { PlaybackProvider } from '../playback/PlaybackProvider.tsx'
import { Stack } from '../Stack/Stack.tsx'
import { Text } from '../Text/Text.tsx'
import { CandidateCard } from './CandidateCard.tsx'
import { CandidateList, type CandidateSelection } from './CandidateList.tsx'
import { CandidateSearch } from './CandidateSearch.tsx'
import { EvidencePanel } from './EvidencePanel.tsx'
import { FitSummary } from './FitSummary.tsx'
import { filterCandidates } from './filter.ts'
import {
  ARTIST_CANDIDATES,
  EDITION_TIE,
  GREATEST_HITS_BOOTLEG,
  OFF_THE_WALL_2015,
  OFF_THE_WALL_SUBJECT,
  RECORDING_CANDIDATES,
  RELEASE_CANDIDATES,
  type ReleaseCandidateFixture,
} from './fixtures.ts'
import { type SlotRow, SlotTable } from './SlotTable.tsx'

/**
 * The three screens, assembled from the same components — which is the claim the
 * whole set makes and the only place it can be checked.
 *
 * The play controls are the real `PlayButton` under a real `PlaybackProvider`,
 * fed synthesized WAV data URIs. Nothing here touches the network.
 */

/** The AppShell transport slot, reproduced. */
function Shell({ children }: { readonly children: ReactNode }) {
  return (
    <Stack direction="column" gap={0} style={{ minHeight: '520px' }}>
      <div style={{ flex: 1, padding: 'var(--space-24)', minWidth: 0 }}>{children}</div>
      <div
        data-slot="transport"
        style={{
          height: 'var(--density-transport-height)',
          borderBlockStart: 'var(--border-width-thin) solid var(--color-border-subtle)',
          background: 'var(--color-surface-raised)',
        }}
      >
        <AudioTransport />
      </div>
    </Stack>
  )
}

function Title({ children }: { readonly children: ReactNode }) {
  return (
    <h1 style={{ margin: 0 }}>
      <Text size="xl" weight="bold">
        {children}
      </Text>
    </h1>
  )
}

/** A fixture tone per slot, so every play button in a screen is a real one. */
function trackFor(row: SlotRow, index: number): PlaybackTrack {
  const tone = TONE_TRACKS[index % TONE_TRACKS.length]
  return {
    id: `${row.discNumber}:${row.position}`,
    src: tone?.src ?? '',
    title: row.title,
    subtitle: row.file?.path ?? '',
    duration: 1.5,
  }
}

function facts(candidate: ReleaseCandidateFixture): string {
  return [
    candidate.year?.toString(),
    candidate.country,
    candidate.status,
    candidate.formats,
    `${candidate.fit.trackCount} tracks`,
  ]
    .filter(Boolean)
    .join(' · ')
}

const REFUSAL = {
  label: 'None of these',
  description:
    'A wrong album is worse than a missing one, and far harder to notice. Refusing leaves these files unfiled and asks again later.',
}

function releaseOption(candidate: ReleaseCandidateFixture) {
  return {
    id: candidate.id,
    render: (selection: Parameters<typeof CandidateCard>[0]['selection']) => (
      <CandidateCard
        title={candidate.title}
        subtitle={candidate.artist}
        facts={facts(candidate)}
        {...(candidate.catalogNumber != null
          ? { detail: `${candidate.label ?? '—'} · ${candidate.catalogNumber}` }
          : {})}
        {...(candidate.image != null ? { image: candidate.image } : {})}
        {...(selection != null ? { selection } : {})}
        fit={<FitSummary fit={candidate.fit} />}
        disclosureAside={
          <Badge tone="neutral" size="sm" mono>
            {candidate.fit.held} of {candidate.fit.trackCount}
          </Badge>
        }
      >
        <SlotTable
          caption={
            <Text size="xs" tone="tertiary">
              {candidate.title}
              {candidate.year != null ? ` (${candidate.year})` : ''}
            </Text>
          }
          rows={candidate.slots}
          renderPlay={(row) => {
            const index = candidate.slots.indexOf(row)
            return <PlayButton track={trackFor(row, index)} size="sm" />
          }}
        />
      </CandidateCard>
    ),
  }
}

const meta = {
  title: 'Matching/Screens',
  parameters: { layout: 'fullscreen' },
} satisfies Meta

export default meta
type Story = StoryObj<typeof meta>

/**
 * The primary case, whole: a component of files the attribution pass refused,
 * everything known about it, a filter over the candidates, the options with their
 * evidence, and a refusal that is a peer of the rest.
 *
 * The subject is the documented failure. *Off the Wall* — ten tracks, all ten
 * held, a 2015 remaster matching to 0.00 s — never reaches the component's
 * candidate set in the live run, so the files land on whichever compilation did.
 * This is the screen where a person fixes that, and the numbers are laid out so
 * that the right answer is the obvious one.
 */
export const AFileSetToARelease: Story = {
  render: () => {
    const [query, setQuery] = useState('')
    const deferred = useDeferredValue(query.trim())
    const [answer, setAnswer] = useState<CandidateSelection | null>(null)

    const shown = filterCandidates(RELEASE_CANDIDATES, deferred, (candidate) => ({
      title: candidate.title,
      artist: candidate.artist,
      ...(candidate.year != null ? { year: candidate.year } : {}),
      ...(candidate.country != null ? { country: candidate.country } : {}),
      formats: candidate.formats,
      extra: [candidate.status, candidate.label, candidate.catalogNumber].filter(
        (part): part is string => part != null,
      ),
    }))

    return (
      <PlaybackProvider>
        <Shell>
          <Stack direction="column" gap={24} style={{ maxWidth: '900px' }}>
            <Title>What are these ten files?</Title>

            <EvidencePanel
              heading="These ten files"
              subject={OFF_THE_WALL_SUBJECT.folder}
              subjectMono
              subjectNote="The folder name and the audio are the only two claims in existence, and one of them is the one not to believe."
              badges={<Badge tone="danger">Refused — NoConfidentFit</Badge>}
              rows={[
                { label: 'Files', value: '10' },
                { label: 'Format', value: OFF_THE_WALL_SUBJECT.formats, mono: true },
                { label: 'Total length', value: OFF_THE_WALL_SUBJECT.totalDuration, mono: true },
                { label: 'Album tag', value: 'not present', tone: 'warning' },
                { label: 'Track number tag', value: 'not present', tone: 'warning' },
                { label: 'Barcode tag', value: 'not present', tone: 'warning' },
                { label: 'AcoustID', value: 'on all 10 files', tone: 'success', mono: true },
                {
                  label: 'Folder says',
                  value: '1979',
                  note: 'The audio measures as the 2015 remaster.',
                },
              ]}
              actions={
                <Button variant="secondary" size="sm">
                  Re-fingerprint
                </Button>
              }
            >
              <Stack gap={8} align="center" wrap>
                <Text size="xs" tone="tertiary">
                  Listen:
                </Text>
                {OFF_THE_WALL_SUBJECT.files.slice(0, 4).map((file, index) => (
                  <Stack key={file.path} gap={6} align="center">
                    <PlayButton
                      size="sm"
                      track={{
                        id: file.path,
                        src: TONE_TRACKS[index % TONE_TRACKS.length]?.src ?? '',
                        title: file.path,
                        subtitle: OFF_THE_WALL_SUBJECT.folder,
                        duration: 1.5,
                      }}
                    />
                    <Text size="2xs" tone="tertiary" family="mono">
                      {file.fingerprint}
                    </Text>
                  </Stack>
                ))}
              </Stack>
            </EvidencePanel>

            <CandidateSearch
              value={query}
              onValueChange={setQuery}
              shown={shown.length}
              total={RELEASE_CANDIDATES.length}
              busy={query.trim() !== deferred}
            />

            <CandidateList
              label="Which release explains these ten files?"
              description="Coverage says whether the release is accounted for; drift says whether the audio agrees."
              value={answer}
              onSelect={setAnswer}
              options={shown.map(releaseOption)}
              refusal={REFUSAL}
              empty={
                <Text size="sm" tone="tertiary">
                  No candidate matches that filter.
                </Text>
              }
              footer={
                <>
                  <Button variant="primary" size="sm" disabled={answer == null}>
                    Commit
                  </Button>
                  <Text size="xs" tone="tertiary">
                    {answer == null
                      ? 'Nothing chosen yet.'
                      : answer.kind === 'none'
                        ? 'These files will stay unfiled.'
                        : 'All ten files will be attributed to this release.'}
                  </Text>
                </>
              }
            />
          </Stack>
        </Shell>
      </PlaybackProvider>
    )
  },
  play: async ({ canvas }) => {
    await expect(
      canvas.getByRole('group', { name: 'Which release explains these ten files?' }),
    ).toBeVisible()

    // Choosing, and then hearing what was chosen — the two things the screen
    // exists to make possible, in that order.
    await userEvent.click(canvas.getByRole('radio', { name: /Off the Wall.*2015/ }))
    await expect(
      canvas.getByText('All ten files will be attributed to this release.'),
    ).toBeVisible()

    await userEvent.click(canvas.getAllByRole('button', { name: /^Play / })[0] as HTMLElement)
    await waitFor(async () => {
      await expect(canvas.getByRole('region', { name: 'Now playing' })).toBeVisible()
    })
  },
}

/**
 * The identification case. Same components, smaller cards, no track table —
 * because the table is `children` rather than a prop, so leaving it out costs
 * nothing.
 *
 * The content is the documented AcoustID case: two clusters at 0.957 and 0.939
 * that are *one answer arriving twice* — a lossless rip and a 128kbps rip that
 * were never merged — both naming the same MusicBrainz recording. The third is a
 * live recording, which is why letting summed sources outvote a near tie would
 * tag a track from an album called *Live* with the studio take.
 */
export const AFileToARecording: Story = {
  render: () => {
    const [answer, setAnswer] = useState<CandidateSelection | null>(null)

    return (
      <PlaybackProvider>
        <Shell>
          <Stack direction="column" gap={24} style={{ maxWidth: '820px' }}>
            <Title>What is this file?</Title>

            <EvidencePanel
              heading="This file"
              subject="Joe Bonamassa/Sloe Gin/04 Sloe Gin.flac"
              subjectMono
              badges={<Badge tone="warning">Ambiguous</Badge>}
              rows={[
                { label: 'Size', value: '58.2 MB', mono: true },
                { label: 'Fingerprint length', value: '8:23.14', mono: true },
                { label: 'Clusters returned', value: '3' },
                {
                  label: 'Album tag',
                  value: 'not present',
                  tone: 'warning',
                  note: 'The directory name is the only text evidence there is.',
                },
              ]}
            >
              <Stack gap={8} align="center">
                <Text size="xs" tone="tertiary">
                  Listen:
                </Text>
                <PlayButton
                  size="sm"
                  track={{
                    id: 'sloe-gin-file',
                    src: TONE_TRACKS[0]?.src ?? '',
                    title: '04 Sloe Gin.flac',
                    subtitle: 'Joe Bonamassa/Sloe Gin',
                    duration: 1.5,
                  }}
                />
              </Stack>
            </EvidencePanel>

            <CandidateList
              label="Which recording is this?"
              value={answer}
              onSelect={setAnswer}
              options={RECORDING_CANDIDATES.map((candidate) => ({
                id: candidate.id,
                render: (selection) => (
                  <CandidateCard
                    title={candidate.title}
                    subtitle={`${candidate.artist} — ${candidate.release}`}
                    facts={`${candidate.length} · score ${candidate.score} · ${candidate.sources} sources`}
                    detail={candidate.mbid}
                    selection={selection}
                    fit={
                      <Text size="xs" tone="secondary">
                        {candidate.id === 'sloe-gin-live'
                          ? 'A different performance, and 84 seconds longer.'
                          : 'The same MusicBrainz recording as the other near-tied cluster.'}
                      </Text>
                    }
                  />
                ),
              }))}
              refusal={{
                label: 'None of these',
                description: 'AcoustID has never heard of plenty of bootlegs and field recordings.',
              }}
              footer={
                <Button variant="primary" size="sm" disabled={answer == null}>
                  Commit
                </Button>
              }
            />
          </Stack>
        </Shell>
      </PlaybackProvider>
    )
  },
}

/**
 * The third subject, through the same components again.
 *
 * Two Michael Jacksons — a musician and a British beer writer — is the canonical
 * MusicBrainz disambiguation case; the orchestra and the conductor under it are
 * the classical case `PrimaryCredits` exists for, where reading the credit line
 * alone would file a Karajan reading of Beethoven's Fifth under a man who died in
 * 1827.
 */
export const ARecordingToAnArtist: Story = {
  render: () => {
    const [answer, setAnswer] = useState<CandidateSelection | null>(null)

    return (
      <PlaybackProvider>
        <Shell>
          <Stack direction="column" gap={24} style={{ maxWidth: '820px' }}>
            <Title>Who is this?</Title>

            <EvidencePanel
              heading="This recording"
              subject="Rock with You"
              subjectNote="Held as 3 files, in 2 folders."
              rows={[
                { label: 'Length', value: '3:40', mono: true },
                { label: 'Billed credit', value: 'Michael Jackson' },
                { label: 'Artist tag', value: 'not present', tone: 'warning' },
                { label: 'Folder says', value: 'Michael Jackson', mono: true },
              ]}
            />

            <CandidateList
              label="Which artist is credited?"
              value={answer}
              onSelect={setAnswer}
              options={ARTIST_CANDIDATES.map((candidate) => ({
                id: candidate.id,
                render: (selection) => (
                  <CandidateCard
                    title={candidate.name}
                    subtitle={
                      candidate.disambiguation != null
                        ? `${candidate.type} — ${candidate.disambiguation}`
                        : candidate.type
                    }
                    facts={`${candidate.trackCount.toLocaleString()} tracks held`}
                    detail={`sorts as ${candidate.sortName}`}
                    artworkShape="circle"
                    selection={selection}
                    {...(candidate.image != null ? { image: candidate.image } : {})}
                    fit={
                      <Text size="xs" tone="secondary">
                        {candidate.trackCount === 0
                          ? 'Nothing in this library is credited to them.'
                          : 'Already browsable under this name.'}
                      </Text>
                    }
                  />
                ),
              }))}
              refusal={{ label: 'None of these' }}
              footer={
                <Button variant="primary" size="sm" disabled={answer == null}>
                  Commit
                </Button>
              }
            />
          </Stack>
        </Shell>
      </PlaybackProvider>
    )
  },
}

/**
 * Four pressings on identical track lists — one at 0.00 s and three at 0.76 s.
 *
 * The tie is visible **before** choosing, and drift is the only thing separating
 * them. `FingerprintDuration` is stored to 10 ms, and against per-release track
 * lengths that is precise enough to tell a 2015 remaster from a 1979 pressing.
 * The play button is what settles it for a person who does not trust the numbers.
 */
export const TheEditionTie: Story = {
  render: () => {
    const [answer, setAnswer] = useState<CandidateSelection | null>(null)

    return (
      <PlaybackProvider>
        <Shell>
          <Stack direction="column" gap={24} style={{ maxWidth: '900px' }}>
            <Title>Which pressing?</Title>
            <Text size="sm" tone="secondary">
              Four editions, one track list, and 0.76 seconds between them.
            </Text>
            <CandidateList
              label="Which edition do these files come from?"
              value={answer}
              onSelect={setAnswer}
              options={EDITION_TIE.map(releaseOption)}
              refusal={REFUSAL}
              footer={
                <Button variant="primary" size="sm" disabled={answer == null}>
                  Commit
                </Button>
              }
            />
          </Stack>
        </Shell>
      </PlaybackProvider>
    )
  },
  play: async ({ canvas }) => {
    // Every edition claims full coverage; only drift separates them. `getAllBy`,
    // because each figure appears both in the summary and in that edition's slot
    // table — which is the point, not a duplication to remove.
    await expect(canvas.getAllByText('100%').length).toBeGreaterThan(4)
    await expect(canvas.getAllByText('0.00 s').length).toBeGreaterThan(0)
    await expect(canvas.getAllByText('0.76 s').length).toBeGreaterThanOrEqual(3)
  },
}

/**
 * The trap, side by side.
 *
 * The bootleg *Greatest Hits* explains **seventeen** files where the album
 * explains ten, and the obvious greedy — take the release accounting for the most
 * files — picks it. Putting coverage next to files-explained is what makes the
 * wrong answer look wrong: 0.55 against 1.00, drift 1.99 s against 0.00 s, and a
 * status of Bootleg.
 */
export const TheBootlegTrap: Story = {
  render: () => {
    const [answer, setAnswer] = useState<CandidateSelection | null>(null)

    return (
      <PlaybackProvider>
        <Shell>
          <Stack direction="column" gap={24} style={{ maxWidth: '900px' }}>
            <Title>Seventeen files, or ten?</Title>
            <CandidateList
              label="Which release explains these files?"
              description="The first line explains more files. The second explains all of itself."
              value={answer}
              onSelect={setAnswer}
              options={[GREATEST_HITS_BOOTLEG, OFF_THE_WALL_2015].map(releaseOption)}
              refusal={REFUSAL}
              footer={
                <Button variant="primary" size="sm" disabled={answer == null}>
                  Commit
                </Button>
              }
            />
          </Stack>
        </Shell>
      </PlaybackProvider>
    )
  },
  play: async ({ canvas }) => {
    await expect(canvas.getByText('17 of 31 slots filled')).toBeVisible()
    await expect(canvas.getByText('10 of 10 slots filled')).toBeVisible()
    await expect(canvas.getAllByText('1.99 s').length).toBeGreaterThan(0)
    await expect(canvas.getByText('Unofficial')).toBeVisible()
  },
}
