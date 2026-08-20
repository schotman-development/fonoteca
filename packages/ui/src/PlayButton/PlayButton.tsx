import type { ButtonHTMLAttributes, Ref } from 'react'

import { type PlaybackTrack, useTrackPlayback } from '../playback/PlaybackContext.ts'
import { AlertGlyph, PauseGlyph, PlayGlyph, SpinnerGlyph } from './glyphs.tsx'
import styles from './PlayButton.module.css'

export type PlayButtonSize = 'sm' | 'md'

export type PlayButtonLabels = {
  readonly play: (title: string) => string
  readonly pause: (title: string) => string
  readonly loading: (title: string) => string
  readonly retry: (title: string) => string
}

const DEFAULT_LABELS: PlayButtonLabels = {
  play: (title) => `Play ${title}`,
  pause: (title) => `Pause ${title}`,
  loading: (title) => `Loading ${title}`,
  retry: (title) => `${title} could not be played — try again`,
}

export type PlayButtonProps = Omit<
  ButtonHTMLAttributes<HTMLButtonElement>,
  // The name IS the state, so a caller who overwrote it would freeze the
  // control's only announcement of what it does. `aria-pressed` is omitted
  // because this control deliberately does not have one — see below.
  'type' | 'children' | 'aria-label' | 'aria-labelledby' | 'aria-pressed'
> & {
  readonly track: PlaybackTrack
  /** `sm` fits a `--density-row-compact` table row. */
  readonly size?: PlayButtonSize
  readonly labels?: PlayButtonLabels
  readonly ref?: Ref<HTMLButtonElement>
}

/**
 * Play or pause one track, from anywhere.
 *
 * **The accessible name changes; there is no `aria-pressed`.** Four reasons, in
 * descending order of strength:
 *
 * 1. The state changes without the user. A track ends, a file 404s, autoplay is
 *    refused. A toggle button that un-presses itself is describing something
 *    other than a toggle, and the pattern has no vocabulary for "it stopped on
 *    its own". A name that follows reality has no such problem.
 * 2. "Play, toggle button, pressed" is genuinely ambiguous — does *pressed* mean
 *    audio is coming out, or that I pressed it and it is loading? "Pause"
 *    answers the only question anyone has: what happens if I hit this.
 * 3. There are four states, not two. A boolean cannot carry loading or error;
 *    a name and `aria-busy` can.
 * 4. It is what the APG media player example does.
 *
 * The cost is real and is paid elsewhere: a screen reader does not reliably
 * announce that the *focused* element's name changed. So the change is announced
 * once, globally, by `AudioTransport`'s polite live region — rather than five
 * hundred times by five hundred buttons.
 */
export function PlayButton({
  track,
  size = 'sm',
  labels = DEFAULT_LABELS,
  className,
  onClick,
  ...rest
}: PlayButtonProps) {
  const { isCurrent, isPlaying, isLoading, error, toggle } = useTrackPlayback(track)

  const state = error != null ? 'error' : isLoading ? 'loading' : isPlaying ? 'playing' : 'idle'
  const label =
    error != null
      ? labels.retry(track.title)
      : isLoading
        ? labels.loading(track.title)
        : isPlaying
          ? labels.pause(track.title)
          : labels.play(track.title)

  return (
    <button
      type="button"
      className={className ? `${styles.root} ${className}` : styles.root}
      data-size={size}
      data-state={state}
      data-current={isCurrent || undefined}
      aria-label={label}
      // Emitted for assistive tech and never selected on. That duplication is
      // deliberate and worth the note, because ThemeSwitch's stylesheet
      // establishes the opposite rule — there the ARIA state IS the variant.
      // Here it cannot be: ARIA has no attribute meaning "playing", so keying
      // the stylesheet off ARIA would need two selectors for four states.
      aria-busy={isLoading || undefined}
      onClick={(event) => {
        toggle()
        onClick?.(event)
      }}
      {...rest}
    >
      {state === 'error' ? (
        <AlertGlyph className={styles.glyph} />
      ) : state === 'loading' ? (
        <SpinnerGlyph className={styles.glyph} />
      ) : state === 'playing' ? (
        <PauseGlyph className={styles.glyph} />
      ) : (
        <PlayGlyph className={styles.glyph} />
      )}
    </button>
  )
}
