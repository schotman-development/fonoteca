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

/**
 * The path and query parameters a GET declares, read off the generated types.
 *
 * Not a loose `Record<string, string>`: `/api/catalogue/artists/{id}` cannot be
 * called without an `id`, cannot be called with a misspelt one, and cannot be
 * given a `take` it does not accept. That is the guarantee the path union
 * already gives for the route, extended to the part of the URL it cannot see.
 */
type GetParams<P extends GetPath> = paths[P] extends { get: { parameters: infer Q } } ? Q : never

/**
 * Whether the route has a required path segment to fill in.
 *
 * `openapi-typescript` writes `path?: never` for a route with no segments and
 * `path: { … }` for one that has them, so the presence of a required `path`
 * member is the whole test. Query parameters are always optional and never make
 * the argument mandatory.
 */
type NeedsParams<P extends GetPath> =
  GetParams<P> extends { path: Record<string, unknown> } ? true : false

export type GetOptions<P extends GetPath> = RequestInit & { params?: GetParams<P> }

/**
 * The argument list after the path: mandatory when the route has segments.
 *
 * A rest tuple rather than an optional parameter, because an optional one
 * cannot be made required by a condition — and a call that forgets `{ id }`
 * would otherwise compile and request a URL containing a literal `{id}`.
 */
type GetArgs<P extends GetPath> =
  NeedsParams<P> extends true
    ? [options: GetOptions<P> & { params: GetParams<P> }]
    : [options?: GetOptions<P>]

/** The two halves of a URL beyond the base: `{placeholders}` and `?a=b`. */
type UrlParams = {
  readonly path?: Record<string, unknown>
  readonly query?: Record<string, unknown>
}

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
  /**
   * GET, with the route's own parameters when it has any.
   *
   * `api.get('/api/catalogue/artists', { params: { query: { take: 50 } } })`,
   * and the `params` argument becomes mandatory for a route with a `{segment}`.
   */
  get<P extends GetPath>(path: P, ...options: GetArgs<P>): Promise<GetResponse<P>>
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

  async function request(
    method: string,
    path: string,
    options?: RequestInit & { params?: UrlParams },
  ): Promise<unknown> {
    const { params, ...init } = options ?? {}
    const url = buildUrl(baseUrl, path, params)

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
    async get<P extends GetPath>(path: P, ...options: GetArgs<P>): Promise<GetResponse<P>> {
      // The generic is the contract and it is checked at every call site; inside
      // the implementation it is erased, and `GetParams<P>` cannot be proved
      // assignable to the structural shape `buildUrl` reads without repeating
      // the whole conditional here for no benefit.
      const request0 = options[0] as (RequestInit & { params?: UrlParams }) | undefined

      return (await request('GET', String(path), request0)) as GetResponse<P>
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
 * Substitutes `{segments}` and appends a query string.
 *
 * Hand-rolled rather than a template library, in keeping with the rest of this
 * file: it is a `replace` and a `URLSearchParams`, both of which the platform
 * ships. `undefined` and `null` query values are dropped rather than sent as
 * the strings "undefined" and "null" — an omitted filter is not a filter for
 * the word.
 */
function buildUrl(baseUrl: string, path: string, params?: UrlParams): string {
  let route = path

  for (const [name, value] of Object.entries(params?.path ?? {})) {
    route = route.replace(`{${name}}`, encodeURIComponent(String(value)))
  }

  const search = new URLSearchParams()

  for (const [name, value] of Object.entries(params?.query ?? {})) {
    if (value === undefined || value === null) continue

    if (Array.isArray(value)) {
      for (const item of value) search.append(name, String(item))
    } else {
      search.append(name, String(value))
    }
  }

  const query = search.toString()

  return `${baseUrl}${route}${query ? `?${query}` : ''}`
}

/**
 * What went wrong, as a line to show somebody.
 *
 * Every panel had its own copy of this. Hoisted rather than left duplicated
 * because the interesting half is the RFC 9457 unwrapping below, and a copy
 * that forgets it turns a server's careful explanation into "500 Internal
 * Server Error".
 */
export function describeError(cause: unknown): string {
  if (cause instanceof ApiError) {
    return problemDetail(cause.body) ?? `${cause.status} ${cause.statusText}`
  }

  return cause instanceof Error ? cause.message : 'Unknown error'
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
