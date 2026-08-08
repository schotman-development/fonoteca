/**
 * The one boundary, mounted around the route outlet.
 *
 * ── what it is NOT for ───────────────────────────────────────────────────────
 *
 * **A failed request must never reach here.** Every fetch goes through a query
 * hook, every screen renders `PageError` from its own `isError`, and a 502 from
 * Qobuz is an ordinary state of an ordinary screen — the nav, the footer and the
 * five screens that are fine all keep working. A boundary catching fetch
 * failures would blank the application every time an upstream blinked, which is
 * exactly the failure mode this app's whole error envelope exists to avoid.
 *
 * What lands here is a *render* crash: a component read `x.y` off something the
 * type checker promised would be there, a `null` that means "nothing has counted
 * this" reached arithmetic. Those are bugs, and the honest response is to say so
 * on that screen and keep the shell alive.
 *
 * ── resetKey ─────────────────────────────────────────────────────────────────
 *
 * A boundary that has caught stays caught: React unmounts the subtree and will
 * not re-mount it on its own. So a crashed screen would still be crashed after
 * navigating away and back — the URL says `/activity`, the panel still shows the
 * library's stack trace, and no amount of clicking fixes it.
 *
 * `resetKey` is the cure and `App` keys it on `location.pathname`, so the crash
 * lives exactly as long as the visit to the screen that caused it. It is
 * compared with `Object.is` in `getDerivedStateFromProps` rather than in an
 * effect, because the reset has to happen *during* the render that changed it —
 * an effect would paint the stale fallback for one frame under the new address.
 *
 * ── the fallback ─────────────────────────────────────────────────────────────
 *
 * `PageError`, reused rather than re-drawn, so a crash and a 500 look the same
 * to the person reading them: both are "this block is unavailable", both sit in
 * the page rather than over it, and neither is a red screen. The error's own
 * message is rendered — it is developer prose, but it is the only information
 * there is, and hiding it turns a bug report into "it broke".
 *
 * No retry button by default, and no `Button` import: recovery is navigation
 * (see `resetKey`), and a caller who genuinely wants a press supplies
 * `fallback`, which receives the error and a `reset` function. That keeps this
 * component free of the primitive every failure panel would otherwise drag in.
 *
 * ── logging ──────────────────────────────────────────────────────────────────
 *
 * `console.error` in development only. In a build, the message is already on the
 * screen and a console line nobody opens is noise; in `npm run dev` it is the
 * component stack, which is the whole of the debugging value.
 */

import { Component, type ErrorInfo, type ReactNode } from 'react'

import { PageError } from '@/design/PageError/PageError'

export interface ErrorBoundaryFallbackArgs {
  error: Error
  /** Clears the caught state and re-mounts the children. */
  reset: () => void
}

export interface ErrorBoundaryProps {
  children: ReactNode
  /**
   * Change this and the boundary forgets. `App` passes `location.pathname`, so
   * one crashed screen does not outlive the visit to it.
   */
  resetKey?: unknown
  /** Replaces the `PageError` fallback. Receives the error and a reset. */
  fallback?: (args: ErrorBoundaryFallbackArgs) => ReactNode
  /** The fallback's heading, when the default one is used. */
  title?: string
}

interface ErrorBoundaryState {
  error: Error | null
  /** The `resetKey` the current state was captured under. */
  seenKey: unknown
}

/** Anything can be thrown in JavaScript; only an `Error` has a message worth showing. */
function toError(thrown: unknown): Error {
  if (thrown instanceof Error) return thrown
  return new Error(typeof thrown === 'string' ? thrown : 'Something threw a non-Error.')
}

export class ErrorBoundary extends Component<ErrorBoundaryProps, ErrorBoundaryState> {
  state: ErrorBoundaryState = { error: null, seenKey: undefined }

  static getDerivedStateFromError(thrown: unknown): Partial<ErrorBoundaryState> {
    return { error: toError(thrown) }
  }

  static getDerivedStateFromProps(
    props: ErrorBoundaryProps,
    state: ErrorBoundaryState,
  ): Partial<ErrorBoundaryState> | null {
    if (Object.is(props.resetKey, state.seenKey)) return null
    // The address changed. Forget whatever the last screen did, in the same
    // render — a reset in an effect paints the stale fallback for one frame.
    return { error: null, seenKey: props.resetKey }
  }

  componentDidCatch(error: Error, info: ErrorInfo) {
    if (import.meta.env.DEV) {
      // The component stack is the reason this exists; in a build it is noise.
      console.error('[ErrorBoundary]', error, info.componentStack)
    }
  }

  reset = () => {
    this.setState({ error: null })
  }

  render() {
    const { error } = this.state
    if (!error) return this.props.children

    if (this.props.fallback) return this.props.fallback({ error, reset: this.reset })

    return (
      <PageError
        title={this.props.title ?? 'This screen stopped'}
        message={error.message}
      />
    )
  }
}
