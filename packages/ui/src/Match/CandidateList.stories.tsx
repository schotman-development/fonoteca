import type { Meta, StoryObj } from '@storybook/react-vite'
import { useDeferredValue, useState } from 'react'
import { expect, userEvent, waitFor } from 'storybook/test'

import { Badge } from '../Badge/Badge.tsx'
import { Button } from '../Button/Button.tsx'
import { Stack } from '../Stack/Stack.tsx'
import { Text } from '../Text/Text.tsx'
import { CandidateCard } from './CandidateCard.tsx'
import { CandidateList, type CandidateSelection } from './CandidateList.tsx'
import { CandidateSearch } from './CandidateSearch.tsx'
import { FitSummary } from './FitSummary.tsx'
import { filterCandidates } from './filter.ts'
import { RELEASE_CANDIDATES, type ReleaseCandidateFixture } from './fixtures.ts'
import { SlotTable } from './SlotTable.tsx'

const QUESTION = 'Which release explains these ten files?'

const REFUSAL = {
  label: 'None of these',
  description:
    'A wrong album is worse than a missing one, and far harder to notice. Refusing leaves the files where they are.',
}

function facts(candidate: ReleaseCandidateFixture): string {
  return [
    candidate.year?.toString(),
    candidate.country,
    candidate.status,
    candidate.formats,
    `${candidate.fit.trackCount} tracks`,
  ]
    .filter(Boolean)
    .join(' · ')
}

function toOption(candidate: ReleaseCandidateFixture) {
  return {
    id: candidate.id,
    render: (selection: Parameters<typeof CandidateCard>[0]['selection']) => (
      <CandidateCard
        title={candidate.title}
        subtitle={candidate.artist}
        facts={facts(candidate)}
        {...(candidate.catalogNumber != null
          ? { detail: `${candidate.label ?? ''} · ${candidate.catalogNumber}` }
          : {})}
        {...(candidate.image != null ? { image: candidate.image } : {})}
        {...(selection != null ? { selection } : {})}
        fit={<FitSummary fit={candidate.fit} />}
        disclosureAside={
          <Badge tone="neutral" size="sm" mono>
            {candidate.fit.held} of {candidate.fit.trackCount}
          </Badge>
        }
      >
        <SlotTable
          caption={
            <Text size="xs" tone="tertiary">
              {candidate.title}
              {candidate.year != null ? ` (${candidate.year})` : ''}
            </Text>
          }
          rows={candidate.slots}
        />
      </CandidateCard>
    ),
  }
}

/** The list is controlled; these stories hold the answer. */
function Answering({
  candidates = RELEASE_CANDIDATES,
  initial = null,
  withRefusal = true,
}: {
  readonly candidates?: readonly ReleaseCandidateFixture[]
  readonly initial?: CandidateSelection | null
  readonly withRefusal?: boolean
}) {
  const [value, setValue] = useState<CandidateSelection | null>(initial)

  return (
    <div style={{ maxWidth: '860px' }}>
      <CandidateList
        label={QUESTION}
        description="Pick the one the audio agrees with, or refuse."
        value={value}
        onSelect={setValue}
        options={candidates.map(toOption)}
        {...(withRefusal ? { refusal: REFUSAL } : {})}
        empty={
          <Text size="sm" tone="tertiary">
            No candidate matches that filter.
          </Text>
        }
        footer={
          <>
            <Button variant="primary" size="sm" disabled={value == null}>
              Commit
            </Button>
            <Text size="xs" tone="tertiary" family="mono">
              answer {value == null ? 'none yet' : value.kind === 'none' ? 'refused' : value.id}
            </Text>
          </>
        }
      />
    </div>
  )
}

const meta = {
  title: 'Matching/CandidateList',
  component: CandidateList,
  parameters: { layout: 'padded' },
  // Every story renders `Answering`, which holds the answer itself — but the
  // list's required props have to be satisfied here for `StoryObj<typeof meta>`
  // to stop demanding them story by story.
  args: { label: QUESTION, value: null, onSelect: () => undefined, options: [] },
} satisfies Meta<typeof CandidateList>

export default meta
type Story = StoryObj<typeof meta>

/** Six candidates in the domain's ranking order. The list never re-sorts them. */
export const ReleaseCandidates: Story = {
  render: () => <Answering />,
  play: async ({ canvas }) => {
    await expect(canvas.getByRole('group', { name: QUESTION })).toBeVisible()
    await expect(canvas.getAllByRole('radio')).toHaveLength(RELEASE_CANDIDATES.length + 1)
  },
}

