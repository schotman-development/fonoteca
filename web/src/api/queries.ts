/**
 * TanStack Query hooks — one family per endpoint — plus the `queryKeys`
 * factory, the polling table and the single `invalidateLive` helper.
 *
 * This file is where §5 of the rebuild spec lives, and four of its rules are
 * load-bearing:
 *
 * 1. **One query key per data set.** A screen and its own refresh are the same
 *    query with the same parameters. The HTML app's version of this rule was
 *    "a live region renders from the same template *and* the same context as
 *    its page" — give the polled fragment a different row limit and the screen
 *    silently rearranges itself five seconds after it loads. Here that means a
 *    component must never fetch a narrowed copy of a list another component
 *    already has, and row limits are the exported constants below rather than
 *    literals at two call sites.
 * 2. **Mutations invalidate; GETs never do.** Every `/ui/*` POST used to fire
 *    `qobuzarr:refresh` and every live region listened; *no GET ever fired it*,
 *    because a GET that claimed a change made every region refresh every other
 *    region forever. So: `invalidateLive()` from a mutation's `onSuccess`, and
 *    **no query callback may invalidate anything**.
 * 3. **One scheduler, not one timer per component.** `refetchInterval` per
 *    query with `refetchIntervalInBackground: false` — the old shim paused on
 *    `document.hidden` and fired exactly one catch-up tick on
 *    `visibilitychange`, never a burst. A poller dies with its component; there
 *    are no module-level timers here.
 * 4. **Filters travel with the refetch.** They are part of the key, so a poll
 *    can never reset what the user typed.
 *
 * Two intervals are *conditional* — the import panel and the integrity pass —
 * because an idle screen must issue **zero** requests, not one every four
 * seconds forever.
 */

import {
  QueryClient,
  useMutation,
  useQuery,
  useQueryClient,
  type UseMutationResult,
  type UseQueryResult,
} from '@tanstack/react-query'

import { del, get, getHealth, patch, post, put, type QueryParams } from '@/api/client'
import type {
  ActivityLevel,
  ActivityListOut,
  AlbumDetailOut,
  AlbumListOut,
  AlbumOut,
  ArtistCreditsOut,
  AlbumStatus,
  AlbumUpdateIn,
  ArtistBulkUpdateIn,
  ArtistCreateIn,
  ArtistDetailOut,
  ArtistFollowOut,
  ArtistListOut,
  ArtistOut,
  ArtistStatsOut,
  ArtistTagsIn,
  ArtistUpdateIn,
  BannerOut,
  ConsensusFieldOut,
  CorruptListOut,
  EnrichmentAcceptOut,
  EnrichmentCandidatesOut,
  EnrichmentIdentifyIn,
  EnrichmentRejectIn,
  EnrichmentReviewListOut,
  EnrichmentStatusOut,
  HealthOut,
  HealthSummaryOut,
  IntegrityRunOut,
  IntegrityStatusOut,
  LibraryImportOut,
  LibraryImportPreviewOut,
  LibraryImportStartIn,
  LibraryScanOut,
  LibraryScanStatusOut,
  LibraryTidyOut,
  MessageOut,
  MetaOut,
  NavCountsOut,
  QueueItemOut,
  QueueListOut,
  QueueState,
  RefileEstimateOut,
  RefilePlanOut,
  ReleaseGroupOut,
  SearchResultOut,
  SettingsOut,
  SettingsUpdateIn,
  SortOrder,
  StatsOut,
  StatusOut,
  TrashOut,
  WantedListOut,
} from '@/api/types'

// ---------------------------------------------------------------------------
// Polling table (spec §5.3) and row limits (were Python constants)
// ---------------------------------------------------------------------------

/**
 * Every interval in the app, in milliseconds, in one place. `false` is a real
 * value here: several screens are deliberately static, and "deliberately not
 * polled" is a decision worth being able to read.
 */
export const REFETCH = {
  /** The status footer. */
  status: 10_000,
  /** Sidebar badges. Must be correct on first paint — prefetch, never render a
   *  blank badge that grows twenty seconds later. */
  navCounts: 20_000,
  /** The library index's stat strip. */
  stats: 30_000,
  /** Backlog rows, artist header, artist album rows, activity history. */
  rows: 15_000,
  /** The artist page's activity strip. */
  activityStrip: 30_000,
  /** The queue — the fastest region, because the progress meter is the only
   *  thing that moves while you watch it. */
  queue: 5_000,
  /** Health overview. */
  healthSummary: 10_000,
  /** Import progress, **only while `running`**. */
  importProgress: 4_000,
  /** Integrity, **only while a pass is running**. */
  integrity: 10_000,
  /** Enrichment coverage and the review list. */
  enrichment: 15_000,
} as const

/**
 * Row limits. Exported because the caller and the query key must use the same
 * number: a fragment with a different limit from its page is exactly the bug
 * the same-context rule exists to prevent.
 *
 * **Every one of these is checked against the endpoint's own `le`**, because a
 * limit above the cap is a 422 and reads on screen as "this list is broken"
 * rather than "this number is too big". The caps as shipped:
 * artists 1000 · wanted 2000 · artist albums 2000 · queue 500 · activity 1000 ·
 * enrichment review 1000 · corrupt 2000 · search 100. `artists` sits exactly on
 * its cap — raising it means raising `api_list_artists` first.
 */
export const LIMITS = {
  artists: 1000,
  wanted: 500,
  artistAlbums: 500,
  queue: 300,
  activity: 100,
  /** The artist page's compact strip. */
  artistActivity: 25,
  /** A horizontal `Shelf` of artwork tiles — the dashboard's "Last downloaded".
   *  Small on purpose: a shelf is scrolled, not paged, and a hundred tiles is a
   *  hundred gradients nobody reaches the end of. */
  shelf: 12,
  /** The dashboard's "Missing albums" block. Was the Python `DASHBOARD_WANTED`
   *  constant, and it is exported for the same reason every limit here is: the
   *  caller and the query key must use one number. It is deliberately NOT
   *  `wanted` — that is the backlog screen's full list, and the dashboard shows
   *  the head of it under its own key. */
  dashboardWanted: 6,
  search: 25,
  enrichmentReview: 100,
  corrupt: 200,
  /** The Release radar's "New & upcoming" column. Cap is 200. A radar is a
   *  glance rather than a list — anything below the fold on this column is
   *  competing with the Library's own release table, which pages properly. */
  radar: 20,
} as const

/** `staleTime` for the two payloads that only change on deploy / on restart. */
export const STALE = {
  /** `/api/meta` changes when the server is redeployed and never otherwise. */
  meta: Infinity,
  /** `/api/settings` changes on two events and neither is a clock: an edit to
   *  `.env` plus a restart, or `PATCH /api/settings`, which returns the whole
   *  new payload and writes it straight into this cache. Five minutes is
   *  generous rather than risky — nothing else moves it. */
  settings: 5 * 60_000,
} as const

// ---------------------------------------------------------------------------
// Key factory
// ---------------------------------------------------------------------------

