import react from '@vitejs/plugin-react'
import { defineConfig } from 'vite'

export default defineConfig({
  plugins: [react()],
  server: {
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
