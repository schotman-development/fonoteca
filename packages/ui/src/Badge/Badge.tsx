import type { HTMLAttributes, Ref } from 'react'

import styles from './Badge.module.css'

export type BadgeTone = 'neutral' | 'accent' | 'success' | 'warning' | 'danger' | 'info'
export type BadgeVariant = 'subtle' | 'solid' | 'outline'

export type BadgeProps = HTMLAttributes<HTMLSpanElement> & {
  readonly tone?: BadgeTone
  readonly variant?: BadgeVariant
  readonly size?: 'sm' | 'md'
  /** Use a monospace face — for codec names, bit depths and sample rates. */
  readonly mono?: boolean
  readonly ref?: Ref<HTMLSpanElement>
}

/**
 * A status chip. In this product it does real work in dense table rows: quality
 * tier, codec, "duplicate", "upgrade available", scan state.
 *
 * Colour alone never carries the meaning — the label always says what the badge
 * means, so it survives both greyscale and colour-blindness.
 */
export function Badge({
  tone = 'neutral',
  variant = 'subtle',
  size = 'sm',
  mono = false,
  className,
  ...rest
}: BadgeProps) {
  return (
    <span
      className={className ? `${styles.root} ${className}` : styles.root}
      data-tone={tone}
      data-variant={variant}
      data-size={size}
      data-mono={mono || undefined}
      {...rest}
    />
  )
}