/**
 * Drop `null`/`undefined` so a key describes exactly the URL that was fetched.
 *
 * Two jobs in one: it is the client's "never send `null` for an absent filter"
 * rule (a `?monitored=null` is a 422), and it makes `{q: undefined}` and `{}`
 * hash to the same key — so two components asking for the same effective list
 * genuinely share one query rather than quietly running two.
 */
export function cleanParams(params?: QueryParams): QueryParams {
  const out: QueryParams = {}
  if (!params) return out
  for (const [key, value] of Object.entries(params)) {
    if (value === null || value === undefined) continue
    out[key] = value
  }
  return out
}

/**
 * The single source of query keys. Every hook below builds its key from here,
 * and so must every `invalidateQueries` call — a key spelled out by hand at a
 * call site is how two components stop sharing a cache entry.
 */
export const queryKeys = {
  meta: () => ['meta'] as const,
  settings: () => ['settings'] as const,
  status: (activityLimit: number) => ['status', { activityLimit }] as const,
  navCounts: () => ['nav-counts'] as const,
  banners: () => ['banners'] as const,
  stats: () => ['stats'] as const,
  health: () => ['health'] as const,
  healthSummary: (activityLimit: number) =>
    ['health-summary', { activityLimit }] as const,

  artists: (params?: QueryParams) => ['artists', cleanParams(params)] as const,
  artist: (artistId: string, albumLimit?: number) =>
    ['artist', artistId, cleanParams({ album_limit: albumLimit })] as const,
  artistStats: (artistId: string) => ['artist-stats', artistId] as const,
  artistCredits: (artistId: string) => ['artist-credits', artistId] as const,
  artistAlbums: (artistId: string, params?: QueryParams) =>
    ['artist-albums', artistId, cleanParams(params)] as const,

  wanted: (params?: QueryParams) => ['wanted', cleanParams(params)] as const,
  recentReleases: (params?: QueryParams) =>
    ['recent-releases', cleanParams(params)] as const,
  album: (albumId: string) => ['album', albumId] as const,
  albumDetail: (albumId: string) => ['album-detail', albumId] as const,
  albumConsensus: (albumId: string) => ['album-consensus', albumId] as const,
  releaseGroup: (key: string, artistId?: string | null) =>
    ['release-group', key, cleanParams({ artist_id: artistId })] as const,

  queue: (params?: QueryParams) => ['queue', cleanParams(params)] as const,
  queueItem: (itemId: number) => ['queue-item', itemId] as const,
  activity: (params?: QueryParams) => ['activity', cleanParams(params)] as const,
  search: (params?: QueryParams) => ['search', cleanParams(params)] as const,

  libraryScan: () => ['library-scan'] as const,
  libraryImport: () => ['library-import'] as const,
  libraryImportPreview: () => ['library-import-preview'] as const,
  /** The candidate template is part of the key, so pressing again for a
   *  template already counted is served from cache and editing the box cannot
   *  re-fire anything. */
  // The endpoint also takes `artist_id`, and this key deliberately does not:
  // no screen asks the per-artist question, and a key parameter nothing passes
  // is a key that is always spelled one way while claiming to be spelled two.
  // Add it here the day a caller needs it, not before.
  refileEstimate: (template: string) =>
    ['library/refile/estimate', cleanParams({ template })] as const,
  trash: () => ['trash'] as const,

  enrichment: () => ['enrichment'] as const,
  enrichmentReview: (params?: QueryParams) =>
    ['enrichment-review', cleanParams(params)] as const,
  enrichmentCandidates: (
    entityType: string,
    entityId: string,
    params?: QueryParams,
  ) => ['enrichment-candidates', entityType, entityId, cleanParams(params)] as const,

  integrity: () => ['integrity'] as const,
  integrityCorrupt: (params?: QueryParams) =>
    ['integrity-corrupt', cleanParams(params)] as const,
} as const

/**
 * Keys that are **never** invalidated by a mutation: they describe the build,
 * not the database. Invalidating `meta` on every button press would refetch a
 * payload that changes on deploy, forever.
 */
const FROZEN_KEY_ROOTS: ReadonlySet<string> = new Set(['meta', 'settings'])

// ---------------------------------------------------------------------------
// The client
// ---------------------------------------------------------------------------

/**
 * The app's `QueryClient`, with the global fetch policy from §5.2.
 *
 * `refetchIntervalInBackground: false` is the one that matters: it is how the
 * old shim behaved — pollers paused with the tab and fired **one** catch-up
 * tick on `visibilitychange`, never a queued burst. `refetchOnWindowFocus`
 * supplies that single tick.
 */
export function createQueryClient(): QueryClient {
  return new QueryClient({
    defaultOptions: {
      queries: {
        refetchIntervalInBackground: false,
        refetchOnWindowFocus: true,
        refetchOnReconnect: true,
        // A poll is coming anyway on every live region, and a screen that is
        // not polled is one the user asked for on purpose.
        refetchOnMount: true,
        staleTime: 2_000,
        // 503 means a subsystem is not wired up and 4xx means the request was
        // wrong; neither improves by asking again. Only 5xx-that-is-not-503 and
        // transport failures are worth a retry, and one is enough.
        retry: (failureCount: number, error: unknown) => {
          const status = (error as { status?: number }).status
          if (typeof status === 'number' && status >= 400 && status < 500) return false
          if (status === 503) return false
          return failureCount < 1
        },
      },
      mutations: { retry: false },
    },
  })
}

/**
 * Invalidate every live query. The one thing a mutation's `onSuccess` calls,
 * and the direct translation of `qobuzarr:refresh`.
 *
 * Deliberately broad rather than clever: the old contract was that *any*
 * mutating response refreshed *every* live region, and the failures that
 * produced — a queue row that kept saying "pending" after a retry, a badge that
 * disagreed with the page it linked to — all came from someone deciding a
 * particular press could not possibly affect a particular counter.
 */
export function invalidateLive(client: QueryClient): Promise<void> {
  return client.invalidateQueries({
    predicate: (query) => {
      const root = query.queryKey[0]
      return typeof root === 'string' ? !FROZEN_KEY_ROOTS.has(root) : true
    },
  })
}

/** Hook form of {@link invalidateLive}, for use inside components. */
export function useInvalidateLive(): () => Promise<void> {
  const client = useQueryClient()
  return () => invalidateLive(client)
}

// ---------------------------------------------------------------------------
// Shell
// ---------------------------------------------------------------------------

/** Every enum vocabulary and label map. Fetched once, cached forever. */
export function useMeta(): UseQueryResult<MetaOut> {
  return useQuery({
    queryKey: queryKeys.meta(),
    queryFn: ({ signal }) => get<MetaOut>('/meta', undefined, signal),
    staleTime: STALE.meta,
    gcTime: STALE.meta,
  })
}

/**
 * The effective configuration — the environment, plus the settings overlay.
 *
 * Mostly environment-only, but `overridable` names the seven keys
 * `PATCH /api/settings` accepts and `origins` says, per key, whether the live
 * value came from `.env` or from a stored override. Read both from here rather
 * than keeping a copy: a stale client-side list offers a control whose write is
 * a 400, or hides one that would have worked.
 */
