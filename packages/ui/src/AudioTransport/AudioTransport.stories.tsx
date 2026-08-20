import type { Meta, StoryObj } from '@storybook/react-vite'
import { type ReactNode, useState } from 'react'
import { expect, userEvent, waitFor } from 'storybook/test'

import { PlayButton } from '../PlayButton/PlayButton.tsx'
import { LONG_TRACK, OFF_THE_WALL_TRACK, TONE_TRACKS } from '../playback/fixtures.ts'
import {
  PlaybackContext,
  type PlaybackContextValue,
  type PlaybackError,
  type PlaybackProgress,
  PlaybackProgressContext,
  type PlaybackStatus,
  type PlaybackTrack,
} from '../playback/PlaybackContext.ts'
import { PlaybackProvider } from '../playback/PlaybackProvider.tsx'
import { Stack } from '../Stack/Stack.tsx'
import { Text } from '../Text/Text.tsx'
import { AudioTransport } from './AudioTransport.tsx'

/** The AppShell slot, reproduced: 72px when occupied, nothing when empty. */
function Slot({ children }: { readonly children: ReactNode }) {
  return (
    <div
      data-slot="transport"
      style={{
        height: 'var(--density-transport-height)',
        borderBlockStart: 'var(--border-width-thin) solid var(--color-border-subtle)',
        background: 'var(--color-surface-raised)',
      }}
    >
      {children}
    </div>
  )
}

/**
 * Both contexts pinned, with no media element in play. The transport's states —
 * scrubbing, buffering, an unknown length, a failure — are all transient against
 * a real element and all photographable here.
 */
function Pinned({
  track,
  status,
  progress,
  error,
  children,
}: {
  readonly track: PlaybackTrack | null
  readonly status: PlaybackStatus
  readonly progress: PlaybackProgress
  readonly error?: PlaybackError
  readonly children: ReactNode
}) {
  const [muted, setMuted] = useState(false)
  const [volume, setVolume] = useState(0.7)

  const value: PlaybackContextValue = {
    track,
    status,
    error: error ?? null,
    volume,
    muted,
    play: () => undefined,
    toggle: () => undefined,
    pause: () => undefined,
    resume: () => undefined,
    stop: () => undefined,
    seek: () => undefined,
    setVolume,
    setMuted,
  }

  return (
    <PlaybackContext.Provider value={value}>
      <PlaybackProgressContext.Provider value={progress}>
        {children}
      </PlaybackProgressContext.Provider>
    </PlaybackContext.Provider>
  )
}

const PLAYING: PlaybackProgress = {
  currentTime: 83,
  duration: 245,
  seeking: false,
  buffered: 160,
}

const meta = {
  title: 'Playback/AudioTransport',
  component: AudioTransport,
  parameters: { layout: 'fullscreen' },
} satisfies Meta<typeof AudioTransport>

export default meta
type Story = StoryObj<typeof meta>

/**
 * Nothing is playing, so the component renders `null` and the slot really is
 * `:empty` — which is what collapses 72px of a screen tuned for six-figure row
 * counts. React 19 inserts no placeholder node for a `null` return.
 */
export const NothingPlaying: Story = {
  render: () => (
    <PlaybackProvider>
      <Slot>
        <AudioTransport />
      </Slot>
    </PlaybackProvider>
  ),
  play: async ({ canvasElement }) => {
    const slot = canvasElement.querySelector('[data-slot="transport"]')
    await expect(slot).not.toBeNull()
    await expect(slot?.childElementCount).toBe(0)
    await expect(slot?.textContent).toBe('')
  },
}

/** The full bar at its real height, with the times in tabular figures. */
export const Playing: Story = {
  render: () => (
    <Pinned track={OFF_THE_WALL_TRACK} status="playing" progress={PLAYING}>
      <Slot>
        <AudioTransport />
      </Slot>
    </Pinned>
  ),
  play: async ({ canvas }) => {
    await expect(canvas.getByRole('region', { name: 'Now playing' })).toBeVisible()
    await expect(canvas.getByRole('slider', { name: 'Seek' })).toHaveAttribute(
      'aria-valuetext',
      '1:23 of 4:05',
    )
  },
}

