/**
 * Missing releases — the whole backlog, on its own address.
 *
 * The dashboard has drawn the head of this list since the rebuild (six rows,
 * under `LIMITS.dashboardWanted`) and there was nowhere to see the rest of it.
 * On a real library that is not a rounding error: the backlog is every release
 * of every followed artist that is not on disk, and it reaches four figures.
 * This screen is the list; the dashboard block keeps its six rows and now links
 * here.
 *
 * ── it is the same query as the dashboard's, at a different limit ─────────
 *
 * Which is allowed, and is the one shape the one-query-key rule permits: the
 * two limits are `LIMITS.wanted` and `LIMITS.dashboardWanted`, both exported,
 * both part of their own key, and neither component is fetching a *narrowed
 * copy of the other's data* — the dashboard's six rows are a deliberate,
 * separately-keyed digest. What would break the rule is this screen and its own
 * refresh disagreeing, and they cannot: `useWanted` polls its own key.
 *
 * ── the button is labelled from `queueable_total`, never from `total` ─────
 *
 * `POST /api/wanted/download` queues every **monitored** `wanted` release and
 * ignores every filter this list was drawn with. So the count in the label is
 * `queueable_total` — a figure the endpoint computes with its own unfiltered
 * query precisely so this label can exist — and it does not move when the chips
 * or the search box do. If it ever moves, it has been computed from the
 * filtered query and the button is lying about how many downloads one press
 * starts.
 *
 * The 500-per-press cap is named in the sub-line rather than hidden. A press
 * that silently queues 500 of 4,693 and reports success is the sort of thing
 * somebody finds out about a week later.
 *
 * ── the four chips are four real parameters ──────────────────────────────
 *
 * `GET /api/wanted` takes `q`, `status` and `monitored`, and the last one is
 * the trap: it declares `monitored: bool | None = True`, so **omitting it is a
 * filter**. *All missing* therefore sends nothing and gets the monitored
 * backlog, which is the right default (an ignored release is not missing, it is
 * declined) and *Ignored* sends `false` to see what was declined. Nothing here
 * sends `null`, which is a 422.
 *
 * ── two empty states, because "no match" is not "nothing missing" ─────────
 *
 * `unfiltered_total` is what tells them apart, and it is the reason the field
 * exists. A single empty state has to be written to cover both and ends up
 * saying neither.
 */

import { useEffect, useState } from 'react'
import { useSearchParams } from 'react-router-dom'

import {
  useDownloadAllWanted,
  useMonitorAlbum,
  useQueueAlbum,
  useWanted,
  type WantedFilters,
} from '@/api/queries'
import {
  Button,
  Chip,
  EmptyState,
  Field,
  PageError,
  PageLoading,
  TextInput,
  useToast,
} from '@/design'
import { MissingRow, toastTone } from '@/widgets'

import styles from '@/screens/missing/Missing.module.css'

/** The four chips that map onto a real parameter. See the note above. */
const FILTERS = [
  { id: 'all', label: 'All missing' },
  { id: 'wanted', label: 'Wanted' },
  { id: 'failed', label: 'Failed' },
  { id: 'ignored', label: 'Ignored' },
] as const

type FilterId = (typeof FILTERS)[number]['id']

function filterFrom(value: string | null): FilterId {
  return value === 'wanted' || value === 'failed' || value === 'ignored' ? value : 'all'
}

/**
 * A chip as query parameters.
 *
 * `monitored` is omitted rather than set to `true` for the three monitored
 * views: the endpoint's own default is `True`, so omitting it and sending it
 * are the same request, and one absent key keeps the query key — and therefore
 * the cache entry — identical to the shape every other caller of `/wanted`
 * uses.
 */
function filtersFor(filter: FilterId): WantedFilters {
  switch (filter) {
    case 'wanted':
      return { status: 'wanted' }
    case 'failed':
      return { status: 'failed' }
    case 'ignored':
      return { monitored: false }
    case 'all':
      return {}
  }
}

