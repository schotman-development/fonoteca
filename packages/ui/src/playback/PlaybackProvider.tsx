import { type ReactNode, useEffect, useMemo, useRef, useState } from 'react'

import {
  PlaybackContext,
  type PlaybackContextValue,
  type PlaybackError,
  type PlaybackProgress,
  PlaybackProgressContext,
  type PlaybackStatus,
  type PlaybackTrack,
} from './PlaybackContext.ts'

export type PlaybackProviderProps = {
  readonly children: ReactNode
  /** 0–1. Not persisted — see the note in the body. */
  readonly initialVolume?: number
  /** How an application implements "next". This package has no queue. */
  readonly onEnded?: (track: PlaybackTrack) => void
  readonly preload?: 'none' | 'metadata' | 'auto'
  readonly crossOrigin?: 'anonymous' | 'use-credentials'
}

const IDLE_PROGRESS: PlaybackProgress = {
  currentTime: 0,
  duration: null,
  seeking: false,
  buffered: 0,
}

/**
 * One `HTMLAudioElement` for the whole application, constructed with
 * `new Audio()` and **never attached to the document**.
 *
 * One element is what makes "two rows cannot play at once" a fact rather than a
 * convention, and what lets the transport always show what is actually audible.
 * Keeping it detached is the other half: an `<audio>` node in the tree is a
 * permanent axe surface (`audio-caption`, `no-autoplay-audio`) that every story
 * in the package would have to argue with, for an element that renders nothing.
 *
 * The provider re-renders on its own progress state, but `children` arrives as a
 * prop — its element reference is unchanged — so React bails out of that subtree
 * and only `PlaybackProgressContext` consumers re-render. In practice that is
 * one `AudioTransport`. It looks wrong at a glance and it is not.
 */
