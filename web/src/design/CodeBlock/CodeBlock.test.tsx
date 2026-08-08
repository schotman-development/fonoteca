import { render, screen } from '@testing-library/react'
import { describe, expect, it } from 'vitest'

import { CodeBlock } from '@/design/CodeBlock/CodeBlock'

/**
 * The line elements, counted from the BLOCK rather than from the render
 * container. Testing Library's `container` is itself a `<div>`, so a
 * `'div > div'` query run against it matches the block as well as the lines
 * inside it and every count comes back one too high.
 */
function linesOf(container: HTMLElement): HTMLElement[] {
  const block = container.firstElementChild
  return block === null
    ? []
    : Array.from(block.querySelectorAll<HTMLElement>(':scope > div'))
}

describe('CodeBlock', () => {
  it('renders a string as one run of text', () => {
    const { container } = render(
      <CodeBlock content="/music/Mark Knopfler/Down the Road Wherever" />,
    )

    expect(container.textContent).toBe(
      '/music/Mark Knopfler/Down the Road Wherever',
    )
    expect(linesOf(container)).toHaveLength(0)
  })

  it('renders a list as one element per line, so two panels can align', () => {
    const { container } = render(
      <CodeBlock content={['01 Trapper Man', '02 Back on the Dance Floor']} />,
    )

    const lines = linesOf(container)
    expect(lines).toHaveLength(2)
    expect(lines[0]?.textContent).toBe('01 Trapper Man')
    expect(lines[1]?.textContent).toBe('02 Back on the Dance Floor')
  })

  it('keeps two identical lines as two lines', () => {
    const { container } = render(<CodeBlock content={['same', 'same']} />)

    expect(linesOf(container)).toHaveLength(2)
  })

  it('renders an empty list as an empty block rather than throwing', () => {
    const { container } = render(<CodeBlock content={[]} />)

    expect(container.firstElementChild).not.toBeNull()
    expect(container.textContent).toBe('')
  })

  it('shows the caption above the content when there is one', () => {
    render(<CodeBlock caption="canonical · 12 files" content="/music/x" />)

    expect(screen.getByText('canonical · 12 files')).toBeInTheDocument()
    expect(screen.getByText('/music/x')).toBeInTheDocument()
  })

  it('renders no caption element when none was given', () => {
    const { container } = render(<CodeBlock content="/music/x" />)

    expect(container.querySelectorAll('span')).toHaveLength(0)
  })

  it('distinguishes the accent tone from the neutral one', () => {
    const { container, rerender } = render(
      <CodeBlock content="x" tone="accent" />,
    )
    const accent = container.firstElementChild?.className ?? ''

    rerender(<CodeBlock content="x" />)
    const neutral = container.firstElementChild?.className ?? ''

    expect(accent).not.toBe(neutral)
  })

  it('distinguishes the loose spacing of a diff panel', () => {
    const { container, rerender } = render(
      <CodeBlock content="x" spacing="loose" />,
    )
    const loose = container.firstElementChild?.className ?? ''

    rerender(<CodeBlock content="x" />)
    expect(container.firstElementChild?.className ?? '').not.toBe(loose)
  })
})
