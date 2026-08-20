import type { Meta, StoryObj } from '@storybook/react-vite'
import { useState } from 'react'
import { expect, fn, userEvent, waitFor } from 'storybook/test'

import { Button } from '../Button/Button.tsx'
import { PlayButton } from '../PlayButton/PlayButton.tsx'
import { Stack } from '../Stack/Stack.tsx'
import { Text } from '../Text/Text.tsx'
import { OFF_THE_WALL_TRACK, TONE_TRACKS } from './fixtures.ts'
import { usePlayback, usePlaybackProgress } from './PlaybackContext.ts'
import { PlaybackProvider } from './PlaybackProvider.tsx'

/** Something for axe to look at, and something for a play function to read. */
function Debug() {
  const { track, status, error } = usePlayback()
  const { currentTime, duration } = usePlaybackProgress()

  return (
    <Stack direction="column" gap={2}>
      <Text size="xs" tone="tertiary" family="mono" data-testid="status">
        status {status} · track {track?.id ?? 'none'}
      </Text>
      <Text size="xs" tone="tertiary" family="mono">
        {currentTime.toFixed(1)} / {duration?.toFixed(1) ?? '—'}
      </Text>
      {error != null ? (
        <Text size="xs" tone="danger" data-testid="error">
          {error.message}
        </Text>
      ) : null}
    </Stack>
  )
}

const meta = {
  title: 'Playback/PlaybackProvider',
  component: PlaybackProvider,
  parameters: { layout: 'padded' },
  args: { children: null },
} satisfies Meta<typeof PlaybackProvider>

export default meta
type Story = StoryObj<typeof meta>

/**
 * Two independent lists, one element. Only one track can ever be current, and
 * that is a property of the provider rather than a convention the lists keep.
 */
export const OneElementOnly: Story = {
  render: () => (
    <PlaybackProvider>
      <Stack gap={32} align="start">
        {[0, 1].map((column) => (
          <Stack key={column} direction="column" gap={6}>
            {TONE_TRACKS.slice(column * 2, column * 2 + 2).map((track) => (
              <Stack key={track.id} gap={8} align="center">
                <PlayButton track={track} size="sm" />
                <Text size="sm">{track.title}</Text>
              </Stack>
            ))}
          </Stack>
        ))}
      </Stack>
      <Debug />
    </PlaybackProvider>
  ),
  play: async ({ canvas, canvasElement }) => {
    await userEvent.click(canvas.getByRole('button', { name: /Play Rock with You/ }))
    await waitFor(async () => {
      await expect(canvasElement.querySelectorAll('[data-current]')).toHaveLength(1)
    })

    await userEvent.click(canvas.getByRole('button', { name: /Play Get on the Floor/ }))
    await waitFor(async () => {
      await expect(canvasElement.querySelectorAll('[data-current]')).toHaveLength(1)
    })
  },
}

/**
 * What this environment actually permits, documented rather than assumed.
 *
 * Playwright's Chromium is headless with `--mute-audio`, and its autoplay policy
 * is `document-user-activation-required`. Whether `play()` resolves there is not
 * a property of these components, so **no assertion may depend on it** — this
 * one asserts *coherence either way*: if it plays, the button offers to pause
 * it; if the browser refused, a message says so and the button still offers to
 * play. `blocked` is a first-class state for exactly this reason.
 *
 * The click is real Playwright input, so it is a trusted gesture; nothing here
 * ever calls `play()` without one.
 */
export const AutoplayPolicy: Story = {
  render: () => (
    <PlaybackProvider>
      <Stack direction="column" gap={12} align="start">
        <PlayButton track={OFF_THE_WALL_TRACK} size="md" />
        <Debug />
      </Stack>
    </PlaybackProvider>
  ),
  play: async ({ canvas, canvasElement }) => {
    await userEvent.click(canvas.getByRole('button', { name: /^Play / }))

    await waitFor(
      async () => {
        const status = canvasElement.querySelector('[data-testid="status"]')?.textContent ?? ''
        await expect(status).toMatch(/status (playing|paused|ended|error)/)
      },
      { timeout: 2_000 },
    )

    const status = canvasElement.querySelector('[data-testid="status"]')?.textContent ?? ''
    if (status.includes('playing')) {
      await expect(canvas.getByRole('button', { name: /^Pause / })).toBeVisible()
    } else if (status.includes('error')) {
      await expect(canvasElement.querySelector('[data-testid="error"]')).toBeInTheDocument()
    }
  },
}

/**
 * A 1.5-second tone reaching its end. `onEnded` is how an application implements
 * "next"; this package has no queue and is not going to grow one.
 *
 * Asserted in the same either-way shape, for the same reason.
 */
export const Ended: Story = {
  // Conditional spread: `exactOptionalPropertyTypes` means an explicit
  // `undefined` is not assignable to an optional prop.
  render: (args) => (
    <PlaybackProvider {...(args.onEnded != null ? { onEnded: args.onEnded } : {})}>
      <Stack direction="column" gap={12} align="start">
        <PlayButton track={OFF_THE_WALL_TRACK} size="md" />
        <Debug />
      </Stack>
    </PlaybackProvider>
  ),
  args: { onEnded: fn() },
  play: async ({ args, canvas, canvasElement }) => {
    await userEvent.click(canvas.getByRole('button', { name: /^Play / }))

    await waitFor(
      async () => {
        const status = canvasElement.querySelector('[data-testid="status"]')?.textContent ?? ''
        await expect(status).toMatch(/status (ended|error|paused)/)
      },
      { timeout: 5_000 },
    )

    const status = canvasElement.querySelector('[data-testid="status"]')?.textContent ?? ''
    if (status.includes('ended')) {
      await expect(args.onEnded).toHaveBeenCalledWith(OFF_THE_WALL_TRACK)
    }
  },
}

/**
 * Unmounting the provider mid-play must not throw, and a remount must start from
 * `idle` rather than from whatever the last element was doing.
 *
 * The cleanup is where the sharpest one-liner in the file lives: `src` is removed
 * with `removeAttribute`, never set to `''` — the empty string resolves against
 * the document URL, and the browser would fetch this very page as audio.
 */
export const UnmountStopsEverything: Story = {
  render: () => {
    const [mounted, setMounted] = useState(true)

    return (
      <Stack direction="column" gap={12} align="start">
        <Button variant="secondary" size="sm" onClick={() => setMounted((on) => !on)}>
          {mounted ? 'Unmount the player' : 'Mount the player'}
        </Button>
        {mounted ? (
          <PlaybackProvider>
            <Stack direction="column" gap={8} align="start">
              <PlayButton track={OFF_THE_WALL_TRACK} size="md" />
              <Debug />
            </Stack>
          </PlaybackProvider>
        ) : (
          <Text size="sm" tone="tertiary">
            No player.
          </Text>
        )}
      </Stack>
    )
  },
  play: async ({ canvas, canvasElement }) => {
    await userEvent.click(canvas.getByRole('button', { name: /^Play / }))
    await userEvent.click(canvas.getByRole('button', { name: 'Unmount the player' }))
    await expect(canvas.getByText('No player.')).toBeVisible()

    await userEvent.click(canvas.getByRole('button', { name: 'Mount the player' }))
    await waitFor(async () => {
      const status = canvasElement.querySelector('[data-testid="status"]')?.textContent ?? ''
      await expect(status).toContain('status idle')
    })
  },
}
