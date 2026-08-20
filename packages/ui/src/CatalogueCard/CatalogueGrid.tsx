import { Children, type HTMLAttributes, type ReactNode, type Ref } from 'react'

import type { CatalogueCardVariant } from './CatalogueCard.tsx'
import styles from './CatalogueGrid.module.css'

export type CatalogueGridProps = HTMLAttributes<HTMLUListElement> & {
  /**
   * Pins the column size instead of letting the contents decide it.
   *
   * The default is the useful behaviour and this is the escape hatch: a grid
   * rendering a loading placeholder has no cards to be measured yet, and one
   * that will gain albums a moment after it mounts should not resize under the
   * reader when they arrive.
   */
  readonly size?: CatalogueCardVariant
  readonly ref?: Ref<HTMLUListElement>
}

/**
 * A responsive grid of `CatalogueCard`s.
 *
 * **Artists get smaller tiles than albums, and a grid holding both uses the
 * album size.** That rule is in the stylesheet rather than here, keyed off the
 * `data-catalogue-card` attribute every card carries — see the comment there
 * for why it is worth a `:has()`. The short version is that the alternative is
 * a prop the caller has to keep in sync with its own data, and a mixed grid is
 * exactly the case where they would forget.
 *
 * A `<ul>`, because a grid of tiles is a list: without it a screen reader
 * announces forty separate links and never says how many there are, and with it
 * the reader gets "list, 40 items" and can skip the whole thing. Children are
 * wrapped in `<li>` here rather than by the caller so that the markup cannot be
 * got wrong from outside — `Children.map` preserves the keys you gave them.
 */
export function CatalogueGrid({ size, className, children, ...rest }: CatalogueGridProps) {
  return (
    <ul
      className={className ? `${styles.root} ${className}` : styles.root}
      data-size={size}
      {...rest}
    >
      {Children.map(children, (child: ReactNode) =>
        child == null || typeof child === 'boolean' ? null : (
          <li className={styles.item}>{child}</li>
        ),
      )}
    </ul>
  )
}