export function useSettings(): UseQueryResult<SettingsOut> {
  return useQuery({
    queryKey: queryKeys.settings(),
    queryFn: ({ signal }) => get<SettingsOut>('/settings', undefined, signal),
    staleTime: STALE.settings,
  })
}

/**
 * The shell's status payload. `activityLimit` defaults to 0 — the footer shows
 * no feed, and asking for one would make this a different data set from the
 * Activity screen's while looking like the same query.
 */
export function useStatus(activityLimit = 0): UseQueryResult<StatusOut> {
  return useQuery({
    queryKey: queryKeys.status(activityLimit),
    queryFn: ({ signal }) =>
      get<StatusOut>('/status', { activity_limit: activityLimit }, signal),
    refetchInterval: REFETCH.status,
  })
}

/** The five sidebar badges. */
export function useNavCounts(): UseQueryResult<NavCountsOut> {
  return useQuery({
    queryKey: queryKeys.navCounts(),
    queryFn: ({ signal }) => get<NavCountsOut>('/nav-counts', undefined, signal),
    refetchInterval: REFETCH.navCounts,
  })
}

/**
 * Standalone banners.
 *
 * **The shell does not use this** — `App.tsx` renders `status.data.banners`,
 * which is the same list inside a payload it already polls. This hook exists
 * for a screen that needs banners *without* the status payload, and using it
 * beside `useStatus` in the same tree would put one data set behind two keys,
 * which is the first thing the polling contract forbids.
 */
export function useBanners(): UseQueryResult<BannerOut[]> {
  return useQuery({
    queryKey: queryKeys.banners(),
    queryFn: ({ signal }) => get<BannerOut[]>('/banners', undefined, signal),
    refetchInterval: REFETCH.status,
  })
}

/**
 * The compact counters.
 *
 * One key, two clocks: the library index polls this every 30s and the queue
 * pane every 5s. That is the same *data set* observed at two rates, which is
 * exactly what the one-key rule allows — what it forbids is two keys holding
 * the same numbers, because then they can disagree.
 */
export function useStats(
  refetchInterval: number | false = REFETCH.stats,
): UseQueryResult<StatsOut> {
  return useQuery({
    queryKey: queryKeys.stats(),
    queryFn: ({ signal }) => get<StatsOut>('/stats', undefined, signal),
    refetchInterval,
  })
}

/** The unprefixed liveness probe. */
export function useHealth(): UseQueryResult<HealthOut> {
  return useQuery({
    queryKey: queryKeys.health(),
    queryFn: ({ signal }) => getHealth<HealthOut>(signal),
    refetchInterval: REFETCH.status,
  })
}

/**
 * Every subsystem's payload for the Health overview. The sub-screens keep
 * polling their own endpoints; this is the overview, and its numbers are those
 * endpoints' numbers because the server builds them with the same calls.
 */
export function useHealthSummary(activityLimit = 0): UseQueryResult<HealthSummaryOut> {
  return useQuery({
    queryKey: queryKeys.healthSummary(activityLimit),
    queryFn: ({ signal }) =>
      get<HealthSummaryOut>(
        '/health/summary',
        { activity_limit: activityLimit },
        signal,
      ),
    refetchInterval: REFETCH.healthSummary,
  })
}

// ---------------------------------------------------------------------------
// Artists
// ---------------------------------------------------------------------------

export interface ArtistFilters {
  q?: string
  /** Tri-state: `true`, `false`, or omitted for "both". Never `null`. */
  monitored?: boolean
  sort?: 'name' | 'added_at' | 'last_checked_at' | 'albums_count'
  order?: SortOrder
  limit?: number
  offset?: number
}

/** The artists index. Not polled — it refetches on mutation only. */
export function useArtists(filters: ArtistFilters = {}): UseQueryResult<ArtistListOut> {
  const params = cleanParams({ limit: LIMITS.artists, ...filters })
  return useQuery({
    queryKey: queryKeys.artists(params),
    queryFn: ({ signal }) => get<ArtistListOut>('/artists', params, signal),
  })
}

/** One artist plus their releases. */
export function useArtist(
  artistId: string,
  albumLimit?: number,
): UseQueryResult<ArtistDetailOut> {
  return useQuery({
    queryKey: queryKeys.artist(artistId, albumLimit),
    queryFn: ({ signal }) =>
      get<ArtistDetailOut>(
        `/artists/${encodeURIComponent(artistId)}`,
        cleanParams({ album_limit: albumLimit }),
        signal,
      ),
    enabled: Boolean(artistId),
  })
}

/** The artist header's numbers, with a count for **every** album status. */
export function useArtistStats(artistId: string): UseQueryResult<ArtistStatsOut> {
  return useQuery({
    queryKey: queryKeys.artistStats(artistId),
    queryFn: ({ signal }) =>
      get<ArtistStatsOut>(
        `/artists/${encodeURIComponent(artistId)}/stats`,
        undefined,
        signal,
      ),
    enabled: Boolean(artistId),
    refetchInterval: REFETCH.rows,
  })
}

export interface AlbumFilters {
  q?: string
  /** The **wire** name is `status`; the Python parameter is `album_status`. */
  status?: AlbumStatus
  release_type?: string
  limit?: number
  offset?: number
}

/** One artist's releases, filtered. Filters are part of the key. */
export function useArtistAlbums(
  artistId: string,
  filters: AlbumFilters = {},
): UseQueryResult<AlbumListOut> {
  const params = cleanParams({ limit: LIMITS.artistAlbums, ...filters })
  return useQuery({
    queryKey: queryKeys.artistAlbums(artistId, params),
    queryFn: ({ signal }) =>
      get<AlbumListOut>(
        `/artists/${encodeURIComponent(artistId)}/albums`,
        params,
        signal,
      ),
    enabled: Boolean(artistId),
    refetchInterval: REFETCH.rows,
  })
}

// ---------------------------------------------------------------------------
// Releases
// ---------------------------------------------------------------------------

export interface WantedFilters {
  q?: string
  status?: AlbumStatus
  /**
   * **Omitting this is not "no filter" here.** `GET /api/wanted` declares
   * `monitored: bool | None = True`, so an absent parameter means *monitored
   * only* — which is right, because the backlog is `(WANTED | FAILED) AND
   * monitored` (spec §6.9), but it is the one endpoint in the API where the
   * default is a filter rather than its absence. Send `false` for the "ignored
   * only" view.
   *
   * `''` is the third state and the only way to ask for **both**: the parameter
   * carries `EmptyAsNone`, so an explicitly empty `?monitored=` is `None` on
   * the server and drops the clause, whereas *omitting* it re-applies the
   * `True` default. Empty string and absence are identical on every other
   * filter in the API and are opposites here; that is the endpoint's shape, not
   * a client convention, so it is spelled out in the type rather than left to a
   * caller to rediscover. Never `null` — that is a 422 (spec §6.8).
   */
  monitored?: boolean | ''
  limit?: number
  offset?: number
}

