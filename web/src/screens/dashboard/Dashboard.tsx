/**
 * The dashboard — design 80–202, build spec §6.1.
 *
 * Five blocks in the design's order: library health, last downloaded, missing
 * albums, and — in the sticky aside — to do and running now.
 *
 * Three things here are corrections rather than transcriptions, and each is a
 * §7.3 rewrite of prose that would otherwise assert something false:
 *
 *  - the health note claims SHA-256 and a nightly full scan. Qobuzarr hashes
 *    with blake2b-128 plus an audio sample count, and re-hashes a *fixed slice*
 *    nightly (`INTEGRITY_REVERIFY_FRACTION`, 1/30) rather than the library.
 *  - the missing-albums note says gaps are "grabbed automatically". Downloading
 *    is opt-in (`auto_download` defaults to false), so following an artist
 *    queues nothing; the sentence says what actually happens instead.
 *  - the second health tile counts `replaced`, which is a verdict only a *pass*
 *    can give. `IntegrityStatusOut.states` never contains it — a pass
 *    re-baselines whatever it classifies — so it is read from
 *    `last_result.states`, and with no pass on record the tile shows the em
 *    dash and says so. Zero would be a claim nothing has made.
 *
 * Two of the blocks are digests of screens that now exist in full: the
 * missing-albums block draws `LIMITS.dashboardWanted` rows of `/missing`, and
 * *Running now* draws the one item in flight out of `/queue`. Both keep their
 * own keys and their own limits — they are deliberately-narrowed summaries with
 * a link, which is the one shape the same-context rule allows; what it forbids
 * is a component silently fetching a narrower copy of a list another component
 * is already showing.
 */

import { useNavigate } from 'react-router-dom'

import {
  LIMITS,
  useIntegrityStatus,
  useLibraryScan,
  useLibraryScanStatus,
  useMonitorAlbum,
  useNavCounts,
  useQueue,
  useQueueAlbum,
  useStats,
  useStatus,
  useWanted,
} from '@/api/queries'
import type { QueueItemOut } from '@/api/types'
import {
  Button,
  Eyebrow,
  PageError,
  PageLoading,
  Placeholder,
  SectionHead,
  Shelf,
  Spinner,
  StatCard,
  useToast,
} from '@/design'
import { EM_DASH, fmtAgo, fmtCount, fmtRatioPercent } from '@/format'
import { statusSentence } from '@/shell'
import { JobRow, MissingRow, ShelfCard, TodoRow, toastTone } from '@/widgets'

import styles from '@/screens/dashboard/Dashboard.module.css'

/** Design 1201–1202, verbatim. */
const GREETING = 'Your library'
const SUMMARY = 'Identified, tagged and filed automatically since you last looked.'

/** Newest finish first. A queue item with no stamp sorts last rather than first. */
function byFinishedDesc(a: QueueItemOut, b: QueueItemOut): number {
  const left = a.finished_at ?? ''
  const right = b.finished_at ?? ''
  if (left === right) return b.id - a.id
  if (left === '') return 1
  if (right === '') return -1
  return left < right ? 1 : -1
}

