import { type components, describeError } from '@fonoteca/api-client'
import { Badge, Button, Stack, Text } from '@fonoteca/ui'
import { useCallback, useEffect, useState } from 'react'

import { api } from '../api.ts'
import { type JobProgress, useJobsConnection } from '../useJobsConnection.ts'

type Status = components['schemas']['EnrichmentStatusResponse']
type Summary = components['schemas']['EnrichmentSummary']

/** The hub's `kind` for this pass. Matches `EnrichmentService.JobKind`. */
const JOB_KIND = 'library.enrich'

/** Fallback poll, for the case the hub is down. Same reasoning as the identify card. */
const POLL_MS = 5000

/**
 * The pass that turns identified files into a browsable library.
 *
 * A separate card from identification, because they are separate passes with
 * opposite risk profiles: that one rewrites the user's files, this one only
 * reads the catalogue and asks two web services. It is also the one that can be
 * re-run freely — every fingerprint it needs is already stored, so it opens no
 * file and runs with the library volume unmounted.
 */
/**
 * What is left to do, in the nouns it is actually left to do it to.
 *
 * Every clause that applies, which on a partly enriched library is two of them
 * and is also the only rendering that says why the run will take as long as it
 * will — an artist costs a MusicBrainz turn just as a file does, and a picture
 * costs a fraction of one batched query.
 */
function pendingSentence(status: {
  readonly pendingFiles: number
  readonly pendingArtists: number
  readonly pendingPortraits: number
}): string {
  const parts: string[] = []

  if (status.pendingFiles > 0) {
    parts.push(
      `${status.pendingFiles.toLocaleString()} identified file${
        status.pendingFiles === 1 ? '' : 's'
      } with no recording yet`,
    )
  }

  if (status.pendingArtists > 0) {
    parts.push(
      `${status.pendingArtists.toLocaleString()} artist${
        status.pendingArtists === 1 ? '' : 's'
      } to describe`,
    )
  }

  // Third clause, and on a library described before pictures existed it is the
  // only one — which is exactly why it is here. The count is deliberately not
  // added to the artist one: they are the same rows and different work, and a
  // person reading "2,902 artists to describe" would be told to expect the
  // forty-five minutes that number used to mean.
  if (status.pendingPortraits > 0) {
    parts.push(
      `${status.pendingPortraits.toLocaleString()} picture${
        status.pendingPortraits === 1 ? '' : 's'
      } to look for`,
    )
  }

  return `${parts.join(' · ')}.`
}

export function EnrichmentPanel({ onEnriched }: { readonly onEnriched?: () => void }) {
  const [status, setStatus] = useState<Status | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  const { jobs } = useJobsConnection()
  const live: JobProgress | undefined = jobs[JOB_KIND]

  const refresh = useCallback(async () => {
    try {
      setStatus(await api.get('/api/library/enrich'))
      setError(null)
    } catch (cause) {
      setError(describeError(cause))
    }
  }, [])

  useEffect(() => {
    void refresh()
  }, [refresh])

  const running = status?.running === true || live?.state === 'running'

  useEffect(() => {
    if (!running) return undefined

    const timer = window.setInterval(() => void refresh(), POLL_MS)
    return () => window.clearInterval(timer)
  }, [running, refresh])

  // A finished pass has changed the catalogue counts as well as the summary, so
  // the card that shows them is told too.
  useEffect(() => {
    if (live?.state !== 'completed') return

    void refresh()
    onEnriched?.()
  }, [live?.state, refresh, onEnriched])

  async function enrich() {
    setBusy(true)
    setError(null)

    try {
      await api.post('/api/library/enrich')
      await refresh()
    } catch (cause) {
      setError(describeError(cause))
      await refresh()
    } finally {
      setBusy(false)
    }
  }

  async function cancel() {
    try {
      await api.delete('/api/library/enrich')
    } catch (cause) {
      setError(describeError(cause))
    }

    await refresh()
  }

  if (status === null) {
    return error === null ? (
      <Text tone="tertiary">Contacting API…</Text>
    ) : (
      <Stack direction="column" gap={4} align="start">
        <Badge tone="danger">API unreachable</Badge>
        <Text size="sm" tone="tertiary" family="mono">
          {error}
        </Text>
      </Stack>
    )
  }

  const processed = live?.processed ?? status.processed
  const total = live?.total ?? status.total
  const summary = status.lastCompleted

  return (
    <Stack direction="column" gap={12}>
      <Stack gap={8} align="center" wrap>
        <Button
          variant="primary"
          onClick={() => void enrich()}
          disabled={busy || running || status.pending === 0}
        >
          {running ? 'Enriching…' : 'Enrich'}
        </Button>

        {running ? (
          <Button size="sm" variant="danger" onClick={() => void cancel()}>
            Stop
          </Button>
        ) : null}
      </Stack>

      <Stack direction="column" gap={12} role="status" aria-live="polite" aria-busy={running}>
        {error !== null ? (
          <Stack direction="column" gap={4} align="start">
            <Badge tone="danger">Enrichment failed</Badge>
            <Text size="sm" tone="tertiary">
              {error}
            </Text>
          </Stack>
        ) : null}

        {running ? (
          <Stack direction="column" gap={4}>
            <Text size="sm" family="mono">
              {processed.toLocaleString()} / {(total ?? 0).toLocaleString()}
            </Text>
            {live?.currentItem != null ? (
              <Text size="xs" tone="tertiary" truncate>
                {live.currentItem}
              </Text>
            ) : null}
          </Stack>
        ) : (
          /*
            Two clauses, because the pass now asks two kinds of question and one
            noun cannot cover both. Written as one sentence per kind and joined,
            rather than as a count of "things": on a library whose files are all
            enriched this line used to read "2,838 identified files with no
            recording yet", which was wrong in both nouns and pointed a person at
            the wrong screen.
          */
          <Text size="sm" tone={status.pending === 0 ? 'tertiary' : 'primary'}>
            {status.pending === 0 ? 'Everything has been looked up.' : pendingSentence(status)}
          </Text>
        )}

        {summary !== null && !running ? <Result summary={summary} /> : null}
      </Stack>

      <Text size="xs" tone="tertiary">
        Takes each file's stored fingerprint back to AcoustID for the recording it names, then asks
        MusicBrainz who made it. Opens no file.
      </Text>
    </Stack>
  )
}

