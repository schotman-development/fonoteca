/**
 * The spinning ring — design 185 (10px, beside the dashboard's "Running now"
 * eyebrow) and 832 (11px, at the left end of the status footer).
 *
 * ── THE ACCESSIBILITY CONTRACT: this spinner is decoration ─────────────────
 *
 * It is `aria-hidden`, always, and it has no `label` prop. The **caller** names
 * the region it sits in. That is a deliberate choice between the two available
 * contracts, and the design is what settles it:
 *
 *   - At 185 the ring sits inside the eyebrow "Running now", directly above a
 *     list of jobs each naming itself ("Downloading Bon Iver — Sable, Fable",
 *     "about a minute left").
 *   - At 832 it sits immediately left of `{{ scanStatus }}`, a live sentence
 *     that says what is running in words.
 *
 * In both places the meaning is already on the screen, in text, at the moment
 * the ring appears. A self-labelling spinner (`role="status"` plus a hidden
 * "Loading") would announce a second, vaguer copy of a sentence the user is
 * about to hear anyway — "Busy. Scanning library, 412 of 1,284 files." — and
 * the vague half arrives first. Worse, it would re-announce on every mount, and
 * these mount and unmount on a four-second poll.
 *
 * **The prop shape enforces it.** There is no `label`, no `role` and no
 * `aria-*` passthrough, so this component cannot contribute an accessible name
 * even by accident; a caller who wants one has to put it on the region, which
 * is where it belongs. The rule for the caller is one line: *if the spinner is
 * the only thing that changed, the region is wrong.* Wrap the ring and its
 * sentence in the live region and let the sentence do the talking.
 *
 * ── reduced motion ─────────────────────────────────────────────────────────
 *
 * The ring stops turning and stays visible as a static arc. That is honest
 * *because of* the contract above: the meaning was never carried by the motion,
 * it is carried by the sentence next to it, which is the same reason the ring
 * is hidden from the reading in the first place. A pulse or a fade would be
 * substituting one animation for another, which is what the preference asks us
 * not to do. It is handled in the module rather than left to the global reduce
 * block in tokens.css, because that block zeroes durations — which leaves a
 * ring frozen mid-turn at an arbitrary angle, indistinguishable from a bug.
 */

import { cx } from '@/design/cx'
import type { Size, StyleableProps } from '@/design/types'

import styles from '@/design/Spinner/Spinner.module.css'

/**
 * `sm` is the dashboard's 10px ring (design 185); `md` the footer's 11px (832).
 * Narrowed from the shared `Size` rather than spelled again, so a caller never
 * has to remember which two words this particular primitive picked.
 */
export type SpinnerSize = Extract<Size, 'sm' | 'md'>

/**
 * Which of the two paints. `accent` is the design's ring — a `--c-fill` circle
 * with a `--c-accent` cap — and is correct on paper, which is everywhere the
 * design draws one. `current` inherits instead: the ring is `currentcolor` at
 * low opacity and the cap is the gap, which is the only version that reads on
 * a filled control. It exists because `Button` had otherwise written a second
 * spinner into its own module; see the note in `Spinner.module.css`.
 *
 * It is deliberately NOT the shared `Tone` union: those five words are a
 * *status*, and a spinner has no status — it is the same "working" in all five.
 */
export type SpinnerTone = 'accent' | 'current'

export interface SpinnerProps extends StyleableProps {
  size?: SpinnerSize
  tone?: SpinnerTone
}

const SIZE: Readonly<Record<SpinnerSize, string | undefined>> = {
  sm: styles.sm,
  md: styles.md,
}

const TONE: Readonly<Record<SpinnerTone, string | undefined>> = {
  accent: undefined,
  current: styles.toneCurrent,
}

export function Spinner({ size = 'sm', tone = 'accent', className }: SpinnerProps) {
  // `aria-hidden` is not a prop and is not conditional. See the contract above.
  return (
    <span
      aria-hidden="true"
      className={cx(styles.spinner, SIZE[size], TONE[tone], className)}
    />
  )
}