export function PlaybackProvider({
  children,
  initialVolume = 1,
  onEnded,
  preload = 'metadata',
  crossOrigin,
}: PlaybackProviderProps) {
  const audioRef = useRef<HTMLAudioElement | null>(null)
  const trackRef = useRef<PlaybackTrack | null>(null)
  // Every play() is stamped; a promise that settles after a newer request
  // superseded it updates nothing.
  const requestRef = useRef(0)
  const endedRef = useRef(onEnded)
  endedRef.current = onEnded

  const [track, setTrack] = useState<PlaybackTrack | null>(null)
  const [status, setStatus] = useState<PlaybackStatus>('idle')
  const [error, setError] = useState<PlaybackError | null>(null)
  const [volume, setVolumeState] = useState(initialVolume)
  const [muted, setMutedState] = useState(false)
  const [progress, setProgress] = useState<PlaybackProgress>(IDLE_PROGRESS)

  useEffect(() => {
    const audio = new globalThis.Audio()
    audio.preload = preload
    if (crossOrigin != null) audio.crossOrigin = crossOrigin
    audioRef.current = audio

    function durationOf(): number | null {
      const value = audio.duration
      if (Number.isFinite(value) && value > 0) return value
      return trackRef.current?.duration ?? null
    }

    function bufferedAhead(): number {
      const ranges = audio.buffered
      for (let index = 0; index < ranges.length; index += 1) {
        if (ranges.start(index) <= audio.currentTime && ranges.end(index) >= audio.currentTime) {
          return ranges.end(index)
        }
      }
      return 0
    }

    function reportProgress() {
      setProgress({
        currentTime: audio.currentTime,
        duration: durationOf(),
        seeking: audio.seeking,
        buffered: bufferedAhead(),
      })
    }

    const listeners: readonly (readonly [string, () => void])[] = [
      ['play', () => setStatus('loading')],
      ['playing', () => setStatus('playing')],
      // A rebuffer, not a failure.
      ['waiting', () => setStatus('loading')],
      ['pause', () => setStatus((current) => (current === 'ended' ? current : 'paused'))],
      [
        'ended',
        () => {
          setStatus('ended')
          const ended = trackRef.current
          if (ended != null) endedRef.current?.(ended)
        },
      ],
      ['error', () => setError(describeMediaError(audio))],
      ['loadedmetadata', reportProgress],
      ['durationchange', reportProgress],
      ['timeupdate', reportProgress],
      ['progress', reportProgress],
      ['seeking', reportProgress],
      ['seeked', reportProgress],
      [
        'volumechange',
        () => {
          setVolumeState(audio.volume)
          setMutedState(audio.muted)
        },
      ],
      // NOT an error. A slow LAN read of a 30 MB FLAC fires `stalled` routinely,
      // and treating it as a failure would put a red message under half the
      // library.
    ]

    for (const [event, handler] of listeners) audio.addEventListener(event, handler)

    return () => {
      for (const [event, handler] of listeners) audio.removeEventListener(event, handler)
      audio.pause()
      // NEVER `audio.src = ''`: the empty string resolves against the document
      // URL, and the browser dutifully fetches the page itself as audio.
      audio.removeAttribute('src')
      audio.load()
      audioRef.current = null
    }
  }, [preload, crossOrigin])

  /*
   * Kept in sync one way only — the element is the source of truth, and
   * `volumechange` above is what writes the state back.
   *
   * And **not persisted**, breaking deliberately from `ThemeProvider`'s
   * localStorage precedent. This package owns the theme because it owns the
   * tokens the theme selects; it does not own a person's audio preferences, and
   * an application that wants to remember the volume can do it in three lines.
   * The other reason is closer to home: a persisted volume leaks between stories
   * in one Storybook run, which would make `aria-valuetext="70%"` an
   * order-dependent assertion and the suite intermittently red.
   */
  useEffect(() => {
    const audio = audioRef.current
    if (audio != null) audio.volume = volume
  }, [volume])

  const value = useMemo<PlaybackContextValue>(() => {
    function start(next: PlaybackTrack) {
      const audio = audioRef.current
      if (audio == null) return

      const request = requestRef.current + 1
      requestRef.current = request

      // Refuse locally rather than making a request to find out, when the
      // browser has already said it cannot decode this. It is the one case where
      // "missing" and "undecodable" can be told apart, since MediaError reports
      // both as code 4.
      if (next.mimeType != null && audio.canPlayType(next.mimeType) === '') {
        trackRef.current = next
        setTrack(next)
        setStatus('error')
        setError({ kind: 'unsupported', message: `This browser cannot decode ${next.mimeType}.` })
        return
      }

      trackRef.current = next
      setTrack(next)
      setError(null)
      setStatus('loading')
      setProgress({ ...IDLE_PROGRESS, duration: next.duration ?? null })

      audio.src = next.src
      audio.load()

      audio.play().catch((cause: unknown) => {
        if (requestRef.current !== request) return
        // Fired every time a new load() interrupts a play() still in flight —
        // which is what a fast click from one row to the next does. Not a
        // failure, and showing it as one would put an error on every impatient
        // double-click.
        if (cause instanceof globalThis.DOMException && cause.name === 'AbortError') return

        if (cause instanceof globalThis.DOMException && cause.name === 'NotAllowedError') {
          setStatus('error')
          setError({
            kind: 'blocked',
            message: 'The browser would not start playback without a click.',
          })
          return
        }

        setStatus('error')
        setError(describeMediaError(audioRef.current))
      })
    }

    return {
      track,
      status,
      error,
      volume,
      muted,
      play: start,
      toggle: (next) => {
        const audio = audioRef.current
        if (audio == null) return

        if (trackRef.current?.id !== next.id) {
          start(next)
          return
        }
        // Clicking during `loading` cancels rather than queueing a second play.
        if (audio.paused) void audio.play().catch(() => undefined)
        else audio.pause()
      },
      pause: () => audioRef.current?.pause(),
      resume: () => void audioRef.current?.play().catch(() => undefined),
      stop: () => {
        const audio = audioRef.current
        requestRef.current += 1
        trackRef.current = null
        if (audio != null) {
          audio.pause()
          audio.removeAttribute('src')
          audio.load()
        }
        setTrack(null)
        setStatus('idle')
        setError(null)
        setProgress(IDLE_PROGRESS)
      },
      seek: (seconds) => {
        const audio = audioRef.current
        if (audio != null) audio.currentTime = seconds
      },
      // Raising the volume above zero while muted un-mutes: a volume slider that
      // moved and changed nothing audible would read as broken.
      setVolume: (next) => {
        const audio = audioRef.current
        setVolumeState(next)
        if (audio != null) {
          audio.volume = next
          if (next > 0 && audio.muted) audio.muted = false
        }
      },
      setMuted: (next) => {
        const audio = audioRef.current
        setMutedState(next)
        if (audio != null) audio.muted = next
      },
    }
  }, [track, status, error, volume, muted])

  return (
    <PlaybackContext.Provider value={value}>
      <PlaybackProgressContext.Provider value={progress}>
        {children}
      </PlaybackProgressContext.Provider>
    </PlaybackContext.Provider>
  )
}

/**
 * `MEDIA_ERR_SRC_NOT_SUPPORTED` is what a 404 and an undecodable codec both
 * arrive as, so the message for it must claim neither. `PlaybackTrack.mimeType`
 * is the only path to a definite answer, and it is taken before the request.
 */
function describeMediaError(audio: HTMLAudioElement | null): PlaybackError {
  switch (audio?.error?.code) {
    case 2:
      return { kind: 'network', message: 'The file could not be read.' }
    case 3:
      return { kind: 'decode', message: 'The audio is damaged and stopped decoding.' }
    case 4:
      return {
        kind: 'unsupported',
        message:
          'Could not play — the file may be missing, or in a format this browser cannot decode.',
      }
    default:
      return { kind: 'unknown', message: 'Playback failed.' }
  }
}
