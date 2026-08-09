import { type components, describeError } from '@fonoteca/api-client'
import { Badge, Button, Stack, Text } from '@fonoteca/ui'
import { useCallback, useEffect, useState } from 'react'

import { api } from '../api.ts'
import { type JobProgress, useJobsConnection } from '../useJobsConnection.ts'

type Status = components['schemas']['AttributionStatusResponse']
type Summary = components['schemas']['AttributionSummary']

/** The hub's `kind` for this pass. Matches `ReleaseAttributionService.JobKind`. */
const JOB_KIND = 'library.attribute'

/** Fallback poll, for the case the hub is down. Same reasoning as the other cards. */
const POLL_MS = 5000

/**
 * The pass that works out which album each file came from.
 *
 * The third card and the last of the chain: scan finds files, identify decides
 * what audio they are, enrich decides who made it, this decides where it was
 * published. Like enrichment it only reads the catalogue and asks MusicBrainz,
 * so it can be re-run freely.
 *
 * The result deliberately shows the refusals next to the answers. This pass
 * declines to guess, and a run that attributed four fifths of a library and
 * refused the rest is working correctly — a card that showed only the successes
 * would make that look like a partial failure.
 */
export function AttributionPanel({ onAttributed }: { readonly onAttributed?: () => void }) {
  const [status, setStatus] = useState<Status | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  const { jobs } = useJobsConnection()
  const live: JobProgress | undefined = jobs[JOB_KIND]

  const refresh = useCallback(async () => {
    try {
      setStatus(await api.get('/api/library/attribute'))
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

  useEffect(() => {
    if (live?.state !== 'completed') return

    void refresh()
    onAttributed?.()
  }, [live?.state, refresh, onAttributed])

  async function attribute() {
    setBusy(true)
    setError(null)

    try {
      await api.post('/api/library/attribute')
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
      await api.delete('/api/library/attribute')
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
          onClick={() => void attribute()}
          disabled={busy || running || status.pending === 0}
        >
          {running ? 'Attributing…' : 'Attribute'}
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
            <Badge tone="danger">Attribution failed</Badge>
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
          <Text size="sm" tone={status.pending === 0 ? 'tertiary' : 'primary'}>
            {status.pending === 0
              ? 'Every identified file has been asked which album it came from.'
              : `${status.pending.toLocaleString()} file${status.pending === 1 ? '' : 's'} with no album yet.`}
          </Text>
        )}

        {summary !== null && !running ? <Result summary={summary} /> : null}
      </Stack>

      <Text size="xs" tone="tertiary">
        Decides files in sets, because one file cannot name its album. The folders on disk are not
        consulted — they are used afterwards to check the answer.
      </Text>
    </Stack>
  )
}

/**
 * What the last pass did, refusals included.
 *
 * The four shortfalls mean four different things and are shown apart. A file
 * with no confident fit is the strict gate working and belongs in a review
 * queue; a recording on no release at all is a gap in MusicBrainz; a failure is
 * transient and retries on the next run. Summed into one number they would be
 * indistinguishable, and only some of them are worth acting on.
 */
function Result({ summary }: { readonly summary: Summary }) {
  const filed = summary.attributed + summary.ambiguous + summary.groupOnly

  return (
    <Stack direction="column" gap={8}>
      <Stack gap={20} wrap>
        <Stat label="Filed" value={filed} />
        <Stat label="Albums" value={summary.releases} />
        <Stat label="Unresolved" value={summary.noConfidentFit + summary.noCandidate} />
      </Stack>

      {summary.ambiguous > 0 ? (
        <Text size="xs" tone="tertiary">
          {summary.ambiguous.toLocaleString()} landed on a pressing chosen from several that fitted
          exactly as well. The track list is the same on all of them, so nothing was lost by
          choosing — but the choice was a coin flip and is recorded as one.
        </Text>
      ) : null}

      {summary.groupOnly > 0 ? (
        <Text size="xs" tone="tertiary">
          {summary.groupOnly.toLocaleString()} know their album but not their pressing, because the
          editions that fitted disagree about which disc and track the music is on.
        </Text>
      ) : null}

      {summary.noConfidentFit > 0 ? (
        <Text size="xs" tone="tertiary">
          {summary.noConfidentFit.toLocaleString()} were left unattributed because no release
          explained them well enough. Usually anthologies of licensed catalogue, where the same
          recording sits on twenty compilations and nothing chooses between them.
        </Text>
      ) : null}

      {summary.noCandidate > 0 ? (
        <Text size="xs" tone="tertiary">
          {summary.noCandidate.toLocaleString()} name a recording MusicBrainz puts on no release at
          all.
        </Text>
      ) : null}

      <Text size="xs" tone="tertiary">
        {summary.examined.toLocaleString()} examined in {summary.components.toLocaleString()} set
        {summary.components === 1 ? '' : 's'} · took {formatDuration(summary.durationMilliseconds)}
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
  if (ms < 60_000) return `${(ms / 1000).toFixed(1)} s`

  return `${Math.floor(ms / 60_000)} min ${Math.round((ms % 60_000) / 1000)} s`
}
