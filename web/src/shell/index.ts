/**
 * The shell's public surface.
 *
 * Everything the frame is made of, plus one re-export that is not the shell's
 * at all: **`ToastProvider` and `useToast` come from `@/design` and are
 * re-exported here** because `web/src/test/providers.tsx` does
 * `import { ToastProvider } from '@/shell'` and that file is not this layer's
 * to edit. The alternative — moving the provider into the shell — would put the
 * toast's state in one directory and its pill, host and context in another, and
 * `@/design/Toast` already documents the name as load-bearing.
 *
 * `nav.ts` is exported because it is the IA as data: the sidebar renders it, the
 * command palette's "Go to" group is built from it, and a test can read it
 * without mounting anything.
 */

export { AppShell, MAIN_CONTENT_ID } from '@/shell/AppShell'
export type { AppShellProps } from '@/shell/AppShell'

export { Banners } from '@/shell/Banners'
export { useCommandBar } from '@/shell/commandBar'
export type { CommandBarHandle } from '@/shell/commandBar'
export { CommandPalette } from '@/shell/CommandPalette'
export type { CommandPaletteProps } from '@/shell/CommandPalette'
export { Sidebar } from '@/shell/Sidebar'
export { StatusFooter } from '@/shell/StatusFooter'
export { statusSentence } from '@/shell/statusSentence'
export type { StatusSentenceInput } from '@/shell/statusSentence'
export { TopBar } from '@/shell/TopBar'
export type { TopBarProps } from '@/shell/TopBar'

export { NAV_ITEMS, navBadgeCount } from '@/shell/nav'
export type { NavBadgeKey, NavItem } from '@/shell/nav'

/* The one re-export that is not this layer's own — see the note above. */
export { ToastProvider, useToast } from '@/design'
export type { ShowToast, ToastProviderProps } from '@/design'
