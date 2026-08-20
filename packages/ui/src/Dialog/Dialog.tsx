import { type ReactNode, useEffect, useId, useLayoutEffect, useRef } from 'react'

import styles from './Dialog.module.css'

export type DialogProps = {
  /** State drives the element; the element drives it back through `onClose`. */
  readonly open: boolean
  /**
   * Called for every dismissal, including the ones we do not initiate.
   *
   * `Escape` and the platform's own close both fire the element's `close` event
   * without asking us first, so a caller that only handled its own close button
   * would render an open dialog over a closed one.
   */
  readonly onClose: () => void
  /** The heading, as a real `<h2>`, and the dialog's accessible name. */
  readonly title: ReactNode
  /** Under the heading, inside the same non-scrolling header. */
  readonly description?: ReactNode
  /** 560px for a confirmation, 880px for a screen that used to be a page. */
  readonly size?: 'md' | 'lg'
  /** Pinned below the scroll area — a commit bar, a row of actions. */
  readonly footer?: ReactNode
  readonly closeLabel?: string
  readonly children: ReactNode
  readonly className?: string
}

/**
 * A modal dialog: a native `<dialog>` opened with `showModal()`.
 *
 * Five behaviours ADR 0003 budgeted for are the platform's here and not ours —
 * the focus trap, focus restoration to whatever opened it, `Escape`, the inert
 * background, and top-layer stacking. The last of those is why **there is no
 * z-index in this file**: the top layer sits above every stacking context in the
 * document, including the sticky navigation rail, without joining the scale.
 *
 * `CommandBar` opens its palette the same way and predates this component. It is
 * deliberately not rewired onto it: the palette is a combobox that happens to
 * live in a dialog, its behaviour is pinned by ADR 0010 and its own stories, and
 * folding it in would be a rewrite of a working screen rather than a reuse. If a
 * third dialog appears, that is the moment to reconsider — not before.
 *
 * The panel scrolls, not the page behind it: `showModal()` does not lock the
 * document's scroll, so a body long enough to overflow has to own its own scroll
 * container or the wheel falls through to the page underneath.
 */
