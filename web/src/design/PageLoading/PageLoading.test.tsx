import { render, screen } from '@testing-library/react'
import { describe, expect, it } from 'vitest'

import { PageLoading } from '@/design/PageLoading/PageLoading'
import styles from '@/design/PageLoading/PageLoading.module.css'

function bars(container: HTMLElement): number {
  const bar = styles.bar?.split(' ')[0] ?? 'bar'
  return container.querySelectorAll(`.${CSS.escape(bar)}`).length
}

describe('PageLoading', () => {
  it('tells a screen reader it is waiting', () => {
    render(<PageLoading />)
    const region = screen.getByRole('status')
    expect(region).toHaveAttribute('aria-busy', 'true')
    // The bars are empty divs; without the word, the page is silent.
    expect(region).toHaveTextContent('Loading')
  })

  it('takes a label so the wait can name itself', () => {
    render(<PageLoading label="Loading releases" />)
    expect(screen.getByRole('status')).toHaveTextContent('Loading releases')
  })

  it('reserves the shape it is asked for', () => {
    const { container } = render(<PageLoading rows={5} />)
    expect(bars(container)).toBe(6) // five rows plus the title bar
  })

  it('drops the title bar where the caller already drew the heading', () => {
    const { container } = render(<PageLoading rows={3} title={false} />)
    expect(bars(container)).toBe(3)
  })

  it('renders no rows rather than throwing on a nonsense count', () => {
    const { container } = render(<PageLoading rows={-2} title={false} />)
    expect(bars(container)).toBe(0)
  })

  it('shows no number — there is nothing to be a percentage of', () => {
    const { container } = render(<PageLoading rows={3} />)
    expect(container.textContent).toBe('Loading')
  })
})
