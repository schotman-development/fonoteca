import { render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import type { ReactNode } from 'react'
import { describe, expect, it, vi } from 'vitest'

import { Toast } from '@/design/Toast/Toast'
import { ToastHost } from '@/design/Toast/ToastHost'
import { ToastProvider } from '@/design/Toast/ToastProvider'
import { useToast, type ToastLevel } from '@/design/Toast/toastContext'

function Raise({ message, level }: { message: string; level?: ToastLevel }) {
  const toast = useToast()
  return (
    <button type="button" onClick={() => toast(message, level)}>
      raise {message}
    </button>
  )
}

function wrap(children: ReactNode, durationMs = 40) {
  return render(<ToastProvider durationMs={durationMs}>{children}</ToastProvider>)
}

describe('Toast', () => {
  it('renders the message and no pip for a neutral level', () => {
    const { container } = render(<Toast message="Queued Blue Train" />)
    expect(screen.getByText('Queued Blue Train')).toBeInTheDocument()
    expect(container.querySelector('[aria-hidden="true"]')).toBeNull()
  })

  it('marks a level with a pip rather than colouring the text', () => {
    const { container } = render(<Toast message="Two files are corrupt" level="bad" />)
    // Amber or red TEXT on --c-ink would be ~2:1, so the level lands on a mark.
    expect(container.firstElementChild).toHaveAttribute('data-level', 'bad')
    expect(container.querySelector('[aria-hidden="true"]')).not.toBeNull()
  })
})

describe('useToast', () => {
  it('throws outside a provider — a toast that goes nowhere is a lost acknowledgement', () => {
    const quiet = vi.spyOn(console, 'error').mockImplementation(() => {})
    expect(() => render(<Raise message="nope" />)).toThrow(/ToastProvider/)
    quiet.mockRestore()
  })

  it('shows a message through the provider’s own region', async () => {
    const user = userEvent.setup()
    wrap(<Raise message="Re-tagged 12 releases" />, 10_000)
    await user.click(screen.getByRole('button', { name: 'raise Re-tagged 12 releases' }))
    expect(screen.getByRole('status')).toHaveTextContent('Re-tagged 12 releases')
  })

  it('auto-dismisses', async () => {
    const user = userEvent.setup()
    wrap(<Raise message="Queued 4" />, 20)
    await user.click(screen.getByRole('button', { name: 'raise Queued 4' }))
    expect(screen.getByRole('status')).toHaveTextContent('Queued 4')
    await waitFor(() => expect(screen.getByRole('status')).toHaveTextContent(''))
  })

  it('replaces the previous message rather than stacking', async () => {
    const user = userEvent.setup()
    wrap(
      <>
        <Raise message="first" />
        <Raise message="second" />
      </>,
      10_000,
    )
    await user.click(screen.getByRole('button', { name: 'raise first' }))
    await user.click(screen.getByRole('button', { name: 'raise second' }))
    const region = screen.getByRole('status')
    expect(region).toHaveTextContent('second')
    expect(region).not.toHaveTextContent('first')
  })

  it('keeps the live region mounted while nothing is showing', () => {
    wrap(<Raise message="idle" />)
    // Announced reliably only if the region pre-dates the text inside it.
    const region = screen.getByRole('status')
    expect(region).toBeInTheDocument()
    expect(region).toHaveTextContent('')
  })
})

describe('ToastHost', () => {
  it('is the only region when one is mounted explicitly', async () => {
    const user = userEvent.setup()
    wrap(
      <>
        <Raise message="only once" />
        <ToastHost />
      </>,
      10_000,
    )
    await user.click(screen.getByRole('button', { name: 'raise only once' }))
    // Two regions would announce the same sentence twice and break getByRole.
    expect(screen.getByRole('status')).toHaveTextContent('only once')
    expect(screen.getAllByRole('status')).toHaveLength(1)
  })

  it('renders nothing at all outside a provider', () => {
    const { container } = render(<ToastHost />)
    expect(container).toBeEmptyDOMElement()
  })
})
