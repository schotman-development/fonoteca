import { fireEvent, render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it, vi } from 'vitest'

import { Slider } from '@/design/Slider/Slider'

describe('Slider', () => {
  it('is a named range with its bounds published', () => {
    render(<Slider label="Auto-accept above" min={70} max={99} value={85} onChange={() => {}} />)

    const slider = screen.getByRole('slider', { name: 'Auto-accept above' })
    expect(slider).toHaveAttribute('min', '70')
    expect(slider).toHaveAttribute('max', '99')
    expect(slider).toHaveValue('85')
  })

  /**
   * `fireEvent.change` rather than an arrow key: jsdom does not implement a
   * range input's keyboard behaviour, so `{ArrowRight}` moves nothing there and
   * a test written that way would pass on an empty handler. What is actually
   * this component's own is the mapping from the DOM's string to a number, and
   * that is what is asserted.
   */
  it('reports a number, never the change event', () => {
    const onChange = vi.fn()
    render(<Slider label="Threshold" min={0} max={10} value={5} onChange={onChange} />)

    fireEvent.change(screen.getByRole('slider', { name: 'Threshold' }), {
      target: { value: '6' },
    })

    expect(onChange).toHaveBeenCalledTimes(1)
    expect(onChange).toHaveBeenCalledWith(6)
    expect(typeof onChange.mock.calls[0]?.[0]).toBe('number')
  })

  it('is a real range input, so the platform gives it the keyboard for free', async () => {
    render(<Slider label="Threshold" min={0} max={10} value={5} onChange={() => {}} />)

    const slider = screen.getByRole('slider', { name: 'Threshold' })
    expect(slider.tagName).toBe('INPUT')
    expect(slider).toHaveAttribute('type', 'range')

    await userEvent.tab()
    expect(slider).toHaveFocus()
  })

  it('draws the formatted figure and reads the same string, not a second copy', () => {
    render(
      <Slider label="Threshold" min={0} max={100} value={85} valueText="85%" onChange={() => {}} />,
    )

    expect(screen.getByRole('slider', { name: 'Threshold' })).toHaveAttribute(
      'aria-valuetext',
      '85%',
    )
    const figure = screen.getByText('85%')
    expect(figure).toHaveAttribute('aria-hidden', 'true')
  })

  it('draws no figure when none is given', () => {
    const { container } = render(
      <Slider label="Threshold" min={0} max={10} value={5} onChange={() => {}} />,
    )
    expect(container.textContent).toBe('Threshold')
  })

  it('keeps the name when the caption is hidden', () => {
    render(
      <Slider labelHidden label="Threshold" min={0} max={10} value={5} onChange={() => {}} />,
    )

    const label = screen.getByText('Threshold')
    expect(label).toHaveClass('visuallyHidden')
    expect(screen.getByRole('slider', { name: 'Threshold' })).toBeInTheDocument()
  })

  it('does not report a change when disabled', async () => {
    const onChange = vi.fn()
    render(
      <Slider disabled label="Threshold" min={0} max={10} value={5} onChange={onChange} />,
    )

    const slider = screen.getByRole('slider', { name: 'Threshold' })
    expect(slider).toBeDisabled()

    await userEvent.click(slider)
    expect(onChange).not.toHaveBeenCalled()
  })

  it('gives two sliders distinct ids, so one label cannot name both', () => {
    render(
      <>
        <Slider label="One" min={0} max={10} value={1} onChange={() => {}} />
        <Slider label="Two" min={0} max={10} value={2} onChange={() => {}} />
      </>,
    )

    const [first, second] = screen.getAllByRole('slider')
    expect(first?.id).not.toBe(second?.id)
  })

  it('carries the caller class', () => {
    const { container } = render(
      <Slider className="placed" label="One" min={0} max={10} value={1} onChange={() => {}} />,
    )
    expect(container.firstElementChild).toHaveClass('placed')
  })
})
