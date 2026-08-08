import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it, vi } from 'vitest'

import { EM_DASH } from '@/format'

import { StatCard } from '@/design/StatCard/StatCard'
import styles from '@/design/StatCard/StatCard.module.css'

/**
 * A CSS-module class is `string | undefined` under `noUncheckedIndexedAccess`,
 * and `undefined` reaching `toHaveClass` would assert nothing while passing.
 * Failing loudly on a renamed or misspelt class is the point.
 */
function recipe(name: string | undefined): string {
  if (name === undefined) throw new Error('the StatCard class is missing')
  return name
}

describe('StatCard', () => {
  it('renders the figure, what it counts and the share under it', () => {
    render(
      <StatCard value="1,284" label="verified" share="62% of measured files" onClick={() => {}} />,
    )

    expect(screen.getByText('1,284')).toBeInTheDocument()
    expect(screen.getByText('verified')).toBeInTheDocument()
    expect(screen.getByText('62% of measured files')).toBeInTheDocument()
  })

  it('renders a caller-supplied em dash verbatim and never a zero', () => {
    // The dashboard's second tile reads `last_result?.states?.replaced`, which
    // is `undefined` until a pass has run. The honest rendering is the em dash
    // with the share line saying so — a `0` there claims a measurement that was
    // never taken. This component must not substitute anything for either.
    const { container } = render(
      <StatCard value={EM_DASH} label="changed elsewhere" share="no pass yet" tone="warn" onClick={() => {}} />,
    )

    expect(screen.getByText(EM_DASH)).toBeInTheDocument()
    expect(container.textContent).not.toContain('0')
  })

  it('does not format the value — a node arrives on the screen as it was passed', () => {
    render(
      <StatCard
        value={<span data-testid="composed">99.1%</span>}
        label="identified"
        onClick={() => {}}
      />,
    )

    expect(screen.getByTestId('composed')).toHaveTextContent('99.1%')
  })

  it('puts the tone on the figure and nowhere else', () => {
    // A tile whose every line is amber reads as four warnings rather than as one
    // number that happens to be amber, so the label and share stay on the ramp.
    render(<StatCard value="3" label="corrupt" share="decode errors" tone="bad" onClick={() => {}} />)

    const value = screen.getByText('3')
    expect(value).toHaveClass(recipe(styles.toneBad))
    expect(screen.getByText('corrupt')).not.toHaveClass(recipe(styles.toneBad))
    expect(screen.getByRole('button')).not.toHaveClass(recipe(styles.toneBad))
  })

  it('paints nothing by default — no colour is the resting state', () => {
    render(<StatCard value="12" label="verified" onClick={() => {}} />)

    expect(screen.getByText('12')).toHaveClass(recipe(styles.toneNeutral))
  })

  it('renders no share element when there is no share line', () => {
    const { container } = render(<StatCard value="12" label="verified" onClick={() => {}} />)

    expect(container.querySelector(`.${recipe(styles.share)}`)).toBeNull()
  })

  it('fires on a press, and is a real button rather than a clickable div', async () => {
    const onClick = vi.fn()
    render(<StatCard value="7" label="never baselined" onClick={onClick} />)

    const card = screen.getByRole('button', { name: /never baselined/ })
    expect(card).toHaveAttribute('type', 'button')

    await userEvent.click(card)
    expect(onClick).toHaveBeenCalledTimes(1)
  })

  it('is reachable and pressable from the keyboard', async () => {
    const onClick = vi.fn()
    render(<StatCard value="7" label="never baselined" onClick={onClick} />)

    await userEvent.tab()
    expect(screen.getByRole('button')).toHaveFocus()

    await userEvent.keyboard('{Enter}')
    expect(onClick).toHaveBeenCalledTimes(1)
  })

  it('passes a caller class through so a grid can place it', () => {
    render(<StatCard value="7" label="corrupt" className="placed" onClick={() => {}} />)

    expect(screen.getByRole('button')).toHaveClass('placed')
  })
})
