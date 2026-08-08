import { Badge, Stack, Text } from '@fonoteca/ui'

import { type ConnectionState, useJobsConnection } from '../useJobsConnection.ts'

const TONE: Record<ConnectionState, 'success' | 'warning' | 'danger' | 'neutral'> = {
  connected: 'success',
  connecting: 'neutral',
  reconnecting: 'warning',
  disconnected: 'danger',
}

const LABEL: Record<ConnectionState, string> = {
  connected: 'Live',
  connecting: 'Connecting',
  reconnecting: 'Reconnecting',
  disconnected: 'Disconnected',
}

/**
 * Proves the realtime path end to end.
 *
 * Once the job queue is implemented this becomes the scan/download progress
 * view. The connection badge stays either way — an idle queue and a dead socket
 * must never look the same.
 */
export function JobsPanel() {
  const { state, lastHeartbeat } = useJobsConnection()

  return (
    <Stack direction="column" gap={12}>
      <Stack gap={6} align="center">
        <Badge tone={TONE[state]}>{LABEL[state]}</Badge>
        <Text size="sm" tone="tertiary">
          {lastHeartbeat
            ? `${lastHeartbeat.activeJobs} active job${lastHeartbeat.activeJobs === 1 ? '' : 's'}`
            : 'awaiting first heartbeat'}
        </Text>
      </Stack>

      {lastHeartbeat ? (
        <Stack direction="column" gap={2}>
          <Text size="xs" tone="tertiary">
            Last heartbeat
          </Text>
          <Text size="sm" family="mono">
            {new Date(lastHeartbeat.atUtc).toLocaleTimeString()}
          </Text>
        </Stack>
      ) : null}

      <Text size="xs" tone="tertiary">
        No job queue is wired yet; the API emits a heartbeat every five seconds so the transport can
        be verified before there is anything to report.
      </Text>
    </Stack>
  )
}
