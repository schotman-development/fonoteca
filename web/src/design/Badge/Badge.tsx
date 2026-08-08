/**
 * The pill that sits on a piece of artwork (design 281–283).
 *
 * `padding: 2px 7px; border-radius: 6px; background: rgba(255,255,255,.94);
 * color: <status>; font-size: 11px; font-weight: 600` — a translucent paper
 * chip over whatever the tile is painted, so the word stays legible on a dark
 * gradient and on a light cover alike.
 *
 * ── the background is fixed, the ink is the caller's ──────────────────────
 *
 * The design draws three of these and all three share one background
 * (`rgba(255,255,255,0.94)`, DCLogic 1094–1096) and differ only in text
 * colour. That is the right split: the pill is a *legibility* device — it
 * exists so the word can be read at all — and the status lives in the word's
 * own colour. A caller that could tint the background would be able to paint a
 * red plate on artwork, which is a much louder claim than the design ever
 * makes about a release.
 *
 * `warn` therefore resolves to `--c-warn` (5.0:1) and not to the design's
 * `#D97706` (3.2:1). The design uses that value here for TEXT; tokens.css
 * records the substitution and this is one of the sites it is for.
 *
 * Nothing here knows what the word means. `CORRUPT`, `REPLACED` and
 * `UNVERIFIED` are the widget layer's vocabulary; this is a pill.
 */

import type { ReactNode } from 'react'

import { cx } from '@/design/cx'
import type { StyleableProps, Tone } from '@/design/types'
import styles from '@/design/Badge/Badge.module.css'

export interface BadgeProps extends StyleableProps {
  children: ReactNode
  /**
   * Which ink the word is set in. `neutral` — the default — is the resting
   * state and paints ordinary body ink, because a badge that is merely
   * descriptive must not read as a warning.
   */
  tone?: Tone
}

/**
 * Keyed on `Exclude<Tone, 'neutral'>`, the same shape `KeyValue` uses, rather
 * than on the full union with `neutral: undefined`. Neutral has no class here
 * — it is the base — and a map that says so with a key holding `undefined`
 * reads as a class somebody forgot to write.
 */
const TONE_CLASS: Readonly<Record<Exclude<Tone, 'neutral'>, string | undefined>> = {
  accent: styles.accent,
  ok: styles.ok,
  warn: styles.warn,
  bad: styles.bad,
}

export function Badge({ children, tone = 'neutral', className }: BadgeProps) {
  return (
    <span
      className={cx(styles.badge, tone !== 'neutral' && TONE_CLASS[tone], className)}
      data-tone={tone}
    >
      {children}
    </span>
  )
}
