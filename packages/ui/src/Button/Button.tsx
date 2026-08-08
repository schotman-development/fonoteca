import type { ButtonHTMLAttributes, Ref } from 'react'

import styles from './Button.module.css'

export type ButtonVariant = 'primary' | 'secondary' | 'ghost' | 'danger'
export type ButtonSize = 'sm' | 'md'

export type ButtonProps = ButtonHTMLAttributes<HTMLButtonElement> & {
  readonly variant?: ButtonVariant
  readonly size?: ButtonSize
  readonly fullWidth?: boolean
  /** React 19 passes refs as an ordinary prop; `forwardRef` is no longer needed. */
  readonly ref?: Ref<HTMLButtonElement>
}

export function Button({
  variant = 'secondary',
  size = 'md',
  fullWidth = false,
  className,
  type = 'button',
  ...rest
}: ButtonProps) {
  return (
    <button
      // HTML defaults `type` to "submit", which silently submits any enclosing
      // form. In an app whose forms trigger destructive file operations, that
      // default is a hazard; opt in to submit explicitly.
      type={type}
      className={className ? `${styles.root} ${className}` : styles.root}
      data-variant={variant}
      data-size={size}
      data-full-width={fullWidth || undefined}
      {...rest}
    />
  )
}
