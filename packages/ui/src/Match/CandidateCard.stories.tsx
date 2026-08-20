import type { Meta, StoryObj } from '@storybook/react-vite'
import { useState } from 'react'
import { expect, userEvent } from 'storybook/test'

import { Badge } from '../Badge/Badge.tsx'
import { Button } from '../Button/Button.tsx'
import { Text } from '../Text/Text.tsx'
import { CandidateCard } from './CandidateCard.tsx'
import { FitSummary } from './FitSummary.tsx'
import {
  GREATEST_HITS_BOOTLEG,
  JACKSON_MUSICIAN,
  OFF_THE_WALL_2015,
  type ReleaseCandidateFixture,
  SLOE_GIN_LIVE,
  SLOE_GIN_LOSSLESS,
} from './fixtures.ts'
import { SlotTable } from './SlotTable.tsx'

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

const meta = {
  title: 'Matching/CandidateCard',
  component: CandidateCard,
  parameters: { layout: 'padded' },
  // Deliberately minimal. `exactOptionalPropertyTypes` means a story cannot
  // clear an inherited arg by passing `undefined`, so anything not wanted by
  // every story is added per story rather than removed per story.
  args: {
    title: OFF_THE_WALL_2015.title,
    subtitle: OFF_THE_WALL_2015.artist,
    facts: facts(OFF_THE_WALL_2015),
  },
  render: (args) => (
    <div style={{ maxWidth: '820px' }}>
      <CandidateCard {...args} />
    </div>
  ),
} satisfies Meta<typeof CandidateCard>

export default meta
type Story = StoryObj<typeof meta>

/** The full readout, with the slot table collapsed behind a disclosure. */
export const Release: Story = {
  args: {
    detail: 'Epic · 88875 12345 2',
    ...(OFF_THE_WALL_2015.image != null ? { image: OFF_THE_WALL_2015.image } : {}),
    fit: <FitSummary fit={OFF_THE_WALL_2015.fit} />,
    disclosureAside: (
      <Badge tone="neutral" size="sm" mono>
        10 of 10
      </Badge>
    ),
    children: (
      <SlotTable
        caption={
          <Text size="xs" tone="tertiary">
            Off the Wall (2015 remaster)
          </Text>
        }
        rows={OFF_THE_WALL_2015.slots}
      />
    ),
  },
}

export const Expanded: Story = {
  args: { ...Release.args, defaultExpanded: true },
}

/**
 * The identification subject's card: a cluster score, summed sources, an MBID —
 * and **no track table**, which costs nothing to arrange because the table is
 * `children` rather than a prop.
 */
export const Recording: Story = {
  args: {
    title: SLOE_GIN_LOSSLESS.title,
    subtitle: `${SLOE_GIN_LOSSLESS.artist} — ${SLOE_GIN_LOSSLESS.release}`,
    facts: `${SLOE_GIN_LOSSLESS.length} · score ${SLOE_GIN_LOSSLESS.score} · ${SLOE_GIN_LOSSLESS.sources} sources`,
    detail: SLOE_GIN_LOSSLESS.mbid,
    badges: (
      <Badge tone="success" size="sm">
        Best cluster
      </Badge>
    ),
  },
}

/**
 * The classical case `PrimaryCredits` exists for, and the disambiguation case
 * beside it. A circle rather than a square: a person or a group, not a thing you
 * own.
 */
export const Artist: Story = {
  args: {
    title: JACKSON_MUSICIAN.name,
    subtitle: JACKSON_MUSICIAN.type,
    facts: `${JACKSON_MUSICIAN.trackCount} tracks held`,
    detail: `sorts as ${JACKSON_MUSICIAN.sortName}`,
    artworkShape: 'circle',
  },
}

/**
 * Selection is a position (the thick rule down the inline-start edge) and a word
 * ("Selected"). Take every colour away and it still reads — which matters more
 * than it used to, because the radio's own marker is clipped and these two are
 * now the only cues there are.
 *
 * The card body stays on `surface-raised` deliberately — every semantic text pair
 * in the tokens was computed against that surface, and tinting the body would put
 * `warning.text` on a background nobody has measured.
 */
