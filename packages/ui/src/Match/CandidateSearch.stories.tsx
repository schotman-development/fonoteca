import type { Meta, StoryObj } from '@storybook/react-vite'
import { useDeferredValue, useState } from 'react'
import { expect, userEvent, waitFor } from 'storybook/test'

import { Stack } from '../Stack/Stack.tsx'
import { Text } from '../Text/Text.tsx'
import { CandidateSearch } from './CandidateSearch.tsx'
import { filterCandidates } from './filter.ts'
import { RELEASE_CANDIDATES } from './fixtures.ts'

const meta = {
  title: 'Matching/CandidateSearch',
  component: CandidateSearch,
  parameters: { layout: 'padded' },
  args: { value: '', onValueChange: () => undefined, shown: 6, total: 6 },
} satisfies Meta<typeof CandidateSearch>

export default meta
type Story = StoryObj<typeof meta>

/**
 * The hint is not decoration. The mirror runs with no Solr on purpose (ADR 0006)
 * and `IMusicBrainzCatalogue` has no search method, so a box that looked like it
 * queried MusicBrainz would be promising something the architecture will not
 * deliver.
 */
export const Default: Story = {
  play: async ({ canvas }) => {
    await expect(canvas.getByLabelText('Filter candidates')).toBeVisible()
    await expect(canvas.getByText(/does not search MusicBrainz/i)).toBeVisible()
  },
}

/**
 * The count is the **rendered** count, not a prediction — the rows and the number
 * go through one `filterCandidates` call, so they cannot disagree.
 *
 * Diacritics are folded and every whitespace-separated token must appear, so
 * "vinyl 1979" finds the two 1979 pressings and "epic" finds all four Epic
 * releases.
 */
export const Filtering: Story = {
  render: () => {
    const [query, setQuery] = useState('')
    const deferred = useDeferredValue(query.trim())

    const shown = filterCandidates(RELEASE_CANDIDATES, deferred, (candidate) => ({
      title: candidate.title,
      artist: candidate.artist,
      ...(candidate.year != null ? { year: candidate.year } : {}),
      ...(candidate.country != null ? { country: candidate.country } : {}),
      formats: candidate.formats,
      extra: [candidate.status, candidate.label, candidate.catalogNumber].filter(
        (part): part is string => part != null,
      ),
    }))

    return (
      <Stack direction="column" gap={12} align="start">
        <CandidateSearch
          value={query}
          onValueChange={setQuery}
          shown={shown.length}
          total={RELEASE_CANDIDATES.length}
          busy={query.trim() !== deferred}
        />
        <Stack direction="column" gap={2}>
          {shown.map((candidate) => (
            <Text key={candidate.id} size="xs" family="mono" tone="tertiary">
              {candidate.title} · {candidate.year ?? '—'} · {candidate.formats}
            </Text>
          ))}
        </Stack>
      </Stack>
    )
  },
  play: async ({ canvas }) => {
    await userEvent.type(canvas.getByLabelText('Filter candidates'), 'vinyl 1979')

    // `waitFor`, because `useDeferredValue` settles a tick after the keystroke.
    await waitFor(async () => {
      await expect(canvas.getByText('2 of 6 candidates')).toBeVisible()
    })
  },
}

/**
 * Zero is an outcome, not a count. "0 of 6 candidates" reads as arithmetic;
 * this reads as the answer it is.
 */
export const NoMatches: Story = {
  args: { value: 'thriller', shown: 0, total: 6 },
  play: async ({ canvas }) => {
    await expect(canvas.getByText('No candidate matches that.')).toBeVisible()
    await expect(canvas.queryByText('0 of 6 candidates')).toBeNull()
  },
}

/**
 * The box has moved and the list has not caught up. `aria-busy` is written as a
 * real `false` when it settles, unlike the package's data-attribute idiom —
 * `aria-busy="false"` is the announcement that the region is stable, while
 * `data-x="false"` means nothing.
 */
export const Busy: Story = {
  args: { value: 'off the wall', shown: 4, total: 6, busy: true },
  play: async ({ canvas }) => {
    await expect(canvas.getByRole('status')).toHaveAttribute('aria-busy', 'true')
  },
}

/** For a toolbar, where the surrounding copy already says what the box is. */
export const LabelHidden: Story = {
  args: { labelHidden: true, shown: 6, total: 6 },
  play: async ({ canvas }) => {
    await expect(canvas.getByLabelText('Filter candidates')).toBeVisible()
  },
}
