import react from '@vitejs/plugin-react'
import { defineConfig } from 'vite'

export default defineConfig({
  plugins: [react()],
  server: {
    // Listen on every interface, not just loopback, so the UI can be opened
    // from a phone or another machine on the LAN. The API must be reachable
    // too — see VITE_API_BASE_URL in api.ts and Fonoteca__CorsOrigins.
    host: true,
    port: 5173,
    strictPort: true,
  },
  build: {
    // The catalogue view will be the heaviest screen in the app; surface bundle
    // growth early rather than discovering it once it is load-bearing.
    chunkSizeWarningLimit: 600,
    sourcemap: true,
  },
})