export const Selected: Story = {
  args: {
    ...(OFF_THE_WALL_2015.image != null ? { image: OFF_THE_WALL_2015.image } : {}),
    fit: <FitSummary fit={OFF_THE_WALL_2015.fit} />,
    selection: {
      control: <input type="radio" id="selected-demo" name="demo" defaultChecked readOnly />,
      labelFor: 'selected-demo',
      fitId: 'selected-demo-fit',
      selected: true,
    },
  },
}

/** The ordinary case: the catalogue holds no cover art, so most cards look like this. */
export const WithoutArtwork: Story = {
  args: {
    title: GREATEST_HITS_BOOTLEG.title,
    subtitle: GREATEST_HITS_BOOTLEG.artist,
    facts: facts(GREATEST_HITS_BOOTLEG),
    fit: <FitSummary fit={GREATEST_HITS_BOOTLEG.fit} />,
    badges: (
      <Badge tone="warning" size="sm">
        Bootleg
      </Badge>
    ),
  },
}

/**
 * **The story that protects the entire selection design.**
 *
 * The `<label>` covers only the identity block. Clicking the play control or the
 * disclosure toggle must not select the candidate; clicking the title must.
 *
 * axe will not catch a regression here. A `<label>` is not interactive per ARIA,
 * so a play button that drifted inside one is not a violation of anything — the
 * behaviour simply breaks, and every other test stays green. This assertion is
 * the only thing standing in the way, which is why it checks both directions.
 */
export const NestedControls: Story = {
  render: () => {
    const [selected, setSelected] = useState(false)
    const [plays, setPlays] = useState(0)

    return (
      <div style={{ maxWidth: '820px' }}>
        <CandidateCard
          title={OFF_THE_WALL_2015.title}
          subtitle={OFF_THE_WALL_2015.artist}
          facts={facts(OFF_THE_WALL_2015)}
          fit={<FitSummary fit={OFF_THE_WALL_2015.fit} />}
          actions={
            <Button
              variant="ghost"
              size="sm"
              aria-label="Play Off the Wall"
              onClick={() => setPlays((count) => count + 1)}
            >
              ▶
            </Button>
          }
          selection={{
            control: (
              <input
                type="radio"
                id="nested-demo"
                name="nested"
                checked={selected}
                onChange={() => setSelected(true)}
              />
            ),
            labelFor: 'nested-demo',
            fitId: 'nested-demo-fit',
            selected,
          }}
        >
          <Text size="sm" tone="secondary">
            Ten of ten tracks held.
          </Text>
        </CandidateCard>
        <Text size="xs" tone="tertiary" family="mono">
          selected {String(selected)} · plays {plays}
        </Text>
      </div>
    )
  },
  play: async ({ canvas, canvasElement }) => {
    // Playing does not select.
    await userEvent.click(canvas.getByRole('button', { name: 'Play Off the Wall' }))
    await expect(canvasElement.textContent).toContain('selected false')
    await expect(canvasElement.textContent).toContain('plays 1')

    // Nor does opening the detail.
    await userEvent.click(canvas.getByRole('button', { name: /track by track/i }))
    await expect(canvasElement.textContent).toContain('selected false')

    // The title does.
    await userEvent.click(canvas.getByText('Off the Wall'))
    await expect(canvasElement.textContent).toContain('selected true')
    await expect(canvasElement.textContent).toContain('plays 1')
  },
}

/**
 * **Hidden, not removed** — the other half of the selection design, and the only
 * test that can tell the two apart.
 *
 * `getByRole('radio')` searches the accessibility tree, so it stops finding the
 * control the moment somebody reaches for `display: none`, `visibility: hidden`
 * or the `hidden` attribute instead of the clip. Everything downstream of that
 * would break quietly: no tab stop, no arrow keys, no "2 of 6", and a fieldset
 * announcing a group with nothing in it.
 *
 * The width assertion is the same claim from the other side — the control is
 * genuinely out of sight rather than merely small — so the pair of them pins the
 * design from both ends.
 */
