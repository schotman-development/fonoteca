/**
 * The live region the toast appears inside.
 *
 * Always mounted, usually empty. `role="status"` + `aria-live="polite"` +
 * `aria-atomic` on a permanent element, with only the pill inside it coming and
 * going — a live region inserted already holding its text is announced
 * unreliably, and "unreliably" here means the one screen-reader user of this
 * app is never told their re-tag finished.
 *
 * Mounting it is optional. `ToastProvider` renders the region itself while no
 * host has registered, so a tree that has only the provider (every component
 * test, via `@/test/providers`) still shows toasts, and a shell that places one
 * explicitly gets exactly one rather than two announcing the same sentence.
 *
 * That is why the region and the registration are two components. If the
 * provider's fallback registered, it would unmount itself, deregister, mount
 * again — a render loop rather than a duplicate. `ToastRegion` is the view and
 * registers nothing; `ToastHost` is the view plus the claim.
 */

import { useEffect } from 'react'

import { Toast } from '@/design/Toast/Toast'
import styles from '@/design/Toast/Toast.module.css'
import { useToastActions, useToastState } from '@/design/Toast/toastContext'

/** The region itself. Internal to this directory — the provider mounts it as its fallback. */
export function ToastRegion() {
  const current = useToastState()
  return (
    <div className={styles.host} role="status" aria-live="polite" aria-atomic="true">
      {current ? (
        <Toast key={current.id} message={current.message} level={current.level} />
      ) : null}
    </div>
  )
}

export function ToastHost() {
  const actions = useToastActions()
  const registerHost = actions?.registerHost

  useEffect(() => registerHost?.(), [registerHost])

  // Outside a provider there is nothing to announce and no state to read, so
  // the region would be permanent furniture describing nothing.
  if (!actions) return null

  return <ToastRegion />
}
