/**
 * The button — the design's five shapes, in one control.
 *
 * The design draws no button component; it draws the same inline styles over
 * and over, and they fall into five shapes and four sizes (the whole argument,
 * with the design line for every value, is at the top of `Button.module.css`).
 * This file is only about the four decisions that are *behaviour* rather than
 * paint, and each of them is a bug that has been shipped by somebody:
 *
 * ── 1. `type` defaults to `"button"` ────────────────────────────────────────
 *
 * HTML's default is `submit`, so a bare `<button>` inside a `<form>` submits it
 * — and every drawer in this application is a form-shaped stack of fields with
 * a Cancel beside the Save. A Cancel that submits is not a styling mistake, it
 * is data written that somebody asked not to be. The default is therefore
 * flipped here, once, and a caller who really wants a submit says so.
 *
 * ── 2. it can navigate, and a disabled link is a real thing ─────────────────
 *
 * Half the design's "buttons" go somewhere: the section links (100, 119), the
 * health tiles, the row actions on the dashboard. Those must be `<a>` elements
 * — middle-click, copy-link and the browser's own history all live on the
 * anchor, and a `<button onClick={navigate}>` has none of them. So `to` renders
 * a react-router `<Link data-plain>` with identical styling (`data-plain` is
 * the one hook in base.css that stops it from being underlined like prose).
 *
 * An anchor cannot be `disabled`: the attribute does not exist on it, and
 * setting it is silently ignored. A disabled link therefore carries
 * `aria-disabled="true"` — which the stylesheet fades, exactly as it fades
 * `:disabled` — and the click handler calls `preventDefault()`, because
 * `aria-disabled` alone announces "unavailable" and then navigates anyway,
 * which is the worst of the three possible outcomes.
 *
 * ── 3. `loading` blocks the press ───────────────────────────────────────────
 *
 * A control that is already working must not be pressable again: every
 * mutation behind these buttons is a real POST, and the second one is a second
 * download, a second re-tag, a second scan. So `loading` implies disabled, sets
 * `aria-busy`, and draws a `Spinner`.
 *
 * It draws `Spinner tone="current"` rather than a ring of its own. The original
 * objection was real — the design's spinner is a `--c-fill` circle with a
 * `--c-accent` cap, which is very nearly invisible on a primary button's ink
 * fill — but the answer to it was a second, near-identical spinning ring
 * declared in this file's module, i.e. one thing in the design and two in the
 * layer. `current` is that objection answered inside `Spinner`: the ring
 * inherits the control's own colour, so it reads on ink, on teal and on paper.
 *
 * ── 4. an icon-only control still has a name ────────────────────────────────
 *
 * `iconRound` (‹ ›) and `close` (×) have a glyph for a child, and a glyph is
 * not a name: `×` is announced as "times" or as nothing at all. Passing `label`
 * renders the app's `.visuallyHidden` span and marks the glyph `aria-hidden`,
 * so the accessible name is a sentence and the reading of it is not "times
 * close". The two are done together deliberately — hiding the glyph without
 * supplying a name would leave a button with no name whatsoever.
 */

import type { ComponentPropsWithoutRef, MouseEvent, ReactNode } from 'react'
import { Link } from 'react-router-dom'

import { Spinner } from '@/design/Spinner/Spinner'
import { cx } from '@/design/cx'
import type { Size, StyleableProps } from '@/design/types'

import styles from '@/design/Button/Button.module.css'

/**
 * What the button *means*. Three of the five carry their own geometry, which is
 * why `size` is legal on all of them but only changes `primary`/`secondary`.
 */
export type ButtonVariant = 'primary' | 'secondary' | 'quiet' | 'iconRound' | 'close'

/**
 * How much room it takes. `Size`'s three, plus the design's fourth shape: a
 * button that shares a row equally with its siblings (368, 416, 519, 694),
 * which is a `flex: 1` rather than a padding step and so cannot be one of them.
 */
