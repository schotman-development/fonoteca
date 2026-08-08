/**
 * The left navigation — design lines 58–74.
 *
 * It renders `NAV_ITEMS`; it does not contain them. "Which screens exist and in
 * what order" lives in `nav.ts` so the sidebar, the router and the tests cannot
 * disagree about it, and adding a screen is one line in one array.
 *
 * Two decisions worth reading before changing anything here.
 *
 * **The items are `<NavLink>`s, not buttons.** The design draws `<button
 * onClick>` because a DC document has no router, but a destination that is not
 * an anchor cannot be middle-clicked, cannot be copied, and has to be told it is
 * current. React Router's `NavLink` supplies `aria-current="page"` from the
 * address itself, which is the one spelling that keeps the visual "you are here"
 * and the announced one in step.
 *
 * **A badge that has not loaded shows nothing.** `navBadgeCount` folds "no
 * badge", "not fetched yet" and "zero" into one `null`, because the alternative
 * — an empty amber slot that grows a number twenty seconds later — reads as a
 * rendering fault for as long as it is blank, and the sidebar is on screen the
 * whole time. Design line 1068 puts a badge on exactly one entry.
 */

import { NavLink } from 'react-router-dom'

import { useNavCounts, useStatus } from '@/api/queries'
import { cx } from '@/design'
import { NAV_ITEMS, navBadgeCount } from '@/shell/nav'

import styles from '@/shell/Sidebar.module.css'

export function Sidebar() {
  // Both of these are shared caches, not private fetches: `useStatus()` is the
  // same key the footer polls (activityLimit 0 in both places, or they would be
  // two queries holding one payload) and `useNavCounts()` is the badge feed.
  const counts = useNavCounts()
  const status = useStatus()

  const version = status.data?.version

  return (
    <aside className={styles.sidebar}>
      <nav className={styles.nav} aria-label="Sections">
        {NAV_ITEMS.map((item) => {
          const badge = navBadgeCount(item, counts.data)
          return (
            <NavLink
              key={item.id}
              to={item.path}
              end={item.end}
              data-plain=""
              className={({ isActive }) =>
                cx(styles.item, isActive ? styles.itemActive : undefined)
              }
            >
              <span className={styles.glyph} aria-hidden="true">
                {item.glyph}
              </span>
              <span className={styles.label}>{item.label}</span>
              {badge === null ? null : (
                <span className={styles.badge}>
                  {badge}
                  <span className="visuallyHidden"> waiting</span>
                </span>
              )}
            </NavLink>
          )
        })}
      </nav>

      <div className={styles.spacer} />

      {/* Design 72. The version is the server's, from the status payload — not
          the bundle's, which would report the front end and quietly disagree
          with the process it is talking to. Nothing is drawn until it arrives:
          a `v` on its own is worse than a gap. */}
      <div className={styles.version}>{version === undefined ? null : `v${version}`}</div>
    </aside>
  )
}
