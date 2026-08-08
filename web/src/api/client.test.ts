/**
 * Tests for the fetch wrapper. Two things are being pinned:
 *
 * - the uniform error envelope is unwrapped so a screen can show `error`
 *   verbatim and route on `status`, including the 422 `{detail: [...]}` shape;
 * - **`null` is never sent as a filter value** — `?monitored=null` is a 422, and
 *   it is the easiest way to turn "show me everything" into an error page.
 */

import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

import { ApiError, apiPath, buildQuery, del, get, post } from '@/api/client'

function jsonResponse(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { 'Content-Type': 'application/json' },
  })
}

const fetchMock = vi.fn()

beforeEach(() => {
  fetchMock.mockReset()
  vi.stubGlobal('fetch', fetchMock)
})

afterEach(() => {
  vi.unstubAllGlobals()
})

/** The URL the last call was made against. */
function lastUrl(): string {
  const call = fetchMock.mock.calls.at(-1)
  return String(call?.[0])
}

describe('buildQuery — the null-filter rule', () => {
  it('omits null and undefined entirely', () => {
    expect(buildQuery({ q: null, status: undefined, monitored: null })).toBe('')
  })

  it('keeps the empty string, which is the server\'s "no filter"', () => {
    // Every optional filter carries `EmptyAsNone`, so `?status=` is identical
    // to an omitted status — but it is a legitimate thing to send so a URL
    // round-trips what the user cleared.
    expect(buildQuery({ status: '' })).toBe('?status=')
  })

  it('never emits the string "null"', () => {
    const qs = buildQuery({ a: null, b: 'x' })
    expect(qs).not.toContain('null')
    expect(qs).toBe('?b=x')
  })

  it('serialises booleans as true/false and repeats array keys', () => {
    expect(buildQuery({ monitored: false })).toBe('?monitored=false')
    expect(buildQuery({ t: ['album', 'ep'] })).toBe('?t=album&t=ep')
  })

  it('returns an empty string rather than a bare "?" for no params', () => {
    expect(buildQuery({})).toBe('')
    expect(buildQuery(undefined)).toBe('')
  })
})

describe('apiPath — ids are strings', () => {
  it('encodes each segment without coercing it', () => {
    // A real album id. Nothing may parseInt this, and the leading zero has to
    // survive: it is a barcode.
    expect(apiPath(['albums', '0884977859300', 'detail'])).toBe(
      '/api/albums/0884977859300/detail',
    )
  })

  it('encodes a release-group key that is a normalised title, not an MBID', () => {
    expect(apiPath(['release-groups', 'brothers in arms/1985'])).toBe(
      '/api/release-groups/brothers%20in%20arms%2F1985',
    )
  })
})

describe('get — filters reach the URL', () => {
  it('drops null filters from the request URL', async () => {
    fetchMock.mockResolvedValue(jsonResponse({ items: [], total: 0 }))
    await get('/wanted', { q: null, status: undefined, monitored: true, limit: 500 })
    expect(lastUrl()).toBe('/api/wanted?monitored=true&limit=500')
  })

  it('sends no body and no content-type on a plain GET', async () => {
    fetchMock.mockResolvedValue(jsonResponse({}))
    await get('/meta')
    const init = fetchMock.mock.calls.at(-1)?.[1] as RequestInit
    expect(init.body).toBeUndefined()
    expect((init.headers as Record<string, string>)['Content-Type']).toBeUndefined()
  })
})

describe('post — an absent body stays absent', () => {
  it('sends no body at all when none is given (the monitor toggle)', async () => {
    // `POST /api/albums/{id}/monitor` with an empty body *toggles*; a body sets
    // an explicit value. Sending `null` here would be a body.
    fetchMock.mockResolvedValue(jsonResponse({ id: 'a1' }))
    await post('/albums/a1/monitor')
    const init = fetchMock.mock.calls.at(-1)?.[1] as RequestInit
    expect(init.body).toBeUndefined()
  })

  it('serialises a body when one is given', async () => {
    fetchMock.mockResolvedValue(jsonResponse({ ok: true }))
    await post('/artists/bulk', { artist_ids: ['a'], monitor_mode: 'all' })
    const init = fetchMock.mock.calls.at(-1)?.[1] as RequestInit
    expect(init.body).toBe('{"artist_ids":["a"],"monitor_mode":"all"}')
  })

  it('omits a key rather than sending null for "leave alone"', async () => {
    // The tri-state rule: JSON.stringify drops `undefined` values, which is
    // exactly the wire shape a bulk edit needs.
    fetchMock.mockResolvedValue(jsonResponse({ ok: true }))
    await post('/artists/bulk', { artist_ids: ['a'], monitored: undefined })
    const init = fetchMock.mock.calls.at(-1)?.[1] as RequestInit
    expect(init.body).toBe('{"artist_ids":["a"]}')
  })
})

