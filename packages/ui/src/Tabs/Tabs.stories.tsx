import type { Meta, StoryObj } from '@storybook/react-vite'
import { useState } from 'react'
import { expect, userEvent } from 'storybook/test'

import { Text } from '../Text/Text.tsx'
import { Tabs } from './Tabs.tsx'

const TABS = [
  { key: 'overview', label: 'Overview' },
  { key: 'biography', label: 'Biography' },
  { key: 'discography', label: 'Discography' },
] as const

type Key = (typeof TABS)[number]['key']

function Controlled() {
  const [selected, setSelected] = useState<Key>('overview')
  return (
    <Tabs
      tabs={TABS}
      label="Joe Bonamassa"
      selected={selected}
      onSelect={setSelected}
      panel={(key) => <Text>{`The ${key} panel.`}</Text>}
    />
  )
}

const meta = {
  title: 'Primitives/Tabs',
  component: Controlled,
  parameters: { layout: 'padded' },
} satisfies Meta<typeof Controlled>

export default meta
type Story = StoryObj<typeof meta>

/** Only the selected tab is in the tab order; the other panels exist, hidden. */
export const Default: Story = {
  play: async ({ canvas, canvasElement }) => {
    const overview = canvas.getByRole('tab', { name: 'Overview' })
    await expect(overview).toHaveAttribute('aria-selected', 'true')
    await expect(canvas.getByRole('tab', { name: 'Biography' })).toHaveAttribute('tabindex', '-1')
    for (const tab of canvas.getAllByRole('tab')) {
      const panel = tab.getAttribute('aria-controls')
      await expect(canvasElement.querySelector(`#${CSS.escape(panel ?? '')}`)).toBeInTheDocument()
    }
  },
}

/** Arrows move and select, wrapping; Home and End jump. */
export const Keyboard: Story = {
  play: async ({ canvas }) => {
    await userEvent.click(canvas.getByRole('tab', { name: 'Overview' }))
    await userEvent.keyboard('{ArrowLeft}')
    await expect(canvas.getByRole('tab', { name: 'Discography' })).toHaveFocus()
    await expect(canvas.getByRole('tabpanel', { name: 'Discography' })).toBeVisible()
    await userEvent.keyboard('{Home}')
    await expect(canvas.getByRole('tab', { name: 'Overview' })).toHaveAttribute(
      'aria-selected',
      'true',
    )
    await userEvent.keyboard('{End}{ArrowRight}')
    await expect(canvas.getByText('The overview panel.')).toBeVisible()
  },
}
