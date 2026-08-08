/**
 * A labelled control with a hint under it (design 360–366).
 *
 * `Field` owns the wiring and nothing else: it mints one id with `useId()`,
 * points its `<label>` at the control, and hands the control the
 * `aria-describedby` that links the hint. That is the part a caller gets wrong
 * — a hint rendered as a loose `<span>` is invisible to a screen reader, so the
 * sentence explaining what the field expects is read by everyone except the
 * people who most need it — and it is the part that cannot be done by the
 * control on its own, because the hint is Field's element.
 *
 * Children may be given two ways, and the difference is deliberate:
 *
 *   - **as a function** — `{(a) => <TextInput {...a} … />}`. Field passes
 *     `{ id, 'aria-describedby' }` and renders an explicit `<label for=…>`.
 *     This is the form to use for a real control.
 *   - **as a node** — for a slot that holds no form control at all: a read-only
 *     readout, or a `Placeholder` standing in for something the backend cannot
 *     answer yet. Field then renders the design's own structure, a `<label>`
 *     WRAPPING the content, so the caption is still associated with anything
 *     focusable inside it and there is no `for` attribute pointing at nothing.
 *
 * A dangling `for` is worse than an implicit association, which is why the two
 * are not collapsed into one.
 */

import type { ComponentPropsWithRef, ReactNode } from 'react'
import { useId } from 'react'

import { cx } from '@/design/cx'
import type { StyleableProps } from '@/design/types'
import styles from '@/design/Field/Field.module.css'

/** What Field hands a control so the label and hint reach it. */
export interface FieldControl {
  id: string
  'aria-describedby': string | undefined
}

export interface FieldProps extends StyleableProps {
  label: ReactNode
  /** The sentence under the control. Linked by `aria-describedby`. */
  hint?: ReactNode
  children: ReactNode | ((control: FieldControl) => ReactNode)
}

export function Field({ label, hint, children, className }: FieldProps) {
  const id = useId()
  const hintId = `${id}-hint`
  const described = hint === undefined || hint === null ? undefined : hintId

  const caption = <span className={styles.label}>{label}</span>
  const note =
    described === undefined ? null : (
      <span className={styles.hint} id={hintId}>
        {hint}
      </span>
    )

  if (typeof children === 'function') {
    return (
      <div className={cx(styles.field, className)}>
        <label className={styles.label} htmlFor={id}>
          {label}
        </label>
        {children({ id, 'aria-describedby': described })}
        {note}
      </div>
    )
  }

  return (
    <label className={cx(styles.field, className)}>
      {caption}
      {children}
      {note}
    </label>
  )
}

export interface TextInputProps
  extends Omit<ComponentPropsWithRef<'input'>, 'size'> {
  /** Use the mono face — for an MBID, a barcode or a path. */
  mono?: boolean
}

export function TextInput({ mono, className, ...rest }: TextInputProps) {
  return (
    <input
      type="text"
      {...rest}
      className={cx(styles.input, mono === true && styles.mono, className)}
    />
  )
}

export interface TextAreaProps extends ComponentPropsWithRef<'textarea'> {
  /** Use the mono face — for an MBID, a barcode or a path. */
  mono?: boolean
}

/**
 * The multi-line sibling of `TextInput`, for a value that is a *list*.
 *
 * It exists because the Aliases box separates entries by LINE rather than by
 * comma: a comma is a legitimate character inside a name ("Tchaikovsky, Pyotr
 * Ilyich"), which is also why the column behind it stores JSON. A single-line
 * box with a comma separator would silently turn one alias into two.
 */
export function TextArea({ mono, className, rows = 4, ...rest }: TextAreaProps) {
  return (
    // `rest` first, as `TextInput` spreads it — `rows` is destructured out of
    // it, so this is ordering for the reader rather than an override rule.
    <textarea
      {...rest}
      rows={rows}
      className={cx(styles.textArea, mono === true && styles.mono, className)}
    />
  )
}
