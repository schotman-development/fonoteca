import type { HTMLAttributes, ReactNode, Ref } from 'react'

import { Text } from '../Text/Text.tsx'
import styles from './Card.module.css'

export type CardProps = Omit<HTMLAttributes<HTMLElement>, 'title'> & {
  /** Heading shown above a rule. Omit for a card that is only a surface. */
  readonly title?: ReactNode
  /** Shown at the far end of the title row — a count, a state, an action. */
  readonly aside?: ReactNode
  readonly padding?: 'md' | 'lg'
  readonly ref?: Ref<HTMLElement>
}

/**
 * A bordered surface with an optional heading.
 *
 * A `<section>` rather than a `<div>`, and the heading is a real one: a page of
 * cards is a page of landmarks, and a screen reader user navigating by heading
 * is the whole reason to spend an element on it.
 *
 * The title is rendered rather than accepted as arbitrary children so that
 * every card in the application has the same rule under it and the same type
 * scale on it — which is what stops six panels drifting into six headers.
 */
export function Card({ title, aside, padding = 'md', className, children, ...rest }: CardProps) {
  return (
    <section
      className={className ? `${styles.root} ${className}` : styles.root}
      data-padding={padding}
      {...rest}
    >
      {title ? (
        <div className={styles.header}>
          {/*
            A real heading wrapping the styled span, rather than a styled span
            pretending to be one. The element carries the semantics and inherits
            its type from Text, so there is one place that decides what a card
            title looks like and one that decides what it means.
          */}
          <h2 className={styles.title}>
            <Text size="sm" weight="semibold" tone="secondary">
              {title}
            </Text>
          </h2>
          {aside ? <div className={styles.aside}>{aside}</div> : null}
        </div>
      ) : null}
      {children}
    </section>
  )
}
