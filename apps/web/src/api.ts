import { createApiClient } from '@fonoteca/api-client'

/**
 * Where the API lives.
 *
 * Same-origin in production (the API serves the built client); the dev server
 * runs on 5173 while the API runs on 5088, so development points at it
 * explicitly and the API allows that origin via CORS.
 */
export const apiBaseUrl: string =
  import.meta.env.VITE_API_BASE_URL ?? (import.meta.env.DEV ? 'http://localhost:5088' : '')

export const api = createApiClient({ baseUrl: apiBaseUrl })