export const ControlIsHiddenNotRemoved: Story = {
  render: () => {
    const [selected, setSelected] = useState(false)

    return (
      <div style={{ maxWidth: '820px' }}>
        <CandidateCard
          title={OFF_THE_WALL_2015.title}
          subtitle={OFF_THE_WALL_2015.artist}
          facts={facts(OFF_THE_WALL_2015)}
          fit={<FitSummary fit={OFF_THE_WALL_2015.fit} />}
          selection={{
            control: (
              <input
                type="radio"
                id="hidden-demo"
                name="hidden"
                checked={selected}
                onChange={() => setSelected(true)}
              />
            ),
            labelFor: 'hidden-demo',
            fitId: 'hidden-demo-fit',
            selected,
          }}
        />
      </div>
    )
  },
  play: async ({ canvas }) => {
    const radio = canvas.getByRole('radio', { name: /Off the Wall/ })

    // In the tree, and takes focus — which is what the arrow keys move.
    radio.focus()
    await expect(radio).toHaveFocus()

    // And out of sight: a pixel, clipped.
    await expect(radio.getBoundingClientRect().width).toBeLessThanOrEqual(1)

    // The label is what selects it now, because it is all there is to click.
    await userEvent.click(canvas.getByText('Off the Wall'))
    await expect(radio).toBeChecked()
  },
}

/**
 * **An unselected option has to look like an option**, and for a while this one
 * did not.
 *
 * The radio is clipped so the native semantics survive (see
 * `ControlIsHiddenNotRemoved`), and every cue that replaced it — the accent edge,
 * the "Selected" badge, the focus ring — only appears once something *is*
 * selected. Until then the card was a bordered box full of evidence with no
 * visible control anywhere on it. Measured on the live matching screen that put
 * the only drawn radio on "None of these", so the one answer nobody wants to
 * give was the only one that looked like an answer.
 *
 * The marker is what closes that, and it is easy to delete by accident: it is
 * `aria-hidden`, so no accessibility check can miss it, and it is decorative, so
 * nothing else in the suite renders it. This asserts the shape of the pair —
 * a control that reports no box, beside paint that does.
 */
export const UnselectedLooksSelectable: Story = {
  render: () => {
    const [selected, setSelected] = useState(false)

    return (
      <div style={{ maxWidth: '820px' }}>
        <CandidateCard
          title={OFF_THE_WALL_2015.title}
          subtitle={OFF_THE_WALL_2015.artist}
          facts={facts(OFF_THE_WALL_2015)}
          selection={{
            control: (
              <input
                type="radio"
                id="marker-demo"
                name="marker"
                checked={selected}
                onChange={() => setSelected(true)}
              />
            ),
            labelFor: 'marker-demo',
            fitId: 'marker-demo-fit',
            selected,
          }}
        />
      </div>
    )
  },
  play: async ({ canvas, canvasElement }) => {
    const radio = canvas.getByRole('radio', { name: /Off the Wall/ })
    await expect(radio).not.toBeChecked()
    await expect(radio.getBoundingClientRect().width).toBeLessThanOrEqual(1)

    // Drawn, inside the label, and the first thing in it — a radio in the
    // gutter, which is what the browser would have put there.
    const marker = canvasElement.querySelector('label > span[aria-hidden="true"]')
    await expect(marker).not.toBeNull()
    await expect(marker?.getBoundingClientRect().width ?? 0).toBeGreaterThanOrEqual(12)

    // And it selects, because it is inside the label rather than beside it.
    await userEvent.click(marker as Element)
    await expect(radio).toBeChecked()
  },
}

/**
 * The bootleg beside the album it loses to. Files explained is the only number
 * the bootleg wins on, and coverage sits right next to it.
 */
export const Compared: Story = {
  render: () => (
    <div style={{ display: 'grid', gap: 'var(--space-12)', maxWidth: '820px' }}>
      {[OFF_THE_WALL_2015, GREATEST_HITS_BOOTLEG].map((candidate) => (
        <CandidateCard
          key={candidate.id}
          title={candidate.title}
          subtitle={candidate.artist}
          facts={facts(candidate)}
          fit={<FitSummary fit={candidate.fit} />}
          {...(candidate.image != null ? { image: candidate.image } : {})}
        />
      ))}
    </div>
  ),
}

/** The live-recording rival, which is why `Sources` must not outvote a near tie. */
export const NearTiedRival: Story = {
  args: {
    title: SLOE_GIN_LIVE.title,
    subtitle: `${SLOE_GIN_LIVE.artist} — ${SLOE_GIN_LIVE.release}`,
    facts: `${SLOE_GIN_LIVE.length} · score ${SLOE_GIN_LIVE.score} · ${SLOE_GIN_LIVE.sources} sources`,
    detail: SLOE_GIN_LIVE.mbid,
    badges: (
      <Badge tone="warning" size="sm">
        Live
      </Badge>
    ),
  },
}
