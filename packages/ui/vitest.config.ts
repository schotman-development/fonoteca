// No setup file: since Storybook 10.3 the addon applies the preview's
// annotations — decorators, globals and the a11y parameters — automatically.
// A setProjectAnnotations call here would duplicate that and the addon warns.
import { storybookTest } from '@storybook/addon-vitest/vitest-plugin'
import { playwright } from '@vitest/browser-playwright'
import { defineConfig } from 'vitest/config'

/**
 * Runs every story as a test in a real Chromium.
 *
 * This is the enforcement half of the design system's zero-dependency bet. With
 * no primitives library underneath, every ARIA attribute, role and focus
 * behaviour is hand-written — so the only thing standing between a refactor and
 * a keyboard-inaccessible dialog is an axe run that fails the build.
 *
 * A real browser rather than jsdom, deliberately: axe checks computed styles,
 * contrast and the accessibility tree, none of which jsdom models faithfully.
 * Contrast in particular is a large part of what we need checked, given the
 * palette is hand-built and has to hold up in both themes.
 *
 * Chromium comes from Playwright's own cache in ~/.cache/ms-playwright and
 * needs nothing from apt — verified on this host. See docs/toolchain.md.
 */
export default defineConfig({
  plugins: [storybookTest({ configDir: '.storybook' })],
  test: {
    name: 'storybook',
    browser: {
      enabled: true,
      headless: true,
      provider: playwright(),
      instances: [{ browser: 'chromium' }],
    },
  },
})
