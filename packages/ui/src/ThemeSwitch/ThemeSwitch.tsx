import type { ButtonHTMLAttributes, Ref } from 'react'

import { useTheme } from '../theme/ThemeContext.ts'
import { MoonGlyph, SunGlyph } from './glyphs.tsx'
import styles from './ThemeSwitch.module.css'

export type ThemeSwitchProps = Omit<
  ButtonHTMLAttributes<HTMLButtonElement>,
  'role' | 'aria-checked' | 'children' | 'type'
> & {
  /** Matches `Button`'s heights, so the two line up in a toolbar. */
  readonly size?: 'sm' | 'md'
  /** Accessible name. Rendered as `aria-label` — the control has no visible text. */
  readonly label?: string
  readonly ref?: Ref<HTMLButtonElement>
}

/**
 * The colour-theme switcher: a sun at one end, a moon at the other, and a thumb
 * that slides between them.
 *
 * **It is checked against `resolved`, not `setting`.** The theme has three
 * settings — light, dark, and following the operating system — but only two of
 * them are things you can *look at*, and this control's whole job is to say
 * which of those two you are looking at. So "system" is resolved in the
 * background and shown as whichever end it lands on: someone who has never
 * touched this, on a machine set to dark, arrives to find the moon lit.
 *
 * Flipping the switch commits an explicit choice, which means "system" is a
 * state the switch can leave but not return to. That is the deliberate cost of
 * two positions instead of three. `ThemeProvider` still defaults to it, still
 * tracks `prefers-color-scheme` while it holds, and `useTheme().setSetting`
 * still accepts it — so a settings screen can offer the way back in words,
 * where a third glyph on a two-ended track could only confuse.
 *
 * ARIA: a native `<button>` with `role="switch"` and `aria-checked`, which is
 * the pattern's own advice — a button already brings Enter, Space, the focus
 * ring and disabled semantics. Left and right arrows are added on top, because
 * a control shaped like a slider invites them.
 */
export function ThemeSwitch({
  size = 'md',
  label = 'Dark mode',
  className,
  onKeyDown,
  onClick,
  ...rest
}: ThemeSwitchProps) {
  const { resolved, setSetting } = useTheme()
  const isDark = resolved === 'dark'

  return (
    <button
      type="button"
      role="switch"
      aria-checked={isDark}
      aria-label={label}
      className={className ? `${styles.track} ${className}` : styles.track}
      data-size={size}
      onClick={(event) => {
        setSetting(isDark ? 'light' : 'dark')
        onClick?.(event)
      }}
      onKeyDown={(event) => {
        if (event.key === 'ArrowLeft' || event.key === 'ArrowRight') {
          // Set the end absolutely rather than toggling, so the keys mean what
          // their direction suggests and holding one down cannot flicker.
          event.preventDefault()
          setSetting(event.key === 'ArrowLeft' ? 'light' : 'dark')
        }
        onKeyDown?.(event)
      }}
      {...rest}
    >
      {/*
        The thumb is out of flow and sits UNDER the glyphs, so whichever glyph it
        has slid to appears to be riding on it. Two elements, no duplicated
        icons, and lighting one is a colour change rather than a swap.
      */}
      <span className={styles.thumb} aria-hidden="true" />
      <SunGlyph className={`${styles.glyph} ${styles.sun}`} />
      <MoonGlyph className={`${styles.glyph} ${styles.moon}`} />
    </button>
  )
}
