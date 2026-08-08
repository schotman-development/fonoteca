import type { Meta, StoryObj } from '@storybook/react-vite'

import { Badge } from '../Badge/Badge.tsx'
import { Button } from '../Button/Button.tsx'
import { Input } from '../Input/Input.tsx'
import { Stack } from '../Stack/Stack.tsx'
import { Text } from '../Text/Text.tsx'
import { ThemeSwitch } from './ThemeSwitch.tsx'

/**
 * These stories drive the REAL theme. Every switch on the page reads the one
 * `ThemeProvider` the preview wraps stories in, so they all move together, and
 * flipping any of them is the same act as using the toolbar's Theme control —
 * which is the point: there is one theme, and this is the component that owns it.
 *
 * The consequence when reviewing this component is that light and dark cannot be
 * shown side by side. The token stylesheet keys off `data-theme` on the document
 * root, so the page is only ever in one theme at a time. Use the toolbar, or this
 * switch, to see the other.
 */
const meta = {
  title: 'Primitives/ThemeSwitch',
  component: ThemeSwitch,
  parameters: { layout: 'centered' },
  argTypes: {
    size: { control: 'inline-radio', options: ['sm', 'md'] },
    label: { control: 'text' },
  },
} satisfies Meta<typeof ThemeSwitch>

export default meta
type Story = StoryObj<typeof meta>

export const Default: Story = {}

export const Sizes: Story = {
  render: (args) => (
    <Stack direction="column" gap={16} align="start">
      <ThemeSwitch {...args} size="sm" />
      <ThemeSwitch {...args} size="md" />
    </Stack>
  ),
}

/**
 * Where it actually lives. `size="sm"` matches the compact row height a toolbar
 * uses, and the switch's sizes are Button's, so the two align without either one
 * carrying a margin.
 */
export const InAToolbar: Story = {
  args: { size: 'sm' },
  render: (args) => (
    <Stack
      gap={8}
      align="center"
      style={{
        width: 'min(680px, 90vw)',
        height: 'var(--density-toolbar-height)',
        paddingInline: 'var(--space-12)',
        border: 'var(--border-width-thin) solid var(--color-border-subtle)',
        borderRadius: 'var(--radius-md)',
        backgroundColor: 'var(--color-surface-raised)',
      }}
    >
      <Text size="sm" weight="semibold">
        Fonoteca
      </Text>
      <Badge tone="neutral" size="sm">
        scaffold
      </Badge>
      <Input inputSize="sm" aria-label="Search library" placeholder="Search library…" />
      <Button size="sm">Scan</Button>
      <ThemeSwitch {...args} />
    </Stack>
  ),
}

/**
 * The third setting, which has no third position.
 *
 * Until someone flips it, the theme setting is "system" — and the switch shows
 * whichever end that resolves to right now, with no marker saying so. Change the
 * OS appearance while it is untouched and the thumb slides on its own; flip it
 * once and the choice becomes explicit and stops following.
 *
 * That is the trade the two-position shape makes. It cannot be flipped back to
 * "system", so anything that needs to offer the way back should say it in words —
 * `useTheme().setSetting('system')` is still there for a settings screen.
 */
export const FollowingTheSystem: Story = {
  render: (args) => (
    <Stack direction="column" gap={12} align="start">
      <ThemeSwitch {...args} />
      <Text size="sm" tone="secondary" block>
        Untouched, this follows the OS. Toggle your desktop's appearance and the thumb moves without
        being asked.
      </Text>
    </Stack>
  ),
}
