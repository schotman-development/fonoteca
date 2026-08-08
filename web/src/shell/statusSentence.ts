/**
 * One sentence for the whole machine — the design's `scanStatus` (line 1294).
 *
 * A module of its own rather than an export beside the footer for two reasons:
 * it is the only logic in that component and its interesting branch is
 * invisible from a rendered fixture ("idle, and here is when the last scan was"
 * versus "idle, and there has never been one"), and a file that exports both a
 * component and a plain function loses fast refresh.
 *
 * The order is the order somebody watching cares about: a disk scan, then the
 * download worker, then an integrity pass. There is deliberately **no
 * enrichment clause** — `EnrichmentStatusOut` publishes `enabled`,
 * `paused_reason` and `last_run_at` but no `running` flag, so the footer would
 * have to guess, and the query polls every 15s unconditionally, which would
 * make every screen in the app poll enrichment to render a sentence that cannot
 * be written truthfully.
 */

import { fmtAgo } from '@/format'

export interface StatusSentenceInput {
  scanning: boolean
  downloading: boolean
  hashing: boolean
  /** The album the worker is on, when it is on one. */
  currentAlbum: string | null
  lastScanAt: string | null
}

export function statusSentence({
  scanning,
  downloading,
  hashing,
  currentAlbum,
  lastScanAt,
}: StatusSentenceInput): string {
  if (scanning) return 'Scanning the library…'
  if (downloading) {
    return currentAlbum === null ? 'Downloading…' : `Downloading ${currentAlbum}…`
  }
  if (hashing) return 'Verifying file integrity…'
  // fmtAgo answers the em dash for a null; the branch above it is the true
  // sentence rather than a fabricated timestamp — the design's "last scan
  // 08:00" on a library nothing has ever scanned.
  return lastScanAt === null
    ? 'Idle · never scanned'
    : `Idle · last scan ${fmtAgo(lastScanAt)}`
}
