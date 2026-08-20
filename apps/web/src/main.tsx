import { PlaybackProvider, ThemeProvider } from '@fonoteca/ui'
import { RouterProvider } from '@tanstack/react-router'
import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'

import { router } from './routes.tsx'

// Load once, at the root, in this order: tokens define the custom properties
// that the reset and every component then consume.
import '@fonoteca/tokens/tokens.css'
import '@fonoteca/ui/reset.css'
// Last, because it overrides the reset: the shell is a fixed frame rather than
// a document that flows, and only one thing in it may scroll. See app.css.
import './app.css'

const container = document.getElementById('root')
if (!container) {
  throw new Error('#root is missing from index.html')
}

// ThemeProvider outside the router, not inside a route: the theme is a property
// of the application rather than of the page, and a provider under the outlet
// would remount — and re-read the stored preference — on every navigation.
//
// PlaybackProvider is outside it for a harder version of the same reason. It
// owns one detached HTMLAudioElement, and a provider that remounted would stop
// the audio on every navigation — which is the entire point of the transport
// row living outside the outlet in AppShell.
createRoot(container).render(
  <StrictMode>
    <ThemeProvider>
      <PlaybackProvider>
        <RouterProvider router={router} />
      </PlaybackProvider>
    </ThemeProvider>
  </StrictMode>,
)
