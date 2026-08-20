import type { HTMLAttributes, ReactNode } from 'react'

import { Artwork } from '../Artwork/Artwork.tsx'
import { Text } from '../Text/Text.tsx'
import styles from './CatalogueCard.module.css'

export type CatalogueCardVariant = 'album' | 'artist'

/**
 * What `render` is handed. Spread it onto whatever element you return — the
 * `data-catalogue-card` attribute is not decoration, `CatalogueGrid` sizes its
 * columns by looking for it.
 */
export type CatalogueCardElementProps = HTMLAttributes<HTMLElement> & {
  readonly className: string
  readonly 'data-catalogue-card': CatalogueCardVariant
  readonly children: ReactNode
}

export type CatalogueCardProps = Omit<HTMLAttributes<HTMLElement>, 'title'> & {
  /**
   * Album or artist. Required rather than defaulted: it decides the shape of
   * the artwork, the alignment of the text and — through the grid — how much
   * room the card gets, and none of those has an obvious fallback.
   */
  readonly variant: CatalogueCardVariant
  /**
   * A string rather than a `ReactNode`, unlike `Card`. Two reasons: it is the
   * card's accessible name, and it is what the monogram placeholder is derived
   * from when there is no artwork.
   */
  readonly title: string
  /** Under the title — the artist for an album, a track count for an artist. */
  readonly subtitle?: ReactNode
  /** Under the subtitle. Badges, mostly: quality, certainty, "incomplete". */
  readonly meta?: ReactNode
  /** Cover or portrait. Omit and the card draws a monogram instead. */
  readonly image?: string
  /** Renders the card as a link. */
  readonly href?: string
  /**
   * Own the element yourself — this is how a router link becomes a card:
   *
   *   <CatalogueCard
   *     variant="album"
   *     title={release.title}
   *     render={(props) => <Link {...props} to="/library/releases/$releaseId" … />}
   *   />
   *
   * The design system has no router and will not grow one, and `render` is
   * also where a `ref` goes, which is why this component takes no `ref` prop:
   * the element it renders is either an anchor or a div, and a single `Ref`
   * type cannot honestly describe both.
   */
  readonly render?: (props: CatalogueCardElementProps) => ReactNode
}

/**
 * One album or one artist, as a tile.
 *
 * The two variants are the same card with different geometry, which is the
 * point of building them as one component: a square cover with the text ranged
 * left reads as a *thing you own*, a circular portrait with the text centred
 * reads as a *person or group*, and every other decision — type scale, spacing,
 * hover, focus, truncation — is shared and therefore cannot drift.
 *
 * Nothing here is interactive by default. A card with `href` (or a `render`
 * that returns a link) is one link containing all of its text, so the whole
 * tile is one tab stop rather than three, and the artwork is `alt=""` because
 * the title is already inside that link — a cover captioned with the album name
 * next to the album name is noise for a screen reader, not information.
 */
export function CatalogueCard({
  variant,
  title,
  subtitle,
  meta,
  image,
  href,
  render,
  className,
  ...rest
}: CatalogueCardProps) {
  const elementProps: CatalogueCardElementProps = {
    ...rest,
    // Joined rather than the ternary the other components use: `render` is
    // handed this object, so the class has to be a definite string rather than
    // the `string | undefined` a CSS-module lookup is typed as.
    className: [styles.root, className].filter(Boolean).join(' '),
    'data-catalogue-card': variant,
    children: (
      <>
        <Artwork
          name={title}
          shape={variant === 'artist' ? 'circle' : 'square'}
          size="fill"
          {...(image != null ? { src: image } : {})}
        />
        <span className={styles.body}>
          <Text size="sm" weight="medium" truncate>
            {title}
          </Text>
          {subtitle != null ? (
            <Text size="xs" tone="tertiary" truncate>
              {subtitle}
            </Text>
          ) : null}
          {meta != null ? <span className={styles.meta}>{meta}</span> : null}
        </span>
      </>
    ),
  }

  if (render) return render(elementProps)

  // A plain span when there is nowhere to go. Deliberately not a div with an
  // onClick: a handler on a non-interactive element is invisible to the
  // keyboard, and this package has no way to enforce that it isn't done.
  return href != null ? <a href={href} {...elementProps} /> : <span {...elementProps} />
}
