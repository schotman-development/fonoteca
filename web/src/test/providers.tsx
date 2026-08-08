/* The three providers every screen is mounted inside, for tests.
 *
 * A screen is not a pure function of props — it reads the router (its filters
 * are the address), TanStack Query (its data) and the toast host (its
 * acknowledgement of a press). Mounting one without those does not fail
 * gracefully: `useQuery` throws "No QueryClient set" and `useToast` throws on
 * purpose, because a toast that silently goes nowhere is how an acknowledged
 * mutation stops being acknowledged.
 *
 * Two deliberate differences from production:
 *
 *  - **`retry: false`.** The app retries a 5xx once; a test that did would wait
 *    for a backoff before rendering the error state it is asserting.
 *  - **No `gcTime` tuning and no fake clock.** Poll intervals are real, but a
 *    test finishes long before the fastest of them (5s) comes round, so nothing
 *    here has to pretend about time.
 *
 * `queryClient` is created per call, so no cache survives from one test into
 * the next — a shared client is how a test starts passing because of the one
 * before it.
 */

import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { useState, type ReactNode } from 'react'
import { MemoryRouter } from 'react-router-dom'

import { ToastProvider } from '@/shell'

/* Not exported: nothing outside this file builds a client, and a non-component
   export here is the one thing that stops Fast Refresh working for every screen
   test mounted through `TestProviders`. */
function createTestQueryClient(): QueryClient {
  return new QueryClient({
    defaultOptions: {
      queries: { retry: false },
      mutations: { retry: false },
    },
  })
}

export interface TestProvidersProps {
  children: ReactNode
  /** Initial address. Filters live in the query string, so tests set them here. */
  route?: string
  client?: QueryClient
}

export function TestProviders({
  children,
  route = '/',
  client,
}: TestProvidersProps) {
  // Lazy state, not a bare call: a client rebuilt on every render is a cache
  // that empties itself between the fetch and the assertion.
  const [fallback] = useState(createTestQueryClient)
  const queryClient = client ?? fallback
  return (
    <QueryClientProvider client={queryClient}>
      <MemoryRouter initialEntries={[route]}>
        <ToastProvider>{children}</ToastProvider>
      </MemoryRouter>
    </QueryClientProvider>
  )
}
