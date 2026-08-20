import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect } from 'storybook/test'

import { Stack } from '../Stack/Stack.tsx'
import { Text } from '../Text/Text.tsx'
import { FitSummary } from './FitSummary.tsx'
import {
  ELLA_BERLIN_UNDATED,
  GREATEST_HITS_BOOTLEG,
  OFF_THE_WALL_1979_US,
  OFF_THE_WALL_2015,
  THE_COLLECTION,
} from './fixtures.ts'

const meta = {
  title: 'Matching/FitSummary',
  component: FitSummary,
  parameters: { layout: 'padded' },
  args: { fit: OFF_THE_WALL_2015.fit },
  render: (args) => (
    <div style={{ maxWidth: '640px' }}>
      <FitSummary {...args} />
    </div>
  ),
} satisfies Meta<typeof FitSummary>

export default meta
type Story = StoryObj<typeof meta>

/** The 2015 remaster: every slot filled, and the audio agrees to the millisecond. */
export const PerfectFit: Story = {
  play: async ({ canvas }) => {
    // Twice: the release is fully covered *and* every track of it is held. They
    // are different questions that happen to have the same answer here, which is
    // exactly why both are on screen.
    await expect(canvas.getAllByText('100%')).toHaveLength(2)
    await expect(canvas.getByText('0.00 s')).toBeVisible()
    await expect(canvas.getByText('10 of 10 tracks')).toBeVisible()
  },
}

/**
 * The 31-track bootleg *Greatest Hits*. It explains seventeen files — more than
 * any album does — and the numbers beside that say why taking the release with
 * the most files is the wrong rule: coverage 0.55, drift 1.99 s, unofficial.
 */
export const TheBootleg: Story = {
  args: { fit: GREATEST_HITS_BOOTLEG.fit },
  play: async ({ canvas }) => {
    await expect(canvas.getByText('1.99 s')).toBeVisible()
    await expect(canvas.getByText('Unofficial')).toBeVisible()
  },
}

/**
 * The five-disc box set the files actually landed on. Twenty files explained,
 * twice the album — and 26% of itself covered.
 *
 * `filesExplained` never appears without `coverage` beside it. That pairing is
 * the whole lesson of the failure, and a component able to show one without the
 * other would let a caller reproduce the bug in the interface.
 */
export const TheBoxSet: Story = {
  args: { fit: THE_COLLECTION.fit },
  play: async ({ canvas }) => {
    await expect(canvas.getAllByText('26%').length).toBeGreaterThan(0)
    await expect(canvas.getByText('20 of 76 slots filled')).toBeVisible()
  },
}

/**
 * `meanDriftMs: null` — MusicBrainz prints no track lengths for this release, so
 * there was nothing to compare the audio against. It reads as a dash and the
 * words "not measurable", never as `0.00 s`.
 */
export const NotMeasurable: Story = {
  args: { fit: ELLA_BERLIN_UNDATED.fit },
  play: async ({ canvas }) => {
    await expect(canvas.queryByText('0.00 s')).toBeNull()
    // Twice, and deliberately: once for the eye under the dash, once for the ear
    // beside it, since the dash itself says nothing to a screen reader.
    await expect(canvas.getAllByText(/not measurable/i)).toHaveLength(2)
  },
}

/**
 * Three editions fitted exactly as well and agreed about where every track sits,
 * so one was chosen — official first, then earliest. The badge says how many
 * others there were, which is the difference between a rank and a coin flip.
 */
export const TiedEditions: Story = {
  args: { fit: OFF_THE_WALL_1979_US.fit },
  play: async ({ canvas }) => {
    await expect(canvas.getByText(/2 other editions fit as well/i)).toBeVisible()
  },
}

/**
 * All five stacked, which is the component's real job: the readouts line up down
 * the column, so a person can see at a glance that the box set's twenty files
 * are worth less than the album's ten.
 */
export const Compared: Story = {
  render: () => (
    <Stack direction="column" gap={20} style={{ maxWidth: '640px' }}>
      {[
        OFF_THE_WALL_2015,
        GREATEST_HITS_BOOTLEG,
        THE_COLLECTION,
        OFF_THE_WALL_1979_US,
        ELLA_BERLIN_UNDATED,
      ].map((candidate) => (
        <Stack key={candidate.id} direction="column" gap={6}>
          <Text size="sm" weight="medium">
            {candidate.title}
            {candidate.year != null ? ` (${candidate.year})` : ''}
          </Text>
          <FitSummary fit={candidate.fit} />
        </Stack>
      ))}
    </Stack>
  ),
}
