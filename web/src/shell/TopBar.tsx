/**
 * The application's top band — design lines 28–41.
 *
 * Three things, in three slots: the wordmark on the left, the command trigger
 * absolutely centred, and the scan button on the right. The centring is
 * `left: 50%` + `translateX(-50%)` rather than a flex spacer because the two
 * side groups are different widths and the design's trigger is centred on the
 * *viewport*, not on the space between them.
 *
 * ── two rewrites of the design's own prose ────────────────────────────────
 *
 * 1. The placeholder sentence (design 34) offers `MBIDs`. The palette searches
 *    `GET /api/search`, which is the **Qobuz catalogue** — artists and albums.
 *    There is no MBID search anywhere in the application, and the one place a
 *    MusicBrainz id is accepted is the identify picker, which is reached from a
 *    review row rather than from here. Offering it in the hint is a promise the
 *    box cannot keep, so the sentence names what it really does.
 * 2. `scanLabel` (design 1293) toggles between `Scan library` and `Stop scan`.
 *    `POST /api/library/scan` has no cancel — a scan runs to completion — so a
 *    button labelled "Stop scan" would either do nothing or start a second one.
 *    While a pass is running the control reports that it is running and refuses
 *    the press, which is the true version of the same affordance.
 */

import { Link } from 'react-router-dom'

import { useLibraryScan, useLibraryScanStatus } from '@/api/queries'
import { Button, useToast } from '@/design'
import { fmtCount } from '@/format'
import styles from '@/shell/TopBar.module.css'

export interface TopBarProps {
  /** Opens the command palette. The bar owns the trigger, not the panel. */
  onOpenCommandBar: () => void
}

export function TopBar({ onOpenCommandBar }: TopBarProps) {
  const scanStatus = useLibraryScanStatus()
  const scan = useLibraryScan()
  const toast = useToast()

  // Two sources for one question, and both are needed: the stored status is the
  // truth about a pass this tab did not start, and `isPending` covers the
  // seconds between the press and the first refetch.
  const scanning = scan.isPending || scanStatus.data?.running === true

  function runScan() {
    if (scanning) return
    scan.mutate(undefined, {
      onSuccess: (report) => {
        toast(
          `Scan finished — ${fmtCount(report.albums_found, 'album folder')}, ` +
            `${report.albums_adopted} adopted`,
          'ok',
        )
      },
      onError: (error: Error) => toast(error.message, 'bad'),
    })
  }

  return (
    <header className={styles.bar}>
      {/* A router Link, not an <a href>: the wordmark is in the frame, and a
          full document load here would throw away the whole query cache. */}
      <Link to="/" className={styles.wordmark} data-plain="">
        qobuzarr
      </Link>

      <button type="button" className={styles.command} onClick={onOpenCommandBar}>
        <span className={styles.commandGlyph} aria-hidden="true">
          ⌘
        </span>
        <span className={styles.commandHint}>
          Search or run a command — artists, albums, “scan”, “retag”…
        </span>
        <span className={styles.commandKey} aria-hidden="true">
          ⌘K
        </span>
      </button>

      <div className={styles.actions}>
        <Button
          variant="primary"
          onClick={runScan}
          loading={scanning}
          disabled={scanning}
        >
          {scanning ? 'Scanning library…' : 'Scan library'}
        </Button>
      </div>
    </header>
  )
}
