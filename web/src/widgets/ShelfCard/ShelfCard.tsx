/**
 * One card on the dashboard's "Last downloaded" shelf (design 122–131).
 *
 * 126px of artwork, a title, a truncated subtitle and a quiet tag line — the
 * whole thing one control, because the design gives the card a single
 * `onClick` (123) and a card whose picture and title were separately clickable
 * would be two tab stops for one destination.
 *
 * ── it takes strings, not a release ───────────────────────────────────────
 *
 * Every other widget here is handed the wire object it draws. This one is
 * handed six props, and deliberately: the shelf's source is `useQueue({ state:
 * 'done' })`, so what it renders is a `QueueItemOut` — an album *title* and an
 * artist *name* carried on a queue row, with no `AlbumOut` in sight (there is
 * no global "recently downloaded albums" endpoint, and the caller must not
 * fabricate one). Typing this against `AlbumOut` would force the screen to
 * build a fake release out of a queue item, which is the exact shape of lie
 * this layer exists to avoid. It stays a card of strings, and the screen —
 * which knows what it is looking at — decides what goes in them.
 *
 * `seed` is separate from `title` for the same reason it is separate inside
 * `Artwork`: the colour must be stable for the *release*, so the caller seeds
 * it with an id where it has one, while the initials are always read off the
 * title by the `chars` rule (design 1101 — `Kind of Blue` → `KI`, never `KOB`,
 * which reads as an acronym).
 */

import { Artwork, cx, initialsOf } from '@/design'

import styles from '@/widgets/ShelfCard/ShelfCard.module.css'

export interface ShelfCardProps {
  /** The release title. Also the source of the placeholder initials. */
  title: string
  /** The credit line under it, truncated to one line (design 128). */
  subtitle: string
  /** What the gradient hashes on — an id where there is one, so it is stable. */
  seed: string
  /** A real cover, drawn over the gradient rather than instead of it. */
  imageUrl: string | null
  /** The quiet third line: the design's `2 h ago` (129). */
  tag: string
  onClick: () => void
}

export function ShelfCard({
  title,
  subtitle,
  seed,
  imageUrl,
  tag,
  onClick,
}: ShelfCardProps) {
  return (
    <button
      type="button"
      className={styles.card}
      // Wrapped rather than passed by reference — React would otherwise hand
      // the `MouseEvent` to a handler declared `() => void`. See `Toggle`.
      onClick={() => {
        onClick()
      }}
    >
      <Artwork
        seed={seed}
        initials={initialsOf(title, 'chars')}
        shape="square"
        size={126}
        src={imageUrl}
      />
      <span className={styles.title}>{title}</span>
      <span className={cx(styles.subtitle, 'truncate')}>{subtitle}</span>
      <span className={styles.tag}>{tag}</span>
    </button>
  )
}
