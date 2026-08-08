import type { Meta, StoryObj } from '@storybook/react-vite'

import { Stack } from '../Stack/Stack.tsx'
import { Badge, type BadgeTone } from './Badge.tsx'

const TONES: readonly BadgeTone[] = ['neutral', 'accent', 'success', 'warning', 'danger', 'info']

const meta = {
  title: 'Primitives/Badge',
  component: Badge,
  args: { children: 'FLAC' },
} satisfies Meta<typeof Badge>

export default meta
type Story = StoryObj<typeof meta>

export const Default: Story = {}

export const AllTonesAndVariants: Story = {
  render: (args) => (
    <Stack direction="column" gap={8}>
      {(['subtle', 'solid', 'outline'] as const).map((variant) => (
        <Stack key={variant} gap={6} align="center" wrap>
          {TONES.map((tone) => (
            <Badge key={tone} {...args} variant={variant} tone={tone}>
              {tone}
            </Badge>
          ))}
        </Stack>
      ))}
    </Stack>
  ),
}

/**
 * How badges will actually be used: quality tier, codec and library state in a
 * catalogue row.
 *
 * Note that every badge is readable without its colour. Tone reinforces the
 * label; it never carries the meaning on its own, because a greyscale display
 * or a red-green colour deficiency would otherwise erase it.
 */
export const InContext: Story = {
  render: () => (
    <Stack gap={6} align="center" wrap>
      <Badge tone="success" variant="subtle" mono>
        FLAC 24/96
      </Badge>
      <Badge tone="neutral" variant="subtle" mono>
        MP3 320
      </Badge>
      <Badge tone="warning" variant="subtle">
        Upgrade available
      </Badge>
      <Badge tone="danger" variant="subtle">
        3 duplicates
      </Badge>
      <Badge tone="info" variant="subtle">
        Unmatched
      </Badge>
      <Badge tone="accent" variant="outline">
        Scanning
      </Badge>
    </Stack>
  ),
}