/**
 * What the last pass did.
 *
 * The two shortfalls are shown apart because they mean opposite things. A
 * cluster with no MusicBrainz recording is a gap in AcoustID's links that
 * submitting to them would close; a recording MusicBrainz no longer has was
 * merged away since AcoustID last saw it, and nothing here can fix that.
 */
function Result({ summary }: { readonly summary: Summary }) {
  return (
    <Stack direction="column" gap={8}>
      <Stack gap={20} wrap>
        <Stat label="Linked" value={summary.linked} />
        <Stat label="Artists" value={summary.artists} />
        <Stat label="Recordings" value={summary.recordings} />
        <Stat label="Works" value={summary.works} />
      </Stack>

      {summary.noRecording > 0 ? (
        <Text size="xs" tone="tertiary">
          {summary.noRecording.toLocaleString()} file
          {summary.noRecording === 1 ? "'s cluster is" : "s' clusters are"} not linked to any
          MusicBrainz recording. Ordinary rather than broken — linking a fingerprint cluster to
          MusicBrainz is a separate act of curation, and nobody has done it for that audio.
        </Text>
      ) : null}

      {summary.recordingNotFound > 0 ? (
        <Text size="xs" tone="tertiary">
          {summary.recordingNotFound.toLocaleString()} named a recording MusicBrainz has since
          merged away.
        </Text>
      ) : null}

      <Text size="xs" tone="tertiary">
        {summary.examined.toLocaleString()} examined · took{' '}
        {formatDuration(summary.durationMilliseconds)}
        {summary.cancelled ? ' · stopped early' : ''}
      </Text>

      {summary.failed > 0 ? (
        <Text size="sm" tone="tertiary">
          {summary.failed.toLocaleString()} failed transiently and stayed on the list; running again
          retries {summary.failed === 1 ? 'it' : 'them'}.
        </Text>
      ) : null}
    </Stack>
  )
}

function Stat({ label, value }: { readonly label: string; readonly value: number }) {
  return (
    <Stack direction="column" gap={2}>
      <Text size="lg" weight="semibold" family="mono">
        {value.toLocaleString()}
      </Text>
      <Text size="xs" tone="tertiary">
        {label}
      </Text>
    </Stack>
  )
}

function formatDuration(ms: number): string {
  if (ms < 1000) return `${ms.toLocaleString()} ms`

  const seconds = ms / 1000
  if (seconds < 60) return `${seconds.toFixed(1)} s`

  return `${Math.floor(seconds / 60)} m ${Math.round(seconds % 60)} s`
}
