import { ThemeProvider } from '@fonoteca/ui'
import { RouterProvider } from '@tanstack/react-router'
import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'

import { router } from './routes.tsx'

// Load once, at the root, in this order: tokens define the custom properties
// that the reset and every component then consume.
import '@fonoteca/tokens/tokens.css'
import '@fonoteca/ui/reset.css'

const container = document.getElementById('root')
if (!container) {
  throw new Error('#root is missing from index.html')
}

// ThemeProvider outside the router, not inside a route: the theme is a property
// of the application rather than of the page, and a provider under the outlet
// would remount — and re-read the stored preference — on every navigation.
createRoot(container).render(
  <StrictMode>
    <ThemeProvider>
      <RouterProvider router={router} />
    </ThemeProvider>
  </StrictMode>,
)
