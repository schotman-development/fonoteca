import { type components, describeError } from '@fonoteca/api-client'
import { Badge, Button, Stack, Text } from '@fonoteca/ui'
import { useCallback, useEffect, useState } from 'react'

import { api } from '../api.ts'
import { type JobProgress, useJobsConnection } from '../useJobsConnection.ts'

type Status = components['schemas']['AnalysisStatusResponse']
type Summary = components['schemas']['AnalysisSummary']

/** The hub's `kind` for this pass. Matches `AnalysisService.JobKind`. */
const JOB_KIND = 'library.analyse'

/** Fallback poll, for the case the hub is down. Same reasoning as the other cards. */
const POLL_MS = 5000

/**
 * The pass that analyses what the audio is like: loudness for ReplayGain,
 * dynamic range, the bits really used, silence and clipping.
 *
 * `ProbePanel`'s shape, and running is the server's word for the same reason
 * it is there: the hub's last message per kind is never cleared.
 */
export function AnalysisPanel({ onAnalysed }: { readonly onAnalysed?: () => void }) {
  const [status, setStatus] = useState<Status | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  const { jobs } = useJobsConnection()
  const live: JobProgress | undefined = jobs[JOB_KIND]

  const refresh = useCallback(async () => {
    try {
      setStatus(await api.get('/api/library/analyse'))
      setError(null)
    } catch (cause) {
      setError(describeError(cause))
    }
  }, [])

  useEffect(() => {
    void refresh()
  }, [refresh])

  const running = status?.running === true

  useEffect(() => {
    if (!running) return undefined

    const timer = window.setInterval(() => void refresh(), POLL_MS)
    return () => window.clearInterval(timer)
  }, [running, refresh])

  useEffect(() => {
    if (live?.state !== 'completed') return

    void refresh()
    onAnalysed?.()
  }, [live?.state, refresh, onAnalysed])

  async function analyse() {
    setBusy(true)
    setError(null)

    try {
      await api.post('/api/library/analyse')
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
      await api.delete('/api/library/analyse')
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
  const { coverage, lastCompleted: summary } = status

  return (
    <Stack direction="column" gap={12}>
      <Stack gap={8} align="center" wrap>
        <Button
          variant="primary"
          onClick={() => void analyse()}
          disabled={busy || running || coverage.pending === 0}
        >
          {running ? 'Analysing…' : 'Analyse'}
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
            <Badge tone="danger">Analysis failed</Badge>
            <Text size="sm" tone="tertiary">
              {error}
            </Text>
          </Stack>
        ) : null}

        {status.lastError !== null && !running ? (
          <Stack direction="column" gap={4} align="start">
            <Badge tone="danger">The last pass stopped early</Badge>
            <Text size="sm" tone="tertiary">
              {status.lastError}
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
          <Text size="sm" tone={coverage.pending === 0 ? 'tertiary' : 'primary'}>
            {coverage.pending === 0
              ? 'Every file has been analysed.'
              : `${coverage.pending.toLocaleString()} file${coverage.pending === 1 ? '' : 's'} not analysed yet.`}
          </Text>
        )}

        <Stack gap={20} wrap>
          <Stat label="Analysed" value={coverage.measured} />
          <Stat label="Not analysed" value={coverage.pending} />
          {coverage.unmeasurable > 0 ? (
            <Stat label="Would not decode" value={coverage.unmeasurable} />
          ) : null}
        </Stack>

        {summary !== null && !running ? <Result summary={summary} /> : null}
      </Stack>

      <Text size="xs" tone="tertiary">
        Decodes each file again, through ffmpeg's analysis filters: loudness for ReplayGain, dynamic
        range, the bits the audio really uses, silence and clipping. About a second a file. Writes
        nothing to disk.
      </Text>
    </Stack>
  )
}

function Result({ summary }: { readonly summary: Summary }) {
  return (
    <Stack direction="column" gap={8}>
      <Text size="xs" tone="tertiary">
        {summary.examined.toLocaleString()} examined · {summary.measured.toLocaleString()} analysed
        · took {formatDuration(summary.durationMilliseconds)}
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
