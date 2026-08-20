import { type HTMLAttributes, type ReactNode, type Ref, useEffect, useRef, useState } from 'react'

import { PlayButton } from '../PlayButton/PlayButton.tsx'
import { formatTime } from '../playback/formatTime.ts'
import {
  type PlaybackStatus,
  usePlayback,
  usePlaybackProgress,
} from '../playback/PlaybackContext.ts'
import { Slider } from '../Slider/Slider.tsx'
import { Text } from '../Text/Text.tsx'
import { VisuallyHidden } from '../VisuallyHidden/VisuallyHidden.tsx'
import styles from './AudioTransport.module.css'
import { CloseGlyph, SpeakerGlyph, SpeakerMutedGlyph } from './glyphs.tsx'

export type AudioTransportLabels = {
  readonly region: string
  readonly seek: string
  readonly volume: string
  readonly mute: string
  readonly unmute: string
  readonly close: string
  readonly unknownLength: string
}

const DEFAULT_LABELS: AudioTransportLabels = {
  region: 'Now playing',
  seek: 'Seek',
  volume: 'Volume',
  mute: 'Mute',
  unmute: 'Unmute',
  close: 'Close player',
  unknownLength: 'Length unknown',
}

export type AudioTransportProps = Omit<HTMLAttributes<HTMLElement>, 'children'> & {
  readonly labels?: Partial<AudioTransportLabels>
  /** Hide the volume group where there is no room for it. */
  readonly showVolume?: boolean
  /** Rendered at the end. "Previous" and "next" need a queue, which is the app's. */
  readonly actions?: ReactNode
  readonly ref?: Ref<HTMLElement>
}

/**
 * The bar at the bottom of the application, showing whatever is audible.
 *
 * It renders `null` when nothing is playing. `AppShell`'s slot is already
 * `.transport:not(:empty) { height: … }`, so returning nothing collapses 72px of
 * a screen tuned for six-figure row counts — and a permanently visible bar in a
 * "nothing loaded" state would need a disabled variant of every control in it,
 * all of which would be axe surface and none of which would tell anyone
 * anything. The way back is `stop()`, which is why the bar carries a close
 * button: without one it would be permanent once opened.
 *
 * A `<section aria-label>` is a real landmark, so the player can be jumped to
 * from anywhere on the page.
 */
export function AudioTransport({
  labels,
  showVolume = true,
  actions,
  className,
  ...rest
}: AudioTransportProps) {
  const text = { ...DEFAULT_LABELS, ...labels }
  const playback = usePlayback()
  const progress = usePlaybackProgress()

  // While scrubbing, the slider shows the dragged value and `timeupdate` is
  // ignored. Cleared when the element reports it has caught up, not on
  // pointer-up: a seek on a 30 MB file takes a few hundred milliseconds, and
  // clearing early makes the thumb snap backwards and then forwards again.
  const [scrub, setScrub] = useState<number | null>(null)
  const wasSeeking = useRef(false)

  useEffect(() => {
    if (wasSeeking.current && !progress.seeking) setScrub(null)
    wasSeeking.current = progress.seeking
  }, [progress.seeking])

  const { track } = playback
  if (track == null) return null

  const duration = progress.duration
  const known = duration != null && duration > 0
  const position = scrub ?? progress.currentTime

  return (
    <section
      className={className ? `${styles.root} ${className}` : styles.root}
      aria-label={text.region}
      {...rest}
    >
      <div className={styles.identity}>
        <Text size="sm" weight="medium" truncate block>
          {track.title}
        </Text>
        {/*
          The failure is a sentence, in place of the subtitle — not a red border
          and not a colour on the title. It is here as ordinary text as well as
          in the live region below, because the region announces a change once
          and a person arriving afterwards still has to be able to find out what
          went wrong.
        */}
        {playback.error != null ? (
          <Text size="xs" tone="danger" block>
            {playback.error.message}
          </Text>
        ) : track.subtitle != null ? (
          <Text size="xs" tone="tertiary" truncate block>
            {track.subtitle}
          </Text>
        ) : null}
      </div>

      <div className={styles.controls}>
        <PlayButton track={track} size="md" />
        <Text size="xs" tone="secondary" family="mono" className={styles.time}>
          {formatTime(position)}
        </Text>
        <Slider
          aria-label={text.seek}
          className={styles.seek}
          value={position}
          max={duration ?? 0}
          step={1}
          largeStep={10}
          secondaryValue={progress.buffered}
          disabled={!known}
          formatValue={(value, max) =>
            max > 0 ? `${formatTime(value)} of ${formatTime(max)}` : text.unknownLength
          }
          onValueChange={setScrub}
          onValueCommit={(value) => playback.seek(value)}
        />
        <Text size="xs" tone="secondary" family="mono" className={styles.time}>
          {known ? formatTime(duration) : '--:--'}
        </Text>
      </div>

      {showVolume ? (
        <div className={styles.volume}>
          <button
            type="button"
            className={styles.iconButton}
            aria-label={playback.muted ? text.unmute : text.mute}
            onClick={() => playback.setMuted(!playback.muted)}
          >
            {playback.muted ? (
              <SpeakerMutedGlyph className={styles.glyph} />
            ) : (
              <SpeakerGlyph className={styles.glyph} />
            )}
          </button>
          <Slider
            aria-label={text.volume}
            className={styles.volumeSlider}
            sliderSize="sm"
            tone="neutral"
            value={playback.muted ? 0 : playback.volume}
            min={0}
            max={1}
            step={0.05}
            largeStep={0.2}
            formatValue={(value) => `${Math.round(value * 100)}%`}
            onValueChange={playback.setVolume}
          />
        </div>
      ) : null}

      {actions}

      <button
        type="button"
        className={styles.iconButton}
        aria-label={text.close}
        onClick={playback.stop}
      >
        <CloseGlyph className={styles.glyph} />
      </button>

      {/*
        State only, and short.

        `currentTime` is in a different context and a different subtree, and must
        never reach this element: a live region driven by `timeupdate` speaks
        over everything else on the page four times a second, and is the single
        most destructive thing this component could do.

        The failure *message* stays out of it too. It is rendered above as
        ordinary text, and repeating it here would put the same sentence in the
        accessibility tree twice — heard once as an announcement and again on the
        way past. The region says that something changed; the text says what.
      */}
      <VisuallyHidden role="status" aria-live="polite">
        {announce(playback.status, track.title)}
      </VisuallyHidden>
    </section>
  )
}

function announce(status: PlaybackStatus, title: string): string {
  switch (status) {
    case 'playing':
      return `Playing ${title}`
    case 'paused':
      return `Paused ${title}`
    case 'ended':
      return `Finished ${title}`
    case 'error':
      return `Could not play ${title}`
    default:
      return ''
  }
}
