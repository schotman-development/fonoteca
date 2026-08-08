/**
 * The chip — the filter pill the design draws on the library (247) and the
 * activity log (807), and, one size down, the path-template token buttons (725).
 *
 * ── it is a toggle, so it says so ───────────────────────────────────────────
 *
 * A filter chip is not a link and not a menu item: it is a control with an on
 * state that stays on. `aria-pressed` is how that is spelled, and without it a
 * screen reader announces five identical buttons and no way to tell which
 * subset is showing — the colour being the entire visible answer. `selected` is
 * therefore required to be a boolean on the interactive form and is not
 * inferred from anything.
 *
 * ── a chip is never tinted ──────────────────────────────────────────────────
 *
 * There is no `tone` prop and there must not be one. The only colouring a chip
 * has is ink-on-ink when it is on (design 247, DCLogic 1230). In this system
 * colour means *how bad something is*; a chip means *which subset you are
 * looking at*, and a red "Errors" filter chip would be indistinguishable from a
 * count of errors. `accent` is the one exception and is not a status: it is the
 * teal the design gives the token buttons at 725, which tokens.css and
 * `design/types.ts` both define as identity (a link, a proposed value), not as
 * a verdict.
 *
 * ── `as="span"` is a real requirement, not a convenience ────────────────────
 *
 * The same pill appears with nothing to press — a state word beside a row, a
 * static count. Rendering that as a `<button>` puts a tab stop in front of a
 * keyboard user for every one of them and promises a press that does nothing.
 * A `<span>` chip takes no `onClick` and no `selected`, which is enforced by
 * the discriminated union below rather than by a comment.
 */

import type { ReactNode } from 'react'

import { cx } from '@/design/cx'
import type { Size, StyleableProps } from '@/design/types'

import styles from '@/design/Chip/Chip.module.css'

/**
 * The two geometries the design draws: the filter pill (247, 807) and the
 * token button (725). Narrowed from the shared `Size` rather than spelled
 * again, so `sm` means the same "one step down" it means on `Button`.
 */
export type ChipSize = Extract<Size, 'sm' | 'md'>

interface ChipCommonProps extends StyleableProps {
  /** The label. Pass it in ordinary case — nothing here uppercases. */
  children: ReactNode
  /**
   * The trailing `{{ f.count }}` (design 247), rendered in mono at `opacity: .6`
   * and inheriting the chip's colour. `null`/`undefined` renders nothing at all
   * — never a zero standing in for "not counted".
   */
  count?: ReactNode
  /** `md` is the filter pill (247, 807); `sm` is the token button (725). */
  size?: ChipSize
  /** Mono face — for a token (`{artist}`), an id, a format string. */
  mono?: boolean
  /**
   * Teal text (design 725). Identity, not status: this is the accent that
   * paints links and proposed values, and it is why there is no `tone`.
   */
  accent?: boolean
}

interface ChipButtonProps extends ChipCommonProps {
  as?: 'button'
  /** On or off. Announced as `aria-pressed`, which is the whole point. */
  selected: boolean
  onClick: () => void
  disabled?: boolean
}

interface ChipStaticProps extends ChipCommonProps {
  as: 'span'
  /** A static chip has no state to be in and nothing to press. */
  selected?: never
  onClick?: never
  disabled?: never
}

export type ChipProps = ChipButtonProps | ChipStaticProps

const SIZE: Record<ChipSize, string | undefined> = {
  md: styles.chip,
  sm: styles.sm,
}

export function Chip(props: ChipProps) {
  const { children, count, size = 'md', mono = false, accent = false, className } = props

  const body = (
    <>
      {children}
      {/* The design writes a literal space here (247), and it is load-bearing:
          without it the accessible name of a counted chip is "Monitored12". */}
      {count == null ? null : ' '}
      {count == null ? null : <span className={styles.count}>{count}</span>}
    </>
  )

  const shape = cx(
    SIZE[size],
    mono ? styles.mono : undefined,
    accent ? styles.accent : undefined,
    className,
  )

  if (props.as === 'span') {
    return <span className={shape}>{body}</span>
  }

  const { selected, onClick, disabled = false } = props

  return (
    <button
      type="button"
      className={cx(shape, styles.pressable, selected ? styles.on : undefined)}
      aria-pressed={selected}
      disabled={disabled}
      onClick={onClick}
    >
      {body}
    </button>
  )
}
