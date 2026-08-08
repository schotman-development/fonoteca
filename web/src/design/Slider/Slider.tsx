/**
 * A range input — design 601.
 *
 * It is a real `<input type="range">` and it stays one. A range input is the
 * only control in a browser that already answers Home, End, the arrow keys and
 * Page Up/Down, publishes `aria-valuenow`/`min`/`max` without being asked, and
 * has a thumb the platform sizes for touch. Everything a hand-built slider
 * gains over it is paint, and `accent-color` (the design's own choice) buys the
 * paint for one declaration.
 *
 * Three decisions worth stating:
 *
 *   - **`onChange` hands over a number, not an event.** A range's `value` is a
 *     string, and every caller would otherwise write the same `Number(...)`
 *     with the same chance of forgetting it — a slider that reports `"85"` and
 *     a caller that stores it is a filter comparing a string to a number three
 *     screens later.
 *   - **the value display is `aria-hidden`, and its text is `aria-valuetext`.**
 *     Rendering it as a live region would announce the figure twice on every
 *     arrow key, once from the slider and once from the region. The screen
 *     reader gets the formatted string through the property that exists for it.
 *   - **the label is required.** An unnamed range is announced as "slider,
 *     85" with nothing saying what is at 85. `labelHidden` puts it in the
 *     accessibility tree without drawing it, for a row that captions itself.
 *
 * Nothing here knows what is being adjusted: the design's one use of it is the
 * Identify screen's auto-accept threshold, which has no backend and is a
 * placeholder — which is precisely why this component must not carry a word
 * about confidence, thresholds or acceptance.
 */

import type { ChangeEvent } from 'react'
import { useId } from 'react'

import { cx } from '@/design/cx'
import type { StyleableProps } from '@/design/types'

import styles from '@/design/Slider/Slider.module.css'

export interface SliderProps extends StyleableProps {
  /** The accessible name, and the visible caption unless `labelHidden`. */
  label: string
  /** Draw the label into the accessibility tree only. */
  labelHidden?: boolean
  min: number
  max: number
  /** Defaults to the platform's own `1`. */
  step?: number
  value: number
  /** Receives the new value as a **number**, never the change event. */
  onChange: (value: number) => void
  /**
   * The formatted figure beside the track (design 601) — `"85%"`, `"6 h"`.
   * Also becomes `aria-valuetext`, so the reading matches what is drawn.
   * Absent ⇒ no figure is drawn and the platform reads the raw number.
   */
  valueText?: string
  disabled?: boolean
}

export function Slider({
  label,
  labelHidden = false,
  min,
  max,
  step,
  value,
  onChange,
  valueText,
  disabled = false,
  className,
}: SliderProps) {
  const id = useId()

  return (
    <div className={cx(styles.slider, className)}>
      <label htmlFor={id} className={labelHidden ? 'visuallyHidden' : styles.label}>
        {label}
      </label>
      <input
        id={id}
        type="range"
        className={styles.input}
        min={min}
        max={max}
        step={step}
        value={value}
        disabled={disabled}
        aria-valuetext={valueText}
        onChange={(event: ChangeEvent<HTMLInputElement>) => {
          // `.value` rather than `.valueAsNumber`: both are populated for a
          // range, and the string form is the one every environment agrees on.
          onChange(Number(event.currentTarget.value))
        }}
      />
      {valueText === undefined ? null : (
        // Hidden from the reading because `aria-valuetext` already carries it;
        // announcing it twice on every arrow key is worse than not at all.
        <span aria-hidden="true" className={styles.value}>
          {valueText}
        </span>
      )}
    </div>
  )
}
