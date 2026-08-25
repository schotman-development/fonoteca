import { type components, describeError } from '@fonoteca/api-client'
import {
  Artwork,
  Badge,
  Button,
  Stack,
  Table,
  TableCell,
  TableHeaderCell,
  Text,
} from '@fonoteca/ui'
import { Link, useParams } from '@tanstack/react-router'
import { useState } from 'react'

import { api } from '../api.ts'
import { useApiQuery } from '../useApiQuery.ts'
import { CERTAINTY } from './certainty.ts'
import { releaseArt } from './coverArt.ts'
import styles from './ReleasePage.module.css'

type ReleaseTrackRow = components['schemas']['ReleaseTrackRow']

/**
 * One album, and everything on it — including what the library does not have.
 *
 * The missing tracks are the reason this page stores the whole track list rather
 * than only the part that was matched. "Eleven of twelve" is a number; a greyed
 * row where track 7 should be is an answer.
 */
export function ReleasePage() {
  const { releaseId } = useParams({ from: '/library/releases/$releaseId' })
  const [contributions, setContributions] = useState(0)

  const state = useApiQuery(
    () => api.get('/api/catalogue/releases/{id}', { params: { path: { id: releaseId } } }),
    [releaseId, contributions],
  )

  return (
    <Stack direction="column" gap={20}>
      <Link to="/library/releases" className={styles.back}>
        <Text size="sm">← All albums</Text>
      </Link>

      <div role="status" aria-live="polite" aria-busy={state.status === 'loading'}>
        {state.status === 'loading' ? <Text tone="tertiary">Reading the catalogue…</Text> : null}

        {state.status === 'error' ? (
          <Stack direction="column" gap={4} align="start">
            <Badge tone="danger">Could not read this album</Badge>
            <Text size="sm" tone="tertiary">
              {state.message}
            </Text>
          </Stack>
        ) : null}
      </div>

      {state.status === 'ready' ? (
        <Stack direction="column" gap={20}>
          <Header release={state.data.release} />
          <Contribute
            releaseId={releaseId}
            count={state.data.contributable}
            onContributed={() => {
              setContributions((done) => done + 1)
            }}
          />
          <Tracks
            tracks={state.data.tracks}
            title={state.data.release.title}
            discs={state.data.release.discCount ?? 1}
          />
        </Stack>
      ) : null}
    </Stack>
  )
}

function Header({ release }: { readonly release: components['schemas']['ReleaseSummary'] }) {
  const certainty = CERTAINTY[release.certainty]
  const missing = release.trackCount - release.held

  return (
    <Stack direction="column" gap={8}>
      <h1 className={styles.title}>
        <Text size="xl" weight="semibold" block>
          {release.title}
        </Text>
      </h1>

      {release.artist != null ? (
        <Text tone="secondary" block>
          {release.artist}
        </Text>
      ) : null}

      <Stack gap={8} align="center" wrap>
        {release.year != null ? (
          <Badge tone="neutral" size="sm">
            {release.year}
          </Badge>
        ) : null}
        {release.formats != null ? (
          <Badge tone="neutral" size="sm">
            {release.formats}
          </Badge>
        ) : null}
        {release.country != null ? (
          <Badge tone="neutral" size="sm">
            {release.country}
          </Badge>
        ) : null}
        {/*
          Status only when it is not the ordinary one. A "Official" badge on
          almost every album teaches the eye to ignore the badge, and then the
          bootleg goes unnoticed too.
        */}
        {release.status != null && release.status !== 'Official' ? (
          <Badge tone="warning" size="sm">
            {release.status}
          </Badge>
        ) : null}

        <Text size="sm" tone={missing > 0 ? 'warning' : 'tertiary'} family="mono">
          {release.held}/{release.trackCount} tracks
        </Text>

        {release.files > release.held ? (
          <Text size="sm" tone="tertiary">
            {release.files.toLocaleString()} files
          </Text>
        ) : null}
      </Stack>

      {/*
        How sure the catalogue is, spelled out rather than badged. This is the
        page where a person decides whether to trust the answer, and "one of
        several pressings" is not self-explanatory.
      */}
      {certainty !== undefined && certainty.tone !== 'ok' ? (
        <Stack direction="column" gap={4} align="start">
          <Badge tone="warning" size="sm">
            {certainty.label}
            {release.editionAlternatives > 0
              ? ` · ${release.editionAlternatives} others fitted`
              : ''}
          </Badge>
          <Text size="xs" tone="tertiary" block>
            {certainty.note}
          </Text>
        </Stack>
      ) : null}
    </Stack>
  )
}

function Tracks({
  tracks,
  title,
  discs,
}: {
  readonly tracks: readonly ReleaseTrackRow[]
  readonly title: string
  readonly discs: number
}) {
  if (tracks.length === 0) {
    return <Text tone="tertiary">MusicBrainz lists no tracks for this release.</Text>
  }

  return (
    <div className={styles.tracks}>
      <Table density="cozy">
        <caption className={styles.caption}>Every track on {title}, held or not</caption>
        <thead>
          <tr>
            <TableHeaderCell numeric>{discs > 1 ? 'Disc·No' : 'No'}</TableHeaderCell>
            <TableHeaderCell className={styles.titleCol}>Track</TableHeaderCell>
            <TableHeaderCell numeric>Length</TableHeaderCell>
            <TableHeaderCell>Have it</TableHeaderCell>
          </tr>
        </thead>
        <tbody>
          {tracks.map((track) => (
            <Row key={`${track.discNumber}-${track.position}`} track={track} discs={discs} />
          ))}
        </tbody>
      </Table>
    </div>
  )
}

function Row({ track, discs }: { readonly track: ReleaseTrackRow; readonly discs: number }) {
  return (
    <tr data-missing={track.held ? undefined : ''}>
      <TableCell numeric>
        <Text size="sm" family="mono" tone="tertiary">
          {discs > 1 ? `${track.discNumber}·` : ''}
          {track.number ?? track.position}
        </Text>
      </TableCell>

      <TableCell truncate>
        <Text truncate tone={track.held ? 'primary' : 'tertiary'}>
          {track.title}
        </Text>
      </TableCell>

      <TableCell numeric>
        <Text size="sm" family="mono" tone={track.duration == null ? 'tertiary' : 'primary'}>
          {track.duration ?? '—'}
        </Text>
      </TableCell>

      <TableCell>
        {track.held ? (
          /*
            The file count rather than a tick: more than one is the same track in
            several encodings, which is the dedupe question the whole schema
            exists to be able to ask.
          */
          <Text size="sm" family="mono" tone={track.files.length > 1 ? 'warning' : 'tertiary'}>
            {track.files.length === 1 ? 'yes' : `${track.files.length} files`}
          </Text>
        ) : (
          <Text size="sm" tone="tertiary">
            missing
          </Text>
        )}
      </TableCell>
    </tr>
  )
}
