/**
 * Activity — design 798–826, build spec §6.6.
 *
 * **The group chips filter the loaded page, not the whole history, and the
 * screen says so.** `list_activity` matches `Activity.event == event`
 * *exactly*, while the design's four chips are groups spanning seven or eight
 * event names each — no single request can produce one. So there is one
 * unfiltered query at `LIMITS.activity`, and the chip is UI state that
 * partitions what came back with `eventGroup()`.
 *
 * That is why the chip is **not** part of the query key: four chips over one
 * data set would be four identical requests and four cache entries of the same
 * rows. It still lives in the URL, because a poll must never reset what
 * somebody chose and a filtered view should be a link.
 *
 * The fourth column is the row's `level`, not the design's `result` string:
 * `ActivityOut` has no such field, and inventing "quarantined" or "412 files"
 * would be writing fiction into a log.
 */

import { useSearchParams } from 'react-router-dom'

import { LIMITS, REFETCH, useActivity } from '@/api/queries'
import { Button, Chip, EmptyState, PageError, PageLoading } from '@/design'
import { ActivityRow, eventGroup, type EventGroup } from '@/widgets'

import styles from '@/screens/activity/Activity.module.css'

/** The design's four chips (1360), over the real event vocabulary. */
const GROUPS = [
  { id: 'all', label: 'All' },
  { id: 'integrity', label: 'Integrity' },
  { id: 'writes', label: 'Writes' },
  { id: 'grabs', label: 'Grabs' },
] as const

type GroupId = (typeof GROUPS)[number]['id']

function groupFrom(value: string | null): GroupId {
  return value === 'integrity' || value === 'writes' || value === 'grabs' ? value : 'all'
}

export default function Activity() {
  const [params, setParams] = useSearchParams()
  const group = groupFrom(params.get('group'))

  const activity = useActivity({}, REFETCH.rows)

  const loaded = activity.data?.items ?? []
  const shown =
    group === 'all'
      ? loaded
      : loaded.filter((entry) => eventGroup(entry.event) === (group as EventGroup))

  function choose(id: GroupId) {
    setParams((prev) => {
      const next = new URLSearchParams(prev)
      if (id === 'all') next.delete('group')
      else next.set('group', id)
      return next
    })
  }

  return (
    <section className={styles.section} aria-labelledby="activity-title">
      <div className={styles.header}>
        <div className={styles.headerMain}>
          <h1 className={styles.title} id="activity-title">
            Activity
          </h1>
          <p className={styles.lede}>
            Every write, move, repair and integrity check, newest first.
          </p>
        </div>

        <div className={styles.chips}>
          {GROUPS.map((entry) => (
            <Chip
              key={entry.id}
              selected={group === entry.id}
              onClick={() => choose(entry.id)}
            >
              {entry.label}
            </Chip>
          ))}
        </div>
      </div>

      {activity.isError ? (
        <PageError
          message={activity.error.message}
          action={<Button onClick={() => void activity.refetch()}>Retry</Button>}
        />
      ) : activity.isLoading ? (
        <PageLoading rows={8} title={false} label="Loading the log" />
      ) : shown.length === 0 ? (
        <EmptyState
          glyph="≡"
          title={loaded.length === 0 ? 'Nothing has happened yet' : 'Nothing in this group'}
          note={
            loaded.length === 0
              ? 'Follow an artist or run a scan and this fills up.'
              : `None of the ${loaded.length} most recent entries is one of these.`
          }
        />
      ) : (
        <>
          <div className={styles.list}>
            {shown.map((entry) => (
              <ActivityRow key={entry.id} entry={entry} />
            ))}
          </div>
          <p className={`${styles.scope} mono`}>
            {group === 'all'
              ? `${shown.length} of the ${activity.data?.total ?? 0} recorded entries · newest ${LIMITS.activity}`
              : `${shown.length} of the ${loaded.length} most recent entries · this chip filters what is loaded, not the whole history`}
          </p>
        </>
      )}
    </section>
  )
}
