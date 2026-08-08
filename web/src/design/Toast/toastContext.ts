/**
 * The toast's two contexts, its vocabulary, and the hook a screen actually uses.
 *
 * **Two** contexts, because a screen that only ever *raises* a toast must not
 * re-render when one is *shown*. `useToast()` reads the actions context, whose
 * value is a stable object created once; the host reads the state context,
 * which changes on every message. Fold them into one and every component
 * holding a `toast` callback — which, after every mutation is wired, is most of
 * the application — re-renders twice per acknowledgement.
 *
 * No React elements are declared here on purpose: this file is imported by both
 * the provider and the host, and keeping it component-free is what stops the
 * lint rule about mixing hook and component exports from having an opinion.
 */

import { createContext, useContext } from 'react'

import type { Tone } from '@/design/types'

/**
 * A toast's colour, and the whole of it — the same five words every other
 * primitive takes, so a screen never has to remember which vocabulary this one
 * chose. `neutral` is the default and paints nothing.
 */
export type ToastLevel = Tone

/** What the host is currently showing. `id` is a monotonic counter, not a key of anything. */
export interface ToastMessage {
  id: number
  message: string
  level: ToastLevel
}

/** The function `useToast()` hands back. The level is optional and defaults to `neutral`. */
export type ShowToast = (message: string, level?: ToastLevel) => void

export interface ToastActions {
  show: ShowToast
  /**
   * A `ToastHost` calls this on mount and calls the returned function on
   * unmount. The provider renders its own host only while nothing has
   * registered, so mounting a host in the shell — which the shell does — never
   * produces two live regions announcing the same sentence twice.
   */
  registerHost: () => () => void
}

export const ToastActionsContext = createContext<ToastActions | null>(null)
export const ToastStateContext = createContext<ToastMessage | null>(null)

/**
 * The raise-a-toast hook.
 *
 * **It throws outside a provider, deliberately.** A toast that silently goes
 * nowhere is how an acknowledged mutation stops being acknowledged: the press
 * works, the request succeeds, and the person is never told — which is
 * indistinguishable from the button being dead. Failing at mount is loud, is
 * caught by the first test that renders the screen, and is fixed by wrapping
 * the tree once.
 */
export function useToast(): ShowToast {
  const actions = useContext(ToastActionsContext)
  if (!actions) {
    throw new Error(
      'useToast() was called outside a <ToastProvider>. Wrap the tree — a toast ' +
        'that goes nowhere is a mutation nobody was told about.',
    )
  }
  return actions.show
}

/** The host's half. Internal to this directory; screens never read the state. */
export function useToastState(): ToastMessage | null {
  return useContext(ToastStateContext)
}

/** Internal: the host's registration handle. Null outside a provider, where a host renders nothing. */
export function useToastActions(): ToastActions | null {
  return useContext(ToastActionsContext)
}
