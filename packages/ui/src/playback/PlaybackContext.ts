import { createContext, useContext } from 'react'

/**
 * What the player needs to know about a track, and nothing more.
 *
 * No import from the API client, deliberately: this package has no knowledge of
 * `MediaFile`, `Recording` or `Release`, and a track built from a local file, a
 * provider's preview and a story fixture has to be one type. The caller adapts;
 * the player does not.
 */
export type PlaybackTrack = {
  readonly id: string
  readonly src: string
  /** The accessible name of every control that acts on this track. */
  readonly title: string
  readonly subtitle?: string
  /** Seconds. What the catalogue already knows, used until metadata arrives. */
  readonly duration?: number
  /**
   * e.g. `'audio/flac'`. Optional, and the only way to tell a missing file from
   * an undecodable one — `MediaError` reports both as `SRC_NOT_SUPPORTED`.
   */
  readonly mimeType?: string
}

export type PlaybackStatus = 'idle' | 'loading' | 'playing' | 'paused' | 'ended' | 'error'

/**
 * `blocked` is not a failure of the file. It is the browser's autoplay policy
 * refusing a `play()` that did not follow a user gesture, and it needs different
 * words on screen and a different reaction from the caller.
 */
export type PlaybackErrorKind = 'unsupported' | 'network' | 'decode' | 'blocked' | 'unknown'

export type PlaybackError = {
  readonly kind: PlaybackErrorKind
  /** Ready to render. */
  readonly message: string
}

export type PlaybackContextValue = {
  readonly track: PlaybackTrack | null
  readonly status: PlaybackStatus
  readonly error: PlaybackError | null
  readonly volume: number
  readonly muted: boolean
  /** Loads and plays `track`, replacing whatever was playing. */
  readonly play: (track: PlaybackTrack) => void
  /** Play if this is not the current track or is paused; pause if it is playing. */
  readonly toggle: (track: PlaybackTrack) => void
  readonly pause: () => void
  readonly resume: () => void
  /** Clears the track entirely. This is what collapses the transport slot. */
  readonly stop: () => void
  readonly seek: (seconds: number) => void
  readonly setVolume: (volume: number) => void
  readonly setMuted: (muted: boolean) => void
}

/**
 * The hot half, in its own context.
 *
 * This changes roughly four times a second while audio plays;
 * `PlaybackContextValue` changes when a person does something. Merging them
 * would re-render every play button in a five-hundred-row table four times a
 * second in order to move one progress bar.
 */
export type PlaybackProgress = {
  readonly currentTime: number
  /** `null` until metadata arrives, and forever for a stream of unknown length. */
  readonly duration: number | null
  readonly seeking: boolean
  /** Seconds of contiguous buffered audio ahead of the playhead. */
  readonly buffered: number
}

/**
 * Public, exactly as `ThemeContext` is: a story that wants to photograph
 * `loading` or `error` — states that are transient and otherwise
 * unphotographable — pins them by providing a value, with no media element in
 * play at all.
 */
export const PlaybackContext = createContext<PlaybackContextValue | null>(null)

export const PlaybackProgressContext = createContext<PlaybackProgress | null>(null)

export function usePlayback(): PlaybackContextValue {
  const value = useContext(PlaybackContext)
  if (value === null) {
    throw new Error('usePlayback must be used inside a <PlaybackProvider>')
  }
  return value
}

export function usePlaybackProgress(): PlaybackProgress {
  const value = useContext(PlaybackProgressContext)
  if (value === null) {
    throw new Error('usePlaybackProgress must be used inside a <PlaybackProvider>')
  }
  return value
}

/** What one row asks: am I the one, and what is it doing? */
export type TrackPlayback = {
  readonly isCurrent: boolean
  readonly isPlaying: boolean
  readonly isLoading: boolean
  /** Only this track's error. A failure on another track is not this row's news. */
  readonly error: PlaybackError | null
  readonly toggle: () => void
}

/**
 * Compares on `id` alone, and returns an unmemoised `toggle`.
 *
 * Memoising it would mean depending on `track`, which callers build as an object
 * literal per render — the dependency array would churn every render and the
 * `useCallback` would be a lie that cost an allocation to tell.
 */
export function useTrackPlayback(track: PlaybackTrack): TrackPlayback {
  const playback = usePlayback()
  const isCurrent = playback.track?.id === track.id

  return {
    isCurrent,
    isPlaying: isCurrent && playback.status === 'playing',
    isLoading: isCurrent && playback.status === 'loading',
    error: isCurrent ? playback.error : null,
    toggle: () => playback.toggle(track),
  }
}
