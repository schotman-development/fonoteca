import { type HTMLAttributes, type ReactNode, type Ref, useId } from 'react'

import hidden from '../VisuallyHidden/VisuallyHidden.module.css'
import styles from './Field.module.css'

/**
 * What the control is handed. **Spread all of it onto the control.**
 *
 * `id` is what the label's `for` points at, and `aria-describedby` is the only
 * reason a hint or an error is heard rather than merely seen.
 *
 * Every key past `id` is optional, and the object is *built* with conditional
 * spreads rather than with `undefined` values: under `exactOptionalPropertyTypes`
 * an object typed `{ 'aria-describedby': string | undefined }` will not spread
 * onto a control whose prop is `aria-describedby?: string`.
 */
export type FieldControlProps = {
  readonly id: string
  readonly 'aria-describedby'?: string
  readonly 'aria-invalid'?: true
}

export type FieldProps = Omit<HTMLAttributes<HTMLDivElement>, 'children'> & {
  /** The accessible name. Rendered in a real `<label>`, never a styled span. */
  readonly label: ReactNode
  /** Advice, under the control. Reachable through `aria-describedby`. */
  readonly hint?: ReactNode
  /**
   * A failure. Sets `aria-invalid` and **replaces** the hint — both in the
   * description and on screen. Two descriptions read one after the other is how
   * an error goes unheard.
   */
  readonly error?: ReactNode
  /** Hidden from the eye, kept in the accessible name. For a toolbar. */
  readonly labelHidden?: boolean
  readonly children: (control: FieldControlProps) => ReactNode
  readonly ref?: Ref<HTMLDivElement>
}

/**
 * A label, a control, and the wiring between them.
 *
 * `Input` is deliberately bare and says so: it carries no label, and every use
 * must pair it with a `<label htmlFor>` or an `aria-label` or the axe run fails
 * the story. This is the component that makes forgetting impossible — the
 * `<label>` is not optional and its `for` is generated, so two Fields on one
 * page cannot collide however they are composed.
 *
 * A render prop rather than `cloneElement`, which this package does nowhere: it
 * is explicit about which element receives the wiring, it is fully typed, and it
 * follows the escape-hatch idiom `CatalogueCard.render` already established.
 *
 *   <Field label="Filter">
 *     {(control) => <Input {...control} type="search" value={q} onChange={…} />}
 *   </Field>
 */
export function Field({
  label,
  hint,
  error,
  labelHidden = false,
  children,
  className,
  ...rest
}: FieldProps) {
  const base = useId()
  const controlId = `${base}-control`
  const messageId = `${base}-message`

  const message = error ?? hint
  const invalid = error != null

  const control: FieldControlProps = {
    id: controlId,
    ...(message != null ? { 'aria-describedby': messageId } : {}),
    ...(invalid ? { 'aria-invalid': true as const } : {}),
  }

  return (
    <div
      className={className ? `${styles.root} ${className}` : styles.root}
      data-invalid={invalid || undefined}
      {...rest}
    >
      {/* The clip class comes from VisuallyHidden rather than being respelled
          here, so there is one copy of the technique and a `<label>` that is
          hidden is hidden the same way as everything else. */}
      <label
        className={labelHidden ? `${styles.label} ${hidden.root}` : styles.label}
        htmlFor={controlId}
      >
        {label}
      </label>
      {children(control)}
      {message != null ? (
        <p className={styles.message} id={messageId} data-tone={invalid ? 'danger' : 'hint'}>
          {message}
        </p>
      ) : null}
    </div>
  )
}