/**
 * Arrow keys move **and select**, because these are native radios. Nothing here
 * implements roving tabindex — if this story ever needs code to pass, the native
 * group was abandoned somewhere.
 */
export const KeyboardSelection: Story = {
  render: () => <Answering initial={{ kind: 'candidate', id: 'otw-2015' }} />,
  play: async ({ canvas, canvasElement }) => {
    const radios = canvas.getAllByRole('radio')
    const [first, second] = radios
    if (first == null || second == null) throw new Error('expected at least two options')

    first.focus()
    await userEvent.keyboard('{ArrowDown}')

    await expect(second).toBeChecked()
    await expect(second).toHaveFocus()
    await expect(canvasElement.textContent).toContain('answer greatest-hits-bootleg')
  },
}

/**
 * n candidates are **one** tab stop, not n. Tab enters the group at the checked
 * radio; the next Tab leaves the group entirely, landing on the first control
 * inside the selected card.
 */
export const OneTabStop: Story = {
  render: () => <Answering initial={{ kind: 'candidate', id: 'otw-2015' }} />,
  play: async ({ canvas }) => {
    const radios = canvas.getAllByRole('radio')
    const [checked] = radios

    checked?.focus()
    await expect(checked).toHaveFocus()

    await userEvent.tab()
    // Whatever comes next, it is not another radio in this group.
    for (const radio of radios) {
      await expect(radio).not.toHaveFocus()
    }
  },
}

/**
 * Refusing is an **option**, not an escape hatch: the last radio in the same
 * group, reached by the same arrow keys, announced as one of n.
 *
 * That costs a single array entry, and it costs that little precisely because the
 * radios are native — a hand-built radiogroup would have had to grow a special
 * case for it.
 */
export const RefusalIsAnOption: Story = {
  render: () => <Answering />,
  play: async ({ canvas, canvasElement }) => {
    const refusal = canvas.getByRole('radio', { name: /none of these/i })
    await expect(refusal).toHaveAccessibleDescription(/worse than a missing one/i)

    await userEvent.click(refusal)
    await expect(refusal).toBeChecked()
    await expect(canvasElement.textContent).toContain('answer refused')
  },
}

/**
 * Nothing chosen. The question is not pre-answered on the user's behalf — with no
 * radio checked, Tab lands on the first option and the answer stays "none yet".
 */
export const NothingSelected: Story = {
  render: () => <Answering />,
  play: async ({ canvas, canvasElement }) => {
    for (const radio of canvas.getAllByRole('radio')) {
      await expect(radio).not.toBeChecked()
    }
    await expect(canvasElement.textContent).toContain('answer none yet')
    await expect(canvas.getByRole('button', { name: 'Commit' })).toBeDisabled()
  },
}

/**
 * Filtered to nothing — and **the refusal survives**, which is exactly when it is
 * most needed. A screen that hid "none of these" as soon as the list emptied
 * would remove the only remaining answer.
 */
export const Empty: Story = {
  render: () => <Answering candidates={[]} />,
  play: async ({ canvas }) => {
    await expect(canvas.getByText(/no candidate matches/i)).toBeVisible()
    await expect(canvas.getByRole('radio', { name: /none of these/i })).toBeVisible()
  },
}

/**
 * Wired to `CandidateSearch` through `filterCandidates`, so the count in the live
 * region and the rows on screen go through one predicate and cannot disagree.
 *
 * `useDeferredValue` rather than a debounce timer, following `ReleasesPage`.
 */
export const Filtered: Story = {
  render: () => {
    const [query, setQuery] = useState('')
    const deferred = useDeferredValue(query.trim())
    const [value, setValue] = useState<CandidateSelection | null>(null)

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
      <Stack direction="column" gap={16} style={{ maxWidth: '860px' }}>
        <CandidateSearch
          value={query}
          onValueChange={setQuery}
          shown={shown.length}
          total={RELEASE_CANDIDATES.length}
          busy={query.trim() !== deferred}
        />
        <CandidateList
          label={QUESTION}
          value={value}
          onSelect={setValue}
          options={shown.map(toOption)}
          refusal={REFUSAL}
          empty={
            <Text size="sm" tone="tertiary">
              No candidate matches that filter.
            </Text>
          }
        />
      </Stack>
    )
  },
  play: async ({ canvas }) => {
    await userEvent.type(canvas.getByLabelText('Filter candidates'), 'vinyl')

    await waitFor(async () => {
      // Three vinyl pressings, and the count matches the rows.
      await expect(canvas.getByText('3 of 6 candidates')).toBeVisible()
      await expect(canvas.getAllByRole('radio')).toHaveLength(4)
    })
  },
}
