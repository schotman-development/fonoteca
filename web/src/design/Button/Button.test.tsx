import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter, Route, Routes } from 'react-router-dom'
import { describe, expect, it, vi } from 'vitest'

import { Button } from '@/design/Button/Button'

/** A button that navigates needs a router; nothing else here does. */
function renderRouted(ui: React.ReactNode, at = '/') {
  return render(
    <MemoryRouter initialEntries={[at]}>
      <Routes>
        <Route path="/" element={<>{ui}</>} />
        <Route path="/activity" element={<h1>Activity screen</h1>} />
      </Routes>
    </MemoryRouter>,
  )
}

describe('Button', () => {
  it('renders its label and fires on a press', async () => {
    const onClick = vi.fn()
    render(<Button onClick={onClick}>Download</Button>)

    await userEvent.click(screen.getByRole('button', { name: 'Download' }))
    expect(onClick).toHaveBeenCalledTimes(1)
  })

  it('is type="button", so a Cancel beside a Save cannot submit the form', () => {
    const onSubmit = vi.fn()
    render(
      <form onSubmit={onSubmit}>
        <Button>Cancel</Button>
      </form>,
    )
    expect(screen.getByRole('button', { name: 'Cancel' })).toHaveAttribute('type', 'button')
  })

  it('never submits a form it sits in unless it is asked to', async () => {
    const onSubmit = vi.fn((event: React.FormEvent) => {
      event.preventDefault()
    })
    render(
      <form onSubmit={onSubmit}>
        <Button>Cancel</Button>
        <Button type="submit">Save</Button>
      </form>,
    )

    await userEvent.click(screen.getByRole('button', { name: 'Cancel' }))
    expect(onSubmit).not.toHaveBeenCalled()

    await userEvent.click(screen.getByRole('button', { name: 'Save' }))
    expect(onSubmit).toHaveBeenCalledTimes(1)
  })

  it('does not fire when disabled', async () => {
    const onClick = vi.fn()
    render(
      <Button disabled onClick={onClick}>
        Download
      </Button>,
    )

    await userEvent.click(screen.getByRole('button', { name: 'Download' }))
    expect(onClick).not.toHaveBeenCalled()
  })

  it('blocks the second press while it is loading, and says it is busy', async () => {
    const onClick = vi.fn()
    render(
      <Button loading onClick={onClick}>
        Scan library
      </Button>,
    )

    const button = screen.getByRole('button', { name: 'Scan library' })
    expect(button).toBeDisabled()
    expect(button).toHaveAttribute('aria-busy', 'true')

    await userEvent.click(button)
    expect(onClick).not.toHaveBeenCalled()
  })

  it('renders a leading slot when it is not loading', () => {
    render(<Button leading={<span data-testid="dot" />}>Qobuz</Button>)
    expect(screen.getByTestId('dot')).toBeInTheDocument()
  })

  it('names an icon-only control and hides the glyph from the reading', () => {
    render(
      <Button variant="close" label="Close panel">
        ×
      </Button>,
    )

    // The name is the sentence, not "times".
    const button = screen.getByRole('button', { name: 'Close panel' })
    expect(button).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: '× Close panel' })).not.toBeInTheDocument()
  })

  it('renders a real anchor when given an address, and navigates', async () => {
    renderRouted(<Button to="/activity">Activity</Button>)

    const link = screen.getByRole('link', { name: 'Activity' })
    expect(link).toHaveAttribute('href', '/activity')
    // base.css hangs "do not underline me like prose" off this attribute.
    expect(link).toHaveAttribute('data-plain')

    await userEvent.click(link)
    expect(screen.getByRole('heading', { name: 'Activity screen' })).toBeInTheDocument()
  })

  it('a disabled link is aria-disabled, does not fire, and does not navigate', async () => {
    const onClick = vi.fn()
    renderRouted(
      <Button to="/activity" disabled onClick={onClick}>
        Activity
      </Button>,
    )

    const link = screen.getByRole('link', { name: 'Activity' })
    expect(link).toHaveAttribute('aria-disabled', 'true')

    await userEvent.click(link)
    expect(onClick).not.toHaveBeenCalled()
    expect(screen.queryByRole('heading', { name: 'Activity screen' })).not.toBeInTheDocument()
  })

  it('is reachable and operable from the keyboard', async () => {
    const onClick = vi.fn()
    render(<Button onClick={onClick}>Ignore</Button>)

    await userEvent.tab()
    expect(screen.getByRole('button', { name: 'Ignore' })).toHaveFocus()

    await userEvent.keyboard('{Enter}')
    expect(onClick).toHaveBeenCalledTimes(1)
  })

  it('carries the caller class alongside its own', () => {
    render(<Button className="placed">Save</Button>)
    expect(screen.getByRole('button', { name: 'Save' })).toHaveClass('placed')
  })
})
