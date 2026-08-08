import { type ReactNode, useCallback, useEffect, useMemo, useState } from 'react'

import {
  type ResolvedTheme,
  THEME_STORAGE_KEY,
  ThemeContext,
  type ThemeSetting,
} from './ThemeContext.ts'

const DARK_QUERY = '(prefers-color-scheme: dark)'

const isThemeSetting = (value: unknown): value is ThemeSetting =>
  value === 'light' || value === 'dark' || value === 'system'

function readStoredSetting(): ThemeSetting | null {
  try {
    const stored = globalThis.localStorage?.getItem(THEME_STORAGE_KEY)
    return isThemeSetting(stored) ? stored : null
  } catch {
    // Private browsing and some embedded webviews throw on localStorage access.
    // A theme preference is never worth breaking the app over.
    return null
  }
}

function systemTheme(): ResolvedTheme {
  return globalThis.matchMedia?.(DARK_QUERY).matches ? 'dark' : 'light'
}

export type ThemeProviderProps = {
  readonly children: ReactNode
  /** Initial setting when nothing is stored. Defaults to following the system. */
  readonly defaultSetting?: ThemeSetting
  /**
   * Where to write the `data-theme` attribute. Defaults to the document root.
   * Storybook passes the preview iframe's own root so each story frame themes
   * independently.
   */
  readonly target?: HTMLElement | null
}

/**
 * Applies the theme by setting `data-theme` on a root element — or REMOVING it
 * for "system", which is what lets the stylesheet's `prefers-color-scheme`
 * media query take over. Setting `data-theme="light"` and "following the
 * system" are different states and must stay that way.
 */
export function ThemeProvider({
  children,
  defaultSetting = 'system',
  target,
}: ThemeProviderProps): ReactNode {
  const [setting, setSettingState] = useState<ThemeSetting>(
    () => readStoredSetting() ?? defaultSetting,
  )
  const [systemResolved, setSystemResolved] = useState<ResolvedTheme>(systemTheme)

  // Track the OS preference for as long as the setting is "system".
  useEffect(() => {
    const media = globalThis.matchMedia?.(DARK_QUERY)
    if (!media) return undefined

    const onChange = (event: MediaQueryListEvent): void => {
      setSystemResolved(event.matches ? 'dark' : 'light')
    }
    media.addEventListener('change', onChange)
    setSystemResolved(media.matches ? 'dark' : 'light')
    return () => {
      media.removeEventListener('change', onChange)
    }
  }, [])

  const resolved: ResolvedTheme = setting === 'system' ? systemResolved : setting

  useEffect(() => {
    const root = target ?? globalThis.document?.documentElement
    if (!root) return

    if (setting === 'system') {
      root.removeAttribute('data-theme')
    } else {
      root.setAttribute('data-theme', setting)
    }
  }, [setting, target])

  const setSetting = useCallback((next: ThemeSetting): void => {
    setSettingState(next)
    try {
      globalThis.localStorage?.setItem(THEME_STORAGE_KEY, next)
    } catch {
      // See readStoredSetting: persistence is best-effort.
    }
  }, [])

  const value = useMemo(() => ({ setting, resolved, setSetting }), [setting, resolved, setSetting])

  return <ThemeContext value={value}>{children}</ThemeContext>
}
