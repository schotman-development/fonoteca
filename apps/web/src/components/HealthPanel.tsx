import { ApiError } from '@fonoteca/api-client'
import { Badge, Stack, Text } from '@fonoteca/ui'
import { useEffect, useState } from 'react'

import { api } from '../api.ts'

type SystemInfo = {
  version: string
  serverTimeUtc: string
  libraryPath: string
  fileMutationAllowed: boolean
  counts: { files: number; recordings: number; releases: number; artists: number }
}

type LoadState =
  | { status: 'loading' }
  | { status: 'ready'; info: SystemInfo }
  | { status: 'error'; message: string }

/**
 * Reads the API through the generated client.
 *
 * Plain `useState` + `useEffect` rather than a server-state library: choosing
 * one is still an open decision (see the plan), and hard-wiring TanStack Query
 * here would quietly make it. This is deliberately the throwaway version.
 */
export function HealthPanel() {
  const [state, setState] = useState<LoadState>({ status: 'loading' })

  useEffect(() => {
    let cancelled = false

    api
      .get('/api/system/info')
      .then((info) => {
        if (!cancelled) setState({ status: 'ready', info: info as SystemInfo })
      })
      .catch((error: unknown) => {
        if (cancelled) return
        const message =
          error instanceof ApiError
            ? `${error.status} ${error.statusText}`
            : error instanceof Error
              ? error.message
              : 'Unknown error'
        setState({ status: 'error', message })
      })

    return () => {
      cancelled = true
    }
  }, [])

  if (state.status === 'loading') {
    return <Text tone="tertiary">Contacting API…</Text>
  }

  if (state.status === 'error') {
    return (
      <Stack direction="column" gap={4}>
        <Badge tone="danger">API unreachable</Badge>
        <Text size="sm" tone="tertiary" family="mono">
          {state.message}
        </Text>
        <Text size="sm" tone="tertiary">
          Is the API running, and is PostgreSQL up? <code>podman compose up -d</code>
        </Text>
      </Stack>
    )
  }

  const { info } = state

  return (
    <Stack direction="column" gap={12}>
      <Stack gap={6} align="center" wrap>
        <Badge tone="success">API reachable</Badge>
        <Badge tone="neutral" mono>
          v{info.version}
        </Badge>
        {/*
          Surfaced prominently and deliberately. While mutation is off, tag
          edits will appear to do nothing; saying so here is cheaper than
          debugging it later.
        */}
        {info.fileMutationAllowed ? (
          <Badge tone="warning">File mutation ENABLED</Badge>
        ) : (
          <Badge tone="info">File mutation disabled</Badge>
        )}
      </Stack>

      <Stack direction="column" gap={4}>
        <Text size="xs" tone="tertiary">
          Library path
        </Text>
        <Text size="sm" family="mono" truncate>
          {info.libraryPath}
        </Text>
      </Stack>

      <Stack gap={24} wrap>
        <Count label="Files" value={info.counts.files} />
        <Count label="Recordings" value={info.counts.recordings} />
        <Count label="Releases" value={info.counts.releases} />
        <Count label="Artists" value={info.counts.artists} />
      </Stack>
    </Stack>
  )
}

function Count({ label, value }: { label: string; value: number }) {
  return (
    <Stack direction="column" gap={2}>
      <Text size="xl" weight="semibold" family="mono">
        {value.toLocaleString()}
      </Text>
      <Text size="xs" tone="tertiary">
        {label}
      </Text>
    </Stack>
  )
}
