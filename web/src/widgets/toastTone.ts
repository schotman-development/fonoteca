/**
 * `MessageOut.level` → the tone a toast is drawn in.
 *
 * Two vocabularies that look alike and are not: the wire says
 * `info | success | warning | error` (the server's judgement of how a mutation
 * went), the design system says `neutral | accent | ok | warn | bad` (how much
 * colour a mark gets). Translating them at every call site is how one screen
 * ends up painting a refusal green, so the mapping is one function with one
 * test — the same shape as `activityKind` and `albumFlag`.
 *
 * `info` is `neutral` rather than `accent`: in this system colour marks an
 * exception, and "it worked, here is what happened" is the resting state.
 */

import type { MessageLevel } from '@/api/types'
import type { ToastLevel } from '@/design'

const TONE: Readonly<Record<MessageLevel, ToastLevel>> = {
  info: 'neutral',
  success: 'ok',
  warning: 'warn',
  error: 'bad',
}

export function toastTone(level: MessageLevel | null | undefined): ToastLevel {
  if (level === null || level === undefined) return 'neutral'
  return TONE[level] ?? 'neutral'
}
