import react from '@vitejs/plugin-react'
import { defineConfig } from 'vite'

export default defineConfig({
  plugins: [react()],
  server: {
    // Listen on every interface, not just loopback, so the UI can be opened
    // from a phone or another machine on the LAN. The API comes along through
    // the proxy below.
    host: true,
    // Off the LAN the UI is opened over NetBird by name — <anything>.dev.internal,
    // or fonoteca.schotman.app through the NetBird proxy — and Vite refuses host
    // names it was not told about.
    allowedHosts: ['.dev.internal', 'fonoteca.schotman.app'],
    port: 5173,
    strictPort: true,
    // The API on this site's own origin, so the session cookie is first-party
    // whatever name the UI is opened by. See api.ts.
    proxy: {
      '/api': 'http://localhost:5088',
      '/hubs': { target: 'http://localhost:5088', ws: true },
    },
  },
  build: {
    // The catalogue view will be the heaviest screen in the app; surface bundle
    // growth early rather than discovering it once it is load-bearing.
    chunkSizeWarningLimit: 600,
    sourcemap: true,
  },
})
