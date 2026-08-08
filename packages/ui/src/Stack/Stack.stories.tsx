import type { Meta, StoryObj } from '@storybook/react-vite'

import { Badge } from '../Badge/Badge.tsx'
import { Text } from '../Text/Text.tsx'
import { Stack } from './Stack.tsx'

const meta = {
  title: 'Primitives/Stack',
  component: Stack,
  argTypes: {
    direction: { control: 'inline-radio', options: ['row', 'column'] },
    gap: {
      control: 'select',
      options: [0, 2, 4, 6, 8, 12, 16, 20, 24, 32, 40, 48, 64, 96],
    },
    align: {
      control: 'select',
      options: ['start', 'center', 'end', 'stretch', 'baseline'],
    },
    justify: {
      control: 'select',
      options: ['start', 'center', 'end', 'between', 'around'],
    },
  },
  args: { direction: 'row', gap: 8, align: 'center' },
} satisfies Meta<typeof Stack>

export default meta
type Story = StoryObj<typeof meta>

const Box = ({ label }: { label: string }) => (
  <div
    style={{
      padding: 'var(--space-8)',
      background: 'var(--color-surface-inset)',
      borderRadius: 'var(--radius-sm)',
    }}
  >
    <Text size="sm">{label}</Text>
  </div>
)

export const Row: Story = {
  render: (args) => (
    <Stack {...args}>
      <Box label="One" />
      <Box label="Two" />
      <Box label="Three" />
    </Stack>
  ),
}

export const Column: Story = {
  args: { direction: 'column', align: 'start' },
  render: (args) => (
    <Stack {...args}>
      <Box label="One" />
      <Box label="Two" />
      <Box label="Three" />
    </Stack>
  ),
}

/**
 * `min-width: 0` on the root is what lets a truncating child actually shrink.
 * Without it a flex item refuses to go below its content width and the ellipsis
 * never appears — the single most common bug in a dense table layout.
 */
export const AllowsChildrenToTruncate: Story = {
  args: { direction: 'row', gap: 8, align: 'center' },
  render: (args) => (
    <div style={{ width: 320, border: '1px dashed var(--color-border-default)' }}>
      <Stack {...args}>
        <Text size="sm" truncate>
          Sketches of Spain — a title long enough to need truncating here
        </Text>
        <Badge tone="success" mono>
          FLAC
        </Badge>
      </Stack>
    </div>
  ),
}
