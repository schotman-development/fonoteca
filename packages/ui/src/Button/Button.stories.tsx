import type { Meta, StoryObj } from '@storybook/react-vite'

import { Stack } from '../Stack/Stack.tsx'
import { Button } from './Button.tsx'

const meta = {
  title: 'Primitives/Button',
  component: Button,
  parameters: { layout: 'centered' },
  argTypes: {
    variant: {
      control: 'inline-radio',
      options: ['primary', 'secondary', 'ghost', 'danger'],
    },
    size: { control: 'inline-radio', options: ['sm', 'md'] },
    fullWidth: { control: 'boolean' },
    disabled: { control: 'boolean' },
  },
  args: { children: 'Scan library' },
} satisfies Meta<typeof Button>

export default meta
type Story = StoryObj<typeof meta>

export const Primary: Story = { args: { variant: 'primary' } }
export const Secondary: Story = { args: { variant: 'secondary' } }
export const Ghost: Story = { args: { variant: 'ghost' } }

export const Danger: Story = {
  args: { variant: 'danger', children: 'Delete 412 duplicates' },
}

export const AllVariants: Story = {
  render: (args) => (
    <Stack gap={8}>
      <Stack gap={8} align="center">
        <Button {...args} variant="primary" />
        <Button {...args} variant="secondary" />
        <Button {...args} variant="ghost" />
        <Button {...args} variant="danger" />
      </Stack>
      <Stack gap={8} align="center">
        <Button {...args} variant="primary" disabled />
        <Button {...args} variant="secondary" disabled />
        <Button {...args} variant="ghost" disabled />
        <Button {...args} variant="danger" disabled />
      </Stack>
    </Stack>
  ),
}

export const Sizes: Story = {
  render: (args) => (
    <Stack gap={8} align="center">
      <Button {...args} variant="primary" size="sm" />
      <Button {...args} variant="primary" size="md" />
    </Stack>
  ),
}

/**
 * `type` defaults to "button", not the HTML default of "submit". Clicking this
 * must NOT submit the surrounding form — if it ever does, that is a regression
 * with real consequences in a UI that triggers destructive file operations.
 */
export const DoesNotSubmitItsForm: Story = {
  args: { variant: 'secondary', children: 'Safe inside a form' },
  render: (args) => (
    <form
      onSubmit={(event) => {
        event.preventDefault()
        console.error('REGRESSION: the button submitted its form')
      }}
    >
      <Stack gap={8} align="center">
        <Button {...args} />
        <Button variant="primary" type="submit">
          Explicit submit
        </Button>
      </Stack>
    </form>
  ),
}
