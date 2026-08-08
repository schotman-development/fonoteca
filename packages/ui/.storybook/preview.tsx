import type { Decorator, Preview } from '@storybook/react-vite'

import type { ThemeSetting } from '../src/theme/ThemeContext.ts'
import { ThemeProvider } from '../src/theme/ThemeProvider.tsx'

import '@fonoteca/tokens/tokens.css'
import '../src/styles/reset.css'

/**
 * Themes the preview iframe's own root element rather than the toolbar's, so
 * the `data-theme` attribute lands where the token stylesheet is loaded.
 *
 * "system" is offered alongside light and dark because it is a genuinely
 * separate state — it removes the attribute and lets `prefers-color-scheme`
 * decide, which is the path most users are actually on and therefore the one
 * most worth being able to preview.
 */
const isThemeSetting = (value: unknown): value is ThemeSetting =>
  value === 'light' || value === 'dark' || value === 'system'

const withTheme: Decorator = (Story, context) => {
  const raw: unknown = context.globals.theme
  const setting: ThemeSetting = isThemeSetting(raw) ? raw : 'light'
  return (
    <ThemeProvider
      key={setting}
      defaultSetting={setting}
      target={globalThis.document.documentElement}
    >
      <div style={{ padding: 'var(--space-16)' }}>
        <Story />
      </div>
    </ThemeProvider>
  )
}

const preview: Preview = {
  decorators: [withTheme],
  initialGlobals: { theme: 'light' },
  globalTypes: {
    theme: {
      description: 'Colour theme',
      toolbar: {
        title: 'Theme',
        icon: 'circlehollow',
        items: [
          { value: 'light', title: 'Light', icon: 'sun' },
          { value: 'dark', title: 'Dark', icon: 'moon' },
          { value: 'system', title: 'System', icon: 'browser' },
        ],
        dynamicTitle: true,
      },
    },
  },
  parameters: {
    controls: { matchers: { color: /(background|color)$/i, date: /Date$/i } },
    a11y: {
      // Report violations rather than only logging them. CI treats a failing
      // a11y check as a build failure, not advice.
      test: 'error',
    },
  },
}

export default preview
