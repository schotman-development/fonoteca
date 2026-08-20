import type { Meta, StoryObj } from '@storybook/react-vite'
import type { ReactNode } from 'react'
import { expect, userEvent, waitFor } from 'storybook/test'

import { Artwork } from '../Artwork/Artwork.tsx'
import { cover } from '../CatalogueCard/fixtures.ts'
import { BROKEN_TRACK, OFF_THE_WALL_TRACK, TONE_TRACKS } from '../playback/fixtures.ts'
import {
  PlaybackContext,
  type PlaybackContextValue,
  type PlaybackStatus,
  type PlaybackTrack,
} from '../playback/PlaybackContext.ts'
import { PlaybackProvider } from '../playback/PlaybackProvider.tsx'
import { Stack } from '../Stack/Stack.tsx'
import { Table, TableCell, TableHeaderCell } from '../Table/Table.tsx'
import { Text } from '../Text/Text.tsx'
import { VisuallyHidden } from '../VisuallyHidden/VisuallyHidden.tsx'
import { PlayButton } from './PlayButton.tsx'

/**
 * A pinned context, with no media element anywhere.
 *
 * `loading` and `error` are transient and therefore unphotographable from a real
 * provider; this is what `PlaybackContext` is exported for, exactly as
 * `ThemeContext` is exported for the Storybook manager.
 */
function Pinned({
  track,
  status,
  children,
}: {
  readonly track: PlaybackTrack
  readonly status: PlaybackStatus
  readonly children: ReactNode
}) {
  const value: PlaybackContextValue = {
    track,
    status,
    error: status === 'error' ? { kind: 'unsupported', message: 'Could not play.' } : null,
    volume: 1,
    muted: false,
    play: () => undefined,
    toggle: () => undefined,
    pause: () => undefined,
    resume: () => undefined,
    stop: () => undefined,
    seek: () => undefined,
    setVolume: () => undefined,
    setMuted: () => undefined,
  }

  return <PlaybackContext.Provider value={value}>{children}</PlaybackContext.Provider>
}

const meta = {
  title: 'Playback/PlayButton',
  component: PlayButton,
  parameters: { layout: 'centered' },
  argTypes: { size: { control: 'inline-radio', options: ['sm', 'md'] } },
  args: { track: OFF_THE_WALL_TRACK, size: 'md' },
  decorators: [
    (Story) => (
      <PlaybackProvider>
        <Story />
      </PlaybackProvider>
    ),
  ],
} satisfies Meta<typeof PlayButton>

export default meta
type Story = StoryObj<typeof meta>

/**
 * The name is the state, and there is **no `aria-pressed`** — asserted, so that
 * re-adding it is a failing test and a conversation rather than a silent change
 * of pattern.
 */
export const Default: Story = {
  play: async ({ canvas }) => {
    const button = canvas.getByRole('button', { name: 'Play Don’t Stop ’Til You Get Enough' })
    await expect(button).not.toHaveAttribute('aria-pressed')
    await expect(button).toHaveAttribute('type', 'button')
  },
}

/**
 * The four states side by side, pinned. Two of them cannot be reached on demand
 * from a real element, and all four have to be legible by *shape* — the glyph
 * differs in every one, so none of this is carried by colour.
 */
export const States: Story = {
  decorators: [],
  render: () => (
    <Stack gap={24}>
      {(['idle', 'loading', 'playing', 'error'] as const).map((status) => (
        <Stack key={status} direction="column" gap={6} align="center">
          <Pinned track={OFF_THE_WALL_TRACK} status={status}>
            <PlayButton track={OFF_THE_WALL_TRACK} size="md" />
          </Pinned>
          <Text size="2xs" tone="tertiary" family="mono">
            {status}
          </Text>
        </Stack>
      ))}
    </Stack>
  ),
}

