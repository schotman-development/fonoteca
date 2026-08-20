import type { Meta, StoryObj } from '@storybook/react-vite'
import { useState } from 'react'
import { expect, fn, userEvent } from 'storybook/test'

import { formatTime } from '../playback/formatTime.ts'
import { Stack } from '../Stack/Stack.tsx'
import { Text } from '../Text/Text.tsx'
import { Slider, type SliderProps } from './Slider.tsx'

/**
 * A slider is controlled; these stories supply the state it needs, taking
 * `value` as the initial one.
 *
 * It takes `SliderProps` **whole** rather than `Omit<SliderProps, 'value'>`, and
 * that is not a style choice: `SliderProps` is a union (a name is `aria-label`
 * xor `aria-labelledby`), `Omit` over a union collapses it into one object with
 * both keys optional, and the result is then assignable to neither branch. A
 * wrapper that wants to narrow the props must pass the type through intact.
 */
function Controlled(props: SliderProps) {
  const [value, setValue] = useState(props.value)
  return <Slider {...props} value={value} onValueChange={setValue} />
}

const meta = {
  title: 'Primitives/Slider',
  component: Slider,
  parameters: { layout: 'padded' },
  argTypes: {
    orientation: { control: 'inline-radio', options: ['horizontal', 'vertical'] },
    sliderSize: { control: 'inline-radio', options: ['sm', 'md'] },
    tone: { control: 'inline-radio', options: ['accent', 'neutral'] },
  },
  args: { 'aria-label': 'Seek', value: 40, max: 100, onValueChange: fn() },
  render: (args) => (
    <div style={{ width: '320px' }}>
      <Controlled {...args} />
    </div>
  ),
} satisfies Meta<typeof Slider>

export default meta
type Story = StoryObj<typeof meta>

export const Default: Story = {
  play: async ({ canvas }) => {
    const slider = canvas.getByRole('slider', { name: 'Seek' })
    await expect(slider).toHaveAttribute('aria-valuenow', '40')
    await expect(slider).toHaveAttribute('aria-valuemin', '0')
    await expect(slider).toHaveAttribute('aria-valuemax', '100')
  },
}

/**
 * The whole key map, asserted — it is invisible in the DOM, so a regression in
 * it produces a control that still looks perfect and no longer works.
 */
export const Keyboard: Story = {
  args: { largeStep: 10 },
  play: async ({ canvas }) => {
    const slider = canvas.getByRole('slider', { name: 'Seek' })
    slider.focus()

    await userEvent.keyboard('{ArrowRight}')
    await expect(slider).toHaveAttribute('aria-valuenow', '41')

    await userEvent.keyboard('{ArrowLeft}{ArrowLeft}')
    await expect(slider).toHaveAttribute('aria-valuenow', '39')

    await userEvent.keyboard('{ArrowUp}')
    await expect(slider).toHaveAttribute('aria-valuenow', '40')

    await userEvent.keyboard('{PageUp}')
    await expect(slider).toHaveAttribute('aria-valuenow', '50')

    await userEvent.keyboard('{PageDown}{PageDown}')
    await expect(slider).toHaveAttribute('aria-valuenow', '30')

    await userEvent.keyboard('{Home}')
    await expect(slider).toHaveAttribute('aria-valuenow', '0')

    await userEvent.keyboard('{End}')
    await expect(slider).toHaveAttribute('aria-valuenow', '100')
  },
}

/**
 * The commit contract, which is what lets a scrub avoid seeking on every pixel:
 * `onValueChange` fires throughout a drag, `onValueCommit` exactly once, on
 * release.
 *
 * A pointer press also moves focus to the thumb — the role lives there, so a
 * control that were operable by mouse and unfocusable by click would be a
 * control the keyboard could not follow.
 */
export const PointerDrag: Story = {
  render: (args) => {
    const [value, setValue] = useState(40)
    const [commits, setCommits] = useState<readonly number[]>([])

    return (
      <Stack direction="column" gap={12} style={{ width: '320px' }}>
        <Slider
          {...args}
          value={value}
          onValueChange={setValue}
          onValueCommit={(next) => setCommits((seen) => [...seen, next])}
        />
        <Text size="xs" tone="tertiary" family="mono">
          value {value} · commits {commits.length}
        </Text>
      </Stack>
    )
  },
  play: async ({ canvas, canvasElement }) => {
    const slider = canvas.getByRole('slider', { name: 'Seek' })
    const rail = canvasElement.querySelector('[aria-hidden="true"]')
    if (rail == null) throw new Error('expected a rail')

    const box = rail.getBoundingClientRect()
    await userEvent.pointer([
      { target: rail, coords: { clientX: box.left + box.width * 0.25, clientY: box.top + 2 } },
      { keys: '[MouseLeft>]', target: rail },
      { target: rail, coords: { clientX: box.left + box.width * 0.75, clientY: box.top + 2 } },
      { keys: '[/MouseLeft]', target: rail },
    ])

    await expect(slider).toHaveFocus()
    await expect(canvas.getByText(/commits 1$/)).toBeVisible()
  },
}

