/**
 * Download queue — the sequential worker, watched.
 *
 * Until now the queue was visible in three fragments and nowhere whole: the
 * dashboard's *Running now* drew the item in flight, its *Last downloaded*
 * shelf drew the twelve most recent `done` rows, and the status footer drew a
 * sentence. Nothing showed what was **waiting**, and nothing showed a `failed`
 * row at all — the To-do block counted `failed_albums` and sent you to the
 * activity log, which is a history and not a list of things you can press.
 *
 * ── it is the fastest region in the application, and that is deliberate ───
 *
 * `useQueue` carries `REFETCH.queue` — five seconds — because the progress bar
 * is the only thing in this program that moves while you watch it. The interval
 * is unconditional here (unlike the import panel and the integrity pass, which
 * poll only while `running`) because a queue that is empty right now is exactly
 * the screen somebody leaves open waiting for it not to be. That is one request
 * every five seconds against the local database and no upstream: `GET /queue`
 * reaches Qobuz never.
 *
 * ── the ordering is the worker's, so the screen does not sort ─────────────
 *
 * `list_queue_items` returns active first, then pending in the order the worker
 * will take them. That is the answer to the question this screen is opened
 * with — *when will mine start* — and a client-side sort on any other column
 * would replace it with something that only looks tidier. The state chips
 * narrow; nothing reorders.
 *
 * ── the chips are the `state` parameter, and *All* carries the real total ─
 *
 * Each chip is one `QueueState`, sent as `?state=`. `All` sends nothing, which
 * makes the endpoint's own `unfiltered_total` genuinely the count of that chip
 * — unlike the Missing screen, where the unfiltered figure drops a filter the
 * "All" chip still applies, so the count is only shown here.
 *
 * ── the two presses are the row's, and which of them appears is a rule ────
 *
 * `queueActions()` in `@/widgets/queueState`, not a ternary in the row. Retry
 * is a 409 on an active item; Cancel over a finished one writes `cancelled` and
 * reverses nothing. Neither is drawn where it cannot help.
 *
 * Both invalidate on success, which is what re-reads this list — a cancelled
 * row's album goes back to `wanted`, so the Missing screen's count moves too,
 * and `invalidateLive()` is deliberately broad for exactly that reason.
 */

import { useSearchParams } from 'react-router-dom'

import {
  useCancelQueueItem,
  useQueue,
  useRetryQueueItem,
  useStatus,
} from '@/api/queries'
import type { QueueState } from '@/api/types'
import {
  Button,
  Chip,
  EmptyState,
  PageError,
  PageLoading,
  Spinner,
  useToast,
} from '@/design'
import { QueueRow, toastTone } from '@/widgets'

import styles from '@/screens/queue/Queue.module.css'

/** One chip per `QueueState`, plus the unfiltered view. */
const FILTERS = [
  { id: 'all', label: 'All' },
  { id: 'active', label: 'Downloading' },
  { id: 'pending', label: 'Waiting' },
  { id: 'done', label: 'Done' },
  { id: 'failed', label: 'Failed' },
  { id: 'cancelled', label: 'Cancelled' },
] as const

type FilterId = (typeof FILTERS)[number]['id']

function filterFrom(value: string | null): FilterId {
  const match = FILTERS.find((entry) => entry.id === value)
  return match === undefined ? 'all' : match.id
}

/** `undefined` for *All* — never `null`, which is a 422. */
function stateFor(filter: FilterId): QueueState | undefined {
  return filter === 'all' ? undefined : filter
}

export default function Queue() {
  const [params, setParams] = useSearchParams()
  const filter = filterFrom(params.get('state'))

  const toast = useToast()
  // The same key the shell's footer and the sidebar use (`activityLimit` 0), so
  // this screen adds no second poll of `/status` — it reads the one already
  // running.
  const status = useStatus()
  const queue = useQueue({ state: stateFor(filter) })
  const retry = useRetryQueueItem()
  const cancel = useCancelQueueItem()

  const items = queue.data?.items ?? []
  const everything = queue.data?.unfiltered_total ?? null
  const worker = status.data?.queue
  const running = worker?.worker_running === true

  const lede = running
    ? `Downloading ${worker?.current_album_title ?? 'a release'} — one album at a time, in order.`
    : 'The worker takes one album at a time, in order. Nothing here was queued automatically.'

  function choose(id: FilterId) {
    setParams((prev) => {
      const next = new URLSearchParams(prev)
      if (id === 'all') next.delete('state')
      else next.set('state', id)
      return next
    })
  }

  /** Both presses say what came back, in the server's own words. Cancelling
   *  answers `level: 'warning'` — it succeeded, and what it did was stop
   *  something somebody asked for — and `toastTone` is what keeps that. */
  function run(action: 'retry' | 'cancel', itemId: number) {
    const mutation = action === 'retry' ? retry : cancel
    mutation.mutate(itemId, {
      onSuccess: (message) => toast(message.message, toastTone(message.level)),
      onError: (error: Error) => toast(error.message, 'bad'),
    })
  }

  const busyId =
    (retry.isPending ? retry.variables : undefined) ??
    (cancel.isPending ? cancel.variables : undefined)

  return (
    <section className={styles.section} aria-labelledby="queue-title">
      <div className={styles.header}>
        <div className={styles.headerMain}>
          <h1 className={styles.title} id="queue-title">
            Download queue
          </h1>
          <p className={styles.lede}>{lede}</p>
        </div>

        <div className={styles.headerActions}>
          {running ? <Spinner /> : null}
          <Button variant="quiet" to="/missing">
            Missing releases
          </Button>
        </div>
      </div>

      <div className={styles.filters}>
        {FILTERS.map((entry) => (
          <Chip
            key={entry.id}
            selected={filter === entry.id}
            onClick={() => choose(entry.id)}
            count={
              entry.id === 'all' && everything !== null
                ? everything.toLocaleString()
                : undefined
            }
          >
            {entry.label}
          </Chip>
        ))}

        <div className={styles.filterSpacer} />
        <div className={`${styles.shown} mono`}>{items.length} shown</div>
      </div>

      {queue.isError ? (
        <PageError
          message={queue.error.message}
          action={<Button onClick={() => void queue.refetch()}>Retry</Button>}
        />
      ) : queue.isLoading ? (
        <PageLoading rows={6} title={false} label="Loading the queue" />
      ) : items.length === 0 ? (
        <EmptyState
          glyph="⇣"
          title={everything === 0 ? 'The queue is empty' : 'Nothing in this state'}
          note={
            everything === 0
              ? 'Download a release from Missing releases or from an artist page and it appears here.'
              : `None of the ${(everything ?? 0).toLocaleString()} entries is in this state.`
          }
        />
      ) : (
        <>
          <div className={styles.list}>
            {items.map((item) => (
              <QueueRow
                key={item.id}
                item={item}
                busy={busyId === item.id}
                onRetry={() => run('retry', item.id)}
                onCancel={() => run('cancel', item.id)}
              />
            ))}
          </div>

          <p className={`${styles.scope} mono`}>
            {items.length.toLocaleString()} of {(queue.data?.total ?? 0).toLocaleString()}{' '}
            entries · active first, then in the order the worker will take them
          </p>
        </>
      )}
    </section>
  )
}
