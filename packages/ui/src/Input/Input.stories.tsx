import type { Meta, StoryObj } from '@storybook/react-vite'

import { Stack } from '../Stack/Stack.tsx'
import { Text } from '../Text/Text.tsx'
import { Input } from './Input.tsx'

const meta = {
  title: 'Primitives/Input',
  component: Input,
  args: {
    placeholder: 'Search artists, albums, recordings…',
    'aria-label': 'Search library',
  },
} satisfies Meta<typeof Input>

export default meta
type Story = StoryObj<typeof meta>

export const Default: Story = {}

export const Sizes: Story = {
  render: (args) => (
    <Stack direction="column" gap={8}>
      <Input {...args} inputSize="sm" />
      <Input {...args} inputSize="md" />
    </Stack>
  ),
}

export const States: Story = {
  render: (args) => (
    <Stack direction="column" gap={8}>
      <Input {...args} />
      <Input {...args} invalid defaultValue="not/a/valid/path" />
      <Input {...args} disabled defaultValue="Disabled" />
    </Stack>
  ),
}

/**
 * The accessible pairing this component deliberately does NOT enforce.
 *
 * `Input` renders only the control, so a label must always be supplied — either
 * a real `<label htmlFor>` as here, or an `aria-label` as in the other stories.
 * The a11y addon fails the story if neither is present, which is what keeps the
 * omission honest until a `Field` component exists to enforce it structurally.
 */
export const WithVisibleLabel: Story = {
  render: () => (
    <Stack direction="column" gap={4}>
      <label htmlFor="library-path">
        <Text size="sm" weight="medium">
          Library path
        </Text>
      </label>
      <Input id="library-path" mono defaultValue="/mnt/music" fullWidth />
    </Stack>
  ),
}
