import type { Meta, StoryObj } from '@storybook/react-vite'

import { Badge } from '../Badge/Badge.tsx'
import { Stack } from '../Stack/Stack.tsx'
import { Text } from '../Text/Text.tsx'
import { Card } from './Card.tsx'

const meta = {
  title: 'Primitives/Card',
  component: Card,
  args: {
    title: 'Library — scan',
    children: (
      <Text size="sm" tone="secondary" block>
        Walks the library root and reconciles the catalogue with what is on disk.
      </Text>
    ),
  },
} satisfies Meta<typeof Card>

export default meta
type Story = StoryObj<typeof meta>

export const Default: Story = {}

/** No title: a plain surface, for content that brings its own heading. */
export const Untitled: Story = {
  args: { title: undefined },
}

export const Padding: Story = {
  render: (args) => (
    <Stack direction="column" gap={16}>
      <Card {...args} title="Comfortable (md)" padding="md" />
      <Card {...args} title="Roomy (lg)" padding="lg" />
    </Stack>
  ),
}

/**
 * The aside carries the card's state, which is what keeps a status out of the
 * body where it would scroll away from the heading it belongs to.
 */
export const WithAside: Story = {
  args: {
    title: 'Library — identify',
    aside: <Badge tone="success">7,677 identified</Badge>,
  },
}

/** How the dashboard actually uses them: a responsive grid of equal surfaces. */
export const InContext: Story = {
  render: () => (
    <div
      style={{
        display: 'grid',
        gridTemplateColumns: 'repeat(auto-fit, minmax(280px, 1fr))',
        gap: 'var(--space-16)',
      }}
    >
      <Card title="Library — scan" aside={<Badge tone="neutral">idle</Badge>}>
        <Text size="sm" tone="secondary" block>
          7,962 files catalogued.
        </Text>
      </Card>
      <Card title="Library — identify" aside={<Badge tone="accent">running</Badge>}>
        <Text size="sm" tone="secondary" block>
          Fingerprinting and asking AcoustID.
        </Text>
      </Card>
      <Card title="MusicBrainz" aside={<Badge tone="success">reachable</Badge>}>
        <Text size="sm" tone="secondary" block>
          Local mirror, ungated.
        </Text>
      </Card>
    </div>
  ),
}
