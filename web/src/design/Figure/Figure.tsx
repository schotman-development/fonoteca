/**
 * A mono number with an uppercase caption under it — design 229–241 (the
 * Library header's trio: `99.1% / Identified`, `142 / Flagged`, and the hi-res
 * share beside them) and 683–686 (the Identify stack card's lead figure).
 *
 * ── it is not a StatCard, and the difference is the point ──────────────────
 *
 * A StatCard is a bordered `<button>` that navigates; this is two lines of
 * text with no box, no edge and nothing to press. The design puts them in
 * genuinely different places — the card is a tile in a grid of four, the figure
 * is a readout tucked into the end of a header row — and collapsing the two
 * into one component with a `bordered` prop would mean every caller choosing
 * between two shapes that are not variants of each other.
 *
 * ── the value is a `ReactNode` and this component never formats it ─────────
 *
 * Same rule as StatCard, for the same reason. Two of the three figures the
 * design draws here are three-valued at the source: a coverage percentage
 * needs `scope.albums` to be non-zero before it means anything, and the hi-res
 * share is `null` until something on disk has a determinable format. The
 * honest rendering of either is `EM_DASH`
 * from `@/format`, and a primitive that accepted a `number` would have to
 * invent a fallback — every fallback available to it being a lie about whether
 * anything was counted. So the caller decides, because the caller is the only
 * one who knows.
 *
 * ── always mono, always tabular ────────────────────────────────────────────
 *
 * There is no `mono` prop. Every figure in the design is `IBM Plex Mono`, and
 * the reason is not typographic taste: these sit in a row and change on a poll,
 * so proportional digits make the caption under them shift left and right while
 * nobody has touched anything. `tabular-nums` is set explicitly rather than
 * inherited from `body`, because this is the one element where losing it is
 * visible.
 *
 * ── the caption is a caption ───────────────────────────────────────────────
 *
 * It composes `shared/label.module.css`'s `.labelNav` — the eyebrow recipe at
 * the design's tighter `--ls-nav`, which is what the design uses under a number
 * (231, 235, 239, 685) as against over a block. It is a `<div>`, never a
 * heading: a figure is a readout inside a section that has already been headed.
 */

import type { ReactNode } from 'react'

import label from '@/design/shared/label.module.css'
import { cx } from '@/design/cx'
import type { Size, StyleableProps, Tone } from '@/design/types'

import styles from '@/design/Figure/Figure.module.css'

/**
 * The design draws two, and only two: 19px in the Library header (230) and
 * 24px on the Identify stack card (684). Derived from the shared `Size` union
 * rather than re-spelled, so a screen never has to remember which word this
 * primitive picked — but `sm` is excluded, because there is no third size and
 * a prop value with no rendering is a silent no-op.
 */
export type FigureSize = Extract<Size, 'md' | 'lg'>

export interface FigureProps extends StyleableProps {
  /**
   * The figure. Pass `EM_DASH` for "nothing has counted this" — this component
   * will not substitute one, and must not.
   */
  value: ReactNode
  /** The uppercase line under it: `Identified`, `Flagged`, `AcoustID`. */
  caption: ReactNode
  /** Colours the figure alone; the caption is always the ramp. Defaults to ink. */
  tone?: Tone
  /** `md` is the header readout (design 230); `lg` is the stack card's lead (684). */
  size?: FigureSize
  /** Put on the caption, so a group of figures can be `aria-labelledby` one. */
  id?: string
}

const TONE: Readonly<Record<Tone, string | undefined>> = {
  neutral: styles.toneNeutral,
  accent: styles.toneAccent,
  ok: styles.toneOk,
  warn: styles.toneWarn,
  bad: styles.toneBad,
}

const SIZE: Readonly<Record<FigureSize, string | undefined>> = {
  md: styles.md,
  lg: styles.lg,
}

export function Figure({
  value,
  caption,
  tone = 'neutral',
  size = 'md',
  id,
  className,
}: FigureProps) {
  return (
    <div className={cx(styles.figure, className)}>
      <div className={cx(styles.value, SIZE[size], TONE[tone])}>{value}</div>
      <div id={id} className={cx(label.labelNav, styles.caption)}>
        {caption}
      </div>
    </div>
  )
}
