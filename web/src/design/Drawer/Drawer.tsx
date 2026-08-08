/**
 * The side sheet — design lines 350–357, 376–384 and 425–437.
 *
 * ── it is a flex sibling, not an overlay ─────────────────────────────────────
 *
 * The design's own markup puts the `<aside>` NEXT TO the screen's `<section>`
 * inside one flex row, at `flex: none; width: min(408px, 45%)`. So the content
 * column shrinks beside an open drawer instead of being covered by it, and the
 * artist grid, the review list and the album wall behind one stay fully
 * readable and clickable. A caller places it by rendering it as the last child
 * of that row; this component never positions itself.
 *
 * ── which makes it deliberately NON-MODAL ────────────────────────────────────
 *
 * `aria-modal="false"`, and **no focus trap**. Trapping focus in a panel that
 * does not cover anything is a lie to a screen-reader user: it says "the rest
 * of the page is inert" about a list that is still perfectly usable — and on
 * the Identify screen it is worse than a lie, because the whole point of the
 * picker living outside the polled list's tree (CLAUDE.md, "the picker must
 * survive the list's refetch") is that the list keeps working underneath.
 *
 * What is kept from the modal playbook is the half that costs a keyboard user
 * nothing and is pure gain:
 *
 *   - **Escape closes.** Bound on the panel, so it fires for anything focused
 *     inside it and does NOT swallow Escape from the list beside it — a
 *     non-modal panel must not eat a key press aimed at something else.
 *   - **Focus moves in on open, and back to the opener on close.** Without the
 *     return trip, closing a drawer drops focus onto `<body>` and the next Tab
 *     starts again from the top of the document, which is how a keyboard user
 *     loses their place in a 400-artist grid. The panel itself takes focus
 *     (`tabIndex={-1}`) rather than the first field: the header names what just
 *     opened, and focusing a text input would skip past it.
 *
 * The opener is captured at the moment `open` flips, not at mount, so a drawer
 * that stays mounted across two different openers returns focus to the right
 * one. It is only restored if that element is still in the document — the row
 * that opened a release drawer may have been removed by the refetch behind it.
 *
 * ── the × is `Button variant="close"`, not a `<button>` of its own ──────────
 *
 * The design draws the same borderless × at 356, 383 and 436, and `Button`
 * already holds that shape — including the `label` mechanism that turns a
 * glyph into an accessible name instead of "multiplication sign". This file
 * used to re-declare it, which meant one control in the design and two in the
 * layer, differing the moment either was retuned.
 */

import { useEffect, useId, useRef, type ReactNode } from 'react'

import { Button } from '@/design/Button/Button'
import { cx } from '@/design/cx'
import label from '@/design/shared/label.module.css'
import type { StyleableProps } from '@/design/types'

import styles from '@/design/Drawer/Drawer.module.css'

export interface DrawerProps extends StyleableProps {
  /** Closed renders nothing at all — the row beside it reflows to full width. */
  open: boolean
  /** Escape, the ×, and whatever the footer wires up all call this. */
  onClose: () => void
  /** The eyebrow over the title: "Edit tags", "Artist settings", "Release". */
  kicker?: ReactNode
  /** The accessible name of the dialog. Rendered as the panel's own heading. */
  title: ReactNode
  children?: ReactNode
  /** The bordered action row at the foot of the body (design 367, 415). */
  footer?: ReactNode
  /** Accessible name for the × control. Icon-only, so it always has one. */
  closeLabel?: string
}

export function Drawer({
  open,
  onClose,
  kicker,
  title,
  children,
  footer,
  closeLabel = 'Close',
  className,
}: DrawerProps) {
  const titleId = useId()
  const panelRef = useRef<HTMLElement>(null)
  const openerRef = useRef<Element | null>(null)

  useEffect(() => {
    if (!open) return

    openerRef.current = document.activeElement
    panelRef.current?.focus()

    return () => {
      const opener = openerRef.current
      openerRef.current = null
      // Only if it is still there: the list behind the drawer may have
      // refetched the row that opened it out of existence, and focusing a
      // detached node silently sends focus to <body> anyway.
      if (opener instanceof HTMLElement && opener.isConnected) opener.focus()
    }
  }, [open])

  if (!open) return null

  return (
    <aside
      ref={panelRef}
      className={cx(styles.drawer, className)}
      role="dialog"
      aria-modal="false"
      aria-labelledby={titleId}
      tabIndex={-1}
      onKeyDown={(event) => {
        if (event.key !== 'Escape') return
        event.stopPropagation()
        onClose()
      }}
    >
      <div className={styles.header}>
        <div className={styles.heading}>
          {kicker ? <div className={label.label}>{kicker}</div> : null}
          <h2 id={titleId} className={styles.title}>
            {title}
          </h2>
        </div>
        <Button
          variant="close"
          label={closeLabel}
          onClick={onClose}
          className={styles.close}
        >
          ×
        </Button>
      </div>
      <div className={styles.body}>
        {children}
        {footer ? <div className={styles.footer}>{footer}</div> : null}
      </div>
    </aside>
  )
}