export function Dialog({
  open,
  onClose,
  title,
  description,
  size = 'md',
  footer,
  closeLabel = 'Close',
  children,
  className,
}: DialogProps): ReactNode {
  const dialogRef = useRef<HTMLDialogElement>(null)
  const id = useId()
  const titleId = `${id}-title`
  const descriptionId = `${id}-description`

  /**
   * Whether the press that started this click landed on the backdrop.
   *
   * A click is dispatched at the common ancestor of press and release, so a drag
   * that begins inside the panel and ends outside it reports the dialog as its
   * target — indistinguishable, from `click` alone, from a click on the
   * backdrop. Selecting text in a dialog and releasing past its edge is an
   * ordinary thing to do, and dismissing on it throws away whatever was typed.
   */
  const pressedBackdrop = useRef(false)

  // `showModal()` is imperative and the DOM owns "is it open" — the browser
  // closes the dialog on Escape without telling us first. So state drives the
  // element here, and the element's `close` event drives the state back.
  useEffect(() => {
    const dialog = dialogRef.current
    if (!dialog) return
    if (open && !dialog.open) {
      dialog.showModal()
    } else if (!open && dialog.open) {
      dialog.close()
    }
  }, [open])

  /**
   * Closed on unmount, if it is still open.
   *
   * The one dismissal that does not pass through `dismiss` — a caller that
   * removes the dialog rather than setting `open` to false, which is what
   * happens when a command elsewhere navigates the page out from under it.
   * Without it focus is stranded on `<body>`, exactly the failure the comment
   * below is about.
   *
   * **A layout effect, and that is the whole of why it works.** Passive effect
   * cleanups run after the commit, by which time React has already detached the
   * element — and `close()` on a node that is no longer in the document restores
   * focus to nothing. Layout cleanups run during the mutation phase, while it is
   * still there. Written with `useEffect` this reads identically and does
   * nothing at all.
   *
   * Under StrictMode the unmount is a simulation and the dialog is shown again
   * immediately afterwards, which is what the staleness guard on `onClose` is
   * for — see it for why a flag set here could not do that job.
   */
  useLayoutEffect(() => {
    const dialog = dialogRef.current
    return () => {
      dialog?.close()
    }
  }, [])

  /**
   * Every dismissal goes through the element, and `onClose` is raised by the
   * element's own `close` event rather than called here.
   *
   * That is what makes the three dismissals one path. It matters most for the
   * caller that unmounts on close: focus restoration happens when `close()`
   * runs, so a backdrop click that only told React would remove the dialog from
   * the document with the focus still inside it — and focus would land on
   * `<body>`, which is the one place a keyboard cannot navigate onward from.
   */
  const dismiss = (): void => {
    dialogRef.current?.close()
  }

  return (
    // biome-ignore lint/a11y/useKeyWithClickEvents: this handler is the backdrop's dismiss, and the keyboard's dismiss is Escape — which showModal() already handles, without a listener of ours to pair with
    <dialog
      ref={dialogRef}
      className={className ? `${styles.dialog} ${className}` : styles.dialog}
      data-size={size}
      aria-labelledby={titleId}
      {...(description != null ? { 'aria-describedby': descriptionId } : {})}
      /*
       * The element's own state decides whether this close still means
       * anything.
       *
       * `close()` **queues** its event rather than dispatching it, so one can
       * land after the element has been shown again. StrictMode's simulated
       * remount does exactly that — the layout cleanup above closes it, the
       * effect re-opens it, and the queued event arrives afterwards. Reported,
       * it makes a caller that unmounts on close tear down a dialog that is
       * open on screen, so the dialog silently never appears in development.
       *
       * A boolean set around the close cannot express this: whatever sets it
       * back to true runs before the queued event, every time. The dialog being
       * open again is the fact, and the element is the one holding it.
       */
      onClose={() => {
        if (dialogRef.current?.open) return
        onClose()
      }}
      onMouseDown={(event) => {
        pressedBackdrop.current = event.target === dialogRef.current
      }}
      onClick={(event) => {
        // A click on the backdrop reports the dialog itself as the target;
        // anything inside the panel reports the panel or its descendants. Both
        // ends of the gesture have to have been out there — see the ref above.
        if (event.target === dialogRef.current && pressedBackdrop.current) dismiss()
        pressedBackdrop.current = false
      }}
    >
      <div className={styles.panel}>
        <div className={styles.header}>
          <div className={styles.heading}>
            <h2 className={styles.title} id={titleId}>
              {title}
            </h2>
            {description != null ? (
              <p className={styles.description} id={descriptionId}>
                {description}
              </p>
            ) : null}
          </div>

          <button type="button" className={styles.close} aria-label={closeLabel} onClick={dismiss}>
            <CloseGlyph />
          </button>
        </div>

        {/*
          `tabIndex={0}` because this is the scroll container, and the two
          linters disagree about it. A region that scrolls and holds nothing
          focusable cannot be scrolled by a keyboard at all — axe fails it as
          `scrollable-region-focusable`, at `test: 'error'`, and it is right: the
          content below the fold would be unreachable without a pointer. Biome's
          rule is the general one, about div soup that has been made clickable.
          The cost is one extra tab stop inside a dialog that already traps
          focus, and it lands after the close button rather than before it.
        */}
        {/* biome-ignore lint/a11y/noNoninteractiveTabindex: the axe run enforces the opposite — a scrollable region with no focusable content must be focusable, or its overflow is unreachable by keyboard */}
        <div className={styles.body} tabIndex={0}>
          {children}
        </div>

        {footer != null ? <div className={styles.footer}>{footer}</div> : null}
      </div>
    </dialog>
  )
}

/** 16px cross, inheriting `currentColor` like the other glyphs in the package. */
function CloseGlyph(): ReactNode {
  return (
    <svg
      width="16"
      height="16"
      viewBox="0 0 16 16"
      fill="none"
      stroke="currentColor"
      strokeWidth="1.5"
      strokeLinecap="round"
      aria-hidden="true"
      focusable="false"
    >
      <path d="M4 4 12 12M12 4 4 12" />
    </svg>
  )
}
