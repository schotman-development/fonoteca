import { createApiClient } from '@fonoteca/api-client'

/**
 * Where the API lives.
 *
 * Same-origin, always: in production the API serves the built client, and in
 * development Vite proxies `/api` and `/hubs` to it (vite.config.ts). The
 * session is a cookie (ADR 0018), and a cookie only reliably reaches its own
 * site — opened as fonoteca.dev.internal against an API at another host, a
 * browser that blocks third-party cookies would never sign in.
 * `VITE_API_BASE_URL` still points somewhere else where that is wanted.
 */
export const apiBaseUrl: string = import.meta.env.VITE_API_BASE_URL ?? ''

const SIGNED_OUT = 'fonoteca:signed-out'

/**
 * Every request carries the session, and a 401 from any of them means there
 * is none — signed out, the password reset elsewhere, the account removed —
 * which `AccountGate` answers with the sign-in form.
 */
export const api = createApiClient({
  baseUrl: apiBaseUrl,
  fetch: async (input, init) => {
    const response = await fetch(input, { ...init, credentials: 'include' })
    if (response.status === 401) window.dispatchEvent(new Event(SIGNED_OUT))
    return response
  },
})

/** Calls `listener` whenever a request finds nobody signed in; returns the unsubscribe. */
export function onSignedOut(listener: () => void): () => void {
  window.addEventListener(SIGNED_OUT, listener)
  return () => window.removeEventListener(SIGNED_OUT, listener)
}
