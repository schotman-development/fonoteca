import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect } from 'storybook/test'

import { Button } from '../Button/Button.tsx'
import { Text } from '../Text/Text.tsx'
import {
  ELLA_BERLIN_UNDATED,
  OFF_THE_WALL_1979_US,
  OFF_THE_WALL_2015,
  THE_COLLECTION,
} from './fixtures.ts'
import { SlotTable } from './SlotTable.tsx'

const meta = {
  title: 'Matching/SlotTable',
  component: SlotTable,
  parameters: { layout: 'padded' },
  args: {
    caption: (
      <Text size="xs" tone="tertiary">
        Off the Wall (2015 remaster) — ten slots, ten held
      </Text>
    ),
    rows: OFF_THE_WALL_2015.slots,
  },
} satisfies Meta<typeof SlotTable>

export default meta
type Story = StoryObj<typeof meta>

/**
 * The number cell is a `<th scope="row">`, not a `<td>`, so every other cell
 * announces with it: "1, Don't Stop 'Til You Get Enough, 6:05" rather than three
 * loose values a listener has to reassemble.
 */
export const FullAlbum: Story = {
  play: async ({ canvasElement }) => {
    const rowHeaders = canvasElement.querySelectorAll('tbody th[scope="row"]')
    await expect(rowHeaders).toHaveLength(10)
  },
}

/**
 * A slot with no file is a **row**, never an absent row. "You are missing track
 * 7" is the question the full track list is stored to answer, and a table that
 * quietly omitted the empty slots could not answer it.
 *
 * The word "missing" is in the cell, so the row is not distinguished by colour
 * alone.
 */
export const WithMissingTracks: Story = {
  args: {
    caption: (
      <Text size="xs" tone="tertiary">
        The Collection — 20 of 76 held
      </Text>
    ),
    rows: THE_COLLECTION.slots,
  },
  play: async ({ canvas, canvasElement }) => {
    await expect(canvas.getAllByText('missing').length).toBeGreaterThan(0)
    await expect(canvasElement.querySelectorAll('tr[data-missing]').length).toBeGreaterThan(0)
  },
}

/**
 * The disc column appears because a row needs it, and it is decided from the
 * rows rather than from a prop the caller has to keep in sync with its own data.
 * In JS rather than CSS, because a column's *existence* cannot be a `:has()`.
 */
export const MultiDisc: Story = {
  args: {
    caption: (
      <Text size="xs" tone="tertiary">
        The Collection — five discs
      </Text>
    ),
    rows: THE_COLLECTION.slots,
  },
  play: async ({ canvas }) => {
    await expect(canvas.getByRole('columnheader', { name: 'Disc' })).toBeVisible()
  },
}

/** Ten slots on one disc, so the disc column is not rendered at all. */
export const SingleDisc: Story = {
  play: async ({ canvas }) => {
    await expect(canvas.queryByRole('columnheader', { name: 'Disc' })).toBeNull()
  },
}

/**
 * A dash where nothing was measured, and never `0.00 s`.
 *
 * The dash is what makes the column scannable — "not measured" written twelve
 * times down it would destroy the scan that made the column worth having — so
 * the words reach the accessibility tree through `VisuallyHidden` instead.
 */
export const UnmeasuredDrift: Story = {
  args: {
    caption: (
      <Text size="xs" tone="tertiary">
        Ella in Berlin — MusicBrainz prints no track lengths
      </Text>
    ),
    rows: ELLA_BERLIN_UNDATED.slots,
  },
  play: async ({ canvas }) => {
    await expect(canvas.queryByText('0.00 s')).toBeNull()
  },
}

/**
 * The play column's `<th>` has an accessible name. An empty `<th>` is an
 * `empty-table-header` failure, and it is the likeliest way this component turns
 * the suite red.
 *
 * The button here stands in for `PlayButton`; the assembled screens use the real
 * one, under a `PlaybackProvider`.
 */
export const WithPlayControls: Story = {
  args: {
    renderPlay: (row) => (
      <Button variant="ghost" size="sm" aria-label={`Play ${row.title}`}>
        ▶
      </Button>
    ),
  },
  play: async ({ canvas }) => {
    await expect(canvas.getByRole('columnheader', { name: 'Play' })).toBeInTheDocument()
    await expect(canvas.getByRole('button', { name: 'Play Rock with You' })).toBeVisible()
  },
}

/**
 * A vinyl pressing prints "A1"…"B5", and the printed number wins over the
 * position — it is what is on the label, and the position is only the fallback.
 */
export const PrintedNumbers: Story = {
  args: {
    caption: (
      <Text size="xs" tone="tertiary">
        Off the Wall (1979, US) — Epic FE 35745
      </Text>
    ),
    rows: OFF_THE_WALL_1979_US.slots,
  },
  play: async ({ canvas }) => {
    await expect(canvas.getByRole('rowheader', { name: 'A1' })).toBeVisible()
    await expect(canvas.getByRole('rowheader', { name: 'B5' })).toBeVisible()
  },
}
