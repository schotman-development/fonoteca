import { describeError } from '@fonoteca/api-client'
import { useEffect, useState } from 'react'

/**
 * The three states every read in this application has.
 *
 * A discriminated union rather than three parallel `useState`s, because
 * `loading && error && data` is representable in the latter and is not a state
 * anything should have to render.
 */
export type QueryState<T> =
  | { readonly status: 'loading' }
  | { readonly status: 'ready'; readonly data: T }
  | { readonly status: 'error'; readonly message: string }

/**
 * Fetch on mount, and again whenever `deps` change.
 *
 * **Still not a server-state library**, deliberately. Choosing one is an open
 * decision (see the plan and ADR 0008), and two read-only pages are not enough
 * to settle it — what they did make obvious is that the same fifteen lines were
 * about to be written a fourth time. So this is the shared version of the
 * throwaway, not the beginning of a cache: no deduplication, no background
 * refetch, no invalidation. When a screen needs any of those, that is the
 * evidence to adopt TanStack Query with.
 *
 * The cancellation flag is not optional. React 19 in StrictMode mounts effects
 * twice, and a slow response arriving after a navigation would otherwise write
 * the previous page's data into the current one.
 */
export function useApiQuery<T>(load: () => Promise<T>, deps: readonly unknown[]): QueryState<T> {
  const [state, setState] = useState<QueryState<T>>({ status: 'loading' })

  // `load` is a fresh closure on every render, so it cannot be a dependency
  // without re-fetching forever; `deps` is what the caller says actually
  // identifies the request.
  useEffect(() => {
    let cancelled = false

    setState({ status: 'loading' })

    load()
      .then((data) => {
        if (!cancelled) setState({ status: 'ready', data })
      })
      .catch((cause: unknown) => {
        if (!cancelled) setState({ status: 'error', message: describeError(cause) })
      })

    return () => {
      cancelled = true
    }
    // The rule wants a literal array so it can check the closure against it.
    // A hook whose dependencies are its argument cannot give it one, which is
    // the trade this hook makes: the caller owns the request identity.
    // biome-ignore lint/correctness/useExhaustiveDependencies: deps is the caller's request identity, not this closure's
  }, deps)

  return state
}
