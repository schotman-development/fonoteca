/**
 * The pill itself — presentational, and the only part of the toast family with
 * no state in it. Design line 851.
 *
 * It renders no live-region role: `ToastHost` owns that, because the region has
 * to outlive the message (see Toast.module.css). Rendering this component
 * directly is legitimate — a screenshot test, a storybook-shaped harness — and
 * it will simply not be announced, which is correct for a pill that nothing
 * raised.
 */

import { cx } from '@/design/cx'
import type { StyleableProps } from '@/design/types'

import styles from '@/design/Toast/Toast.module.css'
import type { ToastLevel } from '@/design/Toast/toastContext'

const MARK: Record<ToastLevel, string | undefined> = {
  neutral: undefined,
  accent: styles.accent,
  ok: styles.ok,
  warn: styles.warn,
  bad: styles.bad,
}

export interface ToastProps extends StyleableProps {
  message: string
  /** `neutral` (the default) paints no pip — a message where nothing is wrong shows no colour. */
  level?: ToastLevel
}

export function Toast({ message, level = 'neutral', className }: ToastProps) {
  const mark = MARK[level]
  return (
    <div className={cx(styles.toast, className)} data-level={level}>
      {mark ? <span className={cx(styles.mark, mark)} aria-hidden="true" /> : null}
      <span>{message}</span>
    </div>
  )
}
