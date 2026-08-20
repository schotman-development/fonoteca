import { AudioTransport, Badge, CommandBar, Text, ThemeSwitch } from '@fonoteca/ui'
import { Link, Outlet } from '@tanstack/react-router'

import styles from './AppShell.module.css'
import { useAppCommands } from './commands.ts'
import { NAV_ITEMS } from './navigation.ts'

/**
 * The root layout: a navigation rail, a top bar, the current route, and the
 * reserved transport row.
 *
 * The outlet sits inside `<main>` and the transport row sits outside it, which
 * is the whole reason that row was declared before there was a player to put in
 * it — a persistent player must survive navigation, and moving it out of the
 * route tree afterwards means re-nesting the layout.
 *
 * Navigation is a full-height rail rather than a row of links in the top bar.
 * The top bar it left behind holds the two controls that belong to the
 * application rather than to any screen: the command bar and the theme switch.
 * It starts where the content column starts, so it is a bar over the *view* and
 * the rail reads as beside both.
 */
export function AppShell() {
  const commands = useAppCommands()

  return (
    <div className={styles.shell}>
      <header className={styles.sidebar}>
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
            marker ends up on the wrong link after a redirect.

            The links come from NAV_ITEMS, which the command bar reads too — see
            the note there on why the list is not written out twice.
          */}
          {NAV_ITEMS.map((item) => (
            <Link
              key={item.to}
              to={item.to}
              className={styles.navLink}
              activeProps={{ 'data-current': 'page', 'aria-current': 'page' }}
              activeOptions={{ exact: item.exact }}
            >
              {item.label}
            </Link>
          ))}
        </nav>
      </header>

      {/*
        Three columns rather than `justify-content: space-between`, so the
        command bar is centred on the content column and not on whatever is
        left over once the theme switch has taken its width.
      */}
      <div className={styles.topbar}>
        <div className={styles.topbarStart} />
        <div className={styles.topbarCentre}>
          <CommandBar commands={commands} />
        </div>
        <div className={styles.topbarEnd}>
          <ThemeSwitch size="sm" />
        </div>
      </div>

      {/*
        Two elements, two jobs: `main` is the scroll container and owns the
        viewport-wide padding, `content` is the centred column. Capping the
        width on the scroller itself would centre the scrollbar with it.
      */}
      <main className={styles.main}>
        <div className={styles.content}>
          <Outlet />
        </div>
      </main>

      {/*
        The playback transport, in the row that was reserved for it.

        `AudioTransport` renders nothing at all when no track is loaded, so the
        row stays collapsed — `.transport:not(:empty)` is what gives it a height
        — and the 72px only appears once there is something audible to control.
        That is why the bar has a close button: without one it would be
        permanent from the first play onwards.
      */}
      <div className={styles.transport} data-slot="transport">
        <AudioTransport />
      </div>
    </div>
  )
}
