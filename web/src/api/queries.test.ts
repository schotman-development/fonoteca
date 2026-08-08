/**
 * Tests for the query layer's two structural rules:
 *
 * - **one query key per data set** — a key describes exactly the URL that was
 *   fetched, so two components asking for the same effective list share one
 *   cache entry rather than quietly running two that can disagree;
 * - **mutations invalidate, and `meta`/`settings` are never invalidated** —
 *   they describe the build, not the database.
 */

import { QueryClient } from '@tanstack/react-query'
import { describe, expect, it } from 'vitest'

import { ApiError } from '@/api/client'
import {
  cleanParams,
  createQueryClient,
  invalidateLive,
  queryKeys,
  LIMITS,
  REFETCH,
} from '@/api/queries'

describe('cleanParams — the null-filter rule at key level', () => {
  it('drops null and undefined', () => {
    expect(cleanParams({ q: null, status: undefined, monitored: true })).toEqual({
      monitored: true,
    })
  })

  it('keeps the empty string and the boolean false', () => {
    expect(cleanParams({ status: '', monitored: false })).toEqual({
      status: '',
      monitored: false,
    })
  })

  it('makes an omitted filter and an explicit undefined the same key', () => {
    // `{q: undefined}` and `{}` must hash identically, or the backlog page runs
    // two queries for one table and the poll resets what the user typed.
    expect(JSON.stringify(queryKeys.wanted({ q: undefined, limit: 500 }))).toBe(
      JSON.stringify(queryKeys.wanted({ limit: 500 })),
    )
  })

  it('keeps two different filter states apart', () => {
    expect(JSON.stringify(queryKeys.wanted({ status: 'failed' }))).not.toBe(
      JSON.stringify(queryKeys.wanted({ status: 'wanted' })),
    )
  })
})

describe('queryKeys', () => {
  it('never coerces an id', () => {
    const key = queryKeys.album('0884977859300')
    expect(key[1]).toBe('0884977859300')
    expect(typeof key[1]).toBe('string')
  })

  it('gives the status payload a key per activity limit', () => {
    // A footer polling `activity_limit=0` and a screen polling 15 are different
    // data sets; sharing a key would make one silently rearrange the other.
    expect(JSON.stringify(queryKeys.status(0))).not.toBe(
      JSON.stringify(queryKeys.status(15)),
    )
  })
})

describe('invalidateLive', () => {
  it('invalidates live data and leaves meta and settings alone', async () => {
    const client = new QueryClient()
    client.setQueryData(queryKeys.meta(), { app_name: 'Qobuzarr' })
    client.setQueryData(queryKeys.settings(), { library_path: '/music' })
    client.setQueryData(queryKeys.navCounts(), { wanted: 3 })
    client.setQueryData(queryKeys.wanted({ limit: 500 }), { items: [], total: 0 })

    await invalidateLive(client)

    const state = (key: readonly unknown[]) =>
      client.getQueryState([...key])?.isInvalidated ?? false

    expect(state(queryKeys.navCounts())).toBe(true)
    expect(state(queryKeys.wanted({ limit: 500 }))).toBe(true)
    expect(state(queryKeys.meta())).toBe(false)
    expect(state(queryKeys.settings())).toBe(false)
  })
})

describe('the global fetch policy', () => {
  it('never polls a hidden tab', () => {
    // The old shim paused on `document.hidden` and fired exactly one catch-up
    // tick on `visibilitychange` — never a queued burst.
    const defaults = createQueryClient().getDefaultOptions().queries
    expect(defaults?.refetchIntervalInBackground).toBe(false)
    expect(defaults?.refetchOnWindowFocus).toBe(true)
  })

  it('does not retry a 4xx or a 503', () => {
    const retry = createQueryClient().getDefaultOptions().queries?.retry
    if (typeof retry !== 'function') throw new Error('retry must be a predicate')
    expect(retry(0, new ApiError(422, 'Invalid request'))).toBe(false)
    expect(retry(0, new ApiError(404, 'No such album.'))).toBe(false)
    expect(retry(0, new ApiError(503, 'Qobuz is not configured.'))).toBe(false)
    expect(retry(0, new ApiError(500, 'Internal server error'))).toBe(true)
    expect(retry(1, new ApiError(500, 'Internal server error'))).toBe(false)
  })

  it('publishes the polling table and the row limits as constants', () => {
    // These were Python constants read by both a page and its polled fragment;
    // a literal repeated at two call sites is the bug the spec names.
    expect(REFETCH.queue).toBe(5_000)
    expect(REFETCH.status).toBe(10_000)
    expect(REFETCH.navCounts).toBe(20_000)
    expect(LIMITS.wanted).toBe(500)
    expect(LIMITS.artists).toBe(1000)
  })
})
