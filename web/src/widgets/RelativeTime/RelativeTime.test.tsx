import { render, screen } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'

import { EM_DASH } from '@/format'
import { RelativeTime } from '@/widgets/RelativeTime/RelativeTime'

afterEach(() => {
  vi.useRealTimers()
})

describe('RelativeTime', () => {
  it('renders the age as prose', () => {
    vi.useFakeTimers()
    vi.setSystemTime(new Date('2026-08-04T12:00:00Z'))

    render(<RelativeTime value="2026-08-04T11:58:00Z" />)

    expect(screen.getByText('2m 00s ago')).toBeInTheDocument()
  })

  it('is a <time> carrying the machine-readable instant', () => {
    render(<RelativeTime value="2026-08-04T11:58:00Z" />)

    const el = document.querySelector('time')
    expect(el).not.toBeNull()
    expect(el).toHaveAttribute('datetime', '2026-08-04T11:58:00Z')
  })

  it('answers "when exactly?" through the title, in labelled UTC', () => {
    render(<RelativeTime value="2026-08-04T11:58:00Z" />)

    expect(document.querySelector('time')).toHaveAttribute(
      'title',
      '2026-08-04 11:58 UTC',
    )
  })

  it('renders the em dash for null — not the word "never"', () => {
    // `fmtAgo(null)` is the sentence "never", which is right for a history and
    // wrong for a value that has not arrived yet.
    render(<RelativeTime value={null} />)

    expect(screen.getByText(EM_DASH)).toBeInTheDocument()
    expect(screen.queryByText('never')).not.toBeInTheDocument()
    expect(document.querySelector('time')).toBeNull()
  })

  it('treats an empty string as absent rather than as an instant', () => {
    render(<RelativeTime value="" />)

    expect(screen.getByText(EM_DASH)).toBeInTheDocument()
    expect(document.querySelector('time')).toBeNull()
  })

  it('does not install a timer — one scheduler, and it is the query layer', () => {
    vi.useFakeTimers()
    const setInterval = vi.spyOn(globalThis, 'setInterval')
    const setTimeout = vi.spyOn(globalThis, 'setTimeout')

    render(<RelativeTime value="2026-08-04T11:58:00Z" />)

    expect(setInterval).not.toHaveBeenCalled()
    expect(setTimeout).not.toHaveBeenCalled()
    setInterval.mockRestore()
    setTimeout.mockRestore()
  })
})
