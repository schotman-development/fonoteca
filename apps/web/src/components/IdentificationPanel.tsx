import { ApiError, type components, problemDetail } from '@fonoteca/api-client'
import { Badge, Button, Stack, Text } from '@fonoteca/ui'
import { useCallback, useEffect, useState } from 'react'

import { api } from '../api.ts'
import { type JobProgress, useJobsConnection } from '../useJobsConnection.ts'

type Status = components['schemas']['IdentificationStatusResponse']
type Summary = components['schemas']['IdentificationSummary']

/** The hub's `kind` for this pass. Matches `IdentificationService.JobKind`. */
const JOB_KIND = 'library.identify'

/**
 * Fallback poll, for the case the hub is down.
 *
 * Slow on purpose. Progress arrives over SignalR; this only exists so a pass
 * started in another tab, or one running while the socket is broken, is not
 * invisible. Polling fast enough to *be* the progress display would make the
 * hub pointless.
 */
const POLL_MS = 5000

/**
 * Fingerprinting, identification and tagging: what is left, and how it is going.
 *
 * Three things share this card because they are one pass and one queue. Showing
 * them separately would suggest they can be run separately, and the reason they
 * cannot is the point: a fingerprint with no lookup is wasted decoding, and a
 * lookup with no tag is a fact the file does not carry.
 *
 * The card leads with the dry-run state when tags are not being written. That
 * is not a warning — it is the configured default, and someone who has not
 * flipped `Fonoteca:AllowFileMutation` should be told what the pass will and
 * will not do before they wait forty minutes for it.
 */
export function IdentificationPanel() {
  const [status, setStatus] = useState<Status | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  const { jobs } = useJobsConnection()
  const live: JobProgress | undefined = jobs[JOB_KIND]

  const refresh = useCallback(async () => {
    try {
      setStatus(await api.get('/api/library/identify'))
      setError(null)
    } catch (cause) {
      setError(describe(cause))
    }
  }, [])

  useEffect(() => {
    void refresh()
  }, [refresh])

  // The hub is the primary signal; this is the safety net. It runs only while
  // something is happening, so an idle tab makes no requests at all.
  const running = status?.running === true || live?.state === 'running'

  useEffect(() => {
    if (!running) return undefined

    const timer = window.setInterval(() => void refresh(), POLL_MS)
    return () => window.clearInterval(timer)
  }, [running, refresh])

  // A completed message means the summary has changed, and the summary only
  // exists over REST.
  useEffect(() => {
    if (live?.state === 'completed') void refresh()
  }, [live?.state, refresh])

  async function identify() {
    setBusy(true)
    setError(null)

    try {
      await api.post('/api/library/identify')
      await refresh()
    } catch (cause) {
      setError(describe(cause))
      await refresh()
    } finally {
      setBusy(false)
    }
  }

  async function cancel() {
    try {
      await api.delete('/api/library/identify')
    } catch (cause) {
      setError(describe(cause))
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
          onClick={() => void identify()}
          disabled={busy || running || status.pending === 0}
        >
          {running ? 'Identifying…' : 'Identify'}
        </Button>

        {running ? (
          <Button size="sm" variant="danger" onClick={() => void cancel()}>
            Stop
          </Button>
        ) : null}

        {/*
          Not a warning tone. Writing nothing is the shipped default and the
          safe one; it only needs to be unmistakable, not alarming.
        */}
        <Badge tone={status.writesTags ? 'success' : 'info'}>
          {status.writesTags ? 'Writing tags' : 'Dry run — no files written'}
        </Badge>
      </Stack>

      {/*
        Polite, and only on state changes rather than on every progress frame:
        a live region that re-announced four times a second for forty minutes
        would make the page unusable with a screen reader.
      */}
      <Stack direction="column" gap={12} role="status" aria-live="polite" aria-busy={running}>
        {error !== null ? (
          <Stack direction="column" gap={4} align="start">
            <Badge tone="danger">Identification failed</Badge>
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
              ? 'Every catalogued file has been identified.'
              : `${status.pending.toLocaleString()} file${status.pending === 1 ? '' : 's'} with no AcoustID yet.`}
          </Text>
        )}

        {summary !== null && !running ? <Result summary={summary} /> : null}
      </Stack>

      <Text size="xs" tone="tertiary">
        Runs fpcalc on each file and asks AcoustID, which allows three requests a second — so a
        first pass over a large library takes tens of minutes.
      </Text>
    </Stack>
  )
}

/**
 * What the last pass did.
 *
 * `Would tag` earns its place: with file mutation off it is the entire visible
 * outcome, and it is also the number that says how much the next run — with the
 * flag on — will actually change.
 */
function Result({ summary }: { readonly summary: Summary }) {
  return (
    <Stack direction="column" gap={8}>
      <Stack gap={20} wrap>
        <Stat label="Identified" value={summary.identified} />
        <Stat
          label={summary.tagged > 0 ? 'Tagged' : 'Would tag'}
          value={summary.tagged || summary.writeRefused}
        />
        <Stat label="Unknown" value={summary.unknown} />
        <Stat label="Ambiguous" value={summary.ambiguous} />
      </Stack>

      <Text size="xs" tone="tertiary">
        {summary.alreadyTagged.toLocaleString()} already tagged ·{' '}
        {summary.examined.toLocaleString()} examined · took{' '}
        {formatDuration(summary.durationMilliseconds)}
        {summary.cancelled ? ' · stopped early' : ''}
      </Text>

      {summary.unfingerprintable > 0 ? (
        <Text size="sm" tone="warning">
          {summary.unfingerprintable.toLocaleString()} file
          {summary.unfingerprintable === 1 ? ' could' : 's could'} not be decoded, so
          {summary.unfingerprintable === 1 ? ' it was' : ' they were'} not identified. Those are
          marked unreadable in the catalogue.
        </Text>
      ) : null}

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

function describe(error: unknown): string {
  if (error instanceof ApiError) {
    return problemDetail(error.body) ?? `${error.status} ${error.statusText}`
  }

  return error instanceof Error ? error.message : 'Unknown error'
}
