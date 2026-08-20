import type { Meta, StoryObj } from '@storybook/react-vite'
import { useState } from 'react'
import { expect, userEvent } from 'storybook/test'

import { Badge } from '../Badge/Badge.tsx'
import { Stack } from '../Stack/Stack.tsx'
import { Text } from '../Text/Text.tsx'
import { Disclosure } from './Disclosure.tsx'

const meta = {
  title: 'Primitives/Disclosure',
  component: Disclosure,
  parameters: { layout: 'padded' },
  argTypes: { size: { control: 'inline-radio', options: ['sm', 'md'] } },
  args: {
    summary: 'Track by track',
    children: (
      <Text size="sm" tone="secondary">
        Ten slots, ten files, mean drift 0.00 s.
      </Text>
    ),
  },
  render: (args) => (
    <div style={{ maxWidth: '480px' }}>
      <Disclosure {...args} />
    </div>
  ),
} satisfies Meta<typeof Disclosure>

export default meta
type Story = StoryObj<typeof meta>

/**
 * Closed, and the panel is still in the DOM — `hidden`, not unmounted.
 *
 * `aria-controls` pointing at an id that does not exist is an
 * `aria-valid-attr-value` failure, and it would appear only in this state.
 */
export const Closed: Story = {
  play: async ({ canvas, canvasElement }) => {
    const trigger = canvas.getByRole('button', { name: /track by track/i })
    await expect(trigger).toHaveAttribute('aria-expanded', 'false')

    const panelId = trigger.getAttribute('aria-controls')
    await expect(panelId).not.toBeNull()
    await expect(canvasElement.querySelector(`#${panelId}`)).toBeInTheDocument()
  },
}

export const Toggles: Story = {
  play: async ({ canvas }) => {
    const trigger = canvas.getByRole('button', { name: /track by track/i })

    await userEvent.click(trigger)
    await expect(trigger).toHaveAttribute('aria-expanded', 'true')
    await expect(canvas.getByText(/mean drift/i)).toBeVisible()

    await userEvent.click(trigger)
    await expect(trigger).toHaveAttribute('aria-expanded', 'false')
    await expect(canvas.getByText(/mean drift/i)).not.toBeVisible()
  },
}

/**
 * Controlled, which is how an accordion gets built without this component
 * growing one: the caller holds "which is open", and opening one closes the
 * other. A `Disclosure` that enforced that itself could not be used for the
 * far commoner case of several open at once.
 */
export const Controlled: Story = {
  render: () => {
    const [openId, setOpenId] = useState<string | null>('remaster')

    return (
      <Stack direction="column" gap={4} style={{ maxWidth: '480px' }}>
        <Disclosure
          summary="Off the Wall (2015 remaster)"
          aside={
            <Badge tone="success" size="sm" mono>
              0.00 s
            </Badge>
          }
          open={openId === 'remaster'}
          onOpenChange={(next) => setOpenId(next ? 'remaster' : null)}
        >
          <Text size="sm" tone="secondary">
            Ten of ten tracks held. Nothing else fitted as well.
          </Text>
        </Disclosure>
        <Disclosure
          summary="Off the Wall (1979, US)"
          aside={
            <Badge tone="warning" size="sm" mono>
              0.76 s
            </Badge>
          }
          open={openId === 'original'}
          onOpenChange={(next) => setOpenId(next ? 'original' : null)}
        >
          <Text size="sm" tone="secondary">
            The same track list, and every length 0.76 s away from what the files measure.
          </Text>
        </Disclosure>
      </Stack>
    )
  },
  play: async ({ canvas }) => {
    await userEvent.click(canvas.getByRole('button', { name: /1979, US/ }))

    await expect(canvas.getByRole('button', { name: /1979, US/ })).toHaveAttribute(
      'aria-expanded',
      'true',
    )
    await expect(canvas.getByRole('button', { name: /2015 remaster/ })).toHaveAttribute(
      'aria-expanded',
      'false',
    )
  },
}

/**
 * `detail` survives the collapse, and `summary` stays a label.
 *
 * The distinction is the point: the accessible name is read out every time focus
 * lands on the control, so a sentence belongs beside the button rather than
 * inside it. Here that is the line telling you whether the section is worth
 * opening at all.
 */
export const WithDetail: Story = {
  args: {
    summary: 'Known audio, no recording',
    aside: (
      <Badge tone="neutral" size="sm" mono>
        454
      </Badge>
    ),
    detail: (
      <Text size="xs" tone="tertiary" block>
        Closes when somebody links the cluster to a MusicBrainz recording.
      </Text>
    ),
  },
  play: async ({ canvas }) => {
    const trigger = canvas.getByRole('button', { name: 'Known audio, no recording 454' })

    // Visible while closed, and not part of the button's name.
    await expect(trigger).toHaveAttribute('aria-expanded', 'false')
    await expect(canvas.getByText(/links the cluster/i)).toBeVisible()

    await userEvent.click(trigger)
    await expect(canvas.getByText(/links the cluster/i)).toBeVisible()
  },
}

/**
 * The aside sits inside the button on purpose, so it joins the accessible name.
 * "Track by track, 10 of 10" is one answer to one question; a count parked
 * outside the control it describes is a second thing to go and find.
 */
export const WithAside: Story = {
  args: {
    aside: (
      <Badge tone="neutral" size="sm" mono>
        10 of 10
      </Badge>
    ),
  },
  play: async ({ canvas }) => {
    await expect(canvas.getByRole('button', { name: 'Track by track 10 of 10' })).toBeVisible()
  },
}
