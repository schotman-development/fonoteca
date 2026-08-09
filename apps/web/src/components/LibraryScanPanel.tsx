import { describeError } from '@fonoteca/api-client'
import { Badge, Button, Stack, Text } from '@fonoteca/ui'
import { useCallback, useEffect, useState } from 'react'

import { api } from '../api.ts'

type ScanSummary = {
  startedAtUtc: string
  completedAtUtc: string
  durationMilliseconds: number
  filesSeen: number
  added: number
  updated: number
  unchanged: number
  removed: number
  unreadableDirectories: number
}

type ScanStatus = { running: boolean; lastCompleted: ScanSummary | null }

/** How often to re-ask while a scan someone else started is in flight. */
const POLL_MS = 2000

/**
 * Starts a library scan and reports what it found.
 *
 * The scan runs in the foreground of its own request, so the POST's response
 * *is* the result — no job to subscribe to. That only holds for the window that
 * pressed the button; a scan started from another tab is invisible until asked
 * about, which is what the poll below is for. Both halves disappear when the
 * scan moves behind `IJobQueue` and reports on `JobsHub`.
 *
 * Plain `useState`/`useEffect` on purpose, matching the rest of the shell: the
 * server-state library is still an open decision (see the plan), and reaching
 * for one here would settle it by accident.
 */
export function LibraryScanPanel({ onScanned }: { readonly onScanned?: () => void }) {
  const [status, setStatus] = useState<ScanStatus | null>(null)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)

  const refresh = useCallback(async () => {
    try {
      setStatus((await api.get('/api/library/scan')) as ScanStatus)
    } catch (cause) {
      setError(describeError(cause))
    }
  }, [])

  useEffect(() => {
    void refresh()
  }, [refresh])

  // Only while another client holds the scan. Our own is awaited directly, and
  // the button is disabled throughout, so the poll can never race a result we
  // already have.
  const elsewhere = (status?.running ?? false) && !busy

  useEffect(() => {
    if (!elsewhere) return

    const timer = window.setInterval(() => void refresh(), POLL_MS)
    return () => window.clearInterval(timer)
  }, [elsewhere, refresh])

  async function scan() {
    setBusy(true)
    setError(null)

    try {
      const summary = (await api.post('/api/library/scan')) as ScanSummary
      setStatus({ running: false, lastCompleted: summary })

      // A scan is the only thing in the app that changes the catalogue, so
      // anything showing a row count is now stale. Saying so is this panel's
      // job; deciding what to do about it is not.
      onScanned?.()
    } catch (cause) {
      setError(describeError(cause))

      // A 409 means someone else started one between render and click; a 503
      // means the root went missing. Either way the local view of "running" is
      // now a guess, so replace it with the server's.
      await refresh()
    } finally {
      setBusy(false)
    }
  }

  const summary = status?.lastCompleted ?? null

  return (
    <Stack direction="column" gap={12}>
      <Stack gap={8} align="center" wrap>
        <Button variant="primary" onClick={() => void scan()} disabled={busy || elsewhere}>
          {busy ? 'Scanning…' : 'Scan library'}
        </Button>
        {elsewhere ? <Badge tone="info">Running in another window</Badge> : null}
      </Stack>

      {/*
        A scan is slow enough that its result arrives long after focus has moved
        on. Announcing politely is what makes the outcome reachable without
        sighted polling of the card.
      */}
      <Stack direction="column" gap={12} role="status" aria-live="polite" aria-busy={busy}>
        {error ? (
          <Stack direction="column" gap={4} align="start">
            <Badge tone="danger">Scan failed</Badge>
            <Text size="sm" tone="tertiary">
              {error}
            </Text>
          </Stack>
        ) : null}

        {summary ? (
          <Stack direction="column" gap={8}>
            <Stack gap={20} wrap>
              <Stat label="Seen" value={summary.filesSeen} />
              <Stat label="Added" value={summary.added} />
              <Stat label="Updated" value={summary.updated} />
              <Stat label="Removed" value={summary.removed} />
            </Stack>

            <Text size="xs" tone="tertiary">
              {summary.unchanged.toLocaleString()} unchanged · took{' '}
              {formatDuration(summary.durationMilliseconds)} · finished{' '}
              {new Date(summary.completedAtUtc).toLocaleTimeString()}
            </Text>

            {/*
              Not cosmetic. The scan refuses to remove anything from a pass it
              could not read in full, so this is the difference between "your
              library has 400 fewer files" and "400 files were behind a
              directory I was denied".
            */}
            {summary.unreadableDirectories > 0 ? (
              <Text size="sm" tone="warning">
                {summary.unreadableDirectories.toLocaleString()} director
                {summary.unreadableDirectories === 1 ? 'y' : 'ies'} could not be read, so nothing
                was removed from the catalogue this pass.
              </Text>
            ) : null}
          </Stack>
        ) : (
          <Text size="sm" tone="tertiary">
            {status === null ? 'Contacting API…' : 'No scan has run since the API started.'}
          </Text>
        )}
      </Stack>

      <Text size="xs" tone="tertiary">
        Records path, size and modification time. Reads no file contents and writes to nothing.
      </Text>
    </Stack>
  )
}

function Stat({ label, value }: { label: string; value: number }) {
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
