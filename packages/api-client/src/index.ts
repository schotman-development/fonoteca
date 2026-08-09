/**
 * @fonoteca/api-client
 *
 * `src/schema.d.ts` is GENERATED from `openapi.json` at the repository root and
 * is committed. CI regenerates it and fails on a diff, so a backend change that
 * is not reflected here breaks the build rather than the browser.
 *
 * Regenerate with `pnpm gen:api`.
 */

export type { ApiClient, ApiClientOptions, GetOptions } from './client.ts'
export { ApiError, createApiClient, describeError, problemDetail } from './client.ts'
export type { components, operations, paths } from './schema.d.ts'
