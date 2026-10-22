import type { components } from '@fonoteca/api-client'
import { createContext, type ReactNode, useCallback, useContext, useEffect, useState } from 'react'

import { api, onRefused, onSignedOut } from './api.ts'
import { SignInPage } from './pages/SignInPage.tsx'

type Account = components['schemas']['AccountResponse']

/** One of OpenSubsonic's roles, as an account holds it (ADR 0018). */
export type Role = components['schemas']['AccountRoles']

const SignedIn = createContext<{
  readonly account: Account | undefined
  readonly reload: () => void
}>({
  account: undefined,
  reload: () => undefined,
})

/** The signed-in account, once it has answered; undefined until then. */
export function useAccount(): Account | undefined {
  return useContext(SignedIn).account
}

/** Asks for the signed-in account again, after a change to it. */
export function useReloadAccount(): () => void {
  return useContext(SignedIn).reload
}

/**
 * Whether the signed-in account holds any of `roles`.
 *
 * False until the account has answered, so nothing it may not do is offered
 * while its roles are unknown; the API refuses it either way.
 */
export function useCan(...roles: readonly Role[]): boolean {
  const account = useAccount()
  return account !== undefined && roles.some((role) => account.roles.includes(role))
}

/**
 * The application, or the form that signs somebody in to it (ADR 0018).
 *
 * Open until a request says otherwise, so a signed-in person sees the library
 * at once rather than a blank page waiting on a check: the first request to
 * come back 401 — this one's own, or any page's — closes it. Closed, it asks
 * whether any account exists yet, because the first visit makes the first one.
 *
 * The account it reads is what every screen hides buttons by. A 403 from any
 * request reads it again, since that is the server saying a role has changed
 * since.
 */
export function AccountGate({ children }: { readonly children: ReactNode }) {
  const [gate, setGate] = useState<'open' | 'sign-in' | 'set-up'>('open')
  const [account, setAccount] = useState<Account>()

  // Its 401, if any, is what closes the gate; nothing else to do with it.
  const reload = useCallback(
    () => void api.get('/api/account').then(setAccount, () => undefined),
    [],
  )

  useEffect(() => {
    const close = async () => {
      setAccount(undefined)
      const setup = await api.get('/api/account/setup')
      setGate(setup.needed ? 'set-up' : 'sign-in')
    }

    const unsubscribe = onSignedOut(() => void close().catch(() => setGate('sign-in')))
    const unrefused = onRefused(reload)

    reload()

    return () => {
      unsubscribe()
      unrefused()
    }
  }, [reload])

  return gate === 'open' ? (
    <SignedIn.Provider value={{ account, reload }}>{children}</SignedIn.Provider>
  ) : (
    <SignInPage
      setUp={gate === 'set-up'}
      onSignedIn={() => {
        setGate('open')
        reload()
      }}
    />
  )
}
