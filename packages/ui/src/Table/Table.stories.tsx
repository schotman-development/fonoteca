import type { Meta, StoryObj } from '@storybook/react-vite'

import { Badge } from '../Badge/Badge.tsx'
import { Stack } from '../Stack/Stack.tsx'
import { Text } from '../Text/Text.tsx'
import { Table, TableCell, type TableDensity, TableHeaderCell } from './Table.tsx'

type Track = {
  readonly title: string
  readonly roles: readonly string[]
  readonly duration: string
  readonly files: number
}

const TRACKS: readonly Track[] = [
  {
    title: 'Symphony No. 40 in G minor, K. 550: I. Molto allegro',
    roles: ['conductor'],
    duration: '7:17',
    files: 2,
  },
  {
    title: 'Symphony No. 40 in G minor, K. 550: II. Andante',
    roles: ['conductor'],
    duration: '13:04',
    files: 1,
  },
  { title: 'Nutbush City Limits', roles: ['billed'], duration: '4:12', files: 3 },
  { title: 'Seesaw', roles: ['billed', 'composer'], duration: '3:48', files: 1 },
]

/** The artist page's track list, which is the screen this component was built for. */
function Tracks(props: Parameters<typeof Table>[0]) {
  return (
    <Table {...props}>
      <thead>
        <tr>
          <TableHeaderCell>Track</TableHeaderCell>
          <TableHeaderCell>Credited as</TableHeaderCell>
          <TableHeaderCell numeric>Length</TableHeaderCell>
          <TableHeaderCell numeric>Files</TableHeaderCell>
        </tr>
      </thead>
      <tbody>
        {TRACKS.map((track) => (
          <tr key={track.title}>
            <TableCell truncate>{track.title}</TableCell>
            <TableCell>
              <Stack gap={4} wrap>
                {track.roles.map((role) => (
                  <Badge key={role} tone="neutral">
                    {role}
                  </Badge>
                ))}
              </Stack>
            </TableCell>
            <TableCell numeric>
              <Text family="mono" size="sm">
                {track.duration}
              </Text>
            </TableCell>
            <TableCell numeric>
              <Text family="mono" size="sm">
                {track.files}
              </Text>
            </TableCell>
          </tr>
        ))}
      </tbody>
    </Table>
  )
}

const meta = {
  title: 'Data/Table',
  component: Table,
  render: (args) => <Tracks {...args} />,
} satisfies Meta<typeof Table>

export default meta
type Story = StoryObj<typeof meta>

export const Default: Story = {}

/**
 * Row height comes from the density tokens rather than from padding, so it is a
 * number a virtualizer can be told rather than one it has to measure.
 */
export const Densities: Story = {
  render: (args) => (
    <Stack direction="column" gap={24}>
      {(['compact', 'cozy', 'comfortable'] as const satisfies readonly TableDensity[]).map(
        (density) => (
          <Stack key={density} direction="column" gap={8}>
            <Text size="xs" tone="tertiary">
              {density}
            </Text>
            <Tracks {...args} density={density} />
          </Stack>
        ),
      )}
    </Stack>
  ),
}

/**
 * A caption is the accessible name of a table, and it is the difference between
 * "table with four columns" and "the artist's tracks" when a screen reader
 * lists what is on the page.
 */
export const WithCaption: Story = {
  render: (args) => (
    <Table {...args}>
      <caption
        style={{
          captionSide: 'top',
          textAlign: 'start',
          paddingBlockEnd: 'var(--space-8)',
          color: 'var(--color-text-tertiary)',
          fontSize: 'var(--font-size-xs)',
        }}
      >
        Tracks credited to Herbert von Karajan
      </caption>
      <thead>
        <tr>
          <TableHeaderCell>Track</TableHeaderCell>
          <TableHeaderCell numeric>Length</TableHeaderCell>
        </tr>
      </thead>
      <tbody>
        {TRACKS.map((track) => (
          <tr key={track.title}>
            <TableCell truncate>{track.title}</TableCell>
            <TableCell numeric>
              <Text family="mono" size="sm">
                {track.duration}
              </Text>
            </TableCell>
          </tr>
        ))}
      </tbody>
    </Table>
  ),
}