/**
 * The backlog. Read `queueable_total`, **not** `total`, to label *Download
 * all*: the bulk action queues the whole monitored backlog and ignores these
 * filters, so a button labelled from `total` promises one download and delivers
 * however many there really are.
 */
export function useWanted(filters: WantedFilters = {}): UseQueryResult<WantedListOut> {
  const params = cleanParams({ limit: LIMITS.wanted, ...filters })
  return useQuery({
    queryKey: queryKeys.wanted(params),
    queryFn: ({ signal }) => get<WantedListOut>('/wanted', params, signal),
    refetchInterval: REFETCH.rows,
  })
}

export interface RecentReleaseFilters {
  /**
   * Filters on the **album's** monitor flag. Unlike `useWanted`, omitting this
   * really is "no filter": `GET /api/releases/recent` declares
   * `monitored: bool | None = None`, so the radar shows ignored releases too.
   * That is deliberate — a release somebody switched off is still a release
   * that came out, and hiding it hides the evidence it was switched off.
   */
  monitored?: boolean
  limit?: number
  offset?: number
}

/**
 * Releases ordered by **release date**, newest first — what the radar is about.
 *
 * Deliberately not `useQueue()`. The radar drew the download queue under this
 * heading until somebody noticed the top row was whatever finished downloading
 * most recently, which is an ordering on *this program's activity* and puts a
 * 1975 remaster fetched this morning above a record that came out last week.
 * Rows dated in the future sort first and are not filtered out: that is the
 * "upcoming" half of the heading.
 *
 * Polled at `REFETCH.rows` rather than `REFETCH.queue`: a release date changes
 * when the indexer next visits the artist, not while you watch it.
 */
export function useRecentReleases(
  filters: RecentReleaseFilters = {},
): UseQueryResult<AlbumListOut> {
  const params = cleanParams({ limit: LIMITS.radar, ...filters })
  return useQuery({
    queryKey: queryKeys.recentReleases(params),
    queryFn: ({ signal }) => get<AlbumListOut>('/releases/recent', params, signal),
    refetchInterval: REFETCH.rows,
  })
}

/** One release. Used by mutation refetches; the screen wants `useAlbumDetail`. */
export function useAlbum(albumId: string): UseQueryResult<AlbumOut> {
  return useQuery({
    queryKey: queryKeys.album(albumId),
    queryFn: ({ signal }) =>
      get<AlbumOut>(`/albums/${encodeURIComponent(albumId)}`, undefined, signal),
    enabled: Boolean(albumId),
  })
}

/**
 * One release with its editions and vote record. Deliberately **not** polled —
 * nothing on this screen moves on its own.
 */
export function useAlbumDetail(albumId: string): UseQueryResult<AlbumDetailOut> {
  return useQuery({
    queryKey: queryKeys.albumDetail(albumId),
    queryFn: ({ signal }) =>
      get<AlbumDetailOut>(
        `/albums/${encodeURIComponent(albumId)}/detail`,
        undefined,
        signal,
      ),
    enabled: Boolean(albumId),
  })
}

/**
 * The per-field vote record on its own.
 *
 * `AlbumDetailOut` already carries `consensus`, so the release screen reads it
 * from there. Asking for both in one tree is one data set behind two keys —
 * this is for a caller that wants the votes and nothing else.
 */
export function useAlbumConsensus(
  albumId: string,
): UseQueryResult<Record<string, ConsensusFieldOut>> {
  return useQuery({
    queryKey: queryKeys.albumConsensus(albumId),
    queryFn: ({ signal }) =>
      get<Record<string, ConsensusFieldOut>>(
        `/albums/${encodeURIComponent(albumId)}/consensus`,
        undefined,
        signal,
      ),
    enabled: Boolean(albumId),
  })
}

/**
 * Every edition of one record.
 *
 * `key` may be an MBID **or** a normalised title — the server accepts both and
 * resolves through its own asymmetric keying rule. Never group editions by
 * comparing keys in TypeScript: only albums on disk are enriched, so the copy
 * you own carries an MBID and its catalogue-only twins never will, and a naive
 * match files them apart permanently.
 */
export function useReleaseGroup(
  key: string,
  artistId?: string | null,
): UseQueryResult<ReleaseGroupOut> {
  return useQuery({
    queryKey: queryKeys.releaseGroup(key, artistId),
    queryFn: ({ signal }) =>
      get<ReleaseGroupOut>(
        `/release-groups/${encodeURIComponent(key)}`,
        cleanParams({ artist_id: artistId }),
        signal,
      ),
    enabled: Boolean(key),
  })
}

// ---------------------------------------------------------------------------
// Queue and activity
// ---------------------------------------------------------------------------

export interface QueueFilters {
  state?: QueueState
  limit?: number
  offset?: number
}

/** The download queue — the fastest region in the app. */
export function useQueue(filters: QueueFilters = {}): UseQueryResult<QueueListOut> {
  const params = cleanParams({ limit: LIMITS.queue, ...filters })
  return useQuery({
    queryKey: queryKeys.queue(params),
    queryFn: ({ signal }) => get<QueueListOut>('/queue', params, signal),
    refetchInterval: REFETCH.queue,
  })
}

export interface ActivityFilters {
  level?: ActivityLevel
  event?: string
  artist_id?: string
  album_id?: string
  limit?: number
  offset?: number
}

/** One queue item, for a row that wants to re-read itself after a retry. */
export function useQueueItem(itemId: number | null): UseQueryResult<QueueItemOut> {
  return useQuery({
    queryKey: queryKeys.queueItem(itemId ?? 0),
    queryFn: ({ signal }) => get<QueueItemOut>(`/queue/${itemId ?? 0}`, undefined, signal),
    enabled: itemId !== null,
  })
}

/** The history feed. Filters are part of the key. */
export function useActivity(
  filters: ActivityFilters = {},
  refetchInterval: number | false = REFETCH.rows,
): UseQueryResult<ActivityListOut> {
  const params = cleanParams({ limit: LIMITS.activity, ...filters })
  return useQuery({
    queryKey: queryKeys.activity(params),
    queryFn: ({ signal }) => get<ActivityListOut>('/activity', params, signal),
    refetchInterval,
  })
}

// ---------------------------------------------------------------------------
// Search
// ---------------------------------------------------------------------------

export interface SearchFilters {
  q: string
  type?: 'all' | 'artists' | 'albums'
  limit?: number
  offset?: number
}

/**
 * Catalogue search. Debouncing belongs to the input (450ms); this hook only
 * refuses to run on an empty query, so an empty box makes no request.
 *
 * 502/503 from here must render **inside the panel that asked**, never as a
 * global error boundary — that is what "degrades gracefully without
 * credentials" means, and the 503's message names the missing env vars.
 */
export function useSearch(filters: SearchFilters): UseQueryResult<SearchResultOut> {
  const params = cleanParams({ limit: LIMITS.search, ...filters })
  return useQuery({
    queryKey: queryKeys.search(params),
    queryFn: ({ signal }) => get<SearchResultOut>('/search', params, signal),
    enabled: Boolean(filters.q.trim()),
  })
}

