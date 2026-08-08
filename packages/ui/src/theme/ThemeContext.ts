import { createContext, useContext } from 'react'

/**
 * Three states, not two. "system" is the default and is genuinely distinct from
 * an explicit "light" choice — the emitted token stylesheet keys off
 * `prefers-color-scheme` only when no `data-theme` attribute is present.
 */
export type ThemeSetting = 'light' | 'dark' | 'system'

/** What "system" actually resolved to right now. */
export type ResolvedTheme = 'light' | 'dark'

export type ThemeContextValue = {
  /** The user's choice, including "system". */
  readonly setting: ThemeSetting
  /** The concrete theme in effect. Use this to render, never `setting`. */
  readonly resolved: ResolvedTheme
  readonly setSetting: (setting: ThemeSetting) => void
}

export const ThemeContext = createContext<ThemeContextValue | null>(null)

export function useTheme(): ThemeContextValue {
  const value = useContext(ThemeContext)
  if (value === null) {
    throw new Error('useTheme must be used inside a <ThemeProvider>')
  }
  return value
}

export const THEME_STORAGE_KEY = 'fonoteca:theme'
