import { render, screen } from '@testing-library/react'
import { describe, expect, it } from 'vitest'

import { Placeholder } from '@/design/Placeholder/Placeholder'
import { EM_DASH } from '@/format'

describe('Placeholder', () => {
  it('says what the backend would have to provide', () => {
    render(<Placeholder needs="AcoustID confidence" />)
    expect(screen.getByText(/not wired/)).toHaveTextContent(
      'not wired — AcoustID confidence',
    )
  })

  it('carries data-placeholder so the wiring audit can be grepped and counted', () => {
    const { container } = render(<Placeholder needs="disk capacity" />)
    expect(container.querySelector('[data-placeholder="disk capacity"]')).not.toBeNull()
  })

  it('stands in for a number with the em dash, never a zero', () => {
    render(<Placeholder needs="library-wide hi-res share" variant="figure" />)
    expect(screen.getByText(EM_DASH)).toBeInTheDocument()
    expect(screen.queryByText('0')).not.toBeInTheDocument()
    expect(screen.queryByText('0%')).not.toBeInTheDocument()
  })

  it('renders no em dash where a figure is not what is missing', () => {
    render(<Placeholder needs="Vorbis tag map" variant="block" />)
    expect(screen.queryByText(EM_DASH)).not.toBeInTheDocument()
  })

  it('takes its height from the caller so the composition still reads', () => {
    const { container } = render(<Placeholder needs="scan progress" minHeight={120} />)
    const root = container.firstElementChild as HTMLElement
    expect(root.style.getPropertyValue('--placeholder-min-h')).toBe('120px')
  })

  it('accepts a token-valued height as a string', () => {
    const { container } = render(
      <Placeholder needs="collaborations" minHeight="var(--sp-9)" />,
    )
    const root = container.firstElementChild as HTMLElement
    expect(root.style.getPropertyValue('--placeholder-min-h')).toBe('var(--sp-9)')
  })
})
