/**
 * A handle on the command bar, for the one screen that needs to open it.
 *
 * The palette's state belongs to `AppShell` — it is a frame concern, and the
 * ⌘K listener is global — but the Release radar's `+ Follow artist` button is
 * the design's own entry point into artist search (535). Rather than a second
 * search box on that screen, or a synthesised keyboard event, the shell hands
 * down one function.
 *
 * Outside a provider it is a **no-op**, not a throw: a screen mounted in a test
 * without the frame is a legitimate thing to do, and a button that quietly does
 * nothing there is better than a suite that cannot render the page.
 */

import { createContext, useContext } from 'react'

export interface CommandBarHandle {
  open: () => void
}

const NOOP: CommandBarHandle = { open: () => {} }

export const CommandBarContext = createContext<CommandBarHandle>(NOOP)

export function useCommandBar(): CommandBarHandle {
  return useContext(CommandBarContext)
}
