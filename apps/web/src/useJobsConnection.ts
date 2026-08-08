import { HubConnectionBuilder, HubConnectionState, LogLevel } from '@microsoft/signalr'
import { useEffect, useRef, useState } from 'react'

import { apiBaseUrl } from './api.ts'

export type Heartbeat = {
  readonly atUtc: string
  readonly activeJobs: number
}

export type ConnectionState = 'connecting' | 'connected' | 'reconnecting' | 'disconnected'

/**
 * One job's progress, as the hub sends it.
 *
 * The field names are the wire protocol — they come from `JobProgressMessage`
 * on the server — so this type is hand-written rather than generated. The
 * OpenAPI document describes the REST surface and knows nothing about the hub.
 */
export type JobProgress = {
  readonly jobId: string
  readonly kind: string
  readonly state: string
  readonly processed: number
  readonly total: number | null
  readonly currentItem: string | null
  readonly atUtc: string
}

/**
 * Subscribes to the API's jobs hub.
 *
 * Identification runs for tens of minutes over a large library, so progress is
 * pushed rather than polled. The connection state is surfaced alongside the data
 * on purpose: "no jobs running" and "we lost the connection and cannot tell"
 * look identical otherwise, and only one of them is fine.
 *
 * Progress is kept per job kind rather than as a single latest-message. There is
 * only one kind today, but keying by it means a second one does not silently
 * overwrite the first — and the hub is explicitly a transport rather than a
 * source of truth, so a panel still re-reads its REST endpoint on mount.
 */
export function useJobsConnection(): {
  state: ConnectionState
  lastHeartbeat: Heartbeat | null
  jobs: Readonly<Record<string, JobProgress>>
} {
  const [state, setState] = useState<ConnectionState>('connecting')
  const [lastHeartbeat, setLastHeartbeat] = useState<Heartbeat | null>(null)
  const [jobs, setJobs] = useState<Record<string, JobProgress>>({})
  const startedRef = useRef(false)

  useEffect(() => {
    // React 18+ StrictMode mounts effects twice in development; without this
    // guard the hub is connected, torn down and reconnected on every mount.
    if (startedRef.current) return undefined
    startedRef.current = true

    const connection = new HubConnectionBuilder()
      .withUrl(`${apiBaseUrl}/hubs/jobs`)
      .withAutomaticReconnect()
      .configureLogging(LogLevel.Warning)
      .build()

    connection.on('Heartbeat', (message: Heartbeat) => {
      setLastHeartbeat(message)
    })

    connection.on('JobProgress', (message: JobProgress) => {
      setJobs((current) => ({ ...current, [message.kind]: message }))
    })

    connection.onreconnecting(() => setState('reconnecting'))
    connection.onreconnected(() => setState('connected'))
    connection.onclose(() => setState('disconnected'))

    connection
      .start()
      .then(() => setState('connected'))
      .catch(() => setState('disconnected'))

    return () => {
      startedRef.current = false
      if (connection.state !== HubConnectionState.Disconnected) {
        void connection.stop()
      }
    }
  }, [])

  return { state, lastHeartbeat, jobs }
}