// ---------------------------------------------------------------------------
// Library: scan, import, trash
// ---------------------------------------------------------------------------

/**
 * The **stored** last scan. A dry run's report never belongs in this cache —
 * it would masquerade as the last applied run. Hold it in component state.
 */
export function useLibraryScanStatus(): UseQueryResult<LibraryScanStatusOut> {
  return useQuery({
    queryKey: queryKeys.libraryScan(),
    queryFn: ({ signal }) =>
      get<LibraryScanStatusOut>('/library/scan', undefined, signal),
  })
}

/**
 * Import progress. Polls **only while an import is running** — an idle Health
 * screen issues zero requests, which is the whole point of the conditional
 * interval.
 */
export function useLibraryImport(): UseQueryResult<LibraryImportOut> {
  return useQuery({
    queryKey: queryKeys.libraryImport(),
    queryFn: ({ signal }) => get<LibraryImportOut>('/library/import', undefined, signal),
    refetchInterval: (query) =>
      query.state.data?.running ? REFETCH.importProgress : false,
  })
}

/** Who an import would look up, and roughly how long it would take. */
export function useLibraryImportPreview(
  enabled = true,
): UseQueryResult<LibraryImportPreviewOut> {
  return useQuery({
    queryKey: queryKeys.libraryImportPreview(),
    queryFn: ({ signal }) =>
      get<LibraryImportPreviewOut>('/library/import/preview', undefined, signal),
    enabled,
  })
}

/**
 * The library-wide re-file count. **Not a live region.**
 *
 * `enabled` is false until a template is asked about, so an idle Rules screen
 * issues zero requests, and there is deliberately no `REFETCH` entry: the walk
 * hashes every file behind the staleness gate and re-reads the files of every
 * adopted release, which is minutes on a real library. The candidate template
 * is part of the key, so pressing again for a template already counted is
 * served from cache. It is a GET and invalidates nothing.
 */
export function useRefileEstimate(
  template: string | null,
): UseQueryResult<RefileEstimateOut> {
  return useQuery({
    queryKey: queryKeys.refileEstimate(template ?? ''),
    queryFn: ({ signal }) =>
      get<RefileEstimateOut>(
        '/library/refile/estimate',
        cleanParams({ template }),
        signal,
      ),
    enabled: template !== null,
    staleTime: Infinity,
    refetchOnWindowFocus: false,
  })
}

/** Everything currently recoverable. Not polled. */
export function useTrash(): UseQueryResult<TrashOut> {
  return useQuery({
    queryKey: queryKeys.trash(),
    queryFn: ({ signal }) => get<TrashOut>('/library/trash', undefined, signal),
  })
}

// ---------------------------------------------------------------------------
// Enrichment
// ---------------------------------------------------------------------------

/** Coverage and health. Show both scope numbers, never a bare percentage. */
export function useEnrichmentStatus(): UseQueryResult<EnrichmentStatusOut> {
  return useQuery({
    queryKey: queryKeys.enrichment(),
    queryFn: ({ signal }) => get<EnrichmentStatusOut>('/enrichment', undefined, signal),
    refetchInterval: REFETCH.enrichment,
  })
}

export interface EnrichmentReviewFilters {
  state?: string
  source?: string
  limit?: number
  offset?: number
}

/** What needs a human. Holds exactly `{ambiguous, no_key}` server-side. */
export function useEnrichmentReview(
  filters: EnrichmentReviewFilters = {},
): UseQueryResult<EnrichmentReviewListOut> {
  const params = cleanParams({ limit: LIMITS.enrichmentReview, ...filters })
  return useQuery({
    queryKey: queryKeys.enrichmentReview(params),
    queryFn: ({ signal }) =>
      get<EnrichmentReviewListOut>('/enrichment/review', params, signal),
    refetchInterval: REFETCH.enrichment,
  })
}

/**
 * Candidates for a person to pick from — the **only** search in the enrichment
 * layer, and one nothing may auto-resolve. It is deliberately not polled: the
 * picker holds transient user input and must never sit inside a refreshing
 * region.
 */
export function useEnrichmentCandidates(
  entityType: string,
  entityId: string,
  source: string,
  q?: string,
  enabled = true,
): UseQueryResult<EnrichmentCandidatesOut> {
  const params = cleanParams({ source, q })
  return useQuery({
    queryKey: queryKeys.enrichmentCandidates(entityType, entityId, params),
    queryFn: ({ signal }) =>
      get<EnrichmentCandidatesOut>(
        `/enrichment/${encodeURIComponent(entityType)}/${encodeURIComponent(entityId)}/candidates`,
        params,
        signal,
      ),
    enabled: enabled && Boolean(entityType && entityId && source),
    // The results have to survive a failed identify so the picker can show the
    // error without dropping the user back to an empty search.
    staleTime: 60_000,
  })
}

// ---------------------------------------------------------------------------
// Integrity
// ---------------------------------------------------------------------------

/** Polls only while a pass is running; otherwise on demand. */
export function useIntegrityStatus(): UseQueryResult<IntegrityStatusOut> {
  return useQuery({
    queryKey: queryKeys.integrity(),
    queryFn: ({ signal }) => get<IntegrityStatusOut>('/integrity', undefined, signal),
    refetchInterval: (query) => (query.state.data?.running ? REFETCH.integrity : false),
  })
}

/** Files that will not decode. The only actionable integrity verdict. */
export function useCorruptFiles(
  params: { limit?: number; offset?: number } = {},
): UseQueryResult<CorruptListOut> {
  const query = cleanParams({ limit: LIMITS.corrupt, ...params })
  return useQuery({
    queryKey: queryKeys.integrityCorrupt(query),
    queryFn: ({ signal }) => get<CorruptListOut>('/integrity/corrupt', query, signal),
  })
}

// ---------------------------------------------------------------------------
// Mutations
//
// Every one of them calls `invalidateLive` on success and nothing else does.
// Three — and only three — of these can enqueue a download: `useQueueAlbum`,
// `useDownloadArtistWanted`, `useDownloadAllWanted`. Following, indexing,
// scanning, monitoring, bulk-editing, enriching, re-filing, re-tagging and
// quarantining queue nothing, and no new mutation may quietly join the list.
// ---------------------------------------------------------------------------

type Mutation<TVars, TData = MessageOut> = UseMutationResult<TData, Error, TVars>

/** Wire a mutation up to the one invalidation helper. */
function useLiveMutation<TVars, TData>(
  mutationFn: (vars: TVars) => Promise<TData>,
): UseMutationResult<TData, Error, TVars> {
  const client = useQueryClient()
  return useMutation<TData, Error, TVars>({
    mutationFn,
    onSuccess: () => invalidateLive(client),
  })
}

/** Follow an artist. Idempotent — read `created` for which sentence to show. */
export function useFollowArtist(): Mutation<ArtistCreateIn, ArtistFollowOut> {
  return useLiveMutation((payload: ArtistCreateIn) =>
    post<ArtistFollowOut>('/artists', payload),
  )
}

