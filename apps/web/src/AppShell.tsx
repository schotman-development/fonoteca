import { Badge, Text, ThemeSwitch } from '@fonoteca/ui'
import { Link, Outlet } from '@tanstack/react-router'

import styles from './AppShell.module.css'

/**
 * The root layout: header, the current route, and the reserved transport row.
 *
 * The outlet sits inside `<main>` and the transport row sits outside it, which
 * is the whole reason that row was declared before there was a player to put in
 * it — a persistent player must survive navigation, and moving it out of the
 * route tree afterwards means re-nesting the layout.
 */
export function AppShell() {
  return (
    <div className={styles.shell}>
      <header className={styles.header}>
        <div className={styles.brand}>
          <Text size="lg" weight="semibold">
            Fonoteca
          </Text>
          <Badge tone="neutral" size="sm">
            scaffold
          </Badge>
        </div>

        <nav className={styles.nav} aria-label="Main">
          {/*
            `activeProps` rather than a className computed from the current
            location: the router already knows, and asking it twice is how the
            underline ends up on the wrong link after a redirect.

            `activeOptions.exact` on "/" only — without it the dashboard link
            stays active on every route, since every path starts with "/".
          */}
          <Link
            to="/"
            className={styles.navLink}
            activeProps={{ 'data-current': 'page', 'aria-current': 'page' }}
            activeOptions={{ exact: true }}
          >
            Foundation
          </Link>
          <Link
            to="/library"
            className={styles.navLink}
            activeProps={{ 'data-current': 'page', 'aria-current': 'page' }}
          >
            Library
          </Link>
        </nav>

        <ThemeSwitch size="sm" />
      </header>

      <main className={styles.main}>
        <Outlet />
      </main>

      {/*
        Reserved for the playback transport bar. Empty today and collapsed to
        zero height; it exists so a future player does not require re-nesting
        the layout. See the plan's "Designed for, not built" section.
      */}
      <div className={styles.transport} data-slot="transport" />
    </div>
  )
}
