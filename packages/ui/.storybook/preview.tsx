import type { Decorator, Preview } from '@storybook/react-vite'

import type { ThemeSetting } from '../src/theme/ThemeContext.ts'
import { ThemeProvider } from '../src/theme/ThemeProvider.tsx'

import '@fonoteca/tokens/tokens.css'
import '../src/styles/reset.css'

/**
 * Themes the preview iframe's own root element rather than the toolbar's, so
 * the `data-theme` attribute lands where the token stylesheet is loaded.
 *
 * The `theme` global is set from the toolbar by `ThemeSwitch` itself — see
 * manager.tsx. It still accepts all three settings, including "system", which is
 * a genuinely separate state: it removes the attribute and lets
 * `prefers-color-scheme` decide. A two-position switch cannot select it, so
 * reach it with `?globals=theme:system` or a story's own `globals` when the
 * no-attribute path is what needs checking.
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
  // Declared without a `toolbar` block on purpose: the toolbar control is the
  // design system's own ThemeSwitch, registered as a tool in manager.tsx. A
  // dropdown here as well would be a second control for one piece of state.
  globalTypes: {
    theme: {
      description: 'Colour theme — light | dark | system',
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
