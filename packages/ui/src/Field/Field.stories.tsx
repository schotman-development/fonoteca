import type { Meta, StoryObj } from '@storybook/react-vite'
import { useState } from 'react'
import { expect } from 'storybook/test'

import { Input } from '../Input/Input.tsx'
import { Stack } from '../Stack/Stack.tsx'
import { Field } from './Field.tsx'

const meta = {
  title: 'Primitives/Field',
  component: Field,
  parameters: { layout: 'padded' },
  args: {
    label: 'Library root',
    children: (control) => <Input {...control} defaultValue="/mnt/music" mono fullWidth />,
  },
  render: (args) => (
    <div style={{ maxWidth: '360px' }}>
      <Field {...args} />
    </div>
  ),
} satisfies Meta<typeof Field>

export default meta
type Story = StoryObj<typeof meta>

/**
 * The `for` is generated, so two fields on one page cannot collide however they
 * are composed — which is the failure a hand-written `htmlFor` eventually has.
 */
export const Default: Story = {
  play: async ({ canvas }) => {
    await expect(canvas.getByLabelText('Library root')).toBeVisible()
  },
}

/** A hint is reachable through `aria-describedby`, not merely printed near the box. */
export const WithHint: Story = {
  args: { hint: 'The directory the scan walks. Symlinked directories are not followed.' },
  play: async ({ canvas }) => {
    const control = canvas.getByLabelText('Library root')
    await expect(control).toHaveAccessibleDescription(/symlinked directories/i)
  },
}

/**
 * The error **replaces** the hint rather than joining it. Two descriptions read
 * one after the other is how an error goes unheard, so there is one message slot
 * and the error wins it.
 */
export const WithError: Story = {
  args: {
    hint: 'The directory the scan walks.',
    error: 'That path is not readable. The scan removes nothing while it cannot see the library.',
    children: (control) => <Input {...control} defaultValue="/mnt/gone" mono fullWidth />,
  },
  play: async ({ canvas }) => {
    const control = canvas.getByLabelText('Library root')
    await expect(control).toHaveAttribute('aria-invalid', 'true')
    await expect(control).toHaveAccessibleDescription(/not readable/i)
    await expect(control).not.toHaveAccessibleDescription(/the scan walks/i)
  },
}

/**
 * Hidden from the eye, kept in the accessible name — for a toolbar where the
 * placeholder and the surrounding copy already say what the box is.
 *
 * It reuses `VisuallyHidden`'s class rather than respelling the clip technique,
 * so a `<label>` that is hidden is hidden exactly as everything else is.
 */
export const LabelHidden: Story = {
  args: {
    label: 'Filter releases',
    labelHidden: true,
    children: (control) => <Input {...control} type="search" placeholder="Filter releases…" />,
  },
  play: async ({ canvas }) => {
    // Invisible, and still the accessible name.
    await expect(canvas.getByLabelText('Filter releases')).toBeVisible()
  },
}

/**
 * The exact composition `CandidateSearch` uses, kept here so the two cannot
 * drift: a search input, a hint that says what the box actually searches, and a
 * polite live region reporting the count the list is showing.
 */
export const SearchField: Story = {
  render: () => {
    const [query, setQuery] = useState('')
    const total = 6
    const shown = query.trim() === '' ? total : 3

    return (
      <Stack direction="column" gap={8} align="start" style={{ maxWidth: '360px' }}>
        <Field
          label="Filter candidates"
          hint="Filters the candidates already gathered. It does not search MusicBrainz."
        >
          {(control) => (
            <Input
              {...control}
              type="search"
              value={query}
              placeholder="Title, artist, year, country…"
              fullWidth
              onChange={(event) => setQuery(event.currentTarget.value)}
            />
          )}
        </Field>
        <div role="status" aria-live="polite">
          {shown === total ? `${total} candidates` : `${shown} of ${total} candidates`}
        </div>
      </Stack>
    )
  },
}
