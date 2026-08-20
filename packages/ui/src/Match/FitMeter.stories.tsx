import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect } from 'storybook/test'

import { Stack } from '../Stack/Stack.tsx'
import { Text } from '../Text/Text.tsx'
import { FitMeter } from './FitMeter.tsx'

const meta = {
  title: 'Matching/FitMeter',
  component: FitMeter,
  parameters: { layout: 'padded' },
  argTypes: {
    tone: {
      control: 'inline-radio',
      options: ['neutral', 'accent', 'success', 'warning', 'danger'],
    },
    size: { control: 'inline-radio', options: ['sm', 'md'] },
  },
  args: { label: 'Coverage', value: 1, detail: '10 of 10 slots filled', tone: 'success' },
  render: (args) => (
    <div style={{ width: '220px' }}>
      <FitMeter {...args} />
    </div>
  ),
} satisfies Meta<typeof FitMeter>

export default meta
type Story = StoryObj<typeof meta>

/** The bar's length and the written percentage are one number, always. */
export const Coverage: Story = {
  play: async ({ canvas }) => {
    await expect(canvas.getByText('100%')).toBeVisible()
  },
}

/**
 * The four coverages that actually turned up in the live runs, in the order the
 * ranking puts them.
 *
 * The last two are the ones worth looking at. A 76-track box set explaining one
 * file covers 1.3% of itself and reads `1%`; a 124-track anthology explaining
 * one covers 0.8% and reads `<1%`. Neither reads `0%` — rounding a real fit down
 * to zero says "explains nothing" about something that explains something, and
 * anthologies of licensed catalogue are exactly where this lands.
 */
export const Range: Story = {
  render: () => (
    <Stack gap={24} align="start" wrap>
      <div style={{ width: '180px' }}>
        <FitMeter label="Off the Wall" value={1} tone="success" detail="10 of 10" />
      </div>
      <div style={{ width: '180px' }}>
        <FitMeter label="Greatest Hits" value={0.55} tone="warning" detail="17 of 31" />
      </div>
      <div style={{ width: '180px' }}>
        <FitMeter label="The Collection" value={0.26} tone="danger" detail="20 of 76" />
      </div>
      <div style={{ width: '180px' }}>
        <FitMeter label="A box set" value={1 / 76} tone="danger" detail="1 of 76" />
      </div>
      <div style={{ width: '180px' }}>
        <FitMeter label="An anthology" value={1 / 124} tone="danger" detail="1 of 124" />
      </div>
    </Stack>
  ),
  play: async ({ canvas }) => {
    await expect(canvas.getByText('1%')).toBeVisible()
    await expect(canvas.getByText('<1%')).toBeVisible()
    await expect(canvas.queryByText('0%')).toBeNull()
  },
}

/**
 * **The invariant of the whole set.** `null` is not zero.
 *
 * A release MusicBrainz prints no track lengths for produces no evidence at all
 * — it passes the drift gate untested and ranks below anything measured. An
 * empty bar would show the strongest possible reading of the weakest possible
 * evidence, so the track is hatched instead and the words say what happened.
 */
export const NotMeasurable: Story = {
  render: () => (
    <Stack gap={24} align="start">
      <div style={{ width: '200px' }}>
        <FitMeter
          label="Mean drift"
          value={null}
          unmeasurable="MusicBrainz prints no track lengths for this release."
        />
      </div>
      <div style={{ width: '200px' }}>
        <FitMeter label="Coverage" value={0} tone="danger" detail="0 of 12 slots filled" />
      </div>
    </Stack>
  ),
  play: async ({ canvas }) => {
    // A dash, never a percentage — and the zero beside it still reads as 0%.
    await expect(canvas.getByText('—')).toBeVisible()
    await expect(canvas.getByText('0%')).toBeVisible()
  },
}

/**
 * Every tone at a legible value, so a contrast regression fails here rather
 * than three components deep inside a candidate card.
 */
export const Tones: Story = {
  render: () => (
    <Stack direction="column" gap={16} style={{ width: '260px' }}>
      {(['neutral', 'accent', 'success', 'warning', 'danger'] as const).map((tone) => (
        <FitMeter key={tone} label={tone} value={0.72} tone={tone} detail="a measured fit" />
      ))}
    </Stack>
  ),
}

/**
 * Four candidates in a column. The alignment is the point — comparing coverage
 * down a list is what the whole screen is for, and it only works if every bar
 * starts in the same place.
 */
export const Aligned: Story = {
  render: () => (
    <Stack direction="column" gap={16} style={{ width: '280px' }}>
      <Text size="xs" tone="tertiary">
        Coverage, four candidates for the same ten files
      </Text>
      <FitMeter label="Off the Wall (2015)" value={1} tone="success" detail="10 of 10" size="sm" />
      <FitMeter label="Greatest Hits" value={0.55} tone="warning" detail="17 of 31" size="sm" />
      <FitMeter label="The Collection" value={0.26} tone="danger" detail="20 of 76" size="sm" />
      <FitMeter label="Ella in Berlin" value={null} unmeasurable="No printed lengths." size="sm" />
    </Stack>
  ),
}