describe('the uniform error envelope', () => {
  it('throws ApiError carrying the status and the server sentence', async () => {
    fetchMock.mockResolvedValue(
      jsonResponse(
        {
          ok: false,
          error: 'Qobuz is not configured. Set QOBUZ_APP_ID and QOBUZ_USER_AUTH_TOKEN.',
          status_code: 503,
        },
        503,
      ),
    )
    await expect(get('/search', { q: 'x' })).rejects.toMatchObject({
      name: 'ApiError',
      status: 503,
      message: 'Qobuz is not configured. Set QOBUZ_APP_ID and QOBUZ_USER_AUTH_TOKEN.',
    })
  })

  it('exposes the status classes screens route on', async () => {
    const cases: [number, keyof ApiError][] = [
      [503, 'isUnavailable'],
      [409, 'isBusy'],
      [502, 'isUpstream'],
      [404, 'isNotFound'],
    ]
    for (const [status, flag] of cases) {
      fetchMock.mockResolvedValue(
        jsonResponse({ ok: false, error: 'nope', status_code: status }, status),
      )
      const error = await get('/anything').catch((e: unknown) => e)
      expect(error).toBeInstanceOf(ApiError)
      expect((error as ApiError)[flag]).toBe(true)
    }
  })

  it('keeps a structured detail dict so the identify picker can re-draw', async () => {
    fetchMock.mockResolvedValue(
      jsonResponse(
        {
          ok: false,
          status_code: 400,
          error: 'That is not a MusicBrainz id.',
          detail: {
            entity_type: 'album',
            entity_id: 'uyej1o165e870',
            source: 'musicbrainz',
            external_id: 'banana',
            expected: 'MusicBrainz id',
          },
        },
        400,
      ),
    )
    const error = (await post('/enrichment/album/uyej1o165e870/identify', {
      source: 'musicbrainz',
      external_id: 'banana',
    }).catch((e: unknown) => e)) as ApiError
    expect(error.status).toBe(400)
    expect(error.message).toBe('That is not a MusicBrainz id.')
    expect(error.detail).toMatchObject({ expected: 'MusicBrainz id' })
    // A dict detail is not a validation list.
    expect(error.validationErrors).toEqual([])
  })

  it('unwraps the 422 shape, whose detail is a list', async () => {
    fetchMock.mockResolvedValue(
      jsonResponse(
        {
          ok: false,
          error: 'Invalid request',
          detail: [
            {
              loc: ['query', 'state'],
              msg: "Input should be 'pending', 'active', 'done', 'failed' or 'cancelled'",
              type: 'enum',
            },
          ],
        },
        422,
      ),
    )
    const error = (await get('/queue', { state: 'banana' }).catch(
      (e: unknown) => e,
    )) as ApiError
    expect(error.status).toBe(422)
    expect(error.message).toBe('Invalid request')
    expect(error.validationErrors).toHaveLength(1)
    expect(error.validationErrors[0]?.loc).toEqual(['query', 'state'])
  })

  it('falls back to a bare FastAPI {detail: "..."} body', async () => {
    fetchMock.mockResolvedValue(jsonResponse({ detail: 'No such album.' }, 404))
    await expect(get('/albums/nope')).rejects.toMatchObject({
      status: 404,
      message: 'No such album.',
    })
  })

  it('never renders an empty message, whatever the body was', async () => {
    fetchMock.mockResolvedValue(new Response('', { status: 500 }))
    const error = (await get('/status').catch((e: unknown) => e)) as ApiError
    expect(error.message).toBe('Request failed (500).')
  })

  it('reports a dead server as status 0 rather than throwing a TypeError', async () => {
    fetchMock.mockRejectedValue(new TypeError('Failed to fetch'))
    const error = (await get('/status').catch((e: unknown) => e)) as ApiError
    expect(error).toBeInstanceOf(ApiError)
    expect(error.status).toBe(0)
  })

  it('rejects an HTML body from /api — that endpoint does not exist', async () => {
    // The SPA catch-all serves index.html for everything that is not an API
    // route, so a typo'd endpoint would otherwise surface as a parse error
    // three layers away.
    fetchMock.mockResolvedValue(
      new Response('<!doctype html><title>Qobuzarr</title>', {
        status: 200,
        headers: { 'Content-Type': 'text/html' },
      }),
    )
    await expect(get('/artits')).rejects.toThrow(/does not exist/)
  })

  it('returns undefined for 204 without trying to parse a body', async () => {
    fetchMock.mockResolvedValue(new Response(null, { status: 204 }))
    await expect(del('/library/trash')).resolves.toBeUndefined()
  })

  it('lets an AbortError through untouched so TanStack can cancel', async () => {
    fetchMock.mockRejectedValue(new DOMException('aborted', 'AbortError'))
    await expect(get('/status')).rejects.toBeInstanceOf(DOMException)
  })
})
