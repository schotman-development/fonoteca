import type { InputHTMLAttributes, Ref } from 'react'

import styles from './Input.module.css'

export type InputProps = Omit<InputHTMLAttributes<HTMLInputElement>, 'size'> & {
  readonly inputSize?: 'sm' | 'md'
  /**
   * Marks the value as failing validation. Sets `aria-invalid`, so screen
   * readers hear the error state rather than only seeing the red border.
   */
  readonly invalid?: boolean
  readonly fullWidth?: boolean
  readonly mono?: boolean
  readonly ref?: Ref<HTMLInputElement>
}

/**
 * The bare control. It carries no label of its own — every use MUST pair it
 * with a `<label htmlFor>` or an `aria-label`, and the a11y addon will fail the
 * story if neither is present.
 *
 * `Field` is the component that enforces that pairing, and it is what most uses
 * should reach for: it owns the `<label>`, generates the `for`, and wires a hint
 * or an error into `aria-describedby`. This stays bare for the cases a Field
 * would only get in the way of — a cell in a table, a control inside a toolbar
 * that is already named.
 */
export function Input({
  inputSize = 'md',
  invalid = false,
  fullWidth = false,
  mono = false,
  className,
  ...rest
}: InputProps) {
  return (
    <input
      className={className ? `${styles.root} ${className}` : styles.root}
      data-size={inputSize}
      data-invalid={invalid || undefined}
      data-full-width={fullWidth || undefined}
      data-mono={mono || undefined}
      aria-invalid={invalid || undefined}
      {...rest}
    />
  )
}
