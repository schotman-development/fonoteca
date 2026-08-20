import type { HTMLAttributes, Ref } from 'react'

import styles from './VisuallyHidden.module.css'

export type VisuallyHiddenProps = HTMLAttributes<HTMLSpanElement> & {
  readonly ref?: Ref<HTMLSpanElement>
}

/**
 * Text for the accessibility tree and not for the eye.
 *
 * Use it where a column is legible only because it is terse: the header of a
 * column of icon buttons (an empty `<th>` is an axe failure, and a visible
 * "Play" above forty play buttons is noise), or a value written as a dash
 * because writing "not measured" forty times down a narrow column destroys the
 * scan that made the column worth having.
 *
 * It is **not** a way to hide something. `display: none` and `hidden` remove an
 * element from the accessibility tree as well as from the page; this keeps it in
 * one and takes it out of the other, which is the whole point.
 */
export function VisuallyHidden({ className, ...rest }: VisuallyHiddenProps) {
  return <span className={className ? `${styles.root} ${className}` : styles.root} {...rest} />
}
