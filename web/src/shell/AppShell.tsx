/**
 * The frame every screen renders inside — design lines 26, 56, 76–78 and 828.
 *
 * Top bar, an optional banner stack, then a row of sidebar + `<main>`, where
 * main is a column of the route outlet and the status footer. The toast host
 * and the command palette hang off the end because both are floating surfaces
 * that must not be inside the one element that scrolls.
 *
 * ── the scroll model, which is the only thing here that is easy to break ───
 *
 * **The screen's own `<section>` is the only scroller in the application.**
 * Every band in this component is `flex: none`; every container between the
 * viewport and that section carries `min-height: 0` (or `min-width: 0`) and
 * `overflow: hidden`. Nothing is `position: sticky` and nothing is lifted over
 * content. Add a `overflow-y: auto` to `.main` — the obvious-looking fix the
 * first time a screen's content is taller than the window — and the top bar
 * stops being a fixed frame, the footer's counts scroll away from the library
 * they describe, and a drawer's close button ends up under the header.
 *
 * ── the outlet row is a ROW ───────────────────────────────────────────────
 *
 * Design line 77 draws `display: flex` there and it matters: a screen's detail
 * `Drawer` is a **flex sibling** of its section rather than an overlay, so the
 * content column shrinks beside an open drawer. A column here would stack them.
 *
 * ── the command palette is always mounted ─────────────────────────────────
 *
 * It renders `null` while closed and owns the one `window` keydown listener.
 * A panel that mounts on ⌘K cannot be the thing that hears ⌘K, so the state
 * lives here — in the frame, which outlives every screen — and the panel is
 * always in the tree.
 */

import { useMemo, useState, type ReactNode } from 'react'

import { ToastHost } from '@/design'
import { Banners } from '@/shell/Banners'
import { CommandBarContext } from '@/shell/commandBar'
import { CommandPalette } from '@/shell/CommandPalette'
import { Sidebar } from '@/shell/Sidebar'
import { StatusFooter } from '@/shell/StatusFooter'
import { TopBar } from '@/shell/TopBar'

import styles from '@/shell/AppShell.module.css'

/** The skip link's target, and `<main>`'s id. One constant, two readers. */
export const MAIN_CONTENT_ID = 'main-content'

export interface AppShellProps {
  /** The route outlet. It supplies its own `<section>`, which is the scroller. */
  children: ReactNode
}

export function AppShell({ children }: AppShellProps) {
  const [commandBarOpen, setCommandBarOpen] = useState(false)
  // Memoised so the context value is not a new object on every frame of the
  // shell's own polling — a screen subscribing to it would re-render with it.
  const commandBar = useMemo(() => ({ open: () => setCommandBarOpen(true) }), [])

  return (
    <div className={styles.shell}>
      {/* Six nav links stand between the top of the document and the content on
          every screen. This is how a keyboard user gets past them. */}
      <a href={`#${MAIN_CONTENT_ID}`} className={styles.skipLink} data-plain="">
        Skip to content
      </a>

      <TopBar onOpenCommandBar={() => setCommandBarOpen(true)} />
      <Banners />

      <div className={styles.body}>
        <Sidebar />

        <main id={MAIN_CONTENT_ID} className={styles.main} tabIndex={-1}>
          <div className={styles.outlet}>
            <CommandBarContext.Provider value={commandBar}>
              {children}
            </CommandBarContext.Provider>
          </div>
          <StatusFooter />
        </main>
      </div>

      {/* Mounted here rather than left to the provider's fallback, so there is
          exactly one live region and it is a sibling of the frame. */}
      <ToastHost />

      <CommandPalette open={commandBarOpen} onOpenChange={setCommandBarOpen} />
    </div>
  )
}
