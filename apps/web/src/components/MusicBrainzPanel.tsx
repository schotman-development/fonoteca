import { type components, describeError } from '@fonoteca/api-client'
import { Badge, type BadgeTone, Button, Stack, Text } from '@fonoteca/ui'
import { useCallback, useEffect, useState } from 'react'

import { api } from '../api.ts'

type Health = components['schemas']['MusicBrainzHealthResponse']
type Status = components['schemas']['MusicBrainzReachability']

/**
 * Matches the server-side cache in MusicBrainzHealthProbe.
 *
 * Polling faster would only re-serve the same reading; polling slower would let
 * the card claim a server is up after it has gone. The two numbers are a pair,
 * and if one moves the other should.
 */
const POLL_MS = 30_000

const STATUS: Record<Status, { tone: BadgeTone; label: string }> = {
  Reachable: { tone: 'success', label: 'Answering' },
  Unreachable: { tone: 'danger', label: 'Not answering' },
  Rejected: { tone: 'warning', label: 'Refused' },
  NotConfigured: { tone: 'warning', label: 'Not configured' },
}

/**
 * Which MusicBrainz server identification will use, and whether it is up.
 *
 * Worth a card of its own because the answer is not binary. A green light here
 * means four separate things are true — a server is reachable, it is the one
 * intended, a contact is configured so lookups are not refused before they are
 * sent, and the rate the gate will run at is the rate that server permits. Any
 * one of those being wrong shows up as work that silently does not happen.
 */
export function MusicBrainzPanel() {
  const [health, setHealth] = useState<Health | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  const refresh = useCallback(async () => {
    setBusy(true)
    try {
      setHealth(await api.get('/api/system/musicbrainz'))
      setError(null)
    } catch (cause) {
      setError(describeError(cause))
    } finally {
      setBusy(false)
    }
  }, [])

  useEffect(() => {
    void refresh()

    const timer = window.setInterval(() => void refresh(), POLL_MS)
    return () => window.clearInterval(timer)
  }, [refresh])

  if (error !== null) {
    return (
      <Stack direction="column" gap={4} align="start">
        <Badge tone="danger">API unreachable</Badge>
        <Text size="sm" tone="tertiary" family="mono">
          {error}
        </Text>
      </Stack>
    )
  }

  if (health === null) {
    return <Text tone="tertiary">Contacting API…</Text>
  }

  const status = STATUS[health.status]

  return (
    <Stack direction="column" gap={12}>
      {/*
        Polite rather than assertive: this changes on a timer without anyone
        asking, and interrupting a screen reader mid-sentence to report that a
        server is still up would be worse than silence.
      */}
      <Stack gap={6} align="center" wrap role="status" aria-live="polite">
        <Badge tone={status.tone}>{status.label}</Badge>
        <Badge tone={health.isOfficialServer ? 'neutral' : 'info'}>
          {health.isOfficialServer ? 'Public instance' : 'Local mirror'}
        </Badge>
        {/*
          Only on success. On a failure the same number measures how long
          giving up took — retries and all — and a "10.8 s" badge next to "Not
          answering" reads as a slow server rather than an absent one.
        */}
        {health.status === 'Reachable' && health.latencyMs !== null ? (
          <Badge tone="neutral" mono>
            {formatLatency(health.latencyMs)}
          </Badge>
        ) : null}
      </Stack>

      <Stack direction="column" gap={4}>
        <Text size="xs" tone="tertiary">
          Server
        </Text>
        <Text size="sm" family="mono" truncate>
          {health.server}
        </Text>
      </Stack>

      <Stack direction="column" gap={4}>
        <Text size="xs" tone="tertiary">
          Request rate
        </Text>
        <Text size="sm">{describeRate(health)}</Text>
      </Stack>

      {health.detail !== null ? (
        <Text size="sm" tone="tertiary">
          {health.detail}
        </Text>
      ) : null}

      {/*
        The one failure that looks like success from the outside: the server is
        up, the URL is right, and every lookup is still refused before it leaves
        the process. Naming the setting is the whole value of saying it here.
      */}
      {!health.contactConfigured ? (
        <Text size="sm" tone="warning">
          Set <code>Fonoteca:MusicBrainzContact</code> to a URL or email address. Until then lookups
          are refused locally — MusicBrainz block clients that do not identify themselves, so an
          unidentified request is not attempted.
        </Text>
      ) : null}

      <Stack gap={8} align="center" wrap>
        <Button size="sm" onClick={() => void refresh()} disabled={busy}>
          {busy ? 'Checking…' : 'Check now'}
        </Button>
        {/*
          The reading is cached server-side, so "now" is a request for the
          freshest available answer, not necessarily a new one. Showing when it
          was actually taken keeps the button from implying otherwise.
        */}
        <Text size="xs" tone="tertiary">
          Checked {new Date(health.checkedAtUtc).toLocaleTimeString()}
        </Text>
      </Stack>
    </Stack>
  )
}

function describeRate(health: Health): string {
  if (health.minimumRequestIntervalMs <= 0) {
    return 'Ungated — one request at a time, no waiting between them.'
  }

  const perSecond = 1000 / health.minimumRequestIntervalMs
  const rate =
    perSecond >= 1
      ? `${perSecond.toFixed(perSecond % 1 === 0 ? 0 : 1)} requests/second`
      : `one request every ${(health.minimumRequestIntervalMs / 1000).toFixed(1)} s`

  return health.isOfficialServer
    ? `${rate} — the limit MusicBrainz enforce, shared with identification work.`
    : `${rate}.`
}

function formatLatency(ms: number): string {
  return ms < 1000 ? `${ms} ms` : `${(ms / 1000).toFixed(1)} s`
}
