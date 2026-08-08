import type { Meta, StoryObj } from '@storybook/react-vite'

import { Stack } from '../Stack/Stack.tsx'
import { Text } from './Text.tsx'

const meta = {
  title: 'Primitives/Text',
  component: Text,
  args: { children: 'Kind of Blue — Miles Davis' },
} satisfies Meta<typeof Text>

export default meta
type Story = StoryObj<typeof meta>

export const Default: Story = {}

export const Sizes: Story = {
  render: (args) => (
    <Stack direction="column" gap={8}>
      {(['2xs', 'xs', 'sm', 'md', 'lg', 'xl'] as const).map((size) => (
        <Text key={size} {...args} size={size} block>
          {size} — {args.children}
        </Text>
      ))}
    </Stack>
  ),
}

/** Every tone that must meet WCAG AA. `disabled` is covered separately below. */
export const Tones: Story = {
  render: (args) => (
    <Stack direction="column" gap={6}>
      {(
        ['primary', 'secondary', 'tertiary', 'accent', 'success', 'warning', 'danger'] as const
      ).map((tone) => (
        <Text key={tone} {...args} tone={tone} block size="sm">
          {tone}
        </Text>
      ))}
    </Stack>
  ),
}

/**
 * `disabled` is the one tone deliberately below 4.5:1, and it lives in its own
 * story so that the exemption applies to it alone — the story above stays fully
 * checked, and a contrast regression in any other tone still fails the build.
 *
 * WCAG 1.4.3 exempts text that forms part of an inactive control, which is what
 * this tone is for. Rendering it as a bare `<span>` here is the artificial part;
 * axe cannot tell that this sample stands in for the label of a disabled button.
 * Raising it to AA would defeat the purpose, since a "disabled" grey that meets
 * AA is indistinguishable from ordinary body text.
 */
export const DisabledTone: Story = {
  parameters: {
    a11y: {
      config: { rules: [{ id: 'color-contrast', enabled: false }] },
    },
  },
  render: (args) => (
    <Text {...args} tone="disabled" block size="sm">
      disabled — only ever used on genuinely inactive controls
    </Text>
  ),
}

/**
 * The reason the mono face exists. These are the values a dense catalogue table
 * shows in columns, and they are only comparable at a glance when the digits
 * line up.
 */
export const MonospaceForIdentifiers: Story = {
  render: () => (
    <Stack direction="column" gap={4}>
      <Text family="mono" size="sm" block>
        b1a9f3c2e8d47a06 1411 kbps 44.1 kHz/16
      </Text>
      <Text family="mono" size="sm" block>
        7e2c04ab99f13d58 0987 kbps 96.0 kHz/24
      </Text>
      <Text family="mono" size="sm" block>
        3fd8100c7b2e6a94 0320 kbps 44.1 kHz/16
      </Text>
      <Text family="mono" size="sm" tone="tertiary" block>
        d4c1e07f5a3b8926 mbid 2f9a7c31-58ee-4f0b-b0d2-6d3a1c7e4b55
      </Text>
    </Stack>
  ),
}

export const Truncation: Story = {
  render: (args) => (
    <div style={{ width: 220, border: '1px dashed var(--color-border-default)' }}>
      <Text {...args} truncate>
        A title long enough that it cannot possibly fit inside this column
      </Text>
    </div>
  ),
}