export default function Missing() {
  const [params, setParams] = useSearchParams()
  const filter = filterFrom(params.get('filter'))
  const query = params.get('q') ?? ''

  // The box is local so typing is instant; the URL — and therefore the query
  // key — catches up 300 ms later. A debounce is not a poller: it fires once
  // per pause and dies with the component.
  const [typed, setTyped] = useState(query)
  useEffect(() => {
    setTyped(query)
  }, [query])
  useEffect(() => {
    if (typed === query) return
    const id = window.setTimeout(() => {
      setParams(
        (prev) => {
          const next = new URLSearchParams(prev)
          if (typed === '') next.delete('q')
          else next.set('q', typed)
          return next
        },
        { replace: true },
      )
    }, 300)
    return () => window.clearTimeout(id)
  }, [typed, query, setParams])

  const toast = useToast()
  const wanted = useWanted({
    ...filtersFor(filter),
    ...(query === '' ? {} : { q: query }),
  })
  const queueAlbum = useQueueAlbum()
  const monitorAlbum = useMonitorAlbum()
  const downloadAll = useDownloadAllWanted()

  const items = wanted.data?.items ?? []
  const total = wanted.data?.total ?? 0
  const queueable = wanted.data?.queueable_total ?? 0
  // `null` until the first payload lands — never `0`, which would make the
  // "nothing is missing" empty state appear for a moment on every visit.
  const everything = wanted.data?.unfiltered_total ?? null

  const lede =
    wanted.data === undefined
      ? 'Releases by artists you follow that are not on disk.'
      : `${queueable.toLocaleString()} of ${(everything ?? total).toLocaleString()} releases could be downloaded now. ` +
        'Following an artist marks releases wanted; nothing is ever queued for you.'

  function chooseFilter(id: FilterId) {
    setParams((prev) => {
      const next = new URLSearchParams(prev)
      if (id === 'all') next.delete('filter')
      else next.set('filter', id)
      return next
    })
  }

  return (
    <section className={styles.section} aria-labelledby="missing-title">
      <div className={styles.header}>
        <div className={styles.headerMain}>
          <h1 className={styles.title} id="missing-title">
            Missing releases
          </h1>
          <p className={styles.lede}>{lede}</p>
        </div>

        <div className={styles.headerActions}>
          <Button
            variant="primary"
            disabled={queueable === 0}
            loading={downloadAll.isPending}
            onClick={() => {
              downloadAll.mutate(undefined, {
                onSuccess: (message) => toast(message.message, toastTone(message.level)),
                onError: (error: Error) => toast(error.message, 'bad'),
              })
            }}
          >
            {queueable === 0
              ? 'Nothing to download'
              : `Download all (${queueable.toLocaleString()})`}
          </Button>
        </div>
      </div>

      {/* The cap is a fact about the press, so it is stated beside the press
          rather than discovered afterwards in the toast. */}
      {queueable > 500 ? (
        <p className={styles.capNote}>
          One press queues 500 releases — the cap the endpoint applies. Press it again
          for the next 500.
        </p>
      ) : null}

      <div className={styles.filters}>
        {FILTERS.map((entry) => (
          <Chip
            key={entry.id}
            selected={filter === entry.id}
            onClick={() => chooseFilter(entry.id)}
          >
            {entry.label}
          </Chip>
        ))}

        <div className={styles.search}>
          <Field label="Search the backlog">
            {(control) => (
              <TextInput
                {...control}
                type="search"
                value={typed}
                placeholder="Search releases"
                onChange={(event) => setTyped(event.target.value)}
              />
            )}
          </Field>
        </div>

        <div className={styles.filterSpacer} />
        <div className={`${styles.shown} mono`}>{items.length} shown</div>
      </div>

      {wanted.isError ? (
        <PageError
          message={wanted.error.message}
          action={<Button onClick={() => void wanted.refetch()}>Retry</Button>}
        />
      ) : wanted.isLoading ? (
        <PageLoading rows={8} title={false} label="Loading the backlog" />
      ) : items.length === 0 ? (
        <EmptyState
          glyph="◌"
          title={everything === 0 ? 'Nothing is missing' : 'Nothing matches that'}
          note={
            everything === 0
              ? 'Every release of every artist you follow is on disk.'
              : `None of the ${(everything ?? 0).toLocaleString()} releases in the backlog matches this filter.`
          }
        />
      ) : (
        <>
          <div className={styles.list}>
            {items.map((album) => (
              <MissingRow
                key={album.id}
                album={album}
                busy={
                  (queueAlbum.isPending && queueAlbum.variables === album.id) ||
                  (monitorAlbum.isPending && monitorAlbum.variables.albumId === album.id)
                }
                onDownload={() => {
                  queueAlbum.mutate(album.id, {
                    onSuccess: (message) =>
                      toast(message.message, toastTone(message.level)),
                    onError: (error: Error) => toast(error.message, 'bad'),
                  })
                }}
                onIgnore={() => {
                  // No body: the endpoint TOGGLES. A body would be this screen
                  // deciding the new value, which is the bug the empty-body
                  // rule exists to prevent — and this screen has a filter
                  // called `monitored`, which is the exact collision.
                  monitorAlbum.mutate(
                    { albumId: album.id },
                    {
                      onSuccess: (next) =>
                        toast(
                          next.monitored
                            ? `Monitoring ${next.title} again.`
                            : `Ignoring ${next.title} — it leaves the backlog.`,
                          'neutral',
                        ),
                      onError: (error: Error) => toast(error.message, 'bad'),
                    },
                  )
                }}
              />
            ))}
          </div>

          <p className={`${styles.scope} mono`}>
            {items.length.toLocaleString()} of {total.toLocaleString()} matching releases
            · oldest first
          </p>
        </>
      )}
    </section>
  )
}
