import { ThemeProvider } from '@fonoteca/ui'
import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'

import { AppShell } from './AppShell.tsx'

// Load once, at the root, in this order: tokens define the custom properties
// that the reset and every component then consume.
import '@fonoteca/tokens/tokens.css'
import '@fonoteca/ui/reset.css'

const container = document.getElementById('root')
if (!container) {
  throw new Error('#root is missing from index.html')
}

createRoot(container).render(
  <StrictMode>
    <ThemeProvider>
      <AppShell />
    </ThemeProvider>
  </StrictMode>,
)