/**
 * Update one artist. The single-artist form is **not** tri-state: an unchecked
 * `monitored` box genuinely means `false`, and `accepted_release_types` is
 * always sent as a list. Omission still means "leave alone".
 */
export function useUpdateArtist(): Mutation<
  { artistId: string; payload: ArtistUpdateIn },
  ArtistOut
> {
  return useLiveMutation(({ artistId, payload }: { artistId: string; payload: ArtistUpdateIn }) =>
    patch<ArtistOut>(`/artists/${encodeURIComponent(artistId)}`, payload),
  )
}

/**
 * Which credited people share this Qobuz artist id — `GET /api/artists/{id}/credits`.
 *
 * Makes no upstream request: it reports what has already been read. `enabled`
 * is what keeps it that way — the drawer mounts the query, the closed artist
 * page does not. `analysed < total` means the catalogue is only partly read,
 * which is a job for `useAnalyseArtistCredits`, not a reason to refetch.
 */
export function useArtistCredits(
  artistId: string,
  enabled = true,
): UseQueryResult<ArtistCreditsOut> {
  return useQuery({
    queryKey: queryKeys.artistCredits(artistId),
    queryFn: () => get<ArtistCreditsOut>(`/artists/${encodeURIComponent(artistId)}/credits`),
    enabled: enabled && artistId !== '',
  })
}

/**
 * Read credits off releases nobody has looked at yet — one `album/get` each.
 *
 * Bounded per press by the server, so the caller repeats while
 * `analysed < total`. It is a **mutation** and not a polled query even though
 * it only reads: it spends the Qobuz request budget, so it must happen when
 * somebody presses a button and at no other time.
 */
export function useAnalyseArtistCredits(): Mutation<
  { artistId: string; limit?: number },
  ArtistCreditsOut
> {
  return useLiveMutation(({ artistId, limit }: { artistId: string; limit?: number }) =>
    post<ArtistCreditsOut>(
      `/artists/${encodeURIComponent(artistId)}/credits`,
      undefined,
      limit === undefined ? undefined : { limit },
    ),
  )
}

/**
 * Accept only releases credited to these people —
 * `PUT /api/artists/{id}/credit-filter`.
 *
 * `PUT`, so the list replaces whatever was there; an **empty list clears the
 * filter** rather than rejecting everything. It re-derives the status of every
 * release the artist has, so the counts come back on `detail` and the caller
 * must say them — narrowing a conflated artist moved 37 releases out of one
 * real backlog, and nothing else would have reported it.
 */
export function useSetArtistCreditFilter(): Mutation<
  { artistId: string; credits: string[] },
  MessageOut
> {
  return useLiveMutation(({ artistId, credits }: { artistId: string; credits: string[] }) =>
    put<MessageOut>(`/artists/${encodeURIComponent(artistId)}/credit-filter`, { credits }),
  )
}

/**
 * Bulk edit. **Tri-state**: a `<select>` left on "no change" must be *omitted*,
 * never sent as `false` — that would unmonitor every artist in the selection.
 * Clear the selection on success (the old `qobuzarr:selection` event).
 */
export function useBulkUpdateArtists(): Mutation<ArtistBulkUpdateIn> {
  return useLiveMutation((payload: ArtistBulkUpdateIn) =>
    post<MessageOut>('/artists/bulk', payload),
  )
}

/**
 * Edit the identity that names an artist's folders and tags their files —
 * `PATCH /api/artists/{id}/tags`.
 *
 * **Not** `useUpdateArtist`. That one writes monitoring settings, which change
 * what Qobuzarr does; this one writes the values that change what is on disk,
 * which is why recording them and applying them are two presses (see
 * `useRetagArtist`). Omission still means *leave alone*, and the server
 * **forbids unknown keys** — a 422 rather than a silent discard. `aliases` is
 * now one of the known ones: it has a column on `artists`, and `[]` clears it.
 */
export function useUpdateArtistTags(): Mutation<
  { artistId: string; payload: ArtistTagsIn },
  ArtistOut
> {
  return useLiveMutation(
    ({ artistId, payload }: { artistId: string; payload: ArtistTagsIn }) =>
      patch<ArtistOut>(`/artists/${encodeURIComponent(artistId)}/tags`, payload),
  )
}

/**
 * Write one artist's stored identity into every release of theirs on disk.
 *
 * The apply half of the pair above, and a library write — so it goes through
 * `app/core/librarian.py` like every other one, and it queues nothing. `refile`
 * is **opt-in** and separate on purpose: a tag fix and a thousand-file move are
 * different-sized decisions, and the server refuses any release whose path is
 * frozen rather than moving it anyway.
 *
 * Read the counts off `LibraryTidyOut` and report all of them — `changed`,
 * `moved` and `described` are counted apart because one request can do all
 * three, and adding them would report a release twice.
 */
export function useRetagArtist(): Mutation<
  { artistId: string; refile?: boolean; limit?: number },
  LibraryTidyOut
> {
  return useLiveMutation(
    ({ artistId, refile, limit }: { artistId: string; refile?: boolean; limit?: number }) =>
      post<LibraryTidyOut>(
        `/artists/${encodeURIComponent(artistId)}/retag`,
        undefined,
        cleanParams({ refile, limit }),
      ),
  )
}

export function useDeleteArtist(): Mutation<string> {
  return useLiveMutation((artistId: string) =>
    del<MessageOut>(`/artists/${encodeURIComponent(artistId)}`),
  )
}

export function useScanArtist(): Mutation<string> {
  return useLiveMutation((artistId: string) =>
    post<MessageOut>(`/artists/${encodeURIComponent(artistId)}/scan`),
  )
}

export function useScanAll(): Mutation<void> {
  return useLiveMutation(() => post<MessageOut>('/scan-all'))
}

/** One of the three endpoints allowed to enqueue. */
export function useDownloadArtistWanted(): Mutation<string> {
  return useLiveMutation((artistId: string) =>
    post<MessageOut>(`/artists/${encodeURIComponent(artistId)}/download-wanted`),
  )
}

/** One of the three endpoints allowed to enqueue. Capped at 500 per press. */
export function useDownloadAllWanted(): Mutation<{ limit?: number } | void> {
  return useLiveMutation((vars: { limit?: number } | void) =>
    post<MessageOut>('/wanted/download', undefined, cleanParams({ limit: vars?.limit })),
  )
}

/**
 * One of the three endpoints allowed to enqueue.
 *
 * The response's `detail.upgrade` is what distinguishes "Queued an upgrade of X"
 * from "Queued X" — it cannot be re-derived afterwards, because an album being
 * upgraded keeps `status = downloaded` for the whole download.
 */
export function useQueueAlbum(): Mutation<string> {
  return useLiveMutation((albumId: string) =>
    post<MessageOut>(`/albums/${encodeURIComponent(albumId)}/queue`),
  )
}

