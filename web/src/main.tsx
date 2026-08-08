/* Entry point. The three stylesheets load in order — tokens, then the reset,
   then element defaults — so a component module always sees the tokens and
   always wins over the base layer.

   The QueryClient comes from `@/api/queries`, not from a second literal here.
   `createQueryClient()` is where the polling contract (spec section 5.2) lives:
   one scheduler rather than one timer per component, no refetching while the
   tab is hidden, a single catch-up fetch when it comes back rather than a
   burst, and a retry policy that gives up on 4xx and 503 because neither
   improves by asking again. Two clients configured in two places is how a
   contract quietly stops being true. */

import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
import { QueryClientProvider } from '@tanstack/react-query'
import { BrowserRouter } from 'react-router-dom'

import '@/styles/tokens.css'
import '@/styles/reset.css'
import '@/styles/base.css'

import { createQueryClient } from '@/api/queries'
import App from '@/App'

const queryClient = createQueryClient()

const container = document.getElementById('root')
if (container === null) {
  throw new Error('#root is missing from index.html — the SPA has nowhere to mount.')
}

createRoot(container).render(
  <StrictMode>
    <QueryClientProvider client={queryClient}>
      <BrowserRouter>
        <App />
      </BrowserRouter>
    </QueryClientProvider>
  </StrictMode>,
)
