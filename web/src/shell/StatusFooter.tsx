/**
 * The bottom band — design lines 829–845.
 *
 * Four slots, left to right: what is running, the capacity meter, the library's
 * counts, and the address the server answers on.
 *
 * ── the meter is the VOLUME's fullness, not the library's size ─────────────
 *
 * The design draws a 71%-full bar reading `4.26 TB / 6.0 TB`, and the one
 * sentence a future reader needs is *which* number that is. It is
 * `status.disk` — one `statvfs` on `LIBRARY_PATH`, so it counts everything on
 * that volume, including whatever else lives there. That is deliberate: this
 * bar is read by somebody deciding whether to grab a discography, and the
 * answer to that is how full the disk is, not how much of it Qobuzarr put
 * there. The library's own bytes are a different fact and sit in the counts
 * group to the right (`library.size_bytes`, a `SUM` over `tracks.file_size`),
 * where they cannot be mistaken for a denominator.
 *
 * `status.disk` is `null` as a whole when the path could not be probed — a
 * missing directory, a stale mount, a failed syscall. The fraction is then
 * `null` too, which `Meter` draws as an empty track with `aria-valuenow`
 * omitted, and `fmtSize` renders the pair as `— / —`. Neither half invents a
 * zero, because a 0%-full disk is a claim and this is the figure nobody may
 * make up.
 *
 * ── what "running" means here ─────────────────────────────────────────────
 *
 * Three real signals, in the order they matter to somebody watching: a disk
 * scan, the download worker, an integrity pass. The design's `scanStatus`
 * (line 1294) is one sentence for all of it, and so is this — written by
 * `@/shell/statusSentence`, which is a module of its own so it can be tested
 * without a fixture and so this file exports nothing but a component.
 *
 * Every query here is a shared key. `useStatus()` is the same call the sidebar
 * makes (both at `activityLimit` 0), `useIntegrityStatus()` is the dashboard's
 * and only polls while a pass is actually running, and `useSettings()` is
 * cached for five minutes. The footer adds no clock of its own.
 */

import {
  useIntegrityStatus,
  useLibraryScanStatus,
  useSettings,
  useStatus,
} from '@/api/queries'
import { Meter, Spinner } from '@/design'
import { fmtSize } from '@/format'

import { statusSentence } from '@/shell/statusSentence'
import styles from '@/shell/StatusFooter.module.css'

export function StatusFooter() {
  const status = useStatus()
  const scan = useLibraryScanStatus()
  const integrity = useIntegrityStatus()
  const settings = useSettings()

  const scanning = scan.data?.running === true
  const downloading = status.data?.queue.worker_running === true
  const hashing = integrity.data?.running === true
  const busy = scanning || downloading || hashing

  const library = status.data?.library
  const disk = status.data?.disk ?? null
  const fullness =
    disk && disk.total_bytes > 0 ? disk.used_bytes / disk.total_bytes : null

  return (
    <footer className={styles.footer}>
      <div className={styles.state}>
        {busy ? <Spinner size="md" /> : null}
        <span className={styles.stateText}>
          {statusSentence({
            scanning,
            downloading,
            hashing,
            currentAlbum: status.data?.queue.current_album_title ?? null,
            lastScanAt: scan.data?.last?.started_at ?? null,
          })}
        </span>
      </div>

      {/* Design 836–841. The volume's fullness, from one statvfs — `null` when
          the path could not be probed, which draws an empty track and omits
          aria-valuenow rather than painting a 0% disk. */}
      <div className={styles.capacity}>
        <Meter
          className={styles.capacityFill}
          value={fullness}
          label="Disk capacity"
          thickness={4}
        />
        <span className={styles.capacityText}>
          {fmtSize(disk?.used_bytes)} / {fmtSize(disk?.total_bytes)}
        </span>
      </div>

      <div className={styles.spacer} />

      {/* Design 843. Four real figures from one payload. `downloaded_albums of
          albums` is the design's own pair of album figures said honestly: the
          catalogue row count is not a library size. The bytes on the end are
          the library's own — kept here rather than in the meter, where they
          would read as a fullness they are not. */}
      {library === undefined ? null : (
        <div className={styles.counts}>
          {library.tracks.toLocaleString()} tracks ·{' '}
          {library.downloaded_albums.toLocaleString()} of{' '}
          {library.albums.toLocaleString()} albums ·{' '}
          {library.artists.toLocaleString()} artists · {fmtSize(library.size_bytes)}
        </div>
      )}

      {/* Design 844. The design hard-codes `localhost:7373`; this is the host
          and port the process was actually configured with. */}
      {settings.data === undefined ? null : (
        <div className={styles.address}>
          {settings.data.host}:{settings.data.port}
        </div>
      )}
    </footer>
  )
}
