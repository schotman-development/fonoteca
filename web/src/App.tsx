/**
 * The application: the frame, with the route table inside one error boundary.
 *
 * The boundary is **inside** `AppShell` on purpose. A crashed screen must leave
 * the nav, the footer and the command palette working — that is the difference
 * between "this screen stopped" and "the application is gone", and it is the
 * only thing standing between a `null` reaching arithmetic on one panel and a
 * blank white page.
 *
 * `resetKey={location.pathname}` is the other half. React unmounts a caught
 * subtree and will not re-mount it on its own, so without a key a crashed
 * screen is *still* crashed after navigating away and back: the address says
 * `/activity`, the panel still shows the library's stack trace, and nothing a
 * person can press fixes it. Keying on the path makes the crash live exactly as
 * long as the visit to the screen that caused it.
 *
 * **A failed request must never arrive here.** Every fetch is a query hook and
 * every screen renders `PageError` from its own `isError`; a 502 from Qobuz is
 * an ordinary state of an ordinary screen. What lands in this boundary is a
 * render bug.
 *
 * **`ToastProvider` is mounted HERE, not in `main.tsx`**, and that is the fix
 * for a bug that shipped a blank page while 587 tests passed. `TopBar` calls
 * `useToast()` — the scan button acknowledges its press — and `useToast()`
 * throws by design outside a provider, because a toast that goes nowhere is a
 * mutation nobody was told about. The provider used to be mounted nowhere at
 * all: `AppShell` rendered `ToastHost` (the outlet) without the context that
 * feeds it, so the very first render of the very first screen threw and React
 * unmounted the whole tree.
 *
 * Nothing caught it because every screen test mounts through
 * `@/test/providers`, which *does* wrap in `ToastProvider` — so the tests
 * proved every screen works inside a correct frame and said nothing about
 * whether the real frame was correct. Putting the provider inside `App` makes
 * `<App/>` self-sufficient: the thing the tests mount and the thing the browser
 * mounts are now the same thing, and `App.test.tsx` mounts it bare so this
 * cannot come back.
 */

import { useLocation } from 'react-router-dom'

import { ErrorBoundary, ToastProvider } from '@/design'
import { AppRoutes } from '@/routes'
import { AppShell } from '@/shell'

export default function App() {
  const location = useLocation()

  return (
    <ToastProvider>
      <AppShell>
        <ErrorBoundary resetKey={location.pathname}>
          <AppRoutes />
        </ErrorBoundary>
      </AppShell>
    </ToastProvider>
  )
}