/**
 * Monitor or ignore a release.
 *
 * **Sending no body toggles.** A body sets an explicit value, and that is the
 * only way to set one — a filter called `monitored` in a query string is a
 * *filter*, and binding it as the new value is how the backlog page's "ignored
 * only" filter once unmonitored whatever row was pressed. Unmonitoring a
 * `wanted` release collapses it to `skipped`, so the row leaves the backlog and
 * the toast is the only thing that explains it.
 */
export function useMonitorAlbum(): Mutation<
  { albumId: string; payload?: AlbumUpdateIn },
  AlbumOut
> {
  return useLiveMutation(
    ({ albumId, payload }: { albumId: string; payload?: AlbumUpdateIn }) =>
      post<AlbumOut>(`/albums/${encodeURIComponent(albumId)}/monitor`, payload),
  )
}

/**
 * Partial update of one release — `PATCH /api/albums/{id}`.
 *
 * The three per-release switches (`pin_tags`, `freeze_path`, `mute_integrity`)
 * are set here and **only** here: `POST /albums/{id}/monitor` refuses a body
 * carrying any of them, because that route's empty body toggles `monitored` and
 * a route with two meanings for one payload is how the backlog's "ignored only"
 * filter once unmonitored whatever row was pressed.
 *
 * **Send one key per press.** Omitted means *leave alone*, so a component that
 * serialised all three switches on every toggle would write the two nobody
 * touched — which is only invisible until two tabs are open on one release.
 */
export function useUpdateAlbum(): Mutation<
  { albumId: string; payload: AlbumUpdateIn },
  AlbumOut
> {
  return useLiveMutation(
    ({ albumId, payload }: { albumId: string; payload: AlbumUpdateIn }) =>
      patch<AlbumOut>(`/albums/${encodeURIComponent(albumId)}`, payload),
  )
}

/** Move a release's files to the trash. Reversible; say so in the confirm. */
export function useDeleteAlbumFiles(): Mutation<string> {
  return useLiveMutation((albumId: string) =>
    del<MessageOut>(`/albums/${encodeURIComponent(albumId)}/files`),
  )
}

export function useRefileAlbum(): Mutation<
  { albumId: string; dryRun?: boolean },
  RefilePlanOut
> {
  return useLiveMutation(({ albumId, dryRun }: { albumId: string; dryRun?: boolean }) =>
    post<RefilePlanOut>(
      `/albums/${encodeURIComponent(albumId)}/refile`,
      undefined,
      cleanParams({ dry_run: dryRun }),
    ),
  )
}

export function useRetagAlbum(): Mutation<string> {
  return useLiveMutation((albumId: string) =>
    post<MessageOut>(`/albums/${encodeURIComponent(albumId)}/retag`),
  )
}

/**
 * Fingerprint one release again and re-identify it.
 *
 * The design labels the button "Re-fingerprint". Nothing stores a fingerprint
 * per release and `fpcalc` runs inside the AcoustID rung, so what the press
 * actually buys is a fresh identification — every album enrichment rung
 * re-opened and the cascade drained while somebody watches. The button says
 * that. No `queryKeys` entry: this is a mutation, not a data set, and
 * `useLiveMutation` is what makes the drawer, the release row and the nav
 * badges catch up from their own keys.
 */
export function useReidentifyAlbum(): Mutation<string> {
  return useLiveMutation((albumId: string) =>
    post<MessageOut>(`/albums/${encodeURIComponent(albumId)}/reidentify`),
  )
}

export function useWriteAlbumNfo(): Mutation<string> {
  return useLiveMutation((albumId: string) =>
    post<MessageOut>(`/albums/${encodeURIComponent(albumId)}/nfo`),
  )
}

export function useRetryQueueItem(): Mutation<number> {
  return useLiveMutation((itemId: number) =>
    post<MessageOut>(`/queue/${itemId}/retry`),
  )
}

export function useCancelQueueItem(): Mutation<number> {
  return useLiveMutation((itemId: number) => del<MessageOut>(`/queue/${itemId}`))
}

/**
 * Run a disk scan. The returned report is the caller's to render from
 * component state — **do not** write a dry run into the `libraryScan` cache,
 * where it would masquerade as the last applied run.
 */
export function useLibraryScan(): Mutation<
  { artistId?: string; dryRun?: boolean } | void,
  LibraryScanOut
> {
  return useLiveMutation((vars: { artistId?: string; dryRun?: boolean } | void) =>
    post<LibraryScanOut>(
      '/library/scan',
      undefined,
      cleanParams({ artist_id: vars?.artistId, dry_run: vars?.dryRun }),
    ),
  )
}

/**
 * Scan one artist's folder. `POST /api/artists/{id}/library-scan` — a real,
 * shipped endpoint that had no hook, so the only way to reach it was
 * `useLibraryScan({ artistId })`. The two are **not** the same request: this one
 * takes the id in the path, and the artist screen's own Scan button is the
 * caller. Same report shape, same rule that a dry run's report never enters the
 * `libraryScan` cache.
 */
export function useArtistLibraryScan(): Mutation<
  { artistId: string; dryRun?: boolean },
  LibraryScanOut
> {
  return useLiveMutation(({ artistId, dryRun }: { artistId: string; dryRun?: boolean }) =>
    post<LibraryScanOut>(
      `/artists/${encodeURIComponent(artistId)}/library-scan`,
      undefined,
      cleanParams({ dry_run: dryRun }),
    ),
  )
}

/** Re-file. `apply: false` is a preview and moves nothing. */
export function useRefileLibrary(): Mutation<
  { artistId?: string; apply?: boolean; limit?: number } | void,
  LibraryTidyOut
> {
  return useLiveMutation(
    (vars: { artistId?: string; apply?: boolean; limit?: number } | void) =>
      post<LibraryTidyOut>(
        '/library/refile',
        undefined,
        cleanParams({
          artist_id: vars?.artistId,
          apply: vars?.apply,
          limit: vars?.limit,
        }),
      ),
  )
}

export function useRetagLibrary(): Mutation<
  { artistId?: string; limit?: number } | void,
  LibraryTidyOut
> {
  return useLiveMutation((vars: { artistId?: string; limit?: number } | void) =>
    post<LibraryTidyOut>(
      '/library/retag',
      undefined,
      cleanParams({ artist_id: vars?.artistId, limit: vars?.limit }),
    ),
  )
}

export function useWriteLibraryNfo(): Mutation<
  { artistId?: string; limit?: number } | void,
  LibraryTidyOut
> {
  return useLiveMutation((vars: { artistId?: string; limit?: number } | void) =>
    post<LibraryTidyOut>(
      '/library/nfo',
      undefined,
      cleanParams({ artist_id: vars?.artistId, limit: vars?.limit }),
    ),
  )
}

export function useStartLibraryImport(): Mutation<
  LibraryImportStartIn,
  LibraryImportOut
> {
  return useLiveMutation((payload: LibraryImportStartIn) =>
    post<LibraryImportOut>('/library/import', payload),
  )
}

export function useCancelLibraryImport(): Mutation<void> {
  return useLiveMutation(() => post<MessageOut>('/library/import/cancel'))
}

