/// <reference types="vitest/config" />
import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'
import { fileURLToPath, URL } from 'node:url'

// The build lands in ../static/app, which FastAPI already serves through its
// /static mount — so `base` has to be the URL that mount exposes, not '/'.
// index.html is then handed out by the SPA catch-all in main.py, while the
// hashed assets beside it are plain static files with a long cache.
export default defineConfig({
  plugins: [react()],
  base: '/static/app/',
  resolve: {
    alias: { '@': fileURLToPath(new URL('./src', import.meta.url)) },
  },
  build: {
    outDir: '../static/app',
    emptyOutDir: true,
    sourcemap: true,
  },
  server: {
    port: 5173,
    // `npm run dev` talks to a uvicorn on 8000; everything the SPA calls is
    // under one of these three prefixes.
    proxy: {
      '/api': 'http://127.0.0.1:8000',
      '/health': 'http://127.0.0.1:8000',
      '/static': 'http://127.0.0.1:8000',
    },
  },
  test: {
    environment: 'jsdom',
    setupFiles: ['./src/test/setup.ts'],
    globals: true,
    css: true,
    include: ['src/**/*.{test,spec}.{ts,tsx}'],
  },
})
