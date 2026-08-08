import { flatten, fontSize, light, radius, scales, space } from '@fonoteca/tokens'
import type { Meta, StoryObj } from '@storybook/react-vite'

import { Stack } from '../Stack/Stack.tsx'
import { Text } from '../Text/Text.tsx'

/**
 * The token showcase.
 *
 * Everything here reads from `@fonoteca/tokens` rather than hardcoding values,
 * so it cannot drift from the emitted stylesheet — and switching the toolbar
 * theme re-renders it against the other palette, which is the fastest way to
 * catch a semantic token that only works in one theme.
 */
const meta = {
  title: 'Foundations/Tokens',
  parameters: { layout: 'fullscreen' },
} satisfies Meta

export default meta
type Story = StoryObj<typeof meta>

const Section = ({ title, children }: { title: string; children: React.ReactNode }) => (
  <Stack direction="column" gap={12} style={{ marginBlockEnd: 'var(--space-32)' }}>
    <Text size="lg" weight="semibold" block>
      {title}
    </Text>
    {children}
  </Stack>
)

const colorGroups = (): Map<string, string[]> => {
  const groups = new Map<string, string[]>()
  for (const name of Object.keys(flatten({ color: light.color }))) {
    // '--color-surface-base' -> group 'surface'
    const group = name.split('-')[3] ?? 'other'
    groups.set(group, [...(groups.get(group) ?? []), name])
  }
  return groups
}

export const Colour: Story = {
  render: () => (
    <Stack direction="column" gap={16} style={{ padding: 'var(--space-24)' }}>
      {[...colorGroups()].map(([group, names]) => (
        <Section key={group} title={group}>
          <Stack gap={8} wrap>
            {names.map((name) => (
              <Stack key={name} direction="column" gap={4} style={{ width: 168 }}>
                <div
                  style={{
                    height: 44,
                    borderRadius: 'var(--radius-sm)',
                    border: '1px solid var(--color-border-subtle)',
                    background: `var(${name})`,
                  }}
                />
                <Text size="2xs" family="mono" tone="secondary" truncate>
                  {name}
                </Text>
              </Stack>
            ))}
          </Stack>
        </Section>
      ))}
    </Stack>
  ),
}

export const Spacing: Story = {
  render: () => (
    <Stack direction="column" gap={16} style={{ padding: 'var(--space-24)' }}>
      <Section title="Spacing — 2px base, tuned for dense layout">
        <Stack direction="column" gap={4}>
          {Object.keys(space).map((key) => (
            <Stack key={key} gap={12} align="center">
              <Text size="xs" family="mono" tone="secondary" style={{ width: 72 }}>
                space-{key}
              </Text>
              <div
                style={{
                  height: 12,
                  width: `var(--space-${key})`,
                  background: 'var(--color-accent-solid)',
                  borderRadius: 'var(--radius-xs)',
                }}
              />
            </Stack>
          ))}
        </Stack>
      </Section>
    </Stack>
  ),
}

export const Typography: Story = {
  render: () => (
    <Stack direction="column" gap={16} style={{ padding: 'var(--space-24)' }}>
      <Section title="Type scale — steps 1px apart below 16px, because dense UI needs the precision">
        <Stack direction="column" gap={8}>
          {Object.entries(fontSize).map(([key, value]) => (
            <Stack key={key} gap={16} align="baseline">
              <Text size="xs" family="mono" tone="secondary" style={{ width: 96 }}>
                {key} / {value}
              </Text>
              <span style={{ fontSize: `var(--font-size-${key})` }}>
                Sketches of Spain — Miles Davis
              </span>
            </Stack>
          ))}
        </Stack>
      </Section>

      <Section title="Monospace — identifiers, hashes, bitrates">
        <Text family="mono" size="sm" block>
          fpcalc AQADtEmiKEkSJRGPHj2OH8dxHMdxHMdx 2f9a7c31-58ee-4f0b
        </Text>
      </Section>
    </Stack>
  ),
}

export const RadiiAndElevation: Story = {
  render: () => (
    <Stack direction="column" gap={16} style={{ padding: 'var(--space-24)' }}>
      <Section title="Radius">
        <Stack gap={12} wrap>
          {Object.keys(radius).map((key) => (
            <Stack key={key} direction="column" gap={4} align="center">
              <div
                style={{
                  width: 64,
                  height: 64,
                  background: 'var(--color-surface-inset)',
                  border: '1px solid var(--color-border-default)',
                  borderRadius: `var(--radius-${key})`,
                }}
              />
              <Text size="2xs" family="mono" tone="secondary">
                {key}
              </Text>
            </Stack>
          ))}
        </Stack>
      </Section>

      <Section title="Elevation — weaker in dark, where borders do more of the work">
        <Stack gap={24} wrap>
          {(['sm', 'md', 'lg'] as const).map((key) => (
            <Stack key={key} direction="column" gap={8} align="center">
              <div
                style={{
                  width: 120,
                  height: 72,
                  background: 'var(--color-surface-raised)',
                  border: '1px solid var(--color-border-subtle)',
                  borderRadius: 'var(--radius-md)',
                  boxShadow: `var(--shadow-${key})`,
                }}
              />
              <Text size="2xs" family="mono" tone="secondary">
                shadow-{key}
              </Text>
            </Stack>
          ))}
        </Stack>
      </Section>
    </Stack>
  ),
}

export const Density: Story = {
  render: () => (
    <Stack direction="column" gap={16} style={{ padding: 'var(--space-24)' }}>
      <Section title="Row density — fixed heights, because a virtualizer must know them before render">
        <Stack direction="column" gap={16}>
          {Object.entries(scales.density).map(([key, value]) => (
            <Stack key={key} direction="column" gap={4}>
              <Text size="xs" family="mono" tone="secondary">
                {key} / {value}
              </Text>
              <div
                style={{
                  height: `var(--density-${key.replace(/([a-z0-9])([A-Z])/g, '$1-$2').toLowerCase()})`,
                  background: 'var(--color-surface-inset)',
                  border: '1px solid var(--color-border-subtle)',
                  borderRadius: 'var(--radius-xs)',
                }}
              />
            </Stack>
          ))}
        </Stack>
      </Section>
    </Stack>
  ),
}
