/**
 * The "something is not wired up" stack, directly under the top bar.
 *
 * **The design draws none of this**, and adding it is a deliberate departure
 * recorded in `web/WIRING.md`. `GET /api/banners` is a real payload with four
 * real codes — `no_credentials`, `no_client`, `no_app_secret`, `breaker_open` —
 * and a Qobuzarr with no credentials that says nothing is the worst screen in
 * the application: every list is empty, every search returns nothing, and the
 * one fact that explains all of it is in a payload nobody renders.
 *
 * ── why it reads `status.banners` rather than calling `useBanners()` ───────
 *
 * They are the same data set. `StatusOut.banners` is the identical list inside
 * a payload the sidebar and the footer already poll every ten seconds, and
 * `queries.ts` says so in as many words at `useBanners`: *"The shell does not
 * use this — `App.tsx` renders `status.data.banners` … using it beside
 * `useStatus` in the same tree would put one data set behind two keys, which is
 * the first thing the polling contract forbids."* The build spec's own §8.1 is
 * the same rule from the other end. So the frame holds one status query, read
 * by three components, and `useBanners()` stays available for a screen that
 * needs the list *without* the status payload — of which there are currently
 * none.
 *
 * ── the prose is the server's ─────────────────────────────────────────────
 *
 * `title` and `message` are rendered as sent. Routing on `code` and writing a
 * sentence per code here would put the same claim in two repositories, and the
 * server's version is the one that knows which environment variable is missing.
 */

import { useStatus } from '@/api/queries'
import type { BannerLevel } from '@/api/types'
import { cx } from '@/design'

import styles from '@/shell/Banners.module.css'

const TONE: Record<BannerLevel, string | undefined> = {
  info: styles.info,
  warning: styles.warning,
  error: styles.error,
}

export function Banners() {
  const status = useStatus()
  const banners = status.data?.banners ?? []

  if (banners.length === 0) return null

  return (
    // `aria-live` rather than `role="alert"`: these arrive with the first
    // status poll, so they are almost always already on screen when the page
    // is read, and an assertive region would interrupt whatever the person is
    // doing when a breaker trips mid-session.
    <div className={styles.stack} aria-live="polite">
      {banners.map((banner) => (
        <div
          key={banner.code}
          className={cx(styles.banner, TONE[banner.level])}
          data-banner={banner.code}
        >
          <span className={styles.title}>{banner.title}</span>
          <span className={styles.message}>{banner.message}</span>
        </div>
      ))}
    </div>
  )
}