/**
 * The height coupling. `--density-transport-height` lives in `AppShell`, and the
 * transport sets no height of its own — restating 72px inside the component
 * would be a second place to get it wrong.
 */
export const InTheAppShellSlot: Story = {
  render: () => (
    <Pinned track={OFF_THE_WALL_TRACK} status="playing" progress={PLAYING}>
      <Stack direction="column" gap={0} style={{ minHeight: '200px' }}>
        <div style={{ flex: 1, padding: 'var(--space-16)' }}>
          <Text size="sm" tone="tertiary">
            The page, above the transport row.
          </Text>
        </div>
        <Slot>
          <AudioTransport />
        </Slot>
      </Stack>
    </Pinned>
  ),
  play: async ({ canvasElement }) => {
    const bar = canvasElement.querySelector('section[aria-label="Now playing"]')
    const slot = canvasElement.querySelector('[data-slot="transport"]')
    if (bar == null || slot == null) throw new Error('expected a transport in a slot')

    // `clientHeight`, not the bounding rect: the slot carries a 1px top border,
    // and `block-size: 100%` resolves against the content box. Comparing border
    // boxes would be off by exactly that border and would look like the
    // component having an opinion about its height, which is the thing being
    // ruled out.
    await expect(bar.getBoundingClientRect().height).toBe(slot.clientHeight)
  },
}

/**
 * A scrub seeks **once**, on release. `onValueChange` moves the elapsed readout
 * throughout the drag; a seek per pixel would issue a range request per mouse
 * move over a 30 MB file.
 */
export const Scrubbing: Story = {
  render: () => {
    const [seeks, setSeeks] = useState(0)

    const value: PlaybackContextValue = {
      track: OFF_THE_WALL_TRACK,
      status: 'playing',
      error: null,
      volume: 0.7,
      muted: false,
      play: () => undefined,
      toggle: () => undefined,
      pause: () => undefined,
      resume: () => undefined,
      stop: () => undefined,
      seek: () => setSeeks((count) => count + 1),
      setVolume: () => undefined,
      setMuted: () => undefined,
    }

    return (
      <PlaybackContext.Provider value={value}>
        <PlaybackProgressContext.Provider value={PLAYING}>
          <Stack direction="column" gap={0}>
            <div style={{ padding: 'var(--space-16)' }}>
              <Text size="xs" tone="tertiary" family="mono">
                seeks {seeks}
              </Text>
            </div>
            <Slot>
              <AudioTransport />
            </Slot>
          </Stack>
        </PlaybackProgressContext.Provider>
      </PlaybackContext.Provider>
    )
  },
  play: async ({ canvas, canvasElement }) => {
    const seek = canvas.getByRole('slider', { name: 'Seek' })
    const rail = seek.parentElement?.querySelector('[aria-hidden="true"]')
    if (rail == null) throw new Error('expected a seek rail')

    const box = rail.getBoundingClientRect()
    await userEvent.pointer([
      { target: rail, coords: { clientX: box.left + box.width * 0.3, clientY: box.top + 2 } },
      { keys: '[MouseLeft>]', target: rail },
      { target: rail, coords: { clientX: box.left + box.width * 0.6, clientY: box.top + 2 } },
      { keys: '[/MouseLeft]', target: rail },
    ])

    await expect(canvasElement.textContent).toContain('seeks 1')
  },
}

/**
 * A length nobody knows yet. There is no `indeterminate` slider state — ARIA 1.2
 * requires `aria-valuenow` and axe enforces it — so the control is a disabled
 * zero-length range that says what it does not know.
 */
