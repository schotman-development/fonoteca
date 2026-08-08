import type { CSSProperties, HTMLAttributes, Ref } from 'react'

import styles from './Stack.module.css'

/** Keys of the spacing scale in @fonoteca/tokens. */
export type SpaceToken = 0 | 2 | 4 | 6 | 8 | 12 | 16 | 20 | 24 | 32 | 40 | 48 | 64 | 96

export type StackProps = HTMLAttributes<HTMLDivElement> & {
  readonly direction?: 'row' | 'column'
  readonly gap?: SpaceToken
  readonly align?: 'start' | 'center' | 'end' | 'stretch' | 'baseline'
  readonly justify?: 'start' | 'center' | 'end' | 'between' | 'around'
  readonly wrap?: boolean
  readonly inline?: boolean
  readonly ref?: Ref<HTMLDivElement>
}

/**
 * The layout primitive. Components do not carry their own margins — spacing is
 * always the parent's decision — so this is how nearly all layout gets built.
 *
 * `gap` is passed through a CSS custom property rather than a data-attribute
 * per value, which keeps the stylesheet from growing a rule for every step of
 * the spacing scale.
 */
export function Stack({
  direction = 'row',
  gap = 0,
  align = 'stretch',
  justify = 'start',
  wrap = false,
  inline = false,
  className,
  style,
  ...rest
}: StackProps) {
  const gapStyle = {
    '--stack-gap': `var(--space-${gap})`,
    ...style,
  } as CSSProperties

  return (
    <div
      className={className ? `${styles.root} ${className}` : styles.root}
      style={gapStyle}
      data-direction={direction}
      data-align={align}
      data-justify={justify}
      data-wrap={wrap || undefined}
      data-inline={inline || undefined}
      {...rest}
    />
  )
}
