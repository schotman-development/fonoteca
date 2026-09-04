import { type components, describeError } from '@fonoteca/api-client'
import { Badge, Button, Stack, Text } from '@fonoteca/ui'
import { useCallback, useEffect, useState } from 'react'

import { api } from '../api.ts'
import { type JobProgress, useJobsConnection } from '../useJobsConnection.ts'

type Status = components['schemas']['TagWriteStatusResponse']
type Summary = components['schemas']['TagWriteSummary']

/** The hub's `kind` for this pass. Matches `TagWriteService.JobKind`. */
const JOB_KIND = 'library.tags'

/** Fallback poll, for the case the hub is down. Same reasoning as the other cards. */
const POLL_MS = 5000

/**
 * Which files a press covers. Absent means the whole library.
 *
 * The two scoped forms exist because they are the two units a person thinks
 * in — the album they have just corrected, and the artist whose discography
 * they have just enriched. They are not different features: the same pass runs,
 * takes the same gate and reports on the same channel, so this component watches
 * one status endpoint whichever button started the work.
 */
export type TagWriteScope =
  | { readonly kind: 'release'; readonly id: string }
  | { readonly kind: 'artist'; readonly id: string }

/**
 * Writing what the catalogue worked out back into the files it came from.
 *
 * **The last step of the chain, and the only one with no automatic path to it.**
 * Scan, identify, enrich and attribute all write to PostgreSQL; copy the library
 * to another machine, or open it in any other player, and none of their answers
 * exist. This is what makes them portable — and it is also the only operation
 * here that a rescan cannot undo, which is why it is a button and never a
 * consequence of anything else.
 *
 * Two locks, and the second one is the reason the panel says as much as it does.
 * `Fonoteca:AllowFileMutation` off is the default and the *ordinary* path, not
 * an error: the run happens, the diff is computed, the previous values are
 * journalled, and not one byte on disk changes. A card that reported "8,140
 * files done" for that would be true and misleading, so the flag is stated
 * before the press rather than explained after it.
 */
