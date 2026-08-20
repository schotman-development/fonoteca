import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect } from 'storybook/test'

import { Button } from '../Button/Button.tsx'
import { Stack } from '../Stack/Stack.tsx'
import { Table, TableCell, TableHeaderCell } from '../Table/Table.tsx'
import { Text } from '../Text/Text.tsx'
import { VisuallyHidden } from './VisuallyHidden.tsx'

const meta = {
  title: 'Primitives/VisuallyHidden',
  component: VisuallyHidden,
  parameters: { layout: 'padded' },
  args: { children: 'Play' },
} satisfies Meta<typeof VisuallyHidden>

export default meta
type Story = StoryObj<typeof meta>

/**
 * The case it was built for. A column of icon buttons wants no visible heading,
 * and an empty `<th>` is an axe `empty-table-header` failure — so the heading is
 * there, and only the eye is spared it.
 */
export const InsideATableHeader: Story = {
  render: () => (
    <Table density="compact">
      <caption>
        <Text size="xs" tone="tertiary">
          Off the Wall — first three tracks
        </Text>
      </caption>
      <thead>
        <tr>
          <TableHeaderCell numeric>#</TableHeaderCell>
          <TableHeaderCell>Title</TableHeaderCell>
          <TableHeaderCell numeric>Length</TableHeaderCell>
          <TableHeaderCell>
            <VisuallyHidden>Play</VisuallyHidden>
          </TableHeaderCell>
        </tr>
      </thead>
      <tbody>
        {[
          { number: '1', title: "Don't Stop 'Til You Get Enough", length: '6:05' },
          { number: '2', title: 'Rock with You', length: '3:40' },
          { number: '3', title: 'Working Day and Night', length: '5:14' },
        ].map((track) => (
          <tr key={track.number}>
            <TableHeaderCell scope="row" numeric>
              {track.number}
            </TableHeaderCell>
            <TableCell>{track.title}</TableCell>
            <TableCell numeric>{track.length}</TableCell>
            <TableCell>
              <Button variant="ghost" size="sm" aria-label={`Play ${track.title}`}>
                ▶
              </Button>
            </TableCell>
          </tr>
        ))}
      </tbody>
    </Table>
  ),
  play: async ({ canvasElement }) => {
    const headers = [...canvasElement.querySelectorAll('thead th')]
    const last = headers.at(-1)
    if (last == null) throw new Error('expected a header row')

    // The name is there for the accessibility tree...
    await expect(last).toHaveTextContent('Play')
    // ...and takes up none of the column.
    await expect(last.getBoundingClientRect().height).toBeLessThan(
      (headers[0]?.getBoundingClientRect().height ?? 0) + 1,
    )
  },
}

/**
 * Why the clip technique rather than `display: none`.
 *
 * Both hide the word from the page. Only one leaves it findable: the button
 * below is named "Delete 412 duplicates" because the count is in the
 * accessibility tree, and `display: none` would have made it "Delete".
 */
export const WhyNotDisplayNone: Story = {
  render: () => (
    <Stack direction="column" gap={12} align="start">
      <Button variant="danger">
        Delete
        <VisuallyHidden> 412 duplicates</VisuallyHidden>
      </Button>
      <Text size="xs" tone="tertiary">
        Reads as “Delete” and announces as “Delete 412 duplicates”.
      </Text>
    </Stack>
  ),
  play: async ({ canvas }) => {
    await expect(canvas.getByRole('button', { name: 'Delete 412 duplicates' })).toBeVisible()
  },
}
