import { dark as darkTokens, flatten, light as lightTokens, scales } from '@fonoteca/tokens'
import type { CSSProperties } from 'react'
import * as React from 'react'
import { useEffect, useMemo, useState } from 'react'
import { addons, types, useGlobals } from 'storybook/manager-api'
import { useTheme } from 'storybook/theming'
import { ThemeSwitch } from '../src/ThemeSwitch/ThemeSwitch.tsx'
import { type ResolvedTheme, ThemeContext, type ThemeSetting } from '../src/theme/ThemeContext.ts'

/**
 * Storybook's theme control IS the design system's own switch.
 *
 * A custom tool rather than a `globalTypes` dropdown, for the obvious reason: the
 * fastest way to find out that a control feels wrong is to have to use it every
 * time you want to see a story in the other theme. Dogfooding, somewhere it costs
 * nothing.
 *
 * Two things make the real component work up here in the manager, which is a
 * different document from the preview it themes:
 *
 * 1. **The state is Storybook's, not the component's.** `ThemeProvider` owns a
 *    setting and persists it to localStorage; here the setting IS the `theme`
 *    global, so the context is bridged to `useGlobals` instead. One source of
 *    truth — the switch, the URL and the preview cannot disagree.
 * 2. **The tokens are injected, not inherited.** `tokens.css` is loaded in the
 *    preview iframe. Rather than leak our `:root` rules into Storybook's own
 *    chrome, the token VALUES are read from the same TypeScript source that
 *    stylesheet is generated from and set as custom properties on a wrapper,
 *    scoped to this tool. Which set depends on Storybook's UI theme rather than
 *    the previewed one: this control lives in the chrome, so it matches the
 *    chrome, and stays legible when you are previewing the opposite theme.
 *
 * One thing does NOT survive the trip, and it is Storybook's doing rather than
 * the component's: the manager stops `keydown` in the capture phase before it
 * reaches the toolbar's contents, so up here the switch answers to the mouse and
 * to Space, but not to Enter or to the arrow keys. All four work in the app,
 * which is the surface the accessibility guarantee is about.
 */

/**
 * The one concession the reuse demands.
 *
 * Storybook builds the manager with esbuild and hard-codes the CLASSIC JSX
 * transform — `jsxFactory: 'React.createElement'`, no automatic runtime, and no
 * way to configure it. Every .tsx it pulls in therefore needs `React` in scope,
 * and `ThemeSwitch.tsx` has no reason to import it: the app compiles with the
 * automatic runtime, where importing React is precisely what you don't do.
 *
 * So rather than add a bogus import to a component to satisfy a dev tool's
 * bundler, the global that transform expects is supplied here, and only if
 * nothing else has claimed it. Ordering is safe: ES imports evaluate before this
 * module's body, but no `createElement` call runs until Storybook renders the
 * toolbar, long after.
 */
const globalScope = globalThis as unknown as { React?: unknown }
globalScope.React ??= React

const ADDON_ID = 'fonoteca/theme'
const TOOL_ID = `${ADDON_ID}/tool`
const DARK_QUERY = '(prefers-color-scheme: dark)'

const isThemeSetting = (value: unknown): value is ThemeSetting =>
  value === 'light' || value === 'dark' || value === 'system'

/**
 * Only consulted when the global is "system", which the switch cannot select but
 * a URL can. The manager's window answers this identically to the preview's.
 */
function useSystemTheme(): ResolvedTheme {
  const [resolved, setResolved] = useState<ResolvedTheme>(() =>
    globalThis.matchMedia?.(DARK_QUERY).matches ? 'dark' : 'light',
  )

  useEffect(() => {
    const media = globalThis.matchMedia?.(DARK_QUERY)
    if (!media) return undefined

    const onChange = (event: MediaQueryListEvent): void => {
      setResolved(event.matches ? 'dark' : 'light')
    }
    media.addEventListener('change', onChange)
    return () => {
      media.removeEventListener('change', onChange)
    }
  }, [])

  return resolved
}

function ThemeTool() {
  const [globals, updateGlobals] = useGlobals()
  const managerTheme = useTheme()
  const systemTheme = useSystemTheme()

  const setting: ThemeSetting = isThemeSetting(globals.theme) ? globals.theme : 'light'
  const resolved: ResolvedTheme = setting === 'system' ? systemTheme : setting

  const value = useMemo(
    () => ({
      setting,
      resolved,
      setSetting: (next: ThemeSetting): void => {
        updateGlobals({ theme: next })
      },
    }),
    [setting, resolved, updateGlobals],
  )

  const style = {
    display: 'flex',
    alignItems: 'center',
    marginInlineStart: 'var(--space-6)',
    ...flatten(scales),
    ...flatten(managerTheme.base === 'dark' ? darkTokens : lightTokens),
  } as CSSProperties

  return (
    <div style={style}>
      {/*
        `.Provider` rather than React 19's context-as-provider shorthand: this
        file is rendered by Storybook's own React, whose major version is
        Storybook's business rather than ours.
      */}
      <ThemeContext.Provider value={value}>
        <ThemeSwitch size="sm" />
      </ThemeContext.Provider>
    </div>
  )
}

addons.register(ADDON_ID, () => {
  addons.add(TOOL_ID, {
    type: types.TOOL,
    title: 'Theme',
    // Hidden on addon tabs, where there is no story to re-theme.
    match: ({ viewMode, tabId }) => !tabId && (viewMode === 'story' || viewMode === 'docs'),
    render: () => <ThemeTool />,
  })
})