export function TagWritePanel({
  scope,
  label,
  onWritten,
}: {
  readonly scope?: TagWriteScope
  /** What the button is about, for the confirmation sentence. */
  readonly label?: string
  readonly onWritten?: () => void
}) {
  const [status, setStatus] = useState<Status | null>(null)
  const [started, setStarted] = useState<string | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  const { jobs } = useJobsConnection()
  const live: JobProgress | undefined = jobs[JOB_KIND]

  const refresh = useCallback(async () => {
    try {
      setStatus(await api.get('/api/library/tags'))
      setError(null)
    } catch (cause) {
      setError(describeError(cause))
    }
  }, [])

  useEffect(() => {
    void refresh()
  }, [refresh])

  // The server decides whether a pass is running; the hub only supplies numbers.
  // `useJobsConnection` keeps the last message per kind forever and never clears
  // it, so a pass that died after one frame would otherwise leave this stuck.
  const running = status?.running === true

  useEffect(() => {
    if (!running) return undefined

    const timer = window.setInterval(() => void refresh(), POLL_MS)
    return () => window.clearInterval(timer)
  }, [running, refresh])

  useEffect(() => {
    if (live?.state !== 'completed') return

    void refresh()
    onWritten?.()
  }, [live?.state, refresh, onWritten])

  async function write() {
    setBusy(true)
    setError(null)
    setStarted(null)

    try {
      const result =
        scope === undefined
          ? await api.post('/api/library/tags')
          : scope.kind === 'release'
            ? await api.post('/api/catalogue/releases/{id}/tags', {
                params: { path: { id: scope.id } },
              })
            : await api.post('/api/catalogue/artists/{id}/tags', {
                params: { path: { id: scope.id } },
              })

      setStarted(
        result.files === 0
          ? `Nothing in ${result.scope} has an album, a track and a recording yet, so there was nothing to write.`
          : `Writing ${result.files.toLocaleString()} file${result.files === 1 ? '' : 's'} in ${result.scope}.`,
      )

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
      await api.delete('/api/library/tags')
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

  // The dashboard card is about the library and reports whatever ran last; a
  // scoped button reports only what this press did.
  const mine = scope === undefined || started !== null

  return (
    <Stack direction="column" gap={12}>
      <Stack gap={8} align="center" wrap>
        <Button
          variant={scope === undefined ? 'primary' : 'secondary'}
          size={scope === undefined ? 'md' : 'sm'}
          onClick={() => void write()}
          disabled={busy || running}
        >
          {running
            ? 'Writing…'
            : scope === undefined
              ? 'Write tags'
              : `Write tags to ${label ?? 'these files'}`}
        </Button>

        {running ? (
          <Button size="sm" variant="danger" onClick={() => void cancel()}>
            Stop
          </Button>
        ) : null}

        {/*
          Said before the press, not after. With the flag off this pass is a
          complete dry run — everything except the write — and that is the
          default, so it is the first thing somebody needs to know.
        */}
        {!status.willWrite ? (
          <Badge tone="info" size="sm">
            Mutation is off
          </Badge>
        ) : null}
      </Stack>

      <Stack direction="column" gap={12} role="status" aria-live="polite" aria-busy={running}>
        {error !== null ? (
          <Stack direction="column" gap={4} align="start">
            <Badge tone="danger">The tag write was refused</Badge>
            <Text size="sm" tone="tertiary">
              {error}
            </Text>
          </Stack>
        ) : null}

        {/*
          Only what this panel's own press produced. The status endpoint reports
          the last run whatever scope it had, which is right for the dashboard
          card and misleading on an album page — "the last run over the whole
          library" is not news about this album.
        */}
        {status.lastError !== null && !running && mine ? (
          <Stack direction="column" gap={4} align="start">
            <Badge tone="danger">The last run stopped early</Badge>
            <Text size="sm" tone="tertiary">
              {status.lastError}
            </Text>
          </Stack>
        ) : null}

        {started !== null && !running ? (
          <Text size="sm" tone="tertiary">
            {started}
          </Text>
        ) : null}

        {running ? (
          <Stack direction="column" gap={4}>
            <Text size="sm" family="mono">
              {processed.toLocaleString()} / {(total ?? 0).toLocaleString()}
              {status.scope != null ? ` · ${status.scope}` : ''}
            </Text>
            {live?.currentItem != null ? (
              <Text size="xs" tone="tertiary" truncate>
                {live.currentItem}
              </Text>
            ) : null}
          </Stack>
        ) : scope === undefined ? (
          <Text size="sm" tone={status.files === 0 ? 'tertiary' : 'primary'}>
            {status.files === 0
              ? 'No file has an album, a track and a recording yet, so there is nothing to write.'
              : `${status.files.toLocaleString()} file${status.files === 1 ? '' : 's'} hold an answer the file itself does not carry.`}
          </Text>
        ) : null}

        {summary !== null && !running && mine ? <Result summary={summary} /> : null}
      </Stack>

      {scope === undefined ? (
        <Text size="xs" tone="tertiary">
          Title, artist, album, album artist, track and disc numbers, the year and every MusicBrainz
          identifier. Each file is rendered to a staged copy, read back by two independent tag
          libraries and length-checked before the swap; the previous values go to the undo journal.
          A file that already says what the catalogue says is not opened, so running this again is
          cheap.
        </Text>
      ) : null}
    </Stack>
  )
}

/**
 * How a run went, in the four outcomes that are actually different.
 *
 * `Unchanged` is not a failure and neither is `Refused` — one means the file
 * already agreed with the catalogue, the other means the mutation flag is off.
 * Only `Failed` is a problem, and it is the small number, so it is the one that
 * gets a tone.
 */
function Result({ summary }: { readonly summary: Summary }) {
  return (
    <Stack direction="column" gap={4}>
      <Text size="xs" tone="tertiary" block>
        Last run over {summary.scope}: {summary.written.toLocaleString()} written ·{' '}
        {summary.unchanged.toLocaleString()} already correct
        {summary.refused > 0 ? ` · ${summary.refused.toLocaleString()} not written` : ''}
        {summary.unsupported > 0
          ? ` · ${summary.unsupported.toLocaleString()} in containers that cannot carry tags`
          : ''}
        {summary.skipped > 0 ? ` · ${summary.skipped.toLocaleString()} not on disk` : ''} · took{' '}
        {formatDuration(summary.durationMilliseconds)}
        {summary.cancelled ? ' · stopped early' : ''}
      </Text>

      {summary.failed > 0 ? (
        <Text size="sm" tone="warning" block>
          {summary.failed.toLocaleString()} file{summary.failed === 1 ? '' : 's'} could not be
          written and {summary.failed === 1 ? 'was' : 'were'} left byte-for-byte as{' '}
          {summary.failed === 1 ? 'it was' : 'they were'}. The log names{' '}
          {summary.failed === 1 ? 'it' : 'them'}.
        </Text>
      ) : null}
    </Stack>
  )
}

function formatDuration(ms: number): string {
  if (ms < 1000) return `${ms.toLocaleString()} ms`

  const seconds = ms / 1000
  if (seconds < 60) return `${seconds.toFixed(1)} s`

  return `${Math.floor(seconds / 60)} m ${Math.round(seconds % 60)} s`
}
