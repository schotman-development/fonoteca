import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { useState } from 'react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

import { ErrorBoundary } from '@/design/ErrorBoundary/ErrorBoundary'

/** React logs every caught error itself; the noise is not what is under test. */
beforeEach(() => {
  vi.spyOn(console, 'error').mockImplementation(() => {})
})

afterEach(() => {
  vi.restoreAllMocks()
})

function Boom({ throws, message = 'Cannot read properties of null' }: {
  throws: boolean
  message?: string
}) {
  if (throws) throw new Error(message)
  return <p>the screen</p>
}

describe('ErrorBoundary', () => {
  it('renders its children while nothing has thrown', () => {
    render(
      <ErrorBoundary>
        <Boom throws={false} />
      </ErrorBoundary>,
    )
    expect(screen.getByText('the screen')).toBeInTheDocument()
    expect(screen.queryByRole('alert')).not.toBeInTheDocument()
  })

  it('shows the failure panel, with the error’s own message, instead of a blank page', () => {
    render(
      <ErrorBoundary>
        <Boom throws message="tracks_on_disk is null" />
      </ErrorBoundary>,
    )
    expect(screen.getByRole('alert')).toBeInTheDocument()
    expect(screen.getByText('This screen stopped')).toBeInTheDocument()
    // It is developer prose, but it is the only information there is.
    expect(screen.getByText('tracks_on_disk is null')).toBeInTheDocument()
  })

  it('keeps showing the fallback while the resetKey is unchanged', () => {
    const { rerender } = render(
      <ErrorBoundary resetKey="/library">
        <Boom throws />
      </ErrorBoundary>,
    )
    expect(screen.getByRole('alert')).toBeInTheDocument()

    rerender(
      <ErrorBoundary resetKey="/library">
        <Boom throws={false} />
      </ErrorBoundary>,
    )
    // Same address: a re-render is not evidence the bug is gone.
    expect(screen.getByRole('alert')).toBeInTheDocument()
  })

  it('forgets when the resetKey changes, so a crash does not outlive the visit', () => {
    const { rerender } = render(
      <ErrorBoundary resetKey="/library">
        <Boom throws />
      </ErrorBoundary>,
    )
    expect(screen.getByRole('alert')).toBeInTheDocument()

    rerender(
      <ErrorBoundary resetKey="/activity">
        <Boom throws={false} />
      </ErrorBoundary>,
    )
    expect(screen.queryByRole('alert')).not.toBeInTheDocument()
    expect(screen.getByText('the screen')).toBeInTheDocument()
  })

  it('hands a custom fallback the error and a working reset', async () => {
    const user = userEvent.setup()

    function Harness() {
      const [broken, setBroken] = useState(true)
      return (
        <ErrorBoundary
          fallback={({ error, reset }) => (
            <div>
              <p>caught: {error.message}</p>
              <button
                type="button"
                onClick={() => {
                  setBroken(false)
                  reset()
                }}
              >
                Try again
              </button>
            </div>
          )}
        >
          <Boom throws={broken} message="upstream shape changed" />
        </ErrorBoundary>
      )
    }

    render(<Harness />)
    expect(screen.getByText('caught: upstream shape changed')).toBeInTheDocument()

    await user.click(screen.getByRole('button', { name: 'Try again' }))
    expect(screen.getByText('the screen')).toBeInTheDocument()
  })

  it('survives a thrown non-Error rather than crashing the shell with it', () => {
    function ThrowString(): never {
      // eslint-disable-next-line no-throw-literal
      throw 'nope'
    }
    render(
      <ErrorBoundary>
        <ThrowString />
      </ErrorBoundary>,
    )
    expect(screen.getByRole('alert')).toBeInTheDocument()
    expect(screen.getByText('nope')).toBeInTheDocument()
  })

  it('takes a title so a panel-level boundary can name what stopped', () => {
    render(
      <ErrorBoundary title="The release list stopped">
        <Boom throws />
      </ErrorBoundary>,
    )
    expect(screen.getByText('The release list stopped')).toBeInTheDocument()
  })
})