/**
 * **The shared-element guarantee, asserted.** One `HTMLAudioElement` means one
 * current track: clicking a second row takes `data-current` off the first.
 *
 * It also proves the geometry — a 20px `sm` button plus `reset.css`'s 2px focus
 * ring at 2px offset is exactly 28px, one `--density-row-compact`.
 */
export const InATrackTable: Story = {
  parameters: { layout: 'padded' },
  render: () => (
    <Table density="compact">
      <caption>
        <Text size="xs" tone="tertiary">
          Off the Wall — five tracks
        </Text>
      </caption>
      <thead>
        <tr>
          <TableHeaderCell numeric>#</TableHeaderCell>
          <TableHeaderCell>Title</TableHeaderCell>
          <TableHeaderCell>
            <VisuallyHidden>Play</VisuallyHidden>
          </TableHeaderCell>
        </tr>
      </thead>
      <tbody>
        {TONE_TRACKS.map((track, index) => (
          <tr key={track.id}>
            <TableHeaderCell scope="row" numeric>
              {index + 1}
            </TableHeaderCell>
            <TableCell>{track.title}</TableCell>
            <TableCell>
              <PlayButton track={track} size="sm" />
            </TableCell>
          </tr>
        ))}
      </tbody>
    </Table>
  ),
  play: async ({ canvas, canvasElement }) => {
    await userEvent.click(canvas.getByRole('button', { name: /Working Day and Night/ }))
    await waitFor(async () => {
      await expect(canvasElement.querySelectorAll('[data-current]')).toHaveLength(1)
    })

    await userEvent.click(canvas.getByRole('button', { name: /Get on the Floor/ }))
    await waitFor(async () => {
      const current = canvasElement.querySelectorAll('[data-current]')
      await expect(current).toHaveLength(1)
      await expect(current[0]?.getAttribute('aria-label')).toMatch(/Get on the Floor/)
    })
  },
}

/** It is a real `<button>`, so Enter and Space work without a line of code. */
export const KeyboardActivation: Story = {
  play: async ({ canvas }) => {
    const button = canvas.getByRole('button', { name: /Don’t Stop/ })
    button.focus()
    await expect(button).toHaveFocus()

    await userEvent.keyboard('{Enter}')
    await waitFor(async () => {
      await expect(button).toHaveAttribute('data-current')
    })
  },
}

/**
 * A truncated WAV under a real MIME type: it errors deterministically, with no
 * network, and produces the same `MediaError` code a 404 would. The button ends
 * up named for the recovery rather than the action, and shows a warning triangle
 * rather than a red play triangle.
 */
export const ErrorState: Story = {
  args: { track: BROKEN_TRACK },
  play: async ({ canvas }) => {
    await userEvent.click(canvas.getByRole('button', { name: /Play A file that is not there/ }))
    await waitFor(
      async () => {
        await expect(
          canvas.getByRole('button', { name: /could not be played — try again/ }),
        ).toBeVisible()
      },
      { timeout: 2_000 },
    )
  },
}

/**
 * The composition that does **not** trip `nested-interactive`, kept as a story
 * because the wrong one is the obvious thing to try.
 *
 * A `CatalogueCard` with `href` renders one `<a>` wrapping all of its content,
 * and a `<button>` inside an `<a>` is invalid HTML that axe fails. So the play
 * control is the card's *sibling*, not its `meta`, and the link lives inside the
 * card rather than being the card.
 */
export const OnACandidateCard: Story = {
  parameters: { layout: 'padded' },
  render: () => (
    <Stack gap={12} align="center" style={{ maxWidth: '360px' }}>
      <Artwork name="Off the Wall" size="md" src={cover('#f59f00', '#c92a2a')} />
      <Stack direction="column" gap={2} style={{ minWidth: 0, flex: 1 }}>
        <a href="#off-the-wall">Off the Wall</a>
        <Text size="xs" tone="tertiary" truncate>
          Michael Jackson · 2015 · GB · Official
        </Text>
      </Stack>
      <PlayButton track={OFF_THE_WALL_TRACK} size="md" />
    </Stack>
  ),
}
