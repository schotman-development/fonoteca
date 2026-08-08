/**
 * The horizontal scroller (design 121–136, handlers at 1244–1245).
 *
 * A snapping, overflowing row of cards with a `‹ ›` pair under its right edge
 * that steps it by 292px — the design's own number, which is one 126px card
 * plus its gap plus enough of the next to show what direction you went.
 *
 * ── the two buttons are rendered here, not injected ───────────────────────
 *
 * The brief allowed either a `controls` slot or an own-module pair. This
 * renders them, for one reason: whether a button is *disabled* is a fact about
 * the scroller's `scrollLeft`, and only this component holds the ref that
 * knows it. A slot would have to hand the caller both the ref and the reach
 * state to reproduce a disabled arrow, at which point the slot is not a slot
 * but a second copy of this file.
 *
 * They are `Button variant="iconRound"`, NOT a pair re-declared in the module
 * beside this file. That was the original call and it was wrong: the module's
 * `.nav` was `Button.module.css`'s `.iconRound` copied declaration for
 * declaration — 30×30, `--r-full`, a `--c-line` edge over `--c-paper`, the
 * hover to `--c-line-mute`, the disabled fade — so the design had one circular
 * icon button and this layer had two, free to drift apart at the next retune.
 * `label` is what makes the glyph a name rather than "left single quotation
 * mark", and it is `Button`'s own mechanism, so that is one implementation too.
 *
 * ── keyboard and pointer are not the same user ────────────────────────────
 *
 * A pointer user drags or wheels; a keyboard user needs the scroller itself to
 * be focusable before the arrow keys reach it, and a screen-reader user needs
 * to be told the region is scrollable at all. So the scroller takes
 * `tabIndex=0` *only while it actually overflows* — a focus stop that scrolls
 * nothing is a stop that has to be tabbed past for no reason — and carries the
 * caller's `label` as a named group.
 *
 * ── reduced motion is checked in JS, not only in CSS ──────────────────────
 *
 * tokens.css forces `scroll-behavior: auto` under `prefers-reduced-motion`,
 * which governs CSS-driven scrolling. It does NOT govern `behavior: 'smooth'`
 * passed to `scrollBy()`, which is an argument rather than a style — so the
 * preference is read here as well. A shelf that animates for somebody who
 * asked it not to is the one accessibility failure this control can commit.
 */

import type { ReactNode } from 'react'
import { useCallback, useEffect, useLayoutEffect, useRef, useState } from 'react'

import { Button } from '@/design/Button/Button'
import { cx } from '@/design/cx'
import type { StyleableProps } from '@/design/types'
import styles from '@/design/Shelf/Shelf.module.css'

/** The design's step, DCLogic 1244–1245: `el.scrollLeft ±= 292`. */
export const SHELF_STEP_PX = 292

/**
 * How far from an edge still counts as being at it. Sub-pixel layout means
 * `scrollLeft` lands on 0.5 and 291.5 rather than on 0 and 292, and an arrow
 * that stays enabled at the very end is an arrow that does nothing.
 */
const EDGE_SLOP_PX = 1

interface Reach {
  scrollable: boolean
  atStart: boolean
  atEnd: boolean
}

/** Nothing rendered yet is nothing to scroll — and both arrows are off. */
const AT_REST: Reach = { scrollable: false, atStart: true, atEnd: true }

function prefersReducedMotion(): boolean {
  return (
    typeof window !== 'undefined' &&
    typeof window.matchMedia === 'function' &&
    window.matchMedia('(prefers-reduced-motion: reduce)').matches
  )
}

export interface ShelfProps extends StyleableProps {
  /** The cards. Each becomes a snap point. */
  children: ReactNode
  /**
   * What this row of cards is, e.g. `Last downloaded`. Required: a focusable
   * scrolling region with no name is a tab stop nobody can identify.
   */
  label: string
}

export function Shelf({ children, label, className }: ShelfProps) {
  const ref = useRef<HTMLDivElement>(null)
  const [reach, setReach] = useState<Reach>(AT_REST)

  const measure = useCallback(() => {
    const el = ref.current
    if (el === null) return
    const max = el.scrollWidth - el.clientWidth
    const next: Reach = {
      scrollable: max > EDGE_SLOP_PX,
      atStart: el.scrollLeft <= EDGE_SLOP_PX,
      atEnd: el.scrollLeft >= max - EDGE_SLOP_PX,
    }
    // Bail out on an unchanged answer. That is what makes it safe to measure
    // after every render — which is what catches the case no listener does:
    // the children arriving from a query, which changes the scroll width
    // without firing a scroll and without resizing the scroller.
    setReach((prev) =>
      prev.scrollable === next.scrollable &&
      prev.atStart === next.atStart &&
      prev.atEnd === next.atEnd
        ? prev
        : next,
    )
  }, [])

  useLayoutEffect(measure)

  useEffect(() => {
    const el = ref.current
    if (el === null) return

    el.addEventListener('scroll', measure, { passive: true })
    // ResizeObserver is guarded rather than assumed: jsdom does not implement
    // it, and a primitive that throws in a test suite is a primitive nobody
    // can mount.
    const observer =
      typeof ResizeObserver === 'function' ? new ResizeObserver(measure) : null
    observer?.observe(el)

    return () => {
      el.removeEventListener('scroll', measure)
      observer?.disconnect()
    }
  }, [measure])

  const step = useCallback(
    (direction: 1 | -1) => {
      const el = ref.current
      if (el === null) return
      const left = direction * SHELF_STEP_PX

      if (typeof el.scrollBy === 'function') {
        el.scrollBy({ left, behavior: prefersReducedMotion() ? 'auto' : 'smooth' })
      } else {
        // The design's own implementation (1244–1245), and the fallback for an
        // environment with no scroll methods.
        el.scrollLeft += left
      }
      measure()
    },
    [measure],
  )

  return (
    <div className={cx(styles.shelf, className)}>
      <div
        ref={ref}
        className={styles.scroller}
        // Only a scroller that actually overflows is a focus stop.
        {...(reach.scrollable ? { tabIndex: 0 } : {})}
        role="group"
        aria-label={label}
        data-scrollable={reach.scrollable ? '' : undefined}
      >
        {children}
      </div>
      <div className={styles.controls}>
        <Button
          variant="iconRound"
          label="Scroll left"
          onClick={() => {
            step(-1)
          }}
          disabled={!reach.scrollable || reach.atStart}
        >
          ‹
        </Button>
        <Button
          variant="iconRound"
          label="Scroll right"
          onClick={() => {
            step(1)
          }}
          disabled={!reach.scrollable || reach.atEnd}
        >
          ›
        </Button>
      </div>
    </div>
  )
}
