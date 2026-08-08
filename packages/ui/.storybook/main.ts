import type { StorybookConfig } from '@storybook/react-vite'

const config: StorybookConfig = {
  // No .mdx glob: there are no docs pages yet, and an unmatched pattern makes
  // the test runner warn on every run.
  stories: ['../src/**/*.stories.@(ts|tsx)'],
  addons: [
    // Non-negotiable in this design system. With no third-party primitives
    // underneath, every ARIA role, label and focus behaviour is hand-written,
    // so automated axe checks are the only standing safety net.
    '@storybook/addon-a11y',
    '@storybook/addon-docs',
    // Runs every story as a test in a real browser, which is what turns the
    // a11y addon from something you notice while browsing into something CI
    // enforces. See vitest.config.ts.
    '@storybook/addon-vitest',
  ],
  framework: {
    name: '@storybook/react-vite',
    options: {},
  },
  core: {
    // Self-hosted project; nothing about this build needs to leave the machine.
    disableTelemetry: true,
  },
  typescript: {
    // react-docgen (not react-docgen-typescript) reads props with its own
    // parser rather than the TypeScript compiler API. That matters right now:
    // TypeScript 7.0 shipped without a stable programmatic API, so anything
    // driving tsc from Node is on hold until 7.1.
    reactDocgen: 'react-docgen',
  },
}

export default config
