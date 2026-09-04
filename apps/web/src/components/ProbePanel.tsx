import { type components, describeError } from '@fonoteca/api-client'
import { Badge, Button, Stack, Text } from '@fonoteca/ui'
import { useCallback, useEffect, useState } from 'react'

import { api } from '../api.ts'
import { type JobProgress, useJobsConnection } from '../useJobsConnection.ts'
import styles from './ProbePanel.module.css'

type Status = components['schemas']['ProbeStatusResponse']
type Summary = components['schemas']['ProbeSummary']
type Coverage = components['schemas']['ProbeCoverage']

/** The hub's `kind` for this pass. Matches `ProbeService.JobKind`. */
const JOB_KIND = 'library.probe'

/** Fallback poll, for the case the hub is down. Same reasoning as the other cards. */
const POLL_MS = 5000

/**
 * The pass that measures the library.
 *
 * The one card whose value is easiest to state as a number somewhere else: until
 * it has run, the upgrade list on the acquire screen can only see lossy
 * containers, which on a mostly-FLAC library is about a sixth of the upgrades
 * that exist. It is also the only pass that answers a question about the *bytes*
 * rather than about the music, which is why the reading below is split three
 * ways rather than shown as a percentage.
 */
export function ProbePanel({ onProbed }: { readonly onProbed?: () => void }) {
  const [status, setStatus] = useState<Status | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  const { jobs } = useJobsConnection()
  const live: JobProgress | undefined = jobs[JOB_KIND]

  const refresh = useCallback(async () => {
    try {
      setStatus(await api.get('/api/library/probe'))
      setError(null)
    } catch (cause) {
      setError(describeError(cause))
    }
  }, [])

  useEffect(() => {
    void refresh()
  }, [refresh])

  /*
    The server decides whether a pass is running; the hub only supplies numbers.
    `useJobsConnection` keeps the last message per kind forever and never clears
    it, so a pass that dies after its first progress frame leaves `live.state`
    at 'running' — and this card, unlike its three siblings, gates a message on
    not-running: the error badge would never appear and the button would read
    "Measuring…" until somebody reloaded.
  */
  const running = status?.running === true

  useEffect(() => {
    if (!running) return undefined

    const timer = window.setInterval(() => void refresh(), POLL_MS)
    return () => window.clearInterval(timer)
  }, [running, refresh])

  useEffect(() => {
    if (live?.state !== 'completed') return

    void refresh()
    onProbed?.()
  }, [live?.state, refresh, onProbed])

  async function probe() {
    setBusy(true)
    setError(null)

    try {
      await api.post('/api/library/probe')
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
      await api.delete('/api/library/probe')
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
  const pending = status.coverage.pending

  return (
    <Stack direction="column" gap={12}>
      <Stack gap={8} align="center" wrap>
        <Button
          variant="primary"
          onClick={() => void probe()}
          disabled={busy || running || pending === 0}
        >
          {running ? 'Measuring…' : 'Measure'}
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
            <Badge tone="danger">Probe failed</Badge>
            <Text size="sm" tone="tertiary">
              {error}
            </Text>
          </Stack>
        ) : null}

        {/*
          A pass that died on its first file, which for this one is usually
          ffprobe missing from PATH. Without it the card simply goes back to the
          count it started with, and the button reads as doing nothing.
        */}
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
          <Text size="sm" tone={pending === 0 ? 'tertiary' : 'primary'}>
            {pending === 0
              ? 'Every file has been measured.'
              : `${pending.toLocaleString()} file${pending === 1 ? '' : 's'} nothing has opened yet.`}
          </Text>
        )}

        <Reading coverage={status.coverage} />

        {summary !== null && !running ? <Result summary={summary} /> : null}
      </Stack>

      <Text size="xs" tone="tertiary">
        Decodes each file rather than reading its header, so it answers what the audio is and
        whether it still plays. About 0.6s a file. Writes nothing to disk.
      </Text>
    </Stack>
  )
}

/**
 * What the library measures to.
 *
 * Three numbers rather than one, because they are three different situations and
 * only one of them is a problem to act on. A file the decoder objected to has a
 * defect; a file that describes no audio at all is usually not music; a file
 * nothing has opened is a queue position.
 */
function Reading({ coverage }: { readonly coverage: Coverage }) {
  const damaged = coverage.corrupt + coverage.unreadable

  return (
    <Stack direction="column" gap={8}>
      <Stack gap={20} wrap>
        <Stat label="Measured" value={coverage.measured} />
        <Stat label="Not opened" value={coverage.pending} />
        {damaged > 0 ? <Stat label="Will not decode" value={damaged} tone="danger" /> : null}
      </Stack>

      {damaged > 0 ? (
        <Text size="xs" tone="tertiary">
          {coverage.corrupt > 0
            ? `${coverage.corrupt.toLocaleString()} file${coverage.corrupt === 1 ? '' : 's'} the decoder objected to while reading — the reading was shown but not kept, because AudioQuality decides which copy of a duplicate to keep. `
            : ''}
          {coverage.unreadable > 0
            ? `${coverage.unreadable.toLocaleString()} describe${coverage.unreadable === 1 ? 's' : ''} no audio at all. `
            : ''}
          {coverage.damaged.length < damaged
            ? `The first ${coverage.damaged.length}:`
            : coverage.damaged.length === 1
              ? 'It is:'
              : 'They are:'}
        </Text>
      ) : null}

      {/*
        The paths, not just the count. The matching worklist lists none of these
        — every damaged file on the target library is either already identified
        or Unfingerprintable, and both are deliberately not questions there — so
        without this the number names files nothing can reach.
      */}
      {coverage.damaged.length > 0 ? (
        <ul className={styles.damaged}>
          {coverage.damaged.map((path) => (
            <li key={path}>
              <Text size="xs" family="mono" tone="tertiary" truncate block>
                {path}
              </Text>
            </li>
          ))}
        </ul>
      ) : null}
    </Stack>
  )
}

function Result({ summary }: { readonly summary: Summary }) {
  return (
    <Stack direction="column" gap={8}>
      <Text size="xs" tone="tertiary">
        {summary.examined.toLocaleString()} examined · {summary.measured.toLocaleString()} measured
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

function Stat({
  label,
  value,
  tone,
}: {
  readonly label: string
  readonly value: number
  readonly tone?: 'danger'
}) {
  return (
    <Stack direction="column" gap={2}>
      <Text size="lg" weight="semibold" family="mono" {...(tone !== undefined ? { tone } : {})}>
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
