/**
 * One line of a release's track list (design 492–499), inside the detail
 * drawer.
 *
 * A number, a title, a state and a duration — the densest row in the
 * application, and the one where getting the third column wrong does the most
 * damage.
 *
 * ── UNKNOWN IS NOT A PASS ─────────────────────────────────────────────────
 *
 * The design draws a green `✓` on every track it has not marked bad
 * (DCLogic 1170–1173: `bad ? … : '✓'`), because its fixtures know the answer
 * for all of them. Qobuzarr does not. `TrackIntegrityOut.state` is
 * three-valued in the way that matters — `unknown` means **nothing has ever
 * baselined this file**, which is not the same claim as "this file is
 * intact" — and `TrackOut.integrity` is `null` whenever the caller did not
 * build the integrity block at all.
 *
 * Both render the em dash in quiet ink. A `✓` there would be this component
 * telling somebody their library has been verified when it has never been
 * measured, on the one screen they would open to check; it is the single worst
 * thing this row could do, and there is a test named after it.
 *
 * ── the vocabulary, and why the fingerprint is read first ─────────────────
 *
 * `fingerprint_state === 'corrupt'` outranks everything else. It is the only
 * verdict in the system that says *this audio does not decode* — the file is
 * broken however cleanly its hash matches the last one taken of it — and it is
 * what `librarian.quarantine_corrupt_files()` acts on. The design's word for it
 * is `CRC` (1171), which is the failure a FLAC decoder reports, so it is kept.
 *
 * Below it, `integrity.state`: `replaced` (different audio behind the same
 * name) and `retagged` (the same audio behind edited metadata) are amber and
 * keep their own names rather than collapsing into the design's `drift`,
 * because they license different work — a retag needs a new stamp, a
 * replacement needs the release identified again. `missing` is red: the row
 * exists and the file does not. `verified` is the `✓`.
 *
 * ── the glyph carries a word for anyone not looking at it ─────────────────
 *
 * `✓` is announced as "check mark" or as nothing at all, and `CRC` as three
 * letters. Each state therefore renders its mark `aria-hidden` beside a
 * `.visuallyHidden` sentence, and puts the same sentence in a `title` for the
 * sighted reader who cannot guess what `CRC` is either.
 *
 * ── the number is the track's own, and `index` is the fallback ────────────
 *
 * `track_number` is what the release says this track is, and it is what should
 * be shown — a disc-two opener is track 1, not track 13. It can legitimately be
 * `0` (a scan-adopted file whose tags carried no number), and only then does the
 * row fall back to its position in the list, which is why `index` is a prop
 * rather than a thing this component could work out.
 */

import type { TrackOut } from '@/api/types'
import { cx, ListRow } from '@/design'
import { EM_DASH, fmtSpan } from '@/format'

import styles from '@/widgets/TrackRow/TrackRow.module.css'

export interface TrackRowProps {
  track: TrackOut
  /**
   * The row's **zero-based** position in the list it is rendered in, used as
   * the ordinal only when the track carries no number of its own.
   */
  index: number
}

/** What the third column says: a mark, a sentence behind it, and its ink. */
interface TrackState {
  mark: string
  /** Read aloud in place of the mark, and shown on hover. */
  meaning: string
  className: string | undefined
}

/**
 * Nothing has measured this file. The em dash is `@/format`'s, so this row
 * cannot drift from every other unknown in the app.
 */
const UNMEASURED: TrackState = {
  mark: EM_DASH,
  meaning: 'Not measured — no baseline has ever been taken of this file',
  className: styles.stateUnknown,
}

function stateOf(track: TrackOut): TrackState {
  const integrity = track.integrity
  if (integrity === null) return UNMEASURED

  // The audio failing to decode outranks anything the hash says about it.
  if (integrity.fingerprint_state === 'corrupt') {
    return {
      mark: 'CRC',
      meaning: 'Corrupt — this file does not decode',
      className: styles.stateBad,
    }
  }

  switch (integrity.state) {
    case 'verified':
      return {
        mark: '✓',
        meaning: 'Verified against the recorded baseline',
        className: styles.stateOk,
      }
    case 'retagged':
      return {
        mark: 'retagged',
        meaning: 'Retagged — the metadata changed around identical audio',
        className: styles.stateWarn,
      }
    case 'replaced':
      return {
        mark: 'replaced',
        meaning: 'Replaced — different audio under the same name',
        className: styles.stateWarn,
      }
    case 'missing':
      return {
        mark: 'missing',
        meaning: 'Missing — the row is here and the file is not',
        className: styles.stateBad,
      }
    case 'unknown':
      return UNMEASURED
  }
}

/** `01`, `02`, … — the design's own two-digit ordinal (DCLogic 1170). */
function ordinal(track: TrackOut, index: number): string {
  const number = track.track_number > 0 ? track.track_number : index + 1
  return String(number).padStart(2, '0')
}

export function TrackRow({ track, index }: TrackRowProps) {
  const state = stateOf(track)

  return (
    <ListRow dense className={styles.row}>
      <span className={cx(styles.number, 'mono')}>{ordinal(track, index)}</span>

      <span className={cx(styles.title, 'truncate')} title={track.title}>
        {track.title}
      </span>

      <span
        className={cx(styles.state, 'mono', state.className)}
        title={state.meaning}
      >
        <span aria-hidden="true">{state.mark}</span>
        <span className="visuallyHidden">{state.meaning}</span>
      </span>

      <span className={cx(styles.duration, 'mono')}>{fmtSpan(track.duration)}</span>
    </ListRow>
  )
}
