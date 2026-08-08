/**
 * The typed fetch wrapper every request in the app goes through.
 *
 * It exists for four rules that are easy to break one call site at a time:
 *
 * 1. **The error envelope is uniform, so unwrapping it belongs in one place.**
 *    Every failure answers `{ok: false, error: "<sentence>", status_code: n}`,
 *    and a 422 answers `{ok: false, error: "Invalid request", detail: [...]}`.
 *    `error` is prose written to be read by a person — screens show it
 *    **verbatim** and must never match on it (that is what `BannerOut.code` and
 *    `MessageOut.level` exist to prevent). `ApiError` carries the status so a
 *    screen can tell "not wired up" (503) from "busy" (409) from "you typed a
 *    name that is not an id" (400) without reading the sentence.
 * 2. **Never send `null` for an absent filter.** Every optional query parameter
 *    carries `EmptyAsNone` server-side, so `?status=`, `?status` and an omitted
 *    `status` are identical — but the *string* `"null"` is a 422, and that is
 *    exactly what `String(null)` produces. `buildQuery` drops `null` and
 *    `undefined` keys entirely.
 * 3. **Never `parseInt` an id.** Ids are strings (`uyej1o165e870`,
 *    `0884977859300`); path segments are interpolated as-is, encoded, never
 *    coerced.
 * 4. **A `/api/*` path that answers HTML is a bug, not a payload.** The SPA
 *    catch-all serves `index.html` for everything else, so a typo'd endpoint
 *    would otherwise surface as a JSON parse error three layers away.
 */

import type { ErrorEnvelope, ValidationErrorItem } from '@/api/types'

/** Everything is served from the same origin the SPA shell came from. */
export const API_BASE = '/api'

/**
 * A failed request, carrying the server's own sentence and its status code.
 *
 * `status` is the routing key (503 subsystem not wired · 409 busy · 400
 * refusal or bad value · 404 missing · 422 validation · 502 upstream silent);
 * `message` is the sentence to render. `detail` is whatever structured extra
 * the server attached — the identify picker re-draws itself from it.
 */
export class ApiError extends Error {
  readonly status: number
  readonly detail: ErrorEnvelope['detail']
  /** The URL that failed, for logging. Never rendered. */
  readonly url: string

  constructor(
    status: number,
    message: string,
    options: { detail?: ErrorEnvelope['detail']; url?: string } = {},
  ) {
    super(message)
    this.name = 'ApiError'
    this.status = status
    this.detail = options.detail
    this.url = options.url ?? ''
  }

  /** 503: a subsystem is not wired up (no Qobuz client, scanner, enricher). */
  get isUnavailable(): boolean {
    return this.status === 503
  }

  /** 409: something is already running. Retrying now will fail the same way. */
  get isBusy(): boolean {
    return this.status === 409
  }

  /** 502: the upstream did not answer. Unlike 503, trying again may work. */
  get isUpstream(): boolean {
    return this.status === 502
  }

  get isNotFound(): boolean {
    return this.status === 404
  }

  /**
   * The 422 body's per-field errors, or `[]` for every other failure. FastAPI
   * puts a *list* under `detail` for validation and a *dict* for a structured
   * refusal, so the shape has to be checked rather than assumed.
   */
  get validationErrors(): ValidationErrorItem[] {
    return Array.isArray(this.detail) ? this.detail : []
  }
}

/**
 * A query-string value. `null`/`undefined` mean **omit the key**; `''` means
 * "explicitly no filter", which the server reads identically but which is
 * occasionally worth sending so a URL round-trips.
 */
export type QueryValue =
  | string
  | number
  | boolean
  | null
  | undefined
  | readonly (string | number)[]

export type QueryParams = Record<string, QueryValue>

/**
 * Serialise filters. Absent means absent: a `null` filter is dropped, not
 * stringified — `?monitored=null` is a 422, and it is the single easiest way to
 * turn "show me everything" into an error page.
 *
 * Booleans go out as `true`/`false` (what FastAPI's bool parser wants), arrays
 * repeat the key, and `''` is preserved because empty string is a legitimate
 * "no filter" on every `EmptyAsNone` parameter.
 */
export function buildQuery(params: QueryParams | undefined): string {
  if (!params) return ''
  const search = new URLSearchParams()
  for (const [key, value] of Object.entries(params)) {
    if (value === null || value === undefined) continue
    if (Array.isArray(value)) {
      for (const item of value) search.append(key, String(item))
      continue
    }
    search.append(key, String(value))
  }
  const qs = search.toString()
  return qs ? `?${qs}` : ''
}

/**
 * Join a path onto the API base, encoding each interpolated segment.
 *
 * Ids reach this as strings and leave as strings. `encodeURIComponent` matters
 * more than it looks: a release-group key may be a *normalised title* rather
 * than an MBID, so slashes and spaces genuinely occur.
 */
export function apiPath(
  segments: readonly (string | number)[],
  params?: QueryParams,
): string {
  const path = segments.map((part) => encodeURIComponent(String(part))).join('/')
  return `${API_BASE}/${path}${buildQuery(params)}`
}

