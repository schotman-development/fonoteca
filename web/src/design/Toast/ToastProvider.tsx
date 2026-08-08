/**
 * The toast's state, its timer, and the fallback region.
 *
 * Three rules, all of them the design's own (`flash`, lines 1046–1049):
 *
 *   - **one at a time** — a second message clears the first one's timeout and
 *     replaces it, rather than stacking. `id` increments so the pill remounts
 *     and its entrance animation replays; without that, a second toast raised
 *     while the first is still up changes its text with no motion at all and
 *     reads as the same message still sitting there.
 *   - **2600 ms**, from the design.
 *   - **the timer is cleared on unmount**, so a navigation away mid-toast does
 *     not call `setState` on a dead tree.
 *
 * The provider mounts a `ToastRegion` while nothing else has registered a host.
 * That is what makes `<ToastProvider>` alone sufficient — which is exactly the
 * tree `@/test/providers` builds — while a shell that places `<ToastHost/>`
 * somewhere specific still ends up with one live region rather than two.
 *
 * The name `ToastProvider` is load-bearing: `web/src/test/providers.tsx`
 * imports it from `@/shell`, so the shell re-exports this. Renaming it here
 * breaks every screen test in the repository.
 */

import { useCallback, useEffect, useMemo, useRef, useState, type ReactNode } from 'react'

import { ToastRegion } from '@/design/Toast/ToastHost'
import {
  ToastActionsContext,
  ToastStateContext,
  type ToastActions,
  type ToastMessage,
} from '@/design/Toast/toastContext'

/** Design line 1048: `setTimeout(() => this.setState({ toast: '' }), 2600)`. */
export const TOAST_DURATION_MS = 2600

export interface ToastProviderProps {
  children: ReactNode
  /** Overridable so a test can assert dismissal without waiting 2.6 s of real time. */
  durationMs?: number
}

export function ToastProvider({
  children,
  durationMs = TOAST_DURATION_MS,
}: ToastProviderProps) {
  const [current, setCurrent] = useState<ToastMessage | null>(null)
  const [claimedHosts, setClaimedHosts] = useState(0)

  const timer = useRef<ReturnType<typeof setTimeout> | undefined>(undefined)
  const seq = useRef(0)
  // Read through a ref so `show` stays referentially stable for the lifetime of
  // the provider: it is in the dependency array of every mutation callback in
  // the app, and a `show` that changes identity re-runs all of them.
  const duration = useRef(durationMs)
  duration.current = durationMs

  useEffect(
    () => () => {
      if (timer.current !== undefined) clearTimeout(timer.current)
    },
    [],
  )

  const registerHost = useCallback(() => {
    setClaimedHosts((n) => n + 1)
    return () => setClaimedHosts((n) => n - 1)
  }, [])

  const actions = useMemo<ToastActions>(
    () => ({
      show(message, level = 'neutral') {
        if (timer.current !== undefined) clearTimeout(timer.current)
        seq.current += 1
        setCurrent({ id: seq.current, message, level })
        timer.current = setTimeout(() => {
          timer.current = undefined
          setCurrent(null)
        }, duration.current)
      },
      registerHost,
    }),
    [registerHost],
  )

  return (
    <ToastActionsContext.Provider value={actions}>
      <ToastStateContext.Provider value={current}>
        {children}
        {claimedHosts === 0 ? <ToastRegion /> : null}
      </ToastStateContext.Provider>
    </ToastActionsContext.Provider>
  )
}