export default function Dashboard() {
  const navigate = useNavigate()
  const toast = useToast()

  const integrity = useIntegrityStatus()
  const scanStatus = useLibraryScanStatus()
  const scan = useLibraryScan()
  const status = useStatus()
  const stats = useStats()
  const navCounts = useNavCounts()

  const shelf = useQueue({ state: 'done', limit: LIMITS.shelf })
  const active = useQueue({ state: 'active' })
  const wanted = useWanted({ limit: LIMITS.dashboardWanted })

  const queueAlbum = useQueueAlbum()
  const monitorAlbum = useMonitorAlbum()

  const scanning = scan.isPending || scanStatus.data?.running === true
  const downloading = status.data?.queue.worker_running === true
  const hashing = integrity.data?.running === true

  function runScan() {
    if (scanning) return
    scan.mutate(undefined, {
      onSuccess: (report) =>
        toast(
          `Scan finished — ${fmtCount(report.albums_found, 'album folder')}, ` +
            `${report.albums_adopted} adopted`,
          'ok',
        ),
      onError: (error: Error) => toast(error.message, 'bad'),
    })
  }

  return (
    <section className={styles.section} aria-labelledby="dashboard-title">
      <div className={styles.header}>
        <div className={styles.headerMain}>
          <h1 className={styles.title} id="dashboard-title">
            {GREETING}
          </h1>
          <p className={styles.lede}>{SUMMARY}</p>
        </div>
        <Button
          variant="primary"
          onClick={runScan}
          loading={scanning}
          disabled={scanning}
        >
          {scanning ? 'Scanning library…' : 'Scan library'}
        </Button>
      </div>

      <div className={styles.body}>
        <div className={styles.column}>
          <HealthBlock />

          <ShelfBlock
            items={shelf.data?.items ?? []}
            loading={shelf.isLoading}
            error={shelf.isError ? shelf.error.message : null}
            onOpen={(item) => {
              navigate(
                item.artist_id === null
                  ? '/library'
                  : `/library/${encodeURIComponent(item.artist_id)}`,
              )
            }}
          />

          <div>
            <SectionHead
              title="Missing albums"
              sub="gaps in the discographies of artists you follow — download or ignore each one; nothing is queued on its own"
              action={
                <Button variant="quiet" to="/missing">
                  All missing
                </Button>
              }
            />
            {wanted.isError ? (
              <PageError
                message={wanted.error.message}
                action={<Button onClick={() => void wanted.refetch()}>Retry</Button>}
              />
            ) : wanted.isLoading ? (
              <PageLoading rows={3} title={false} label="Loading the backlog" />
            ) : (wanted.data?.items.length ?? 0) === 0 ? (
              <p className={styles.empty}>Nothing is missing.</p>
            ) : (
              <div className={styles.rows}>
                {wanted.data?.items.map((album) => (
                  <MissingRow
                    key={album.id}
                    album={album}
                    busy={
                      (queueAlbum.isPending && queueAlbum.variables === album.id) ||
                      (monitorAlbum.isPending &&
                        monitorAlbum.variables.albumId === album.id)
                    }
                    onDownload={() => {
                      queueAlbum.mutate(album.id, {
                        onSuccess: (message) =>
                          toast(message.message, toastTone(message.level)),
                        onError: (error: Error) => toast(error.message, 'bad'),
                      })
                    }}
                    onIgnore={() => {
                      // No body: the endpoint TOGGLES. A body would be this
                      // screen deciding the new value, which is the bug the
                      // empty-body rule exists to prevent.
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
            )}
          </div>
        </div>

        <aside className={styles.aside}>
          <TodoBlock
            review={navCounts.data?.enrichment_review ?? 0}
            corrupt={integrity.data?.corrupt_files ?? 0}
            missing={navCounts.data?.wanted ?? 0}
            failed={stats.data?.library.failed_albums ?? 0}
          />

          <div>
            <Eyebrow
              as="h2"
              action={
                <>
                  {scanning || downloading || hashing ? <Spinner /> : null}
                  <Button variant="quiet" size="sm" to="/queue">
                    Queue
                  </Button>
                </>
              }
            >
              Running now
            </Eyebrow>

            <JobsBlock
              downloading={downloading}
              currentAlbum={status.data?.queue.current_album_title ?? null}
              activeItem={active.data?.items[0] ?? null}
              scanning={scanning}
              hashing={hashing}
            />

            <div className={styles.jobsStatus}>
              {statusSentence({
                scanning,
                downloading,
                hashing,
                currentAlbum: status.data?.queue.current_album_title ?? null,
                lastScanAt: scanStatus.data?.last?.started_at ?? null,
              })}
            </div>
          </div>
        </aside>
      </div>
    </section>
  )
}

/* ---- block 1: library health (94–111) ------------------------------------ */

function HealthBlock() {
  const navigate = useNavigate()
  const integrity = useIntegrityStatus()

  const data = integrity.data
  const lastRun = data?.last_run_at ?? null
  const note =
    'Every file is hashed on import — blake2b-128 plus an audio sample count — and a fixed slice is re-hashed nightly. ' +
    (lastRun === null ? 'No pass on record yet.' : `Last pass ${fmtAgo(lastRun)}.`)

  const verified = data?.states.verified ?? 0
  const replaced = data?.last_result?.states.replaced ?? null
  const muted = data?.corrupt_muted ?? 0

  return (
    <div>
      <SectionHead
        title="Library health"
        sub={note}
        action={
          <Button variant="quiet" to="/activity">
            Activity
          </Button>
        }
      />

      {integrity.isError ? (
        <PageError
          message={integrity.error.message}
          action={<Button onClick={() => void integrity.refetch()}>Retry</Button>}
        />
      ) : integrity.isLoading ? (
        <PageLoading rows={2} title={false} label="Measuring library health" />
      ) : (
        <div className={styles.stats}>
          <StatCard
            value={verified.toLocaleString()}
            label="verified"
            share={`${fmtRatioPercent(verified, data?.tracks_total, 1)} of measured files`}
            tone="ok"
            onClick={() => navigate('/library')}
          />
          <StatCard
            value={replaced === null ? EM_DASH : replaced.toLocaleString()}
            label="changed elsewhere"
            share={replaced === null ? 'no pass yet' : 'replaced in the last pass'}
            tone="warn"
            onClick={() => navigate('/activity')}
          />
          <StatCard
            value={(data?.never_baselined ?? 0).toLocaleString()}
            label="never baselined"
            share="not measured yet"
            tone="warn"
            onClick={() => navigate('/activity')}
          />
          <StatCard
            value={(data?.corrupt_files ?? 0).toLocaleString()}
            label="corrupt"
            share={muted > 0 ? `decode errors · ${muted} muted` : 'decode errors'}
            tone="bad"
            onClick={() => navigate('/activity')}
          />
        </div>
      )}
    </div>
  )
}

/* ---- block 2: last downloaded (113–137) ---------------------------------- */

interface ShelfBlockProps {
  items: QueueItemOut[]
  loading: boolean
  error: string | null
  onOpen: (item: QueueItemOut) => void
}

function ShelfBlock({ items, loading, error, onOpen }: ShelfBlockProps) {
  const sorted = [...items].sort(byFinishedDesc)

  return (
    <div>
      <SectionHead
        title="Last downloaded"
        sub="grabbed from Qobuz, fingerprinted and filed"
        ruled={false}
        action={
          <Button variant="quiet" to="/radar">
            Release radar
          </Button>
        }
      />

      {error !== null ? (
        <PageError message={error} />
      ) : loading ? (
        <PageLoading rows={1} title={false} label="Loading recent downloads" />
      ) : sorted.length === 0 ? (
        <p className={styles.empty}>Nothing has been downloaded yet.</p>
      ) : (
        <Shelf label="Recently downloaded releases">
          {sorted.map((item) => (
            <ShelfCard
              key={item.id}
              title={item.album_title ?? EM_DASH}
              subtitle={item.artist_name ?? EM_DASH}
              seed={item.album_id}
              imageUrl={item.album_image_url}
              tag={fmtAgo(item.finished_at)}
              onClick={() => {
                onOpen(item)
              }}
            />
          ))}
        </Shelf>
      )}
    </div>
  )
}

/* ---- block 4: to do (164–179) -------------------------------------------- */

interface TodoBlockProps {
  review: number
  corrupt: number
  missing: number
  failed: number
}

function TodoBlock({ review, corrupt, missing, failed }: TodoBlockProps) {
  const open = review + corrupt + missing + failed

  return (
    <div>
      <Eyebrow as="h2" action={<span className="mono">{open} open</span>}>
        To do
      </Eyebrow>

      {review > 0 ? (
        <TodoRow
          count={review}
          title="identities to confirm"
          action="Review queue"
          tone="warn"
          to="/identify"
        />
      ) : null}
      {corrupt > 0 ? (
        <TodoRow
          count={corrupt}
          title="corrupt files"
          action="Quarantine or re-grab"
          tone="bad"
          to="/activity"
        />
      ) : null}
      {missing > 0 ? (
        <TodoRow
          count={missing}
          title="releases missing"
          action="Download or ignore"
          tone="warn"
          to="/missing"
        />
      ) : null}
      {/* The queue, filtered to failed, rather than the activity log — a
          history offers no press and this row's verb is *Retry*. The two
          populations are near-identical rather than equal (`failed_albums`
          counts albums, the page lists queue entries, and a release whose entry
          was cancelled and re-queued can sit in one and not the other), which
          is why the count stays on the row and is not restated over there. */}
      {failed > 0 ? (
        <TodoRow
          count={failed}
          title="downloads failed"
          action="Retry"
          tone="bad"
          to="/queue?state=failed"
        />
      ) : null}

      {/* Design 1219's fourth row. Nothing models a secondary credit, so there
          is no count to render and no pass to run — the row keeps its place
          and says what it would need. */}
      <div className={styles.placeholderRow}>
        <Placeholder
          variant="inline"
          needs="a secondary-credit model — nothing records a release's other credited artists"
        />
      </div>

      {open === 0 ? <p className={styles.empty}>Nothing needs you.</p> : null}
    </div>
  )
}

/* ---- block 5: running now (181–198) -------------------------------------- */

interface JobsBlockProps {
  downloading: boolean
  currentAlbum: string | null
  activeItem: QueueItemOut | null
  scanning: boolean
  hashing: boolean
}

function JobsBlock({
  downloading,
  currentAlbum,
  activeItem,
  scanning,
  hashing,
}: JobsBlockProps) {
  if (!downloading && !scanning && !hashing) {
    return <p className={styles.empty}>Idle.</p>
  }

  return (
    <>
      {downloading ? (
        <JobRow
          label={`Downloading ${currentAlbum ?? 'a release'}`}
          detail={
            activeItem === null || activeItem.progress_tracks_total === 0
              ? 'starting'
              : `${activeItem.progress_tracks_done} of ${activeItem.progress_tracks_total} tracks`
          }
          value={activeItem === null ? null : activeItem.progress_percent / 100}
          indeterminate={activeItem === null}
          tone="ok"
        />
      ) : null}

      {/* A scan publishes no percentage. An indeterminate bar is the honest
          drawing of "running"; a 0% one reads as a job that has stalled. */}
      {scanning ? (
        <JobRow
          label="Scanning the library"
          detail="running"
          value={null}
          indeterminate
          tone="warn"
        />
      ) : null}

      {hashing ? (
        <JobRow
          label="Verifying file integrity"
          detail="running"
          value={null}
          indeterminate
          tone="accent"
        />
      ) : null}
    </>
  )
}
