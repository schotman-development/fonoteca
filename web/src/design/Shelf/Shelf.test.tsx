import { fireEvent, render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, describe, expect, it, vi } from 'vitest'

import { SHELF_STEP_PX, Shelf } from '@/design/Shelf/Shelf'

/**
 * jsdom has no layout, so every element reports `scrollWidth: 0` and a shelf is
 * never scrollable there. These helpers install the one measurement the
 * component reads, and a `scroll` event is what makes it look again — which is
 * exactly the path a real browser takes.
 */
function fakeMetrics(
  el: HTMLElement,
  { scrollWidth = 1000, clientWidth = 300, scrollLeft = 0 } = {},
) {
  let current = scrollLeft
  Object.defineProperty(el, 'scrollWidth', {
    configurable: true,
    get: () => scrollWidth,
  })
  Object.defineProperty(el, 'clientWidth', {
    configurable: true,
    get: () => clientWidth,
  })
  Object.defineProperty(el, 'scrollLeft', {
    configurable: true,
    get: () => current,
    set: (value: number) => {
      current = value
    },
  })
}

function shelf(name = 'Last downloaded'): HTMLElement {
  return screen.getByRole('group', { name })
}

function mountScrollable(scrollLeft = 0) {
  render(
    <Shelf label="Last downloaded">
      <div>Sailing to Philadelphia</div>
      <div>Kind of Blue</div>
      <div>Blue</div>
    </Shelf>,
  )
  const el = shelf()
  fakeMetrics(el, { scrollLeft })
  fireEvent.scroll(el)
  return el
}

afterEach(() => {
  vi.unstubAllGlobals()
})

describe('Shelf', () => {
  it('renders its cards inside a named scrolling region', () => {
    render(
      <Shelf label="Last downloaded">
        <div>Kind of Blue</div>
      </Shelf>,
    )

    expect(shelf()).toContainElement(screen.getByText('Kind of Blue'))
  })

  it('gives both arrows a real name', () => {
    render(
      <Shelf label="Last downloaded">
        <div>a</div>
      </Shelf>,
    )

    // Icon-only controls: the glyph is aria-hidden and the name is the
    // .visuallyHidden span, so these two must be findable by name alone.
    expect(screen.getByRole('button', { name: 'Scroll left' })).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Scroll right' })).toBeInTheDocument()
  })

  it('disables both arrows while there is nothing to scroll', () => {
    render(
      <Shelf label="Last downloaded">
        <div>a</div>
      </Shelf>,
    )

    expect(screen.getByRole('button', { name: 'Scroll left' })).toBeDisabled()
    expect(screen.getByRole('button', { name: 'Scroll right' })).toBeDisabled()
  })

  it('is a tab stop only while it actually overflows', () => {
    render(
      <Shelf label="Last downloaded">
        <div>a</div>
      </Shelf>,
    )
    const el = shelf()
    expect(el).not.toHaveAttribute('tabindex')

    fakeMetrics(el)
    fireEvent.scroll(el)
    expect(el).toHaveAttribute('tabindex', '0')
  })

  it('enables forward but not back at the start of the row', () => {
    mountScrollable(0)

    expect(screen.getByRole('button', { name: 'Scroll left' })).toBeDisabled()
    expect(screen.getByRole('button', { name: 'Scroll right' })).toBeEnabled()
  })

  it('enables back but not forward at the end of the row', () => {
    mountScrollable(700) // scrollWidth 1000 − clientWidth 300

    expect(screen.getByRole('button', { name: 'Scroll left' })).toBeEnabled()
    expect(screen.getByRole('button', { name: 'Scroll right' })).toBeDisabled()
  })

  it('steps by the design’s 292px, smoothly, in the direction pressed', async () => {
    const el = mountScrollable(292)
    const scrollBy = vi.fn()
    Object.defineProperty(el, 'scrollBy', { configurable: true, value: scrollBy })

    await userEvent.click(screen.getByRole('button', { name: 'Scroll right' }))
    expect(scrollBy).toHaveBeenLastCalledWith({
      left: SHELF_STEP_PX,
      behavior: 'smooth',
    })

    await userEvent.click(screen.getByRole('button', { name: 'Scroll left' }))
    expect(scrollBy).toHaveBeenLastCalledWith({
      left: -SHELF_STEP_PX,
      behavior: 'smooth',
    })
    expect(SHELF_STEP_PX).toBe(292)
  })

  it('does not animate for somebody who asked it not to', async () => {
    // tokens.css kills CSS-driven smooth scrolling under this preference, but
    // `behavior: 'smooth'` is an argument rather than a style and has to be
    // decided here.
    vi.stubGlobal(
      'matchMedia',
      vi.fn((query: string) => ({
        matches: query.includes('prefers-reduced-motion'),
        media: query,
        addEventListener: vi.fn(),
        removeEventListener: vi.fn(),
      })),
    )

    const el = mountScrollable(0)
    const scrollBy = vi.fn()
    Object.defineProperty(el, 'scrollBy', { configurable: true, value: scrollBy })

    await userEvent.click(screen.getByRole('button', { name: 'Scroll right' }))

    expect(scrollBy).toHaveBeenCalledWith({ left: SHELF_STEP_PX, behavior: 'auto' })
  })

  it('falls back to the design’s own scrollLeft when scrollBy is unavailable', async () => {
    const el = mountScrollable(0)
    Object.defineProperty(el, 'scrollBy', { configurable: true, value: undefined })

    await userEvent.click(screen.getByRole('button', { name: 'Scroll right' }))

    expect(el.scrollLeft).toBe(SHELF_STEP_PX)
  })

  it('re-measures when the cards arrive rather than only when it is scrolled', () => {
    const { rerender } = render(<Shelf label="Last downloaded">{null}</Shelf>)
    const el = shelf()
    expect(screen.getByRole('button', { name: 'Scroll right' })).toBeDisabled()

    // A query resolving is a re-render with no scroll and no resize — the case
    // neither listener catches.
    fakeMetrics(el)
    rerender(
      <Shelf label="Last downloaded">
        <div>Kind of Blue</div>
      </Shelf>,
    )

    expect(screen.getByRole('button', { name: 'Scroll right' })).toBeEnabled()
  })

  it('survives an environment with no ResizeObserver', () => {
    // jsdom has none, so simply mounting proves it — but the guard is easy to
    // delete in a refactor and the failure is a thrown constructor.
    expect(globalThis.ResizeObserver).toBeUndefined()
    expect(() =>
      render(
        <Shelf label="Last downloaded">
          <div>a</div>
        </Shelf>,
      ),
    ).not.toThrow()
  })

  it('passes a caller class through so a screen can place it', () => {
    const { container } = render(
      <Shelf label="Last downloaded" className="placed">
        <div>a</div>
      </Shelf>,
    )

    expect(container.firstElementChild).toHaveClass('placed')
  })
})
