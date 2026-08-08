/**
 * The band head: a 16px/600 `h2`, a 12.5px `--c-ink-3` subtitle under it, an
 * optional right-hand action, and — usually — a hairline under the lot.
 *
 * Design 95–101 (Library health, ruled, with an "Activity" link),
 * 114–120 (Last downloaded, with a "Release radar" link and **no rule**) and
 * 140–143 (Missing albums, ruled, no action).
 *
 * `rule` defaults to true and block 2 is why it is a prop rather than an
 * assumption. That block's head is followed by a shelf of artwork, and a
 * hairline drawn immediately above a row of cards reads as the top edge of a
 * table the cards are not in — so the design drops it there and only there.
 * Copying the ruled version everywhere would be a difference nobody notices
 * while looking at the code and everybody notices on the screen.
 *
 * The heading's type is not restated here. `base.css` already paints `h2` at
 * `--fs-md`/`--fw-semibold`/`--ls-snug`/`--c-ink`, which is the design's 16px,
 * 600 and −0.01em exactly, and a second copy in this module is a second thing
 * to keep in step — the failure the shared recipes under `design/shared/` exist
 * to prevent, one layer up. All this module does to the heading is remove the
 * margin the reset leaves.
 *
 * There is one `h2` and it is always rendered, because a section head with no
 * heading is a rule with some text over it. The page's single `h1` belongs to
 * the screen, never to this.
 */

import type { ReactNode } from 'react'

import { cx } from '@/design/cx'
import type { StyleableProps } from '@/design/types'

import styles from '@/design/SectionHead/SectionHead.module.css'

export interface SectionHeadProps extends StyleableProps {
  /** The band's name. Rendered as the section's `h2`. */
  title: ReactNode
  /** The 12.5px note under it (design 98, 117, 142). Absent renders no element. */
  sub?: ReactNode
  /** The right-hand slot — in the design, a `quiet` Button to a related screen. */
  action?: ReactNode
  /**
   * The hairline under the head. True everywhere except design 114–120, where
   * the head sits directly over a shelf of artwork.
   *
   * `ruled`, matching `KeyValueList` — this layer had `rule` here and `ruled`
   * there for one idea, and two spellings of one prop is a thing a screen has
   * to look up every time.
   */
  ruled?: boolean
  /** Put on the `h2`, so the section itself can be `aria-labelledby` it. */
  id?: string
}

export function SectionHead({
  title,
  sub,
  action,
  ruled = true,
  id,
  className,
}: SectionHeadProps) {
  return (
    <div className={cx(styles.head, ruled ? styles.ruled : undefined, className)}>
      <div className={styles.text}>
        <h2 id={id} className={styles.title}>
          {title}
        </h2>
        {sub == null ? null : <p className={styles.sub}>{sub}</p>}
      </div>
      {action == null ? null : <div className={styles.action}>{action}</div>}
    </div>
  )
}
