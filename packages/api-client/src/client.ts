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

/** Paths that expose a POST, narrowed from the generated `paths` map. */
type PostPath = {
  [P in keyof paths]: paths[P] extends { post: unknown } ? P : never
}[keyof paths]

/**
 * The JSON body a POST answers with.
 *
 * Both 200 and 202 are extracted, because a command that finishes inside its
 * request and one that hands back a job id are the same call as far as a caller
 * is concerned — only the server knows which it is.
 */
type PostResponse<P extends PostPath> = paths[P] extends {
  post: { responses: { 200: { content: { 'application/json': infer R } } } }
}
  ? R
  : paths[P] extends {
        post: { responses: { 202: { content: { 'application/json': infer R } } } }
      }
    ? R
    : undefined

/** Paths that expose a DELETE, narrowed from the generated `paths` map. */
type DeletePath = {
  [P in keyof paths]: paths[P] extends { delete: unknown } ? P : never
}[keyof paths]

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
  /**
   * POST with no request body.
   *
   * Every mutating endpoint so far is a command with its arguments in the path,
   * so there is nothing to send. When one takes a body, its type comes from
   * `paths[P]['post']['requestBody']` — deriving it now, with no endpoint to
   * check it against, would be a guess dressed as a contract.
   */
  post<P extends PostPath>(path: P, init?: RequestInit): Promise<PostResponse<P>>
  /**
   * DELETE, for endpoints where the thing being removed is a running operation
   * rather than a record. Answers with nothing.
   */
  delete<P extends DeletePath>(path: P, init?: RequestInit): Promise<void>
}

export function createApiClient(options: ApiClientOptions): ApiClient {
  const doFetch = options.fetch ?? globalThis.fetch.bind(globalThis)
  const baseUrl = options.baseUrl.replace(/\/$/, '')

  async function request(method: string, path: string, init?: RequestInit): Promise<unknown> {
    const url = `${baseUrl}${path}`

    const response = await doFetch(url, {
      ...init,
      method,
      headers: { Accept: 'application/json', ...init?.headers },
    })

    if (!response.ok) {
      throw new ApiError(response.status, response.statusText, await response.text(), url)
    }

    // Not every success has a body. A 204, or a 202 that only means "accepted",
    // answers with nothing at all, and `response.json()` on an empty body throws
    // a SyntaxError that would surface as a failed request.
    if (response.status === 204 || response.headers.get('Content-Length') === '0') {
      return undefined
    }

    const body = await response.text()
    return body.length === 0 ? undefined : JSON.parse(body)
  }

  return {
    async get<P extends GetPath>(path: P, init?: RequestInit): Promise<GetResponse<P>> {
      return (await request('GET', String(path), init)) as GetResponse<P>
    },

    async post<P extends PostPath>(path: P, init?: RequestInit): Promise<PostResponse<P>> {
      return (await request('POST', String(path), init)) as PostResponse<P>
    },

    async delete<P extends DeletePath>(path: P, init?: RequestInit): Promise<void> {
      await request('DELETE', String(path), init)
    },
  }
}

/**
 * The `detail` line from an RFC 9457 problem document, if that is what the body is.
 *
 * Returns undefined for anything else — a proxy's HTML error page, an empty
 * body, a JSON document without the field. Callers fall back to the status.
 */
export function problemDetail(body: string): string | undefined {
  try {
    const parsed: unknown = JSON.parse(body)
    if (typeof parsed !== 'object' || parsed === null) return undefined

    const { detail } = parsed as { detail?: unknown }
    return typeof detail === 'string' && detail.length > 0 ? detail : undefined
  } catch {
    return undefined
  }
}
