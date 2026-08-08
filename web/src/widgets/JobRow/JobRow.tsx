/**
 * One line of the dashboard's "Running now" list (design 188–196).
 *
 * What is happening, how it is going, and a 3px bar under it. The design gives
 * every job a percentage (DCLogic 1222–1226: `'64%'`, `'38%'`, `'81%'`) because
 * its fixtures are written by hand. Qobuzarr's three running things are not
 * alike, and the difference is this component's whole reason for existing.
 *
 * ── a running job with no percentage is not a job at 0% ───────────────────
 *
 * A download publishes a real fraction — `QueueItemOut.progress_tracks_done`
 * over `progress_tracks_total`. A **library scan does not**: nothing in
 * `LibraryScanStatusOut` counts the folders it has yet to walk, and the same is
 * true of an enrichment tick and of the integrity pass. There are exactly two
 * honest drawings of that, and a zero-width bar is neither of them: it says the
 * job has done none of its work, which is false and which is the reading that
 * makes somebody press the button again.
 *
 * So `value` is three-valued — a fraction in 0..1, or `null` for *nothing has
 * measured this* — and `indeterminate` is the separate claim *this is running
 * and cannot say how far*. `Meter` draws the first as an empty track with
 * `aria-valuenow` omitted and the second as a moving sliver, and
 * `indeterminate` wins, so a caller may pass both without nulling one out:
 * `<JobRow value={pct} indeterminate={pct === null} …/>`.
 *
 * Deriving a percentage from elapsed time was considered and is exactly what
 * this refuses to do. It is a figure somebody acts on, and it is invented.
 *
 * ── the row is not a link, because there is nowhere to go ─────────────────
 *
 * The design gives these rows no `onClick` (189), and it is right: a job is a
 * thing happening, not an address. Cancelling one is the queue's own control on
 * the Download queue screen, and putting it here would put a destructive button in a
 * list whose contents change under the pointer every five seconds.
 */

import { Meter } from '@/design'
import type { Tone } from '@/design'
import { fmtPercent } from '@/format'

import styles from '@/widgets/JobRow/JobRow.module.css'

export interface JobRowProps {
  /** What is running: `Downloading Miles Davis — Kind of Blue`. */
  label: string
  /** How it is going, in words: `9 of 12 tracks`, `running`. */
  detail: string
  /**
   * A fraction in 0..1, or `null` for "no percentage exists". Never pass `0`
   * for an unmeasured job — see the note above.
   */
  value: number | null
  /** Running with no percentage to report. Draws a moving sliver; wins over `value`. */
  indeterminate?: boolean
  /**
   * Colours the bar. The bar is a MARK sitting in a track rather than on
   * paper, and `Meter` owns which shade of each tone clears the contrast floor
   * there — this row does not paint it.
   */
  tone: Tone
}

export function JobRow({ label, detail, value, indeterminate = false, tone }: JobRowProps) {
  return (
    <div className={styles.row}>
      <div className={styles.label}>{label}</div>
      <div className={styles.detail}>{detail}</div>
      <Meter
        value={value}
        indeterminate={indeterminate}
        thickness={3}
        tone={tone}
        label={label}
        // The words the reader hears instead of the bare percentage. `Meter`
        // ignores it when there is no percentage at all, which is right: a
        // value text on an indeterminate bar is a figure with no bar behind it.
        valueText={detail === '' ? undefined : `${fmtPercent(value)} — ${detail}`}
      />
    </div>
  )
}