/**
 * `aria-valuetext` is what stops a seek bar announcing "83". A time is what the
 * control actually represents, and it comes from `formatTime` rather than a
 * second spelling invented here.
 */
export const TimeAsValueText: Story = {
  args: {
    'aria-label': 'Seek',
    value: 83,
    max: 245,
    formatValue: (value, max) => `${formatTime(value)} of ${formatTime(max)}`,
  },
  play: async ({ canvas }) => {
    await expect(canvas.getByRole('slider', { name: 'Seek' })).toHaveAttribute(
      'aria-valuetext',
      '1:23 of 4:05',
    )
  },
}

/** Up increases, and `aria-orientation` is emitted only when it is not the default. */
export const Vertical: Story = {
  args: { 'aria-label': 'Volume', orientation: 'vertical', value: 60, max: 100 },
  render: (args) => (
    <div style={{ height: '160px' }}>
      <Controlled {...args} />
    </div>
  ),
  play: async ({ canvas }) => {
    const slider = canvas.getByRole('slider', { name: 'Volume' })
    await expect(slider).toHaveAttribute('aria-orientation', 'vertical')

    slider.focus()
    await userEvent.keyboard('{ArrowUp}')
    await expect(slider).toHaveAttribute('aria-valuenow', '61')
  },
}

/**
 * `aria-disabled`, not `disabled`. It stays in the tab order, so a keyboard user
 * can still discover that a seek control exists on a track whose length is not
 * yet known — they simply cannot move it.
 */
export const Disabled: Story = {
  args: { disabled: true },
  play: async ({ canvas }) => {
    const slider = canvas.getByRole('slider', { name: 'Seek' })
    await expect(slider).toHaveAttribute('aria-disabled', 'true')

    slider.focus()
    await expect(slider).toHaveFocus()

    await userEvent.keyboard('{ArrowRight}')
    await expect(slider).toHaveAttribute('aria-valuenow', '40')
  },
}

/**
 * In right-to-left, ArrowRight *decreases* — left and right are physical, and
 * the control travels the other way.
 *
 * Home and End do not mirror, and neither do PageUp and PageDown: those are not
 * directions, they are "least", "most", "more" and "less".
 */
export const RightToLeft: Story = {
  render: (args) => (
    <div dir="rtl" style={{ width: '320px' }}>
      <Controlled {...args} />
    </div>
  ),
  play: async ({ canvas }) => {
    const slider = canvas.getByRole('slider', { name: 'Seek' })
    slider.focus()

    await userEvent.keyboard('{ArrowRight}')
    await expect(slider).toHaveAttribute('aria-valuenow', '39')

    await userEvent.keyboard('{Home}')
    await expect(slider).toHaveAttribute('aria-valuenow', '0')

    await userEvent.keyboard('{End}')
    await expect(slider).toHaveAttribute('aria-valuenow', '100')
  },
}

/**
 * A volume control steps by 0.05, and floating-point addition would report
 * `aria-valuenow="0.30000000000000004"` — a bad announcement, and an assertion
 * nobody could write. Snapping is decimal-aware for exactly this.
 */
export const FractionalSteps: Story = {
  args: { 'aria-label': 'Volume', value: 0.25, min: 0, max: 1, step: 0.05 },
  play: async ({ canvas }) => {
    const slider = canvas.getByRole('slider', { name: 'Volume' })
    slider.focus()

    await userEvent.keyboard('{ArrowRight}')
    await expect(slider).toHaveAttribute('aria-valuenow', '0.3')
  },
}

/**
 * An unknown length, which is why there is no `indeterminate` prop.
 *
 * ARIA 1.2 makes `aria-valuenow` required on `slider` and axe's
 * `aria-required-attr` enforces the 1.2 table, so omitting it fails the build.
 * Swapping in a `progressbar` instead would change the element's role under a
 * focused user the moment metadata arrived. A zero-length disabled range keeps
 * the DOM stable, the ARIA valid, and the statement true.
 */
export const ZeroLengthRange: Story = {
  args: { value: 0, max: 0, formatValue: () => 'Length unknown' },
  play: async ({ canvas }) => {
    const slider = canvas.getByRole('slider', { name: 'Seek' })
    await expect(slider).toHaveAttribute('aria-valuenow', '0')
    await expect(slider).toHaveAttribute('aria-valuetext', 'Length unknown')
    await expect(slider).toHaveAttribute('aria-disabled', 'true')
  },
}

/** The buffered band sits behind the fill and is never part of the value. */
export const WithBuffer: Story = {
  args: { value: 40, max: 245, secondaryValue: 160, formatValue: (v) => formatTime(v) },
}
