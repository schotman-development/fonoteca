import type { paths } from './schema.d.ts'

/**
 * A typed `fetch` wrapper over the generated schema.
 *
 * Hand-written rather than generated, and dependency-free on purpose: the whole
 * runtime surface is one `fetch` call, and `openapi-typescript` already
 * supplies the types. Adding a client library here would mean shipping a
 * runtime dependency to save about thirty lines.
 */

/** Paths that expose a GET, narrowed from the generated `paths` map. */
type GetPath = {
  [P in keyof paths]: paths[P] extends { get: unknown } ? P : never
}[keyof paths]

/** The 200 application/json body for a GET, extracted from the generated types. */
type GetResponse<P extends GetPath> = paths[P] extends {
  get: { responses: { 200: { content: { 'application/json': infer R } } } }
}
  ? R
  : never

export type ApiClientOptions = {
  /** Base URL of the API, without a trailing slash. */
  readonly baseUrl: string
  /** Injectable for tests. Defaults to the global fetch. */
  readonly fetch?: typeof globalThis.fetch
}

/**
 * A non-2xx response, or a transport failure.
 *
 * Carries the status and raw body rather than a parsed error shape: the API
 * returns RFC 9457 problem details for handled failures, but a proxy timing out
 * returns HTML, and the client should not pretend otherwise.
 */
export class ApiError extends Error {
  constructor(
    readonly status: number,
    readonly statusText: string,
    readonly body: string,
    readonly url: string,
  ) {
    super(`${status} ${statusText} for ${url}`)
    this.name = 'ApiError'
  }
}

export type ApiClient = {
  get<P extends GetPath>(path: P, init?: RequestInit): Promise<GetResponse<P>>
}

export function createApiClient(options: ApiClientOptions): ApiClient {
  const doFetch = options.fetch ?? globalThis.fetch.bind(globalThis)
  const baseUrl = options.baseUrl.replace(/\/$/, '')

  return {
    async get<P extends GetPath>(path: P, init?: RequestInit): Promise<GetResponse<P>> {
      const url = `${baseUrl}${String(path)}`

      const response = await doFetch(url, {
        ...init,
        method: 'GET',
        headers: { Accept: 'application/json', ...init?.headers },
      })

      if (!response.ok) {
        throw new ApiError(response.status, response.statusText, await response.text(), url)
      }

      return (await response.json()) as GetResponse<P>
    },
  }
}