export type ButtonSize = Size | 'block'

const VARIANT: Record<ButtonVariant, string | undefined> = {
  primary: styles.primary,
  secondary: styles.secondary,
  quiet: styles.quiet,
  iconRound: styles.iconRound,
  close: styles.close,
}

const SIZE: Record<ButtonSize, string | undefined> = {
  sm: styles.sm,
  md: styles.md,
  lg: styles.lg,
  block: styles.block,
}

interface ButtonOwnProps extends StyleableProps {
  /** The shape. Defaults to `secondary` — the design's quieter, more common half. */
  variant?: ButtonVariant
  /** The room it takes. Defaults to `md` (design 39, 222, 535). */
  size?: ButtonSize
  /**
   * In-app address. Present ⇒ this renders a router `<Link data-plain>` with
   * identical styling, and `type` is not applicable.
   */
  to?: string
  /**
   * Working. Blocks the press, sets `aria-busy`, and draws the inert dot.
   * Never a second POST because somebody pressed twice.
   */
  loading?: boolean
  /**
   * A node set before the label — an icon, a dot, a count. Rendered inside the
   * flex row so it takes the button's own `gap`.
   */
  leading?: ReactNode
  /**
   * The accessible name for a control whose visible content is a glyph.
   * Supplying it hides the children from the accessibility tree, so pass it
   * only where the children really are decoration.
   */
  label?: string
  children?: ReactNode
}

export interface ButtonProps
  extends ButtonOwnProps,
    Omit<ComponentPropsWithoutRef<'button'>, keyof ButtonOwnProps> {
  /**
   * Defaults to `"button"`. A submit-by-default control inside a form is the
   * Cancel that saves — see the note at the top of this file.
   */
  type?: 'button' | 'submit' | 'reset'
}

export function Button({
  variant = 'secondary',
  size = 'md',
  to,
  loading = false,
  leading,
  label,
  children,
  className,
  type = 'button',
  disabled = false,
  onClick,
  ...rest
}: ButtonProps) {
  // One word for the two reasons a press must not land. `loading` is not a
  // separate state to check at every call site: a request in flight and a
  // control the caller switched off are the same answer to "may this fire?".
  const blocked = disabled || loading

  const classes = cx(styles.button, VARIANT[variant], SIZE[size], className)

  const content = (
    <>
      {loading ? <Spinner size="md" tone="current" /> : leading}
      {label === undefined ? children : <span aria-hidden="true">{children}</span>}
      {label === undefined ? null : <span className="visuallyHidden">{label}</span>}
    </>
  )

  if (to !== undefined) {
    // `rest` is typed against `<button>` because that is what this control is
    // nine times in ten. The keys that differ (`form`, `formAction`, `value`)
    // are meaningless on an anchor rather than harmful, and narrowing the prop
    // type per branch would make `<Button to=… disabled>` a compile error at
    // every call site that shares a variable for both.
    const anchorRest = rest as unknown as Omit<ComponentPropsWithoutRef<'a'>, 'href'>

    return (
      <Link
        {...anchorRest}
        to={to}
        data-plain=""
        className={classes}
        // An anchor has no `disabled`; this is the only spelling there is, and
        // the stylesheet fades both.
        aria-disabled={blocked ? 'true' : undefined}
        aria-busy={loading ? 'true' : undefined}
        onClick={(event: MouseEvent<HTMLAnchorElement>) => {
          if (blocked) {
            // Without this the link announces "unavailable" and navigates.
            event.preventDefault()
            return
          }
          onClick?.(event as unknown as MouseEvent<HTMLButtonElement>)
        }}
      >
        {content}
      </Link>
    )
  }

  return (
    <button
      {...rest}
      type={type}
      className={classes}
      disabled={blocked}
      aria-busy={loading ? 'true' : undefined}
      onClick={onClick}
    >
      {content}
    </button>
  )
}