export const UnknownLength: Story = {
  render: () => (
    <Pinned
      track={OFF_THE_WALL_TRACK}
      status="loading"
      progress={{ currentTime: 0, duration: null, seeking: false, buffered: 0 }}
    >
      <Slot>
        <AudioTransport />
      </Slot>
    </Pinned>
  ),
  play: async ({ canvas }) => {
    const seek = canvas.getByRole('slider', { name: 'Seek' })
    await expect(seek).toHaveAttribute('aria-disabled', 'true')
    await expect(seek).toHaveAttribute('aria-valuetext', 'Length unknown')
    await expect(seek).toHaveAttribute('aria-valuenow', '0')
  },
}

/** The failure is a sentence, not a red border — and the seek control is inert. */
export const ErrorInTransport: Story = {
  render: () => (
    <Pinned
      track={OFF_THE_WALL_TRACK}
      status="error"
      error={{
        kind: 'unsupported',
        message:
          'Could not play — the file may be missing, or in a format this browser cannot decode.',
      }}
      progress={{ currentTime: 0, duration: null, seeking: false, buffered: 0 }}
    >
      <Slot>
        <AudioTransport />
      </Slot>
    </Pinned>
  ),
  play: async ({ canvas }) => {
    await expect(canvas.getByText(/may be missing/)).toBeVisible()
    await expect(canvas.getByRole('slider', { name: 'Seek' })).toHaveAttribute(
      'aria-disabled',
      'true',
    )
  },
}

/** Muted is a different glyph and a different name, never a dimmer icon. */
export const Volume: Story = {
  render: () => (
    <Pinned track={OFF_THE_WALL_TRACK} status="playing" progress={PLAYING}>
      <Slot>
        <AudioTransport />
      </Slot>
    </Pinned>
  ),
  play: async ({ canvas }) => {
    await expect(canvas.getByRole('slider', { name: 'Volume' })).toHaveAttribute(
      'aria-valuetext',
      '70%',
    )

    await userEvent.click(canvas.getByRole('button', { name: 'Mute' }))
    await expect(canvas.getByRole('button', { name: 'Unmute' })).toBeVisible()
  },
}

/**
 * A classical title and a long credit line. The identity column truncates rather
 * than squeezing the seek bar out — which is what `minmax(0, 1fr)` buys, and
 * what an `auto` track would not.
 */
export const LongTitles: Story = {
  render: () => (
    <Pinned
      track={LONG_TRACK}
      status="playing"
      progress={{ currentTime: 1_845, duration: 3_847, seeking: false, buffered: 2_000 }}
    >
      <Slot>
        <AudioTransport />
      </Slot>
    </Pinned>
  ),
  play: async ({ canvas }) => {
    // Past an hour, so the readout is h:mm:ss and the fixed-width time cells hold.
    await expect(canvas.getByText('1:04:07')).toBeVisible()
  },
}

/**
 * The real provider, end to end: a list of play buttons above, the transport
 * below. Clicking a row makes the bar appear with that title; clicking another
 * changes it, and only one row is ever current.
 */
export const EndToEnd: Story = {
  render: () => (
    <PlaybackProvider>
      <Stack direction="column" gap={0} style={{ minHeight: '260px' }}>
        <Stack direction="column" gap={8} style={{ flex: 1, padding: 'var(--space-16)' }}>
          {TONE_TRACKS.slice(0, 3).map((track) => (
            <Stack key={track.id} gap={8} align="center">
              <PlayButton track={track} size="sm" />
              <Text size="sm">{track.title}</Text>
            </Stack>
          ))}
        </Stack>
        <Slot>
          <AudioTransport />
        </Slot>
      </Stack>
    </PlaybackProvider>
  ),
  play: async ({ canvas }) => {
    await userEvent.click(canvas.getByRole('button', { name: /Play Rock with You/ }))
    await waitFor(async () => {
      await expect(canvas.getByRole('region', { name: 'Now playing' })).toBeVisible()
    })

    await userEvent.click(canvas.getByRole('button', { name: /Working Day and Night/ }))
    await waitFor(async () => {
      const region = canvas.getByRole('region', { name: 'Now playing' })
      await expect(region.textContent).toContain('Working Day and Night')
    })
  },
}
