/**
 * The eyebrow — the design's most repeated element, and the smallest one that
 * carries a rule.
 *
 * Eleven pixels, uppercase, `--ls-label`, `--c-ink-mark`. It appears at design
 * lines 166, 183, 275, 299, 322, 353, 380, 408, 448, 468, 478, 490, 502, 540,
 * 567, 630, 653, 732 and 743 — nineteen sites, one declaration, which is why
 * the declaration itself lives in `shared/label.module.css` and is `composes:`d
 * here rather than written out again.
 *
 * What this component adds on top of the recipe is the two things the design
 * does *around* the label, and only those two:
 *
 *   `count`   the `Albums · 4` form (275, 299, 322). It is a suffix on the
 *             label's own text, not a separate slot, because it is read as one
 *             phrase. `null`/`undefined` renders nothing at all — the
 *             three-valued rule, in the one place a count is most tempting to
 *             default to zero. A real `0` still renders `· 0`: nought albums
 *             is a measurement, "not counted" is not.
 *
 *   `action`  a right-hand slot. The design fills it two ways and they are
 *             genuinely different layouts, not one with a tolerance:
 *             165–169 and 319–323 push a mono count to the far edge with a
 *             `flex: 1` spacer between (`actionAlign="end"`, the default),
 *             while 181–186 sets a spinner and 298–301 a chip immediately
 *             beside the words (`actionAlign="inline"`). Forcing either into
 *             the other puts a spinner on the opposite side of the panel from
 *             the word it qualifies.
 *
 * `as` exists because roughly half these sites label a real section (the
 * drawer's "Identification & tags", the radar's "Followed artists") and half
 * are a caption over a figure. A heading that is not a section, and a section
 * with no heading, are both wrong in the accessibility tree, and only the
 * caller knows which it has. The default is `div`, so the wrong answer is
 * never the silent one.
 *
 * Everything about spacing below the eyebrow belongs to the caller: the design
 * gives it `margin-bottom` of 6, 9, 12 and 14px depending on what follows, and
 * a primitive that picked one would be overridden at three sites out of four.
 */

import type { ReactNode } from 'react'

import label from '@/design/shared/label.module.css'
import { cx } from '@/design/cx'
import type { StyleableProps } from '@/design/types'

import styles from '@/design/Eyebrow/Eyebrow.module.css'

/** The elements an eyebrow may be. Anything else is a section head, not this. */
export type EyebrowElement = 'div' | 'span' | 'h2' | 'h3' | 'h4'

/** Where the right-hand slot sits. See the note above — the design has both. */
export type EyebrowActionAlign = 'end' | 'inline'

export interface EyebrowProps extends StyleableProps {
  /** The label itself. Uppercased by CSS, so pass it in ordinary case. */
  children: ReactNode
  /**
   * The `· 4` suffix (design 275, 299, 322). `null`/`undefined` renders
   * nothing — never a zero standing in for "not counted".
   */
  count?: number | null
  /** A right-hand slot: a mono count (168), a spinner (185) or a chip (300). */
  action?: ReactNode
  /** `end` pushes the action to the far edge (default); `inline` sets it beside the words. */
  actionAlign?: EyebrowActionAlign
  /** `h2`/`h3`/`h4` where this labels a real section; `div` (the default) where it captions. */
  as?: EyebrowElement
  /** Put on the label element, so a panel can point `aria-labelledby` at it. */
  id?: string
}

export function Eyebrow({
  children,
  count,
  action,
  actionAlign = 'end',
  as: Tag = 'div',
  id,
  className,
}: EyebrowProps) {
  const text = (
    <Tag id={id} className={cx(label.label, styles.text, action == null ? className : undefined)}>
      {children}
      {count == null ? null : ` · ${count}`}
    </Tag>
  )

  if (action == null) return text

  return (
    <div
      className={cx(
        styles.row,
        actionAlign === 'inline' ? styles.rowInline : undefined,
        className,
      )}
    >
      {text}
      <span
        className={cx(styles.action, actionAlign === 'end' ? styles.actionEnd : undefined)}
      >
        {action}
      </span>
    </div>
  )
}
