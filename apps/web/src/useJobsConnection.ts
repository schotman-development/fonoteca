import { HubConnectionBuilder, HubConnectionState, LogLevel } from '@microsoft/signalr'
import { useEffect, useRef, useState } from 'react'

import { apiBaseUrl } from './api.ts'

export type Heartbeat = {
  readonly atUtc: string
  readonly activeJobs: number
}

export type ConnectionState = 'connecting' | 'connected' | 'reconnecting' | 'disconnected'

/**
 * Subscribes to the API's jobs hub.
 *
 * A full library scan runs for hours, so progress is pushed rather than polled.
 * The connection state is surfaced alongside the data on purpose: "no jobs
 * running" and "we lost the connection and cannot tell" look identical
 * otherwise, and only one of them is fine.
 */
export function useJobsConnection(): {
  state: ConnectionState
  lastHeartbeat: Heartbeat | null
} {
  const [state, setState] = useState<ConnectionState>('connecting')
  const [lastHeartbeat, setLastHeartbeat] = useState<Heartbeat | null>(null)
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

  return { state, lastHeartbeat }
}
