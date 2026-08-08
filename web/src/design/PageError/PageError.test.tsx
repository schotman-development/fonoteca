import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it, vi } from 'vitest'

import { PageError } from '@/design/PageError/PageError'

describe('PageError', () => {
  it('is an alert, because it replaces content the reader asked for', () => {
    render(<PageError message="No Qobuz credentials configured." />)
    expect(screen.getByRole('alert')).toBeInTheDocument()
  })

  it('renders the server’s own sentence verbatim', () => {
    // client.ts unwraps the error envelope; the sentence inside it was written
    // to be read, and it is the only part of the response that says what to do.
    const prose = 'another scan is already running — wait for it to finish'
    render(<PageError message={prose} />)
    expect(screen.getByText(prose)).toBeInTheDocument()
  })

  it('has a default title and does not invent a message', () => {
    const { container } = render(<PageError />)
    expect(screen.getByText('That did not work')).toBeInTheDocument()
    // No message means no paragraph at all — never a stand-in apology, which
    // would be indistinguishable from prose the server really sent.
    expect(container.querySelectorAll('p')).toHaveLength(1)
  })

  it('takes a title so the shape of the failure can be named', () => {
    render(<PageError title="Qobuz is not answering" message="upstream was silent" />)
    expect(screen.getByText('Qobuz is not answering')).toBeInTheDocument()
  })

  it('renders the caller’s action and leaves it working', async () => {
    const retry = vi.fn()
    const user = userEvent.setup()
    render(
      <PageError
        message="upstream was silent"
        action={
          <button type="button" onClick={retry}>
            Retry
          </button>
        }
      />,
    )
    await user.click(screen.getByRole('button', { name: 'Retry' }))
    expect(retry).toHaveBeenCalledTimes(1)
  })

  it('renders no action slot when there is nothing to press', () => {
    // A 404 has no retry, and a dead button is worse than no button.
    render(<PageError message="no such release" />)
    expect(screen.queryByRole('button')).not.toBeInTheDocument()
  })
})
