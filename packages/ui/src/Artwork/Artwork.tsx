import { type HTMLAttributes, type Ref, useState } from 'react'

import styles from './Artwork.module.css'

/** A thing you own is a square; a person or a group is a circle. */
export type ArtworkShape = 'square' | 'circle'

export type ArtworkSize = 'fill' | 'sm' | 'md' | 'lg'

export type ArtworkProps = Omit<HTMLAttributes<HTMLSpanElement>, 'children'> & {
  /**
   * What the monogram is derived from when there is no picture.
   *
   * It is **not** the accessible name. The image is `alt=""` and the monogram is
   * `aria-hidden`, because in every use the name is already text beside it, and
   * a cover captioned with the album name next to the album name is noise for a
   * screen reader rather than information.
   */
  readonly name: string
  readonly shape?: ArtworkShape
  /** Omit it, or let it fail to load, and the monogram is drawn instead. */
  readonly src?: string
  /**
   * `fill` takes the width it is given and stays square — what a grid tile
   * needs, since the tile has no width of its own. The fixed sizes are a box in
   * a row, which is what a candidate or an evidence header needs.
   */
  readonly size?: ArtworkSize
  readonly ref?: Ref<HTMLSpanElement>
}

/**
 * Cover art, or the initials that stand in for it.
 *
 * A `<span>` rather than a `<div>`: `CatalogueCard` renders this inside an
 * `<a>`, where only phrasing content is legal.
 *
 * The fallback is the ordinary case, not the exception — the catalogue holds no
 * cover art at all today, so every card in the application draws a monogram.
 */
export function Artwork({
  name,
  shape = 'square',
  src,
  size = 'fill',
  className,
  ...rest
}: ArtworkProps) {
  // The src that failed, not a boolean: a card whose image prop changes must
  // get a fresh attempt, and storing the src means that happens without an
  // effect to reset the flag.
  const [brokenImage, setBrokenImage] = useState<string | null>(null)

  return (
    <span
      className={className ? `${styles.root} ${className}` : styles.root}
      data-shape={shape}
      data-size={size}
      {...rest}
    >
      {src != null && src !== brokenImage ? (
        <img
          className={styles.image}
          src={src}
          alt=""
          loading="lazy"
          decoding="async"
          /*
            No Referer to whoever is serving the picture. Cover art comes from a
            third-party CDN, and the default header would tell it which page of
            a private library the reader is looking at — a self-hosted catalogue
            leaking its browsing one tile at a time.
          */
          referrerPolicy="no-referrer"
          onError={() => setBrokenImage(src)}
        />
      ) : (
        // Hidden from assistive tech: the letters are a stand-in for a picture,
        // and read aloud they would prefix the title with its own initials.
        <span className={styles.monogram} aria-hidden="true">
          {initials(name)}
        </span>
      )}
    </span>
  )
}

/**
 * Up to two initials, for the artwork placeholder.
 *
 * `Array.from` rather than `word[0]`, because a name beginning outside the BMP
 * — and a library of 100,000 tracks has them — would otherwise be cut in half
 * and render as a replacement character.
 */
function initials(name: string): string {
  return name
    .split(/\s+/)
    .filter(Boolean)
    .slice(0, 2)
    .map((word) => Array.from(word)[0] ?? '')
    .join('')
    .toUpperCase()
}
