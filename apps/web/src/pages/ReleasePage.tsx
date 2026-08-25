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
    <Stack gap={16} align="start">
      {/*
        The cover, at the one fixed size the design system has for a header. It
        is the Cover Art Archive's picture of *this pressing*, which is the point
        on the page where an edition is decided: two releases with the same title
        and track list are told apart by their sleeves long before anyone reads
        the year.
      */}
      <Artwork
        name={release.title}
        size="lg"
        {...(release.mbid != null ? { src: releaseArt(release.mbid) } : {})}
      />

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
    </Stack>
  )
}

/**
 * Offering this album's fingerprints back to AcoustID.
 *
 * Only ever shown when there is something to offer, which on an ordinary album
 * is never: the identification pass took its answer from AcoustID, so AcoustID
 * is where the link came from. A non-zero count means somebody chose these
 * recordings by hand — a live recording nobody has fingerprinted, or audio
 * AcoustID knows and MusicBrainz linked to nothing — and the link they made
 * exists only in this library.
 *
 * A button and nothing else. There is no automatic path to this call and there
 * must not be: it writes a claim into a shared database under the operator's own
 * account, and the whole point of the screen behind it was that a person looked
 * at the pairing first.
 */
function Contribute({
  releaseId,
  count,
  onContributed,
}: {
  readonly releaseId: string
  readonly count: number
  readonly onContributed: () => void
}) {
  const [state, setState] = useState<
    { status: 'idle' | 'sending' } | { status: 'done' | 'error'; message: string }
  >({ status: 'idle' })

  if (count === 0) {
    // After a successful send the refetch brings the count back as zero, which
    // is the confirmation — so the sentence survives the row disappearing.
    return state.status === 'done' ? (
      <Text size="sm" tone="tertiary">
        {state.message}
      </Text>
    ) : null
  }

  async function send() {
    setState({ status: 'sending' })

    try {
      const result = await api.post('/api/catalogue/releases/{id}/fingerprints', {
        params: { path: { id: releaseId } },
      })

      setState({ status: 'done', message: result.detail })
      onContributed()
    } catch (cause: unknown) {
      setState({ status: 'error', message: describeError(cause) })
    }
  }

  return (
    <Stack direction="column" gap={8} align="start">
      <Stack gap={12} align="center" wrap>
        <Badge tone="neutral" size="sm">
          {count} to contribute
        </Badge>

        <Button
          size="sm"
          variant="secondary"
          disabled={state.status === 'sending'}
          onClick={() => {
            void send()
          }}
        >
          {state.status === 'sending' ? 'Sending…' : 'Contribute fingerprints'}
        </Button>
      </Stack>

      <Text size="xs" tone="tertiary" block>
        You chose {count === 1 ? "this file's recording" : 'these recordings'} by hand rather than
        taking AcoustID's answer. Sending the stored fingerprint bound to that recording makes the
        music identifiable for everyone — including this library, if it is ever re-ripped. Submitted
        under your own AcoustID account. Nothing on disk is touched.
      </Text>

      {state.status === 'error' ? (
        <Text size="sm" tone="danger" block>
          {state.message}
        </Text>
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