export interface RequestOptions {
  method?: 'GET' | 'POST' | 'PATCH' | 'DELETE' | 'PUT'
  /** JSON request body. Omitted keys stay omitted — that is how "leave alone"
   *  is expressed on a partial update, and `null` must never stand in for it. */
  body?: unknown
  query?: QueryParams
  signal?: AbortSignal
  /** Path segments to encode and join. Use this rather than interpolating an
   *  id into a template string. */
  segments?: readonly (string | number)[]
}

function messageFromEnvelope(payload: unknown, status: number): {
  message: string
  detail: ErrorEnvelope['detail']
} {
  if (payload && typeof payload === 'object') {
    const envelope = payload as Partial<ErrorEnvelope> & {
      detail?: unknown
      message?: unknown
    }
    const detail = envelope.detail as ErrorEnvelope['detail']
    if (typeof envelope.error === 'string' && envelope.error) {
      return { message: envelope.error, detail }
    }
    // A bare FastAPI HTTPException that escaped the app's own handler still
    // answers `{"detail": "..."}`. Treat the string form as the sentence.
    if (typeof envelope.detail === 'string' && envelope.detail) {
      return { message: envelope.detail, detail: undefined }
    }
    if (typeof envelope.message === 'string' && envelope.message) {
      return { message: envelope.message, detail }
    }
    return { message: `Request failed (${status}).`, detail }
  }
  if (typeof payload === 'string' && payload.trim()) {
    return { message: payload.trim(), detail: undefined }
  }
  return { message: `Request failed (${status}).`, detail: undefined }
}

async function readBody(response: Response): Promise<unknown> {
  const text = await response.text()
  if (!text) return null
  try {
    return JSON.parse(text) as unknown
  } catch {
    return text
  }
}

/**
 * Issue one request and return its parsed body, or throw `ApiError`.
 *
 * `T` is asserted, not validated: the server's `response_model` is the
 * guarantee, and a runtime schema check here would be a second copy of
 * `schemas.py` to keep in step. The one thing that *is* checked is that the
 * response is JSON at all — an HTML body from `/api/*` means the request fell
 * through to the SPA catch-all, i.e. the endpoint does not exist.
 */
export async function request<T>(
  path: string,
  options: RequestOptions = {},
): Promise<T> {
  const { method = 'GET', body, query, signal, segments } = options
  const url = segments
    ? apiPath(segments, query)
    : `${API_BASE}${path}${buildQuery(query)}`

  const headers: Record<string, string> = { Accept: 'application/json' }
  let payload: string | undefined
  if (body !== undefined) {
    headers['Content-Type'] = 'application/json'
    payload = JSON.stringify(body)
  }

  let response: Response
  try {
    response = await fetch(url, { method, headers, body: payload, signal })
  } catch (error) {
    if (error instanceof DOMException && error.name === 'AbortError') throw error
    // No status to route on: the server was not reached at all.
    throw new ApiError(0, 'Qobuzarr is not responding. Is the server running?', {
      url,
    })
  }

  if (response.status === 204) return undefined as T

  const parsed = await readBody(response)

  if (!response.ok) {
    const { message, detail } = messageFromEnvelope(parsed, response.status)
    throw new ApiError(response.status, message, { detail, url })
  }

  if (typeof parsed === 'string') {
    throw new ApiError(
      response.status,
      `${url} answered with a non-JSON body — that endpoint does not exist.`,
      { url },
    )
  }

  return parsed as T
}

/** `GET`, with filters. `null`/`undefined` values are omitted, never sent. */
export function get<T>(
  path: string,
  query?: QueryParams,
  signal?: AbortSignal,
): Promise<T> {
  return request<T>(path, { method: 'GET', query, signal })
}

/** `POST`. A body of `undefined` sends no body at all — which is what the
 *  monitor toggle needs: an empty body toggles, a body sets a value. */
export function post<T>(
  path: string,
  body?: unknown,
  query?: QueryParams,
): Promise<T> {
  return request<T>(path, { method: 'POST', body, query })
}

export function patch<T>(path: string, body?: unknown, query?: QueryParams): Promise<T> {
  return request<T>(path, { method: 'PATCH', body, query })
}

/** `PUT` — replace a collection wholesale, where `PATCH`'s "omitted means leave
 *  alone" would be the wrong promise. The credit filter is the case: the screen
 *  shows every credit with a tick beside it, so the list sent *is* the list. */
export function put<T>(path: string, body?: unknown, query?: QueryParams): Promise<T> {
  return request<T>(path, { method: 'PUT', body, query })
}

export function del<T>(path: string, query?: QueryParams): Promise<T> {
  return request<T>(path, { method: 'DELETE', query })
}

/** The unprefixed liveness probe — the one endpoint outside `/api`. */
export async function getHealth<T>(signal?: AbortSignal): Promise<T> {
  const response = await fetch('/health', {
    headers: { Accept: 'application/json' },
    signal,
  })
  const parsed = await readBody(response)
  if (!response.ok) {
    const { message, detail } = messageFromEnvelope(parsed, response.status)
    throw new ApiError(response.status, message, { detail, url: '/health' })
  }
  return parsed as T
}
