import { type ReactNode, useEffect, useState } from 'react'

import { api, onSignedOut } from './api.ts'
import { SignInPage } from './pages/SignInPage.tsx'

/**
 * The application, or the form that signs somebody in to it (ADR 0018).
 *
 * Open until a request says otherwise, so a signed-in person sees the library
 * at once rather than a blank page waiting on a check: the first request to
 * come back 401 — this one's own, or any page's — closes it. Closed, it asks
 * whether any account exists yet, because the first visit makes the first one.
 */
export function AccountGate({ children }: { readonly children: ReactNode }) {
  const [gate, setGate] = useState<'open' | 'sign-in' | 'set-up'>('open')

  useEffect(() => {
    const close = async () => {
      const setup = await api.get('/api/account/setup')
      setGate(setup.needed ? 'set-up' : 'sign-in')
    }

    const unsubscribe = onSignedOut(() => void close().catch(() => setGate('sign-in')))

    // Its 401, if any, is what closes the gate; nothing else to do with it.
    void api.get('/api/account').catch(() => undefined)

    return unsubscribe
  }, [])

  return gate === 'open' ? (
    children
  ) : (
    <SignInPage setUp={gate === 'set-up'} onSignedIn={() => setGate('open')} />
  )
}