export function useRestoreTrash(): Mutation<string> {
  return useLiveMutation((entryId: string) =>
    post<MessageOut>(`/library/trash/${encodeURIComponent(entryId)}/restore`),
  )
}

/**
 * Destroy trashed files. The one irreversible action in the application — and
 * the only `danger-strong` button.
 */
export function useEmptyTrash(): Mutation<{ entryId?: string } | void> {
  return useLiveMutation((vars: { entryId?: string } | void) =>
    del<MessageOut>('/library/trash', cleanParams({ entry_id: vars?.entryId })),
  )
}

/**
 * Move unplayable files to the trash. The tracks go back to `pending` and their
 * albums to `wanted`, and **nothing is queued** — filling the gap stays an
 * explicit user action.
 */
export function useQuarantineCorrupt(): Mutation<{ limit?: number } | void> {
  return useLiveMutation((vars: { limit?: number } | void) =>
    post<MessageOut>('/library/quarantine', undefined, cleanParams({ limit: vars?.limit })),
  )
}

export function useRunEnrichment(): Mutation<{ limit?: number } | void> {
  return useLiveMutation((vars: { limit?: number } | void) =>
    post<MessageOut>('/enrichment/run', undefined, cleanParams({ limit: vars?.limit })),
  )
}

/**
 * A person saying "it is this one".
 *
 * A manual match is never overwritten by a later automatic pass, and only
 * `identifiable_sources` accept one — publish that list, never re-derive it.
 * A 400 keeps the picker's results in place so the user is not dropped back to
 * an empty search.
 */
export function useIdentifyEntity(): Mutation<{
  entityType: string
  entityId: string
  payload: EnrichmentIdentifyIn
}> {
  return useLiveMutation(
    ({
      entityType,
      entityId,
      payload,
    }: {
      entityType: string
      entityId: string
      payload: EnrichmentIdentifyIn
    }) =>
      post<MessageOut>(
        `/enrichment/${encodeURIComponent(entityType)}/${encodeURIComponent(entityId)}/identify`,
        payload,
      ),
  )
}

/**
 * A person saying "there is no answer here" about one work item.
 *
 * `source` is required and unguessable from the URL: a work item is one
 * *(entity, source)* pair, so rejecting MusicBrainz for a release says nothing
 * about Deezer, and a body that omitted it would have to mean *all* of them —
 * a much larger press than the button that sends it.
 *
 * A rejection is the one state nothing re-arms, which is what makes it a
 * decision rather than a delay. It is still reversible, by
 * `POST .../reopen` — so a toast that says so is not decoration.
 */
export function useRejectEnrichment(): Mutation<{
  entityType: string
  entityId: string
  payload: EnrichmentRejectIn
}> {
  return useLiveMutation(
    ({
      entityType,
      entityId,
      payload,
    }: {
      entityType: string
      entityId: string
      payload: EnrichmentRejectIn
    }) =>
      post<MessageOut>(
        `/enrichment/${encodeURIComponent(entityType)}/${encodeURIComponent(entityId)}/reject`,
        payload,
      ),
  )
}

/**
 * Run this entity again, whatever it last answered — `POST .../reopen`.
 *
 * The **only** route back from a rejection, and therefore the thing that makes
 * Reject safe to press without a confirmation. Nothing re-arms a rejected row
 * (`_rearm_stranded` skips it by design: it is waiting on nobody), so the
 * review list has to be asked for them explicitly — `state=rejected` — and this
 * is the press on the row that follows.
 *
 * `source` narrows it to one rung; omitted, it re-opens every rung of the
 * entity.
 */
export function useReopenEnrichment(): Mutation<{
  entityType: string
  entityId: string
  source?: string
}> {
  return useLiveMutation(
    ({
      entityType,
      entityId,
      source,
    }: {
      entityType: string
      entityId: string
      source?: string
    }) =>
      post<MessageOut>(
        `/enrichment/${encodeURIComponent(entityType)}/${encodeURIComponent(entityId)}/reopen`,
        undefined,
        cleanParams({ source }),
      ),
  )
}

/**
 * Apply the proposal this entity is already holding — and nothing else.
 *
 * **It takes no body, because there is nothing to choose.** Accept applies what
 * the sources already agreed on (`EnrichmentReviewOut.suggested_release_type`);
 * it never searches an upstream and takes the top hit, which is the rule that
 * keeps a wrong MusicBrainz id out of every file on disk.
 *
 * `applied: false` is therefore a **success**, not a failure: nothing was held,
 * and `identify_sources` names the pickers to open instead. Label the button
 * from `suggested_release_type` before the press rather than explaining the
 * outcome after it — a button that says "Accept & apply" and opens a search box
 * has told the user the wrong thing.
 */
export function useAcceptEnrichment(): Mutation<
  { entityType: string; entityId: string },
  EnrichmentAcceptOut
> {
  return useLiveMutation(({ entityType, entityId }: { entityType: string; entityId: string }) =>
    post<EnrichmentAcceptOut>(
      `/enrichment/${encodeURIComponent(entityType)}/${encodeURIComponent(entityId)}/accept`,
    ),
  )
}

/**
 * Write the settings overlay — `PATCH /api/settings`.
 *
 * Two verbs in one request: `values` stores an override, `reset` deletes the
 * row so the key returns to whatever `.env` says. A key in neither is left
 * alone, and a key outside `SettingsOut.overridable` is a 400 rather than a
 * silent no-op — which is why a caller must take that list from the payload
 * instead of keeping its own.
 *
 * **It writes the response into the settings cache rather than invalidating.**
 * `settings` is one of the two frozen key roots `invalidateLive` skips, and the
 * endpoint returns the whole of `SettingsOut` — the new value, the new
 * `naming_preview`, the new `origins` and the new `pending` — so `setQueryData`
 * is exact where a refetch would be a second round trip for the same bytes.
 * Everything else still invalidates: `naming_template` changes what a re-file
 * would do, and `enrichment_sources` changes the ladder.
 */
export function useUpdateSettings(): UseMutationResult<SettingsOut, Error, SettingsUpdateIn> {
  const client = useQueryClient()
  return useMutation<SettingsOut, Error, SettingsUpdateIn>({
    mutationFn: (payload: SettingsUpdateIn) => patch<SettingsOut>('/settings', payload),
    onSuccess: async (data) => {
      client.setQueryData(queryKeys.settings(), data)
      await invalidateLive(client)
    },
  })
}

/**
 * Hash files and compare them with their baseline. `unknown_only` is "baseline
 * the library"; `apply: false` measures and writes nothing. It queues nothing
 * and changes no album status.
 */
export function useVerifyIntegrity(): Mutation<
  { limit?: number; unknownOnly?: boolean; apply?: boolean } | void,
  IntegrityRunOut
> {
  return useLiveMutation(
    (vars: { limit?: number; unknownOnly?: boolean; apply?: boolean } | void) =>
      post<IntegrityRunOut>(
        '/integrity/verify',
        undefined,
        cleanParams({
          limit: vars?.limit,
          unknown_only: vars?.unknownOnly,
          apply: vars?.apply,
        }),
      ),
  )
}

